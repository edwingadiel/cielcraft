using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Core.Scheduling;
using CielCraft.Game;

namespace CielCraft.Crafting;

public enum ProductionState
{
    Idle,
    PreparingGather,
    WaitingForWindow,
    Teleporting,
    MovingToArea,
    RunningGather,

    /// <summary>A non-gather source (vendor, exchange, fishing …) is supplying the current material (M3).</summary>
    RunningSource,
    PreparingStep,
    RunningBatch,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// Executes a ProductionPlan's craft steps in dependency order (spec §62):
/// per step, switch to the recipe's job (via gearsets) when needed, open the
/// recipe in the crafting log, run a verified batch, then move on. Missing
/// raw materials are gathered first (spec §67), teleporting to the material's
/// node territory and traveling to the node area when needed (spec §68).
/// Timed nodes follow the schedule (roadmap 7.15): the runner rebuilds a
/// <see cref="Schedule"/> at every decision point and does what its first
/// entry says — leave for a window, gather an untimed material or craft a
/// ready step while waiting, or wait here / at home.
/// Dalamud-free (roadmap 5.1): the plugin ticks it from the framework driver.
/// </summary>
public sealed class ProductionRunner : AutomationMachine<ProductionState>
{
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AreaTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private const float NodeAreaArrivalRange = 60f;

    /// <summary>Teleport + travel before a timed window (7.15); the scheduler's travel lead.</summary>
    public static readonly TimeSpan WindowTravelLead = TimeSpan.FromMinutes(2);

    /// <summary>How far ahead the schedule lists windows (about 2.5 Eorzea days).</summary>
    public static readonly TimeSpan ScheduleHorizon = TimeSpan.FromHours(3);

    /// <summary>The node loop is started this close to the window so its first probe lands as the node pops.</summary>
    private static readonly TimeSpan LoopStartMargin = TimeSpan.FromSeconds(15);

    /// <summary>A timed task that keeps missing its window is given up after this many re-schedules.</summary>
    private const int MaxReschedulesPerTask = 3;

    /// <summary>GP pool assumed when the current job shows none (a crafter's snapshot); the arrival regen dominates anyway.</summary>
    private const int AssumedMaxGp = 800;

    /// <summary>Base GP regeneration per 3 s server tick; each Enhanced GP Regeneration trait adds one.</summary>
    private const int BaseGpRegenPerTick = 5;

    private readonly IGameBridge gameBridge;
    private readonly BatchCrafter batchCrafter;
    private readonly DalamudRecipeProvider recipeProvider;
    private readonly Gathering.GatheringLoop gatheringLoop;
    private readonly Game.MaintenanceService maintenance;
    private readonly IReadOnlyList<IMaterialSource> sources; // asked, in order, for materials no node yields (M3)
    private ISourceRun? sourceRun;                            // the supply job in flight during RunningSource
    private readonly GatheringDatabase gatheringDatabase;
    private readonly INavigationProvider navigation;
    private readonly Configuration configuration;
    private readonly CapabilityReader capabilities;
    private readonly IUserNotifier notifier;
    private readonly Throttle retry;
    private readonly TravelDriver travel;

    private ProductionPlan? plan;
    private int stepIndex;
    private readonly List<GatherTask> gatherQueue = [];
    private int gatherIndex;
    private DateTime phaseStartedAt;
    private bool gearsetRequested;
    private bool keepSavedOnIdle;     // a gentle stop leaves the run resumable
    private bool sawLoadingScreen;
    private System.Numerics.Vector3? areaDestination;
    private bool returnTeleport;      // Teleporting phase is the pre-craft return (zone aetheryte or home, 7.6)
    private uint returnTerritoryId;
    private string returnLabel = "Back at the aetheryte";
    private bool homeTeleportPending; // 7.6: teleport to the crafting location before the first craft step
    private bool finishAfterReturn;   // 7.1: a gather-only plan still goes home / to the aetheryte before completing
    private bool homeFallbackToZone;  // ...and when that fails after gathering, the zone aetheryte return
    private int homeTeleportAttempts;
    private DateTime zoneArrivedAt = DateTime.MinValue;
    private DateTime gatherDoneAt = DateTime.MinValue;
    private DateTime lastNodeProbeAt = DateTime.MinValue;
    private bool lastNodeProbe;
    private readonly List<TargetProgress> targets = []; // the ordered targets with their start-of-run bag counts
    private DateTime productionStartedAt;
    private int replanCount;
    private System.Numerics.Vector3? interferenceAnchor;

    // Timed-node scheduling (7.15).
    private ScheduledVisit? currentVisit;     // the window the current gather task is for; null for untimed work
    private DateTime waitUntil;               // when the current wait ends (departure for a window)
    private ScheduleWaitInfo? waitInfo;
    private bool waitAfterReturn;             // the home teleport in flight is for a wait, not for crafting
    private uint homeTerritoryId;             // where this run's last home teleport landed; 0 = not home
    private bool homeUnavailable;             // a home teleport failed this run: wait in place from now on
    private int gatherDone;                   // tasks finished, for the "task i/n" texts

    /// <summary>
    /// One material to gather; Tier is set for a collectable gather order
    /// (7.1), null for plain gathering. Remaining is what is still missing
    /// (a window that closed short leaves some, 7.15); Reschedules counts
    /// the windows missed so far.
    /// </summary>
    private sealed record GatherTask(
        uint ItemId,
        int Amount,
        uint JobId,
        uint TerritoryId,
        System.Numerics.Vector2 AreaPosition,
        IReadOnlyList<EtWindow> Windows,
        CollectableTier? Tier = null,
        NodeKind Kind = NodeKind.Normal,
        SourceOffer? Offer = null,
        IMaterialSource? Source = null) // set for a material a non-gather source supplies (M3)
    {
        public int Remaining { get; init; } = Amount;

        public bool Done { get; init; }

        public int Reschedules { get; init; }

        public bool IsTimed => Windows.Count > 0;
    }

    /// <summary>What the runner is waiting for and where (roadmap 7.15); shown by the schedule page.</summary>
    public sealed record ScheduleWaitInfo(string Where, DateTime UntilUtc, string ItemName, string WindowLabel, string ZoneName, DateTime? WindowOpensUtc);

    /// <summary>
    /// One target of the run (roadmap 7.13) with the bag counts at start, so
    /// progress is always "count now − initial" and survives replans and a
    /// reload. The plan in flight may carry smaller (remaining) quantities;
    /// this keeps the ordered amount.
    /// </summary>
    private sealed record TargetProgress(PlanTarget Target, int InitialCount, int InitialHqCount)
    {
        public int Produced(IGameBridge bridge) => Math.Max(0, bridge.GetItemCount(Target.ItemId) - InitialCount);

        public int ProducedHq(IGameBridge bridge) => Math.Max(0, bridge.GetHqItemCount(Target.ItemId) - InitialHqCount);

        /// <summary>
        /// What the order still needs: the HQ gain for ForceHq, the total gain
        /// otherwise. A materials-only target has no craft to count, so it is
        /// always re-planned in full — the resolver drops what is in stock.
        /// </summary>
        public int Remaining(IGameBridge bridge) => Target.MaterialsOnly
            ? Target.Quantity
            : Target.Quantity - (Target.Mode == ProductionMode.ForceHq ? ProducedHq(bridge) : Produced(bridge));
    }

    public int CompletedSteps => stepIndex;

    /// <summary>Whether MIN/BTN can gather the item, per the node data; what the order planner needs for gather orders (7.1).</summary>
    public bool IsGatherable(uint itemId) => gatheringDatabase.GetGatheringJob(itemId) != null;

    /// <summary>"Stop gently" (roadmap 7.20): finish the current step or gather task, then stop with the run left resumable.</summary>
    public bool StopAfterStep { get; private set; }
    public int TotalSteps => plan?.CraftSteps.Count ?? 0;

    /// <summary>The schedule of the run in flight (roadmap 7.15), rebuilt at every decision point; null when idle.</summary>
    public Schedule? CurrentSchedule { get; private set; }

    /// <summary>The wait in progress (where, until when, for which window); null unless waiting.</summary>
    public ScheduleWaitInfo? CurrentWait => State == ProductionState.WaitingForWindow ? waitInfo : null;

    /// <summary>Gather tasks not yet finished.</summary>
    private bool GatheringRemains => gatherQueue.Any(t => !t.Done);

    /// <summary>At the home point of this run (the last home teleport landed here and no other teleport followed).</summary>
    private bool AtHome => homeTerritoryId != 0 && gameBridge.CurrentTerritoryId == homeTerritoryId;

    public ProductionRunner(
        IGameBridge gameBridge,
        BatchCrafter batchCrafter,
        DalamudRecipeProvider recipeProvider,
        Gathering.GatheringLoop gatheringLoop,
        GatheringDatabase gatheringDatabase,
        Game.MaintenanceService maintenance,
        INavigationProvider navigation,
        Configuration configuration,
        CapabilityReader capabilities,
        ILog log,
        IClock clock,
        IUserNotifier notifier,
        IReadOnlyList<IMaterialSource>? sources = null)
        : base(log, clock, "[Production]", ProductionState.Idle, "Idle.")
    {
        this.notifier = notifier;
        this.sources = sources ?? [];
        retry = new Throttle(clock, RetryInterval);
        travel = new TravelDriver(navigation, gameBridge, clock, log, "[Production]");
        this.configuration = configuration;
        this.capabilities = capabilities;
        this.gameBridge = gameBridge;
        this.batchCrafter = batchCrafter;
        this.recipeProvider = recipeProvider;
        this.gatheringLoop = gatheringLoop;
        this.gatheringDatabase = gatheringDatabase;
        this.maintenance = maintenance;
        this.navigation = navigation;
    }

    public bool Start(ProductionPlan productionPlan) =>
        Start(productionPlan, productionPlan.Targets.Select(BaselineNow).ToList());

    /// <summary>Bag counts of a target right now; the baseline a fresh run measures progress against.</summary>
    private TargetProgress BaselineNow(PlanTarget target) =>
        new(target, gameBridge.GetItemCount(target.ItemId), gameBridge.GetHqItemCount(target.ItemId));

    /// <summary>Starts a plan against given baselines (a resumed run keeps the ones it was saved with).</summary>
    private bool Start(ProductionPlan productionPlan, List<TargetProgress> progress)
    {
        if (State is ProductionState.PreparingStep or ProductionState.RunningBatch or ProductionState.Paused)
            return false;

        // A materials-only order can be all gathering (7.13); only a plan with
        // neither crafts nor materials has nothing to do.
        if (productionPlan.CraftSteps.Count == 0 && productionPlan.RawMaterials.Count == 0)
        {
            Transition(ProductionState.Idle, "Nothing to craft in this plan.");
            return false;
        }

        // Per-run spend caps (7.3b / 7.17) start fresh with the run.
        foreach (var source in sources)
        {
            if (source is IRunBudget budget)
                budget.ResetRunBudget();
        }

        // Fresh capabilities for the pre-flight checks below (roadmap 7.16):
        // a book learned since login must not be refused.
        capabilities.Refresh();

        if (!TryBuildGatherQueue(productionPlan))
            return false;

        // Pre-flight: every job the plan needs must have a gearset, or the run
        // dies 15s into a step with a vague message (roadmap 7.9).
        var jobWithoutGearset = FindJobWithoutGearset(productionPlan);
        if (jobWithoutGearset != null)
        {
            Transition(ProductionState.Idle, $"No gearset for {jobWithoutGearset}. Save one in the Gear Set list, then run again.");
            return false;
        }

        // Pre-flight: a step whose master book is not unlocked can never be
        // crafted (roadmap 7.16); the planner already routed intermediates
        // around locked books, so this is the target or an unavoidable step.
        if (FindStepWithLockedBook(productionPlan) is { } lockedStep)
        {
            Transition(
                ProductionState.Idle,
                $"{recipeProvider.GetItemName(lockedStep.ItemId)} needs {recipeProvider.GetRecipeBookName(lockedStep.BookId)}, " +
                "which is not unlocked. Learn the book, then run again.");
            return false;
        }

        if (gameBridge.IsCrafting)
        {
            Transition(ProductionState.Idle, "Cannot start while a craft is in progress.");
            return false;
        }

        plan = productionPlan;
        stepIndex = 0;
        StopAfterStep = false;
        targets.Clear();
        targets.AddRange(progress);
        productionStartedAt = Clock.UtcNow;
        replanCount = 0;
        SaveProgress(active: true);
        EnterPreparing();

        if (gatherQueue.Count > 0)
            DecideNext("start");
        else
            GoToCraftingSpotThenCraft(afterGathering: false);
        return true;
    }

    public void Pause(string reason)
    {
        if (State == ProductionState.RunningBatch)
            batchCrafter.Pause("production paused");
        else if (State == ProductionState.RunningGather)
            gatheringLoop.Pause("production paused");
        else if (State == ProductionState.RunningSource)
            sourceRun?.Pause("production paused");

        if (State is ProductionState.MovingToArea)
        {
            travel.Stop();
            navigation.Stop();
        }

        if (State is ProductionState.PreparingStep or ProductionState.RunningBatch
            or ProductionState.PreparingGather or ProductionState.RunningGather or ProductionState.RunningSource
            or ProductionState.Teleporting or ProductionState.MovingToArea
            or ProductionState.WaitingForWindow)
            Transition(ProductionState.Paused, $"Paused: {reason}.");
    }

    public void Resume()
    {
        if (State != ProductionState.Paused)
            return;

        if (sourceRun is { State: SourceRunState.Paused })
        {
            sourceRun.Resume();
            Transition(ProductionState.RunningSource, sourceRun.StatusText);
        }
        else if (gatheringLoop.State == Gathering.GatheringLoopState.Paused)
        {
            gatheringLoop.Resume();
            Transition(ProductionState.RunningGather, GatherText("Gathering"));
        }
        else if (batchCrafter.State == BatchState.Paused)
        {
            batchCrafter.Resume();
            Transition(ProductionState.RunningBatch, StepText("Crafting"));
        }
        else if (GatheringRemains)
        {
            DecideNext("resume");
        }
        else if (plan == null || stepIndex >= plan.CraftSteps.Count)
        {
            // Gather-only plan (materials-only order) paused after its last node.
            Transition(ProductionState.Completed, "Completed: materials gathered.");
        }
        else
        {
            EnterPreparing();
            Transition(ProductionState.PreparingStep, StepText("Preparing"));
        }
    }

    /// <summary>Request a gentle stop; a second call cancels it. No-op unless a run is active.</summary>
    public void StopGently()
    {
        if (State is ProductionState.Idle or ProductionState.Completed or ProductionState.Failed)
            return;

        StopAfterStep = !StopAfterStep;
        Log.Information(StopAfterStep
            ? "[Production] Will stop after the current step."
            : "[Production] Gentle stop cancelled.");
    }

    public void Stop()
    {
        StopAfterStep = false;
        batchCrafter.Stop();
        gatheringLoop.Stop();
        sourceRun?.Stop();
        sourceRun = null;
        travel.Stop();
        navigation.Stop();
        if (State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed))
            Transition(ProductionState.Idle, $"Stopped by user at step {stepIndex + 1}/{TotalSteps}.");
    }

    protected override void OnTick()
    {
        // Manual movement during phases where the character should be still
        // means the user has taken over (spec §49): step aside politely.
        // A mender trip (7.3a) moves the character on purpose during these phases.
        if (State is ProductionState.PreparingStep or ProductionState.PreparingGather or ProductionState.WaitingForWindow
            && !gameBridge.IsCrafting && !gameBridge.IsBetweenAreas && !navigation.IsMoving && !maintenance.IsMovingCharacter)
        {
            var position = gameBridge.GetPlayerState()?.Position;
            if (position != null)
            {
                if (interferenceAnchor is { } anchor
                    && System.Numerics.Vector3.Distance(anchor, position.Value) > 3f)
                {
                    interferenceAnchor = null;
                    Pause("manual movement detected");
                    return;
                }

                interferenceAnchor ??= position;
            }
        }
        else
        {
            interferenceAnchor = null;
        }

        switch (State)
        {
            case ProductionState.PreparingGather:
                TickPreparingGather();
                break;
            case ProductionState.WaitingForWindow:
                TickWaitingForWindow();
                break;
            case ProductionState.Teleporting:
                TickTeleporting();
                break;
            case ProductionState.MovingToArea:
                TickMovingToArea();
                break;
            case ProductionState.RunningGather:
                TickRunningGather();
                break;
            case ProductionState.RunningSource:
                TickRunningSource();
                break;
            case ProductionState.PreparingStep:
                TickPreparing();
                break;
            case ProductionState.RunningBatch:
                TickRunning();
                break;
        }
    }

    private void TickPreparingGather()
    {
        var task = gatherQueue[gatherIndex];

        if (Clock.UtcNow - phaseStartedAt > PrepareTimeout)
        {
            Fail($"could not prepare gathering for {recipeProvider.GetItemName(task.ItemId)}" +
                 (gearsetRequested ? " — is there a MIN/BTN gearset?" : ""));
            return;
        }

        if (gameBridge.IsCrafting)
            return;

        // A node window (left over from an earlier run, or opened by hand)
        // pins the character; travel cannot start until it is closed and the
        // gathering condition has cleared.
        if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
        {
            retry.Try(gameBridge.CloseGatheringWindow);
            return;
        }

        // A sourced material (M3): the source's run owns travel and dialogs.
        if (task.Source != null && task.Offer != null)
        {
            sourceRun = task.Source.Start(task.Offer with { Amount = task.Remaining });
            Log.Information(
                $"[Production] Source task {gatherDone + 1}/{gatherQueue.Count}: {task.Offer.Description} " +
                $"({recipeProvider.GetItemName(task.ItemId)} ×{task.Remaining} via {task.Source.Name}).");
            Transition(ProductionState.RunningSource, $"{task.Offer.Description} ({gatherDone + 1}/{gatherQueue.Count}).");
            return;
        }

        if (!EnsureJob(task.JobId, "gathering job"))
            return;

        maintenance.PrepareFor(Game.MaintenanceActivity.Gathering);

        // A timed task before its departure time (7.15): the schedule decides
        // when to leave; this only catches drift, since DecideNext already
        // waits before handing over a visit whose departure is due.
        if (currentVisit is { } early && Clock.UtcNow < early.Depart)
        {
            BeginWait(early.Depart, early);
            return;
        }

        // Wrong zone: teleport there first (spec §68).
        if (task.TerritoryId != 0 && gameBridge.CurrentTerritoryId != task.TerritoryId)
        {
            retry.Try(() =>
            {
                sawLoadingScreen = false;
                homeTerritoryId = 0; // leaving home (7.15)
                if (gameBridge.TeleportToTerritory(task.TerritoryId))
                {
                    EnterPhase(ProductionState.Teleporting, GatherText("Teleporting for"));
                }
                else
                {
                    Fail($"no attuned aetheryte in territory {task.TerritoryId} " +
                         $"for {recipeProvider.GetItemName(task.ItemId)}");
                }
            });
            return;
        }

        // Right zone but the node area may be far: approach it until nodes
        // appear in the object table. A visible node alone is not enough —
        // a different node group can sit right next to the aetheryte. A timed
        // node that has not popped yet cannot be visible: being near the
        // area is enough then (7.15).
        var windowOpen = WindowOpen(task);
        if (task.AreaPosition != default
            && !(NearNodeArea(task) && (NodeNearby() || !windowOpen)))
        {
            areaDestination = null;
            EnterPhase(ProductionState.MovingToArea, GatherText("Traveling to the node area for"));
            return;
        }

        // Timed node (7.15): hold at the area until the window is about to
        // open, then start the loop with a no-node budget that reaches a
        // minute past the window's start.
        TimeSpan? noNodeTimeout = null;
        if (currentVisit is { } visit && !windowOpen)
        {
            var untilOpen = EorzeaClock.RealTimeUntilOpen(task.Windows, EtNow());
            if (untilOpen > LoopStartMargin)
            {
                // Travel took longer than the window lasted: the next one.
                if (Clock.UtcNow > visit.End)
                {
                    Log.Information($"[Schedule] {recipeProvider.GetItemName(task.ItemId)}'s {visit.EtLabel} window passed while traveling; re-scheduling.");
                    MissWindow(task);
                    return;
                }

                phaseStartedAt = Clock.UtcNow; // the prepare budget starts when the window does
                StatusText = $"At the node area; {recipeProvider.GetItemName(task.ItemId)}'s {visit.EtLabel} window opens in {Countdown(untilOpen)}.";
                return;
            }

            noNodeTimeout = untilOpen + TimeSpan.FromMinutes(1);
        }

        if (gatheringLoop.Start(task.ItemId, task.Remaining, AreaCenterFor(task), task.Tier, task.Kind, noNodeTimeout))
        {
            Log.Information(
                $"[Production] Gather task {gatherDone + 1}/{gatherQueue.Count}: " +
                $"{recipeProvider.GetItemName(task.ItemId)} ×{task.Remaining}" +
                (task.Tier is { } tier ? $" ({tier} collectables)" : "") +
                (currentVisit is { } window ? $" in the {window.EtLabel} window, ≈{window.ExpectedYield} expected from {window.NodesPlanned} node(s)" : "") + ".");
            Transition(ProductionState.RunningGather, GatherText("Gathering"));
        }
    }

    /// <summary>
    /// Waiting for a window (7.15): in place, or at home after the teleport
    /// the schedule asked for. Ends at the departure time the schedule set;
    /// a gentle stop lands here right away since nothing is in flight.
    /// </summary>
    private void TickWaitingForWindow()
    {
        if (homeTeleportPending && TickHomeTeleport())
            return;

        if (StopAfterStep)
        {
            StopGentlyNow("while waiting for a window");
            return;
        }

        if (Clock.UtcNow >= waitUntil)
        {
            DecideNext("wait over");
            return;
        }

        StatusText = WaitText();
    }

    private int EtNow() => EorzeaClock.MinuteOfDay(new DateTimeOffset(Clock.UtcNow));

    private bool WindowOpen(GatherTask task) => EorzeaClock.IsOpen(task.Windows, EtNow());

    private static string Countdown(TimeSpan span) =>
        span < TimeSpan.Zero ? "0s" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:D2}s" : $"{span.Seconds}s";

    /// <summary>Enters the wait phase until <paramref name="until"/>, naming the window waited for.</summary>
    private void BeginWait(DateTime until, ScheduledVisit? visit)
    {
        waitUntil = until;
        currentVisit = visit;
        var atHome = AtHome;
        waitInfo = new ScheduleWaitInfo(
            atHome ? "at home" : "here",
            until,
            visit != null ? recipeProvider.GetItemName(visit.Task.ItemId) : "the next window",
            visit?.EtLabel ?? "",
            visit != null ? GatheringDatabase.GetTerritoryName(visit.Task.TerritoryId) : "",
            visit?.Start);
        EnterPhase(ProductionState.WaitingForWindow, WaitText());
    }

    private string WaitText()
    {
        var info = waitInfo;
        if (info == null)
            return "Waiting for the next window.";

        var now = Clock.UtcNow;
        var opens = info.WindowOpensUtc is { } start ? $"opens in {Countdown(start - now)}" : "";
        return $"Waiting {info.Where} for {info.ItemName}'s {info.WindowLabel} window in {info.ZoneName}: {opens}; leaving in {Countdown(info.UntilUtc - now)}.";
    }

    private void TickTeleporting()
    {
        var targetTerritory = returnTeleport ? returnTerritoryId : gatherQueue[gatherIndex].TerritoryId;

        if (Clock.UtcNow - phaseStartedAt > TeleportTimeout)
        {
            Fail("teleport did not complete (cast interrupted or loading took too long)");
            return;
        }

        if (gameBridge.IsBetweenAreas)
        {
            sawLoadingScreen = true;
            zoneArrivedAt = DateTime.MinValue;
            return;
        }

        if (sawLoadingScreen
            && gameBridge.CurrentTerritoryId == targetTerritory
            && gameBridge.GetPlayerState() != null)
        {
            // Let the zone settle before the next server-visible action (pacing).
            if (zoneArrivedAt == DateTime.MinValue)
                zoneArrivedAt = Clock.UtcNow;
            if (Clock.UtcNow - zoneArrivedAt < Core.Pacing.AfterZoneChange)
                return;

            EnterPreparing();
            if (returnTeleport)
            {
                returnTeleport = false;
                if (waitAfterReturn)
                {
                    // Home for a window wait (7.15): the schedule now sees AtHome.
                    waitAfterReturn = false;
                    DecideNext("home");
                }
                else if (finishAfterReturn)
                {
                    finishAfterReturn = false;
                    Transition(ProductionState.Completed, $"Completed: materials gathered; {returnLabel.ToLowerInvariant()}.");
                }
                else
                {
                    Transition(ProductionState.PreparingStep, StepText($"{returnLabel}; preparing"));
                }
            }
            else
            {
                Transition(ProductionState.PreparingGather, GatherText("Arrived; preparing to gather"));
            }
        }
    }

    /// <summary>
    /// Where the craft steps happen (roadmap 7.6). With a crafting location
    /// set, the home teleport is attempted from the preparing phase (the
    /// crafting log may need closing first and Telepo can refuse a cast).
    /// Otherwise, after gathering, the zone-aetheryte return below; with no
    /// gathering the character crafts where it stands.
    /// </summary>
    private void GoToCraftingSpotThenCraft(bool afterGathering)
    {
        // Already home from a window wait (7.15): craft right here.
        if (AtHome)
        {
            Transition(ProductionState.PreparingStep, StepText("Home; preparing"));
            return;
        }

        if (configuration.CraftingLocation != CraftingLocation.Stay && !homeUnavailable)
        {
            homeTeleportPending = true;
            homeFallbackToZone = afterGathering;
            homeTeleportAttempts = 0;
            Transition(ProductionState.PreparingStep, StepText($"Heading to the {LocationLabel()} before"));
            return;
        }

        if (afterGathering)
            ReturnToAetheryteThenCraft();
        else
            Transition(ProductionState.PreparingStep, StepText("Preparing"));
    }

    /// <summary>
    /// Gathering ends wherever the last node happened to be — often next to
    /// mobs. Teleport back to the zone's aetheryte before crafting so the
    /// character idles somewhere safe; craft in place if no aetheryte is attuned.
    /// </summary>
    private void ReturnToAetheryteThenCraft()
    {
        var territory = gameBridge.CurrentTerritoryId;
        sawLoadingScreen = false;
        homeTerritoryId = 0;
        if (territory != 0 && gameBridge.TeleportToTerritory(territory))
        {
            returnTeleport = true;
            returnTerritoryId = territory;
            returnLabel = "Back at the aetheryte";
            EnterPhase(ProductionState.Teleporting, StepText("Returning to the aetheryte before"));
            return;
        }

        Transition(ProductionState.PreparingStep, StepText("Preparing"));
    }

    /// <summary>
    /// The pending home teleport (7.6), ticked from the preparing phase. True
    /// while it still owns the tick (closing the log, waiting on the retry
    /// throttle, or having just moved to Teleporting); false once the
    /// character is home-bound no more and the step should be prepared here.
    /// </summary>
    private bool TickHomeTeleport()
    {
        // Telepo refuses while the crafting log is open; close it first.
        if (gameBridge.IsPreparingToCraft || gameBridge.IsAddonVisible("RecipeNote"))
        {
            retry.Try(gameBridge.CloseRecipeNote);
            return true;
        }

        retry.Try(() =>
        {
            homeTeleportAttempts++;
            sawLoadingScreen = false;
            if (gameBridge.TeleportHome(configuration.CraftingLocation, out var territory))
            {
                homeTeleportPending = false;
                returnTeleport = true;
                returnTerritoryId = territory;
                homeTerritoryId = territory; // AtHome once the arrival is verified (7.15)
                returnLabel = configuration.CraftingLocation == CraftingLocation.InnRoom
                    ? "At the inn city aetheryte"
                    : "Home";
                EnterPhase(ProductionState.Teleporting, waitAfterReturn
                    ? $"Teleporting to the {LocationLabel()} to wait for {waitInfo?.ItemName ?? "the next window"}'s window."
                    : StepText($"Teleporting to the {LocationLabel()} before"));
                return;
            }

            // No such aetheryte, or the cast keeps being refused: craft where
            // we are (after gathering: at the zone aetheryte, as before 7.6);
            // a wait (7.15) stays in place, and so does the rest of the run.
            if (territory == 0 || homeTeleportAttempts >= 3)
            {
                homeTeleportPending = false;
                homeUnavailable = true;
                Log.Information(territory == 0
                    ? $"[Production] No {LocationLabel()} aetheryte to teleport to; {(waitAfterReturn ? "waiting" : "crafting")} in place."
                    : $"[Production] The teleport to the {LocationLabel()} was refused {homeTeleportAttempts} times; {(waitAfterReturn ? "waiting" : "crafting")} in place.");
                if (waitAfterReturn)
                {
                    waitAfterReturn = false;
                    DecideNext("no home");
                }
                else if (homeFallbackToZone)
                {
                    ReturnToAetheryteThenCraft();
                }
                else
                {
                    EnterPhase(ProductionState.PreparingStep, StepText("Preparing"));
                }
            }
        });

        return homeTeleportPending || State != ProductionState.PreparingStep;
    }

    private string LocationLabel() => configuration.CraftingLocation switch
    {
        CraftingLocation.EstateHall => "estate hall",
        CraftingLocation.Apartment => "apartment",
        CraftingLocation.InnRoom => "inn city",
        _ => "crafting spot",
    };

    private void TickMovingToArea()
    {
        var task = gatherQueue[gatherIndex];

        if (Clock.UtcNow - phaseStartedAt > AreaTimeout)
        {
            Fail("could not reach the node area in time");
            return;
        }

        // A targetable node counts as "area reached" only once we are near the
        // recorded area; nodes of another group can be visible on the way. A
        // timed node before its window cannot be visible: being near the area
        // (the travel leg's own arrival) is the signal then (7.15).
        if (NearNodeArea(task) && (NodeNearby() || (currentVisit != null && !WindowOpen(task) && travel.State == TravelState.Arrived)))
        {
            navigation.Stop();
            EnterPreparing();
            Transition(ProductionState.PreparingGather, GatherText("Node area reached; preparing to gather"));
            return;
        }

        if (!navigation.IsReady)
            return; // navmesh still building after the zone change

        var player = gameBridge.GetPlayerState();
        if (player == null)
            return;

        if (areaDestination == null)
        {
            // The exported node-area position is X/Z only; project it onto the navmesh.
            var approximate = new System.Numerics.Vector3(task.AreaPosition.X, player.Position.Y, task.AreaPosition.Y);
            areaDestination = navigation.FindNearestMeshPoint(approximate, 40f, 500f);
            if (areaDestination == null)
            {
                Fail($"could not project the node area ({task.AreaPosition.X:F0}, {task.AreaPosition.Y:F0}) onto the navmesh");
                return;
            }

            // Loose arrival: the leg ends anywhere within the arrival range,
            // and nodes then appear as they spawn into the object table.
            // Flight needs the zone's aether currents (7.16).
            travel.Start(
                areaDestination.Value,
                NodeAreaArrivalRange,
                capabilities.Current.CanFlyIn(gameBridge.CurrentTerritoryId),
                preciseArrival: false,
                AreaTimeout,
                "the node area");
        }

        travel.Tick();
        if (travel.State == TravelState.Failed)
            Fail(travel.FailureReason);
    }

    /// <summary>A source run (M3) in flight: ticked here, finished like a gather task.</summary>
    private void TickRunningSource()
    {
        if (sourceRun == null)
        {
            Fail("the source run vanished");
            return;
        }

        sourceRun.Tick();
        StatusText = $"{sourceRun.StatusText} ({gatherDone + 1}/{gatherQueue.Count})";
        switch (sourceRun.State)
        {
            case SourceRunState.Completed:
                var task = gatherQueue[gatherIndex];
                Log.Information($"[Production] {task.Offer?.Description}: obtained {sourceRun.Obtained} ({sourceRun.StatusText}).");
                sourceRun = null;
                gatherQueue[gatherIndex] = task with { Done = true, Remaining = 0 };
                gatherDone++;
                areaDestination = null;
                EnterPreparing();
                if (StopAfterStep)
                {
                    StopGentlyNow($"after source task {gatherDone}/{gatherQueue.Count}");
                    break;
                }

                DecideNext("source task done");
                break;

            case SourceRunState.Failed:
                var failed = gatherQueue[gatherIndex];
                var reason = sourceRun.StatusText;
                sourceRun = null;
                Fail($"{failed.Offer?.Description} failed ({reason})");
                break;

            case SourceRunState.Paused:
                Transition(ProductionState.Paused, $"Paused: {sourceRun.StatusText}");
                break;

            case SourceRunState.Idle:
                sourceRun = null;
                Transition(ProductionState.Paused, "Paused: the source run was stopped.");
                break;
        }
    }

    private void TickRunningGather()
    {
        switch (gatheringLoop.State)
        {
            case Gathering.GatheringLoopState.Completed:
                // Settle after leaving the node before teleporting or moving on (pacing).
                if (gatherDoneAt == DateTime.MinValue)
                {
                    gatherDoneAt = Clock.UtcNow;
                    StatusText = GatherText("Gathered; settling after");
                    break;
                }

                if (Clock.UtcNow - gatherDoneAt < Core.Pacing.AfterGatherComplete)
                    break;

                gatherDoneAt = DateTime.MinValue;
                gatherQueue[gatherIndex] = gatherQueue[gatherIndex] with { Done = true, Remaining = 0 };
                gatherDone++;
                areaDestination = null; // never reuse a previous task's area point
                EnterPreparing();
                if (StopAfterStep)
                {
                    StopGentlyNow($"after gather task {gatherDone}/{gatherQueue.Count}");
                    break;
                }

                // The schedule picks what comes next: another task, a ready
                // craft step, a wait — or, with nothing left to gather, the
                // crafting phase / the way home (7.15).
                DecideNext("gather task done");
                break;

            case Gathering.GatheringLoopState.Paused:
                Transition(ProductionState.Paused, $"Paused: {gatheringLoop.StatusText}");
                break;

            case Gathering.GatheringLoopState.Failed:
                // A timed visit that ends short — the window closed, or the
                // loop gave up inside it (an unreachable node, 2026-09-15) —
                // is not a failure: keep what was gathered and take the next
                // window (7.15); MissWindow caps the retries.
                if (currentVisit != null)
                {
                    var task = gatherQueue[gatherIndex];
                    var gathered = Math.Max(0, gatheringLoop.Gathered);
                    Log.Information(
                        $"[Schedule] {recipeProvider.GetItemName(task.ItemId)}'s {currentVisit.EtLabel} window " +
                        (WindowOpen(task) ? "visit failed" : "closed") +
                        $" with {gathered}/{task.Remaining} gathered ({gatheringLoop.StatusText}); re-scheduling the rest.");
                    gatherQueue[gatherIndex] = task with { Remaining = Math.Max(0, task.Remaining - gathered) };
                    gatherDoneAt = DateTime.MinValue;
                    areaDestination = null;
                    MissWindow(gatherQueue[gatherIndex]);
                    break;
                }

                Fail($"gathering failed ({gatheringLoop.StatusText})");
                break;

            case Gathering.GatheringLoopState.Idle:
                Transition(ProductionState.Paused, "Paused: the gathering loop was stopped.");
                break;
        }
    }

    /// <summary>
    /// Gets the character onto the given job: closes the crafting log first
    /// (class changes are blocked while it is open), then equips the best
    /// gearset. Returns true when already on the job; false while working or
    /// after failing.
    /// </summary>
    private bool EnsureJob(uint jobId, string jobLabel = "job")
    {
        if (gameBridge.CurrentClassJobId == jobId)
            return true;

        // The log being *open* is what blocks class changes; the game struct's
        // selected-recipe id can outlive the window, so test the addon itself.
        if (gameBridge.IsPreparingToCraft || gameBridge.IsAddonVisible("RecipeNote"))
        {
            retry.Try(gameBridge.CloseRecipeNote);
            return false;
        }

        retry.Try(() =>
        {
            gearsetRequested = true;
            if (!gameBridge.EquipGearsetForJob(jobId))
                Fail($"no gearset found for {jobLabel} {jobId}");
        });
        return false;
    }

    /// <summary>First craft step whose master book is locked, with the book id; null when every step is craftable.</summary>
    private (uint ItemId, uint BookId)? FindStepWithLockedBook(ProductionPlan productionPlan)
    {
        var caps = capabilities.Current;
        foreach (var step in productionPlan.CraftSteps)
        {
            var info = recipeProvider.GetRecipeById(step.RecipeId);
            if (info != null && !caps.IsRecipeUsable(info))
                return (step.ItemId, info.SecretRecipeBookId);
        }

        return null;
    }

    /// <summary>Name of the first craft or gather job in the plan with no gearset; null when all are covered.</summary>
    private string? FindJobWithoutGearset(ProductionPlan productionPlan)
    {
        var jobs = new List<uint>();
        foreach (var step in productionPlan.CraftSteps)
        {
            var info = recipeProvider.GetRecipeById(step.RecipeId);
            if (info != null)
                jobs.Add(info.ClassJobId);
        }

        foreach (var task in gatherQueue)
            jobs.Add(task.JobId);

        foreach (var jobId in jobs.Distinct())
        {
            if (jobId != 0 && !gameBridge.HasGearsetForJob(jobId))
                return recipeProvider.GetJobAbbreviation(jobId);
        }

        return null;
    }

    /// <summary>Object-table scans are costly; cache "is a node visible?" for a second.</summary>
    private bool NodeNearby()
    {
        if (Clock.UtcNow - lastNodeProbeAt > TimeSpan.FromSeconds(1))
        {
            lastNodeProbeAt = Clock.UtcNow;
            lastNodeProbe = gameBridge.FindNearestGatheringNode() != null;
        }

        return lastNodeProbe;
    }

    /// <summary>
    /// Within twice the arrival range of the task's node area (the projected
    /// mesh point once we have one, the exported X/Z position before that).
    /// Tasks without an area position are always "near".
    /// </summary>
    private bool NearNodeArea(GatherTask task)
    {
        if (task.AreaPosition == default)
            return true;

        var player = gameBridge.GetPlayerState();
        if (player == null)
            return false;

        var target = areaDestination is { } projected
            ? new System.Numerics.Vector2(projected.X, projected.Z)
            : task.AreaPosition;
        var here = new System.Numerics.Vector2(player.Position.X, player.Position.Z);
        return System.Numerics.Vector2.Distance(here, target) <= NodeAreaArrivalRange * 2;
    }

    /// <summary>
    /// Area center handed to the gathering loop so it prefers that node group
    /// and drifts back toward it between spawns: the projected point when we
    /// traveled there, otherwise the exported position at the player's height.
    /// </summary>
    private System.Numerics.Vector3? AreaCenterFor(GatherTask task)
    {
        if (areaDestination != null)
            return areaDestination;

        if (task.AreaPosition == default)
            return null;

        var player = gameBridge.GetPlayerState();
        if (player == null)
            return null;

        var approximate = new System.Numerics.Vector3(task.AreaPosition.X, player.Position.Y, task.AreaPosition.Y);
        return navigation.IsReady
            ? navigation.FindNearestMeshPoint(approximate, 40f, 500f) ?? approximate
            : approximate;
    }

    private string GatherText(string verb)
    {
        var task = gatherQueue[gatherIndex];
        return $"{verb} {gatherDone + 1}/{gatherQueue.Count}: {recipeProvider.GetItemName(task.ItemId)} ×{task.Remaining}" +
               (currentVisit is { } visit ? $" ({visit.EtLabel} window)" : "") + ".";
    }

    private void TickPreparing()
    {
        // Gather-only plan (7.1), or every step already crafted between
        // windows (7.15): nothing to prepare, only the way home.
        if (stepIndex >= plan!.CraftSteps.Count)
        {
            if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
            {
                retry.Try(gameBridge.CloseGatheringWindow);
                return;
            }

            if (homeTeleportPending && TickHomeTeleport())
                return;

            finishAfterReturn = false;
            Transition(ProductionState.Completed, plan.CraftSteps.Count == 0 ? "Completed: materials gathered." : $"Completed all {TotalSteps} steps.");
            return;
        }

        var step = plan.CraftSteps[stepIndex];
        var recipe = recipeProvider.GetRecipeById(step.RecipeId);
        if (recipe == null)
        {
            Fail($"recipe {step.RecipeId} could not be read");
            return;
        }

        if (Clock.UtcNow - phaseStartedAt > PrepareTimeout)
        {
            Fail($"could not prepare step {stepIndex + 1} ({recipeProvider.GetItemName(step.ItemId)}) in time" +
                 (gearsetRequested ? " — is there a gearset for the job?" : ""));
            return;
        }

        if (gameBridge.IsCrafting)
            return;

        // The gather that fed this step usually ends mid-node; gearset and
        // crafting-log commands are refused until the character has left it.
        if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
        {
            retry.Try(gameBridge.CloseGatheringWindow);
            phaseStartedAt = Clock.UtcNow; // the prepare budget starts once we are free
            return;
        }

        // Crafting location (7.6): go home before the first step's preparation.
        if (homeTeleportPending)
        {
            if (TickHomeTeleport())
                return;

            phaseStartedAt = Clock.UtcNow; // the prepare budget starts once the detour is settled
        }

        if (!EnsureJob(recipe.ClassJobId))
            return;

        maintenance.PrepareFor(Game.MaintenanceActivity.Crafting);

        // A job change just happened: let it settle before touching the log (pacing).
        if (gearsetRequested && Clock.UtcNow - retry.LastAttempt < Core.Pacing.AfterJobChange)
            return;

        // Right job: get the crafting log onto this step's recipe.
        if (gameBridge.SelectedRecipeId != step.RecipeId || !gameBridge.IsAddonVisible("RecipeNote"))
        {
            retry.Try(() => gameBridge.OpenRecipe(step.RecipeId));
            return;
        }

        // The log is up on the recipe. Right after a job change it opens with
        // no ingredient assigned (observed 2026-09-15), and re-issuing the
        // open request toggles it closed — so assign with the fill button and
        // wait for Synthesize to become pressable instead.
        if (!gameBridge.IsReadyToStartCraft)
        {
            retry.Try(() =>
            {
                if (!gameBridge.AreIngredientsAssigned())
                    gameBridge.FillIngredients(configuration.PreferHqMaterials);
                else
                    Log.Information($"[Production] Crafting log open on step {stepIndex + 1} but Synthesize is not pressable yet; {gameBridge.DescribeRecipeSelection()}");
            });
            return;
        }

        // Re-verify ingredients right before committing to the step (spec §26).
        foreach (var requirement in gameBridge.GetRecipeRequirements((ushort)step.RecipeId))
        {
            if (requirement.Owned < requirement.AmountPerCraft * step.Crafts)
            {
                Replan($"short {requirement.Name} for {recipeProvider.GetItemName(step.ItemId)}");
                return;
            }
        }

        // Intermediates quick-synth per the setting (roadmap 1.4); a target
        // step follows its order's production mode (7.13). The batch falls
        // back to a normal craft by itself when the game refuses quick synth.
        var collectable = IsTargetStep(step) && step.Mode == ProductionMode.Collectable;
        var quick = IsTargetStep(step)
            ? step.Mode == ProductionMode.QuickSynth && gameBridge.IsQuickSynthAvailable
            : configuration.QuickSynthIntermediates && gameBridge.IsQuickSynthAvailable;
        var requireHq = IsTargetStep(step) && step.Mode == ProductionMode.ForceHq;

        // Collectable (7.23): the tier's collectability threshold, as quality,
        // replaces the settings' quality percentage; with no thresholds known
        // for the item the batch solves for the configured percentage.
        var targetQuality = 0;
        if (collectable)
        {
            targetQuality = recipeProvider.GetCollectableTargetQuality(step.ItemId, step.CollectableTier) ?? 0;
            if (targetQuality == 0)
                Log.Warning(
                    $"[Production] No collectability thresholds known for {recipeProvider.GetItemName(step.ItemId)}; " +
                    "solving for the configured target quality.");
        }

        // HQ intermediates (7.22): the planner marked how many of this step's
        // crafts must land HQ to seed the final craft's quality.
        if (batchCrafter.Start(step.Crafts, quick, requireHq, targetQuality, step.HqCrafts))
        {
            Log.Information(
                $"[Production] Step {stepIndex + 1}/{TotalSteps}: " +
                $"{recipeProvider.GetItemName(step.ItemId)} ×{step.TotalProduced} ({step.Crafts} crafts" +
                (quick ? ", quick synthesis" : "") + (requireHq ? ", HQ required" : "") +
                (step.HqCrafts > 0 ? $", {step.HqCrafts} HQ first" : "") +
                (collectable ? $", {step.CollectableTier} collectable" + (targetQuality > 0 ? $" ≥ {targetQuality / 10} collectability" : "") : "") + ").");
            Transition(ProductionState.RunningBatch, StepText("Crafting"));
        }
        else
        {
            // The batch refused; say why at the retry cadence (diagnosis 2026-09-15).
            retry.Try(() => Log.Warning($"[Production] The batch did not start for step {stepIndex + 1}: {batchCrafter.StatusText} (batch {batchCrafter.State})."));
        }
    }

    /// <summary>A step that produces one of the plan's (crafted) targets, as opposed to an intermediate.</summary>
    private bool IsTargetStep(PlannedCraft step) =>
        plan != null && plan.Targets.Any(t => !t.MaterialsOnly && t.ItemId == step.ItemId);

    private void TickRunning()
    {
        switch (batchCrafter.State)
        {
            case BatchState.Completed:
                stepIndex++;
                if (StopAfterStep && (stepIndex < plan!.CraftSteps.Count || GatheringRemains))
                {
                    StopGentlyNow($"after step {stepIndex}/{TotalSteps}");
                    break;
                }

                // A step crafted while waiting for a window (7.15): back to the schedule.
                if (GatheringRemains)
                {
                    EnterPreparing();
                    DecideNext("step done");
                    break;
                }

                if (stepIndex >= plan!.CraftSteps.Count)
                {
                    Transition(ProductionState.Completed, $"Completed all {TotalSteps} steps.");
                }
                else
                {
                    EnterPreparing();
                    Transition(ProductionState.PreparingStep, StepText("Preparing"));
                }

                break;

            case BatchState.Paused:
                Transition(ProductionState.Paused, $"Paused: {batchCrafter.StatusText}");
                break;

            case BatchState.Failed:
                Fail($"batch failed ({batchCrafter.StatusText})");
                break;

            case BatchState.Idle:
                // The batch was stopped underneath us.
                Transition(ProductionState.Paused, "Paused: the batch was stopped.");
                break;
        }
    }

    /// <summary>Turns a plan's missing raw materials into gather tasks (spec §67/§38); the schedule orders them (7.15).</summary>
    private bool TryBuildGatherQueue(ProductionPlan productionPlan)
    {
        gatherQueue.Clear();
        gatherIndex = 0;
        gatherDone = 0;
        currentVisit = null;
        waitInfo = null;
        waitAfterReturn = false;
        homeTerritoryId = 0;
        homeUnavailable = false;
        CurrentSchedule = null;
        areaDestination = null; // a new plan never inherits a previous area point
        returnTeleport = false;
        homeTeleportPending = false;
        finishAfterReturn = false;
        if (productionPlan.RawMaterials.Count == 0)
            return true;

        if (!navigation.IsAvailable)
        {
            Transition(
                ProductionState.Idle,
                "Cannot start: raw materials are missing and vnavmesh is unavailable for gathering.");
            return false;
        }

        var tasks = BuildGatherTasks(productionPlan, out var problem);
        if (tasks == null)
        {
            Transition(ProductionState.Idle, $"Cannot start: {problem}");
            return false;
        }

        gatherQueue.AddRange(tasks);
        return true;
    }

    /// <summary>
    /// The gather tasks a plan's raw materials need, in plan order; null with
    /// a reason when a material cannot be gathered. Pure: shared by the run
    /// and the schedule preview (7.15).
    /// </summary>
    private List<GatherTask>? BuildGatherTasks(ProductionPlan productionPlan, out string? problem)
    {
        problem = null;
        var tasks = new List<GatherTask>();
        foreach (var material in productionPlan.RawMaterials)
        {
            var job = gatheringDatabase.GetGatheringJob(material.ItemId);

            // Not a node item — or one the user would rather buy than gather
            // (7.3b BuyWhenGatherable): a registered source (vendor, exchange,
            // fishing, retainer — M3) may supply it; the first offer wins.
            if (job == null || configuration.BuyWhenGatherable)
            {
                var offered = false;
                foreach (var supplier in sources)
                {
                    if (job != null && supplier.Kind != MaterialSourceKind.Buy)
                        continue; // only a vendor beats a node the character can work

                    if (supplier.Offer(material.ItemId, material.Amount) is { } offer)
                    {
                        tasks.Add(new GatherTask(material.ItemId, material.Amount, 0, 0, default, [], null, NodeKind.Normal, offer, supplier));
                        offered = true;
                        break;
                    }
                }

                if (offered)
                    continue;
            }

            if (job == null)
            {

                // A cluster (or a crystal with no normal node) comes from the
                // aetherial reduction of an ephemeral collectable (7.15). The
                // reduction itself is not automated yet — see the design note
                // in the P2 report: it needs AgentPurify.ReduceItem on the
                // bridge — so the source is named and the run refused.
                if (GatheringDatabase.IsCrystalOrCluster(material.ItemId)
                    && gatheringDatabase.FindReductionSource(material.ItemId) is { } source)
                {
                    var window = source.Location.Windows.Count > 0
                        ? $"{ScheduledVisit.Hhmm(source.Location.Windows[0].StartMinute)}–{ScheduledVisit.Hhmm((source.Location.Windows[0].StartMinute + source.Location.Windows[0].DurationMinutes) % 1440)} ET"
                        : "always up";
                    problem = $"{recipeProvider.GetItemName(material.ItemId)} ×{material.Amount} comes from the aetherial reduction of " +
                              $"{source.CollectableName} (ephemeral node in {GatheringDatabase.GetTerritoryName(source.Location.TerritoryId)}, {window}); " +
                              "reduction is not automated yet (roadmap 7.15) — reduce by hand, then run again.";
                    return null;
                }

                // A craftable-but-locked intermediate lands here as a raw
                // material (7.16): say which book is missing, not "not gatherable".
                var lockedRecipe = recipeProvider.FindRecipeForItem(material.ItemId);
                var reason = lockedRecipe != null && !capabilities.Current.IsRecipeUsable(lockedRecipe)
                    ? $"is only craftable from {recipeProvider.GetRecipeBookName(lockedRecipe.SecretRecipeBookId)}, which is not unlocked."
                    : "is missing and not gatherable by MIN/BTN.";
                problem = $"{recipeProvider.GetItemName(material.ItemId)} ×{material.Amount} {reason}";
                return null;
            }

            // Known node area enables cross-territory travel (spec §68);
            // without one, gathering is attempted in the current zone.
            var location = gatheringDatabase.FindLocation(material.ItemId);

            // A collectable gather order (7.1) carries its tier on the plan's
            // target; plain materials (and normal gather orders) have none.
            var collectableOrder = productionPlan.Targets.FirstOrDefault(t =>
                t.Kind == OrderKind.Gather && t.ItemId == material.ItemId && t.Mode == ProductionMode.Collectable);

            // An ephemeral node only yields as collectables (7.15).
            var tier = collectableOrder?.CollectableTier
                       ?? (location?.Kind == NodeKind.Ephemeral ? CollectableTier.High : (CollectableTier?)null);

            tasks.Add(new GatherTask(
                material.ItemId,
                material.Amount,
                location?.JobId ?? job.Value,
                location?.TerritoryId ?? 0,
                location?.Position ?? default,
                location?.Windows ?? [],
                tier,
                location?.Kind ?? NodeKind.Normal));
        }

        return tasks;
    }

    // ------------------------------------------------------- scheduling (7.15)

    /// <summary>
    /// Picks what to do now from a fresh schedule (7.15): leave for the
    /// timed window whose departure is due, gather an untimed material or
    /// craft a ready step that fits before it, go home or wait here — or,
    /// with nothing left to gather, start the crafting phase / the way home.
    /// </summary>
    private void DecideNext(string why)
    {
        EnterPreparing();
        currentVisit = null;
        homeTeleportPending = false; // a home trip interrupted by a pause is decided afresh
        waitAfterReturn = false;
        var schedule = RebuildSchedule();
        var entry = schedule.First;

        if (!GatheringRemains)
        {
            if (gatherQueue.Count == 0 && stepIndex >= plan!.CraftSteps.Count)
            {
                Transition(ProductionState.Completed, "Completed: nothing left to do.");
                return;
            }

            // Gathering is over: the crafting phase (or the way home of a
            // gather-only plan / a plan crafted entirely between windows).
            finishAfterReturn = stepIndex >= plan!.CraftSteps.Count;
            GoToCraftingSpotThenCraft(afterGathering: gatherQueue.Count > 0);
            return;
        }

        switch (entry?.Kind)
        {
            case ScheduleEntryKind.TimedVisit:
                gatherIndex = entry.Task!.Id;
                currentVisit = entry.Visit;
                if (Clock.UtcNow < entry.Visit!.Depart)
                {
                    BeginWait(entry.Visit.Depart, entry.Visit);
                    break;
                }

                Log.Information(
                    $"[Schedule] {recipeProvider.GetItemName(entry.Task.ItemId)}: {entry.Visit.EtLabel} window " +
                    (entry.Visit.IsOpenAt(Clock.UtcNow) ? "is open" : $"opens in {Countdown(entry.Visit.Start - Clock.UtcNow)}") +
                    $"; leaving now ({why}).");
                Transition(ProductionState.PreparingGather, GatherText("Preparing to gather"));
                break;

            case ScheduleEntryKind.UntimedGather:
                gatherIndex = entry.Task!.Id;
                if (NextVisit(schedule) is { } upcoming)
                    Log.Information(
                        $"[Schedule] {recipeProvider.GetItemName(entry.Task.ItemId)} first; " +
                        $"{recipeProvider.GetItemName(upcoming.Task.ItemId)}'s {upcoming.EtLabel} window opens in {Countdown(upcoming.Start - Clock.UtcNow)} ({why}).");
                Transition(ProductionState.PreparingGather, GatherText("Preparing to gather"));
                break;

            case ScheduleEntryKind.CraftStep:
                // Between windows: craft where we are (home, when the wait
                // took us there); the main crafting phase comes after gathering.
                Log.Information(
                    $"[Schedule] Crafting step {entry.Step!.StepIndex + 1}/{TotalSteps} ({entry.Step.Name}) while waiting " +
                    (NextVisit(schedule) is { } next ? $"for {recipeProvider.GetItemName(next.Task.ItemId)}'s {next.EtLabel} window ({why})." : $"({why})."));
                Transition(ProductionState.PreparingStep, StepText("Preparing (while waiting)"));
                break;

            case ScheduleEntryKind.GoHome:
                Log.Information($"[Schedule] {entry.Text} ({why}).");
                homeTeleportPending = true;
                homeFallbackToZone = false;
                homeTeleportAttempts = 0;
                waitAfterReturn = true;
                waitUntil = entry.Until;
                var target = NextVisit(schedule);
                waitInfo = new ScheduleWaitInfo(
                    "heading home", entry.Until,
                    target != null ? recipeProvider.GetItemName(target.Task.ItemId) : "the next window",
                    target?.EtLabel ?? "",
                    target != null ? GatheringDatabase.GetTerritoryName(target.Task.TerritoryId) : "",
                    target?.Start);
                Transition(ProductionState.WaitingForWindow, $"Heading to the {LocationLabel()} to wait for {waitInfo.ItemName}'s window.");
                break;

            case ScheduleEntryKind.Idle:
                Log.Information($"[Schedule] {entry.Text} ({why}).");
                BeginWait(entry.Until, NextVisit(schedule));
                break;

            default:
                // Timed work remains but the scheduler found no window for it
                // (no windows within a day never happens with real data).
                Fail("the schedule found no window for the remaining timed material");
                break;
        }
    }

    private static ScheduledVisit? NextVisit(Schedule schedule) =>
        schedule.Entries.FirstOrDefault(e => e.Kind == ScheduleEntryKind.TimedVisit)?.Visit;

    /// <summary>A timed task missed (or ran short in) its window: count it and let the schedule pick the next one, or give up.</summary>
    private void MissWindow(GatherTask task)
    {
        var missed = task with { Reschedules = task.Reschedules + 1 };
        gatherQueue[gatherIndex] = missed;
        if (missed.Remaining <= 0)
        {
            gatherQueue[gatherIndex] = missed with { Done = true, Remaining = 0 };
            gatherDone++;
            DecideNext("amount reached");
            return;
        }

        if (missed.Reschedules > MaxReschedulesPerTask)
        {
            Fail($"{recipeProvider.GetItemName(task.ItemId)} was re-scheduled {MaxReschedulesPerTask} times and is still short by {missed.Remaining}");
            return;
        }

        DecideNext("window closed");
    }

    /// <summary>Rebuilds the schedule for what is left of the run and publishes it for the schedule page.</summary>
    private Schedule RebuildSchedule()
    {
        var tasks = new List<ScheduleTask>();
        for (var i = 0; i < gatherQueue.Count; i++)
        {
            var task = gatherQueue[i];
            if (!task.Done && task.Remaining > 0)
                tasks.Add(ToScheduleTask(i, task, task.Remaining));
        }

        var steps = new List<ScheduleCraftStep>();
        if (plan != null)
        {
            for (var i = stepIndex; i < plan.CraftSteps.Count; i++)
                steps.Add(ToScheduleStep(i, plan.CraftSteps[i]));
        }

        CurrentSchedule = NodeScheduler.Build(tasks, steps, Clock.UtcNow, ReadGpState(tasks), SchedulerOptionsNow(AtHome));
        return CurrentSchedule;
    }

    /// <summary>
    /// The schedule a plan would get if started now (7.15), for the schedule
    /// page's preview; null when a material cannot be gathered. Does not
    /// touch the run in flight.
    /// </summary>
    public Schedule? PreviewSchedule(ProductionPlan productionPlan)
    {
        var gatherTasks = BuildGatherTasks(productionPlan, out _);
        if (gatherTasks == null)
            return null;

        var tasks = gatherTasks.Select((task, i) => ToScheduleTask(i, task, task.Amount)).ToList();
        var steps = productionPlan.CraftSteps.Select(ToScheduleStep).ToList();
        return NodeScheduler.Build(tasks, steps, Clock.UtcNow, ReadGpState(tasks), SchedulerOptionsNow(atHome: false));
    }

    private ScheduleTask ToScheduleTask(int id, GatherTask task, int amount) =>
        new(id, task.ItemId, recipeProvider.GetItemName(task.ItemId), amount, task.Windows, task.Kind,
            task.TerritoryId, task.AreaPosition, task.Tier != null, task.JobId);

    private ScheduleCraftStep ToScheduleStep(PlannedCraft step, int index) => ToScheduleStep(index, step);

    private ScheduleCraftStep ToScheduleStep(int index, PlannedCraft step) =>
        new(index, recipeProvider.GetItemName(step.ItemId), step.Crafts, IsStepReady(step));

    /// <summary>A step is ready when the bag already covers every ingredient of all its crafts (the same check as before a step).</summary>
    private bool IsStepReady(PlannedCraft step)
    {
        var requirements = gameBridge.GetRecipeRequirements((ushort)step.RecipeId);
        return requirements.Count > 0 && requirements.All(r => r.Owned >= r.AmountPerCraft * step.Crafts);
    }

    /// <summary>
    /// GP the scheduler plans with: the pool as the snapshot shows it (a
    /// crafter's snapshot shows none — assume a half-full standard pool),
    /// the regen the traits give for the gather jobs in the queue (the
    /// lowest, when both jobs are involved), and the cordials in the bag.
    /// </summary>
    private GatherGpState ReadGpState(IReadOnlyList<ScheduleTask> tasks)
    {
        var player = gameBridge.GetPlayerState();
        var maxGp = (int)(player?.MaxGp ?? 0);
        var gp = (int)(player?.CurrentGp ?? 0);
        if (maxGp <= 0)
        {
            maxGp = AssumedMaxGp;
            gp = maxGp / 2;
        }

        var jobs = tasks.Select(t => t.JobId).Where(j => j != 0).Distinct().ToList();
        var traits = capabilities.Current.GpRegenTraits;
        var regen = BaseGpRegenPerTick + (jobs.Count == 0
            ? 0
            : jobs.Min(job => traits.Count(t => t.Unlocked && t.JobId == job)));

        var cordials = configuration.UseCordials
            ? GatheringActions.Cordials.Sum(c => gameBridge.GetItemCount(c.ItemId))
            : 0;
        return new GatherGpState(gp, maxGp, regen, cordials);
    }

    private SchedulerOptions SchedulerOptionsNow(bool atHome) =>
        new(
            WindowTravelLead,
            ScheduleHorizon,
            configuration.WaitAtHomeForWindows,
            configuration.WaitAtHomeMinutes,
            HasHome: configuration.CraftingLocation != CraftingLocation.Stay && !homeUnavailable,
            AtHome: atHome)
        {
            // Rotation overrides (7.14) change what a node wants.
            GpPerNode = (kind, collectable, maxGp) => GatheringRotationCost.GpPerNode(kind, collectable, maxGp, configuration),
        };

    /// <summary>
    /// Light dynamic replanning (spec §26): rebuild the plan for what is still
    /// missing. Work already produced is counted through the inventory and is
    /// never redone.
    /// </summary>
    private void Replan(string reason)
    {
        if (plan == null || ++replanCount > 3)
        {
            Fail($"replanning limit reached ({reason})");
            return;
        }

        // Every target is re-planned for what it still needs (7.13); mode
        // and materials-only carry over with the target.
        var remaining = RemainingTargets();
        if (remaining.Count == 0)
        {
            Transition(ProductionState.Completed, "Completed: every target already satisfied.");
            return;
        }

        Log.Information(
            $"[Production] Replanning ({reason}): still needed " +
            string.Join(", ", remaining.Select(t => $"{recipeProvider.GetItemName(t.ItemId)} ×{t.Quantity}")) + ".");
        var newPlan = DependencyResolver.Resolve(remaining, recipeProvider, gameBridge.GetItemCount, capabilities.Current);
        // A re-resolve drops HqCrafts (7.22); the session cache restores the answers without a solve.
        newPlan = HqIntermediatePlanner.Current?.ApplyCached(newPlan, inFlight: true) ?? newPlan;

        if (!TryBuildGatherQueue(newPlan))
        {
            Fail($"replanning found unobtainable materials ({StatusText})");
            return;
        }

        plan = newPlan;
        stepIndex = 0;
        EnterPreparing();
        if (gatherQueue.Count > 0)
            DecideNext("replanned");
        else if (newPlan.CraftSteps.Count > 0)
            Transition(ProductionState.PreparingStep, StepText("Preparing"));
        else
            Transition(ProductionState.Completed, "Completed after replanning.");
    }

    /// <summary>The run's targets with what each still needs, dropping the satisfied ones.</summary>
    private List<PlanTarget> RemainingTargets() =>
        targets
            .Select(t => t.Target with { Quantity = t.Remaining(gameBridge) })
            .Where(t => t.Quantity > 0)
            .ToList();

    private void EnterPhase(ProductionState state, string statusText)
    {
        EnterPreparing();
        Transition(state, statusText);
    }

    private void EnterPreparing()
    {
        phaseStartedAt = Clock.UtcNow;
        retry.Reset();
        gearsetRequested = false;
        travel.Stop();
        lastNodeProbeAt = DateTime.MinValue; // never carry a node probe across phases/tasks
    }

    private string StepText(string verb)
    {
        if (plan == null || stepIndex >= plan.CraftSteps.Count)
            return $"{verb} finishing (materials gathered).";

        var step = plan.CraftSteps[stepIndex];
        return $"{verb} step {stepIndex + 1}/{TotalSteps}: " +
               $"{recipeProvider.GetItemName(step.ItemId)} ×{step.TotalProduced}.";
    }

    private void Fail(string reason) => Transition(ProductionState.Failed, $"Failed: {reason}.");

    /// <summary>The gentle stop lands here between steps: idle, but with the run saved as resumable.</summary>
    private void StopGentlyNow(string where)
    {
        StopAfterStep = false;
        keepSavedOnIdle = true;
        Transition(ProductionState.Idle, $"Stopped gently {where}; Resume continues from here.");
        notifier.Notify(NotificationKind.Completed, StatusText);
    }

    /// <summary>Persist the run state on terminal transitions and tell the user about milestones (roadmap 6.5).</summary>
    protected override void OnTransitioned(ProductionState previous, ProductionState current)
    {
        if (current is ProductionState.Completed or ProductionState.Failed or ProductionState.Idle)
        {
            SaveProgress(active: current == ProductionState.Idle && keepSavedOnIdle);
            keepSavedOnIdle = false;
        }

        if (previous == current || !configuration.ChatNotifications)
            return;

        switch (current)
        {
            case ProductionState.Completed when plan != null:
                notifier.Notify(NotificationKind.Completed, BuildSummary());
                break;
            case ProductionState.Failed:
            case ProductionState.Paused:
                notifier.Notify(NotificationKind.Attention, StatusText);
                break;
        }
    }

    /// <summary>End-of-run summary (roadmap 6.5): one entry per target (7.13); collectables say the quality the solve targeted (7.23).</summary>
    private string BuildSummary()
    {
        var elapsed = Clock.UtcNow - productionStartedAt;
        var parts = targets.Select(t =>
        {
            var name = recipeProvider.GetItemName(t.Target.ItemId);
            if (t.Target.MaterialsOnly)
                return $"materials for {t.Target.Quantity}× {name}";

            if (t.Target.Mode == ProductionMode.Collectable)
            {
                var tier = t.Target.CollectableTier;
                var quality = t.Target.Kind == OrderKind.Craft
                    ? recipeProvider.GetCollectableTargetQuality(t.Target.ItemId, tier)
                    : null;
                return $"{t.Produced(gameBridge)}× {name} ({tier} collectables" +
                       (quality is { } q ? $", targeted quality {q} = collectability {q / 10}" : "") + ")";
            }

            if (t.Target.Kind == OrderKind.Gather)
                return $"gathered {t.Produced(gameBridge)}× {name}";

            var hq = t.ProducedHq(gameBridge);
            return $"{t.Produced(gameBridge)}× {name}" + (hq > 0 ? $" ({hq} HQ)" : "");
        });
        return $"Production complete: {string.Join(", ", parts)} in {(int)elapsed.TotalMinutes}m {elapsed.Seconds}s.";
    }

    /// <summary>Persists the run so a reload/crash can offer resume (roadmap 6.3); one entry per target (7.13).</summary>
    private void SaveProgress(bool active)
    {
        if (plan == null)
            return;

        var first = targets.Count > 0 ? targets[0] : null;
        configuration.SavedProduction = new Configuration.SavedProductionState
        {
            Active = active,
            Targets = targets.Select(t => new Configuration.SavedTarget
            {
                ItemId = t.Target.ItemId,
                Quantity = t.Target.Quantity,
                InitialCount = t.InitialCount,
                InitialHqCount = t.InitialHqCount,
                Mode = t.Target.Mode,
                MaterialsOnly = t.Target.MaterialsOnly,
                Kind = t.Target.Kind,
                CollectableTier = t.Target.CollectableTier,
            }).ToList(),
            // First target mirrored for the resume banner (callers not yet on Targets).
            ItemId = first?.Target.ItemId ?? 0,
            Quantity = first?.Target.Quantity ?? 0,
            InitialCount = first?.InitialCount ?? 0,
        };
        configuration.Save();
    }

    /// <summary>Resumes a persisted run by re-planning what every target still needs.</summary>
    public bool TryResumeSaved()
    {
        var saved = configuration.SavedProduction;
        if (!saved.Active || saved.Targets.Count == 0)
            return false;

        // The saved baselines stay the baselines: progress made before the
        // interruption must count toward the summary and later saves.
        var progress = saved.Targets
            .Where(t => t.ItemId != 0)
            .Select(t => new TargetProgress(
                new PlanTarget(t.ItemId, t.Quantity, t.Mode, t.MaterialsOnly, t.Kind, t.CollectableTier), t.InitialCount, t.InitialHqCount))
            .ToList();
        var remaining = progress
            .Select(t => t.Target with { Quantity = t.Remaining(gameBridge) })
            .Where(t => t.Quantity > 0)
            .ToList();
        if (remaining.Count == 0)
        {
            DiscardSaved();
            Transition(ProductionState.Completed, "Saved production was already complete.");
            return true;
        }

        var resumedPlan = DependencyResolver.Resolve(remaining, recipeProvider, gameBridge.GetItemCount, capabilities.Current);
        resumedPlan = HqIntermediatePlanner.Current?.ApplyCached(resumedPlan, inFlight: true) ?? resumedPlan; // 7.22
        if (resumedPlan.CraftSteps.Count == 0 && resumedPlan.RawMaterials.Count == 0)
        {
            DiscardSaved();
            Transition(ProductionState.Completed, "Saved production was already complete.");
            return true;
        }

        Log.Information(
            "[Production] Resuming saved production: " +
            string.Join(", ", remaining.Select(t => $"{recipeProvider.GetItemName(t.ItemId)} ×{t.Quantity}")) + " remaining.");
        return Start(resumedPlan, progress);
    }

    public void DiscardSaved()
    {
        configuration.SavedProduction = new Configuration.SavedProductionState();
        configuration.Save();
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        foreach (var line in base.Describe())
            yield return line;
        if (plan != null)
        {
            yield return $"Plan: {plan.Targets.Count} target(s); step {stepIndex + 1}/{plan.CraftSteps.Count}; replans {replanCount}; started {productionStartedAt:HH:mm:ss}Z";
            foreach (var target in plan.Targets)
                yield return $"  target: {recipeProvider.GetItemName(target.ItemId)} (item {target.ItemId}) ×{target.Quantity}; {target.Kind}; mode {target.Mode}{(target.Mode == ProductionMode.Collectable ? $" ({target.CollectableTier})" : "")}{(target.MaterialsOnly ? "; materials only" : "")}";
            for (var i = 0; i < plan.CraftSteps.Count; i++)
            {
                var step = plan.CraftSteps[i];
                yield return $"  step {i + 1}{(i == stepIndex ? " (current)" : "")}: recipe {step.RecipeId} -> {recipeProvider.GetItemName(step.ItemId)} (item {step.ItemId}) ×{step.Crafts} crafts, yield {step.ResultAmount}, mode {step.Mode}";
            }

            foreach (var raw in plan.RawMaterials)
                yield return $"  raw: {recipeProvider.GetItemName(raw.ItemId)} (item {raw.ItemId}) ×{raw.Amount}";
        }

        yield return $"Sources registered: {(sources.Count == 0 ? "none" : string.Join(", ", sources.Select(s => $"{s.Name} ({s.Kind})")))}";
        if (sourceRun != null)
        {
            foreach (var line in sourceRun.Describe())
                yield return "  " + line;
        }

        if (gatherQueue.Count > 0)
        {
            yield return $"Gather queue: {gatherDone}/{gatherQueue.Count} done, current index {gatherIndex}:";
            for (var i = 0; i < gatherQueue.Count; i++)
            {
                var task = gatherQueue[i];
                yield return $"  {i}{(i == gatherIndex ? " (current)" : "")}{(task.Done ? " (done)" : "")}: {recipeProvider.GetItemName(task.ItemId)} (item {task.ItemId}) ×{task.Amount}, remaining {task.Remaining}; job {task.JobId}; {task.Kind}; territory {task.TerritoryId}; area {task.AreaPosition.X:F0},{task.AreaPosition.Y:F0}; windows {task.Windows.Count}; tier {task.Tier?.ToString() ?? "-"}; reschedules {task.Reschedules}";
            }
        }

        if (CurrentSchedule is { } schedule)
        {
            yield return $"Schedule built {schedule.BuiltAt:HH:mm:ss}Z: {schedule.Timed.Count} timed item(s), {schedule.Entries.Count} entries; wait {(waitInfo == null ? "-" : $"{waitInfo.Where} until {waitInfo.UntilUtc:HH:mm:ss}Z for {waitInfo.ItemName} {waitInfo.WindowLabel}")}; visit {(currentVisit == null ? "-" : $"{currentVisit.EtLabel} {currentVisit.Start:HH:mm:ss}–{currentVisit.End:HH:mm:ss}Z")}; atHome {AtHome} (territory {homeTerritoryId}); homeUnavailable {homeUnavailable}; waitAfterReturn {waitAfterReturn}";
            foreach (var entry in schedule.Entries.Take(8))
                yield return $"  {entry.From:HH:mm:ss}–{entry.Until:HH:mm:ss}Z {entry.Kind}: {entry.Text}";
        }

        yield return $"Phase since {phaseStartedAt:HH:mm:ss}Z; last attempt {retry.LastAttempt:HH:mm:ss}Z; gearsetRequested {gearsetRequested}; sawLoadingScreen {sawLoadingScreen}; areaDestination {areaDestination?.ToString() ?? "-"}; lastNodeProbe {lastNodeProbe} at {lastNodeProbeAt:HH:mm:ss}Z";
        yield return $"Crafting location {configuration.CraftingLocation}; homeTeleportPending {homeTeleportPending} (attempts {homeTeleportAttempts}, fallback to zone {homeFallbackToZone}); returnTeleport {returnTeleport} to territory {returnTerritoryId}";
        foreach (var line in travel.Describe())
            yield return line;
        yield return $"flight unlocked here {capabilities.Current.CanFlyIn(gameBridge.CurrentTerritoryId)}; interferenceAnchor {interferenceAnchor?.ToString() ?? "-"}";
        foreach (var t in targets)
            yield return $"Target {recipeProvider.GetItemName(t.Target.ItemId)} (item {t.Target.ItemId}) ×{t.Target.Quantity}: initial {t.InitialCount} (HQ {t.InitialHqCount}), now {gameBridge.GetItemCount(t.Target.ItemId)} (HQ {gameBridge.GetHqItemCount(t.Target.ItemId)}), remaining {t.Remaining(gameBridge)}";
        var saved = configuration.SavedProduction;
        yield return $"Saved production: active {saved.Active}; {saved.Targets.Count} target(s)";
        foreach (var t in saved.Targets)
            yield return $"  saved: {recipeProvider.GetItemName(t.ItemId)} (item {t.ItemId}) ×{t.Quantity}; initial {t.InitialCount} (HQ {t.InitialHqCount}); mode {t.Mode}{(t.MaterialsOnly ? "; materials only" : "")}";
    }
}
