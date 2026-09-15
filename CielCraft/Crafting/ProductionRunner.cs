using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
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
/// Dalamud-free (roadmap 5.1): the plugin ticks it from the framework driver.
/// </summary>
public sealed class ProductionRunner : AutomationMachine<ProductionState>
{
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AreaTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private const float NodeAreaArrivalRange = 60f;

    private readonly IGameBridge gameBridge;
    private readonly BatchCrafter batchCrafter;
    private readonly DalamudRecipeProvider recipeProvider;
    private readonly Gathering.GatheringLoop gatheringLoop;
    private readonly Game.MaintenanceService maintenance;
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

    /// <summary>One material to gather; Tier is set for a collectable gather order (7.1), null for plain gathering.</summary>
    private sealed record GatherTask(
        uint ItemId,
        int Amount,
        uint JobId,
        uint TerritoryId,
        System.Numerics.Vector2 AreaPosition,
        IReadOnlyList<EtWindow> Windows,
        CollectableTier? Tier = null);

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
        IUserNotifier notifier)
        : base(log, clock, "[Production]", ProductionState.Idle, "Idle.")
    {
        this.notifier = notifier;
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
            Transition(ProductionState.PreparingGather, GatherText("Preparing to gather"));
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

        if (State is ProductionState.MovingToArea)
        {
            travel.Stop();
            navigation.Stop();
        }

        if (State is ProductionState.PreparingStep or ProductionState.RunningBatch
            or ProductionState.PreparingGather or ProductionState.RunningGather
            or ProductionState.Teleporting or ProductionState.MovingToArea
            or ProductionState.WaitingForWindow)
            Transition(ProductionState.Paused, $"Paused: {reason}.");
    }

    public void Resume()
    {
        if (State != ProductionState.Paused)
            return;

        if (gatheringLoop.State == Gathering.GatheringLoopState.Paused)
        {
            gatheringLoop.Resume();
            Transition(ProductionState.RunningGather, GatherText("Gathering"));
        }
        else if (batchCrafter.State == BatchState.Paused)
        {
            batchCrafter.Resume();
            Transition(ProductionState.RunningBatch, StepText("Crafting"));
        }
        else if (gatherIndex < gatherQueue.Count)
        {
            EnterPreparing();
            Transition(ProductionState.PreparingGather, GatherText("Preparing to gather"));
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
        travel.Stop();
        navigation.Stop();
        if (State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed))
            Transition(ProductionState.Idle, $"Stopped by user at step {stepIndex + 1}/{TotalSteps}.");
    }

    protected override void OnTick()
    {
        // Manual movement during phases where the character should be still
        // means the user has taken over (spec §49): step aside politely.
        if (State is ProductionState.PreparingStep or ProductionState.PreparingGather or ProductionState.WaitingForWindow
            && !gameBridge.IsCrafting && !gameBridge.IsBetweenAreas && !navigation.IsMoving)
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

        if (!EnsureJob(task.JobId, "gathering job"))
            return;

        maintenance.PrepareFor(Game.MaintenanceActivity.Gathering);

        // Timed node not up yet: hold until shortly before the window opens
        // (travel starts ~2 real minutes early so we arrive as it pops).
        var untilOpen = EorzeaClock.RealTimeUntilOpen(task.Windows, EorzeaClock.MinuteOfDay(new DateTimeOffset(Clock.UtcNow)));
        if (untilOpen > TimeSpan.FromMinutes(2))
        {
            EnterPhase(ProductionState.WaitingForWindow, GatherText("Waiting for the ET window for"));
            return;
        }

        // Wrong zone: teleport there first (spec §68).
        if (task.TerritoryId != 0 && gameBridge.CurrentTerritoryId != task.TerritoryId)
        {
            retry.Try(() =>
            {
                sawLoadingScreen = false;
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
        // a different node group can sit right next to the aetheryte.
        if (task.AreaPosition != default
            && !(NearNodeArea(task) && NodeNearby()))
        {
            areaDestination = null;
            EnterPhase(ProductionState.MovingToArea, GatherText("Traveling to the node area for"));
            return;
        }

        if (gatheringLoop.Start(task.ItemId, task.Amount, AreaCenterFor(task), task.Tier))
        {
            Log.Information(
                $"[Production] Gather task {gatherIndex + 1}/{gatherQueue.Count}: " +
                $"{recipeProvider.GetItemName(task.ItemId)} ×{task.Amount}" +
                (task.Tier is { } tier ? $" ({tier} collectables)" : "") + ".");
            Transition(ProductionState.RunningGather, GatherText("Gathering"));
        }
    }

    private void TickWaitingForWindow()
    {
        var task = gatherQueue[gatherIndex];
        var untilOpen = EorzeaClock.RealTimeUntilOpen(task.Windows, EorzeaClock.MinuteOfDay(new DateTimeOffset(Clock.UtcNow)));

        if (untilOpen <= TimeSpan.FromMinutes(2))
        {
            EnterPreparing();
            Transition(ProductionState.PreparingGather, GatherText("Window opening; preparing to gather"));
            return;
        }

        StatusText = $"{GatherText("Waiting for the ET window for")} Opens in {(int)untilOpen.TotalMinutes}m {untilOpen.Seconds:D2}s (real time).";
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
                Transition(ProductionState.PreparingStep, StepText($"{returnLabel}; preparing"));
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
        if (configuration.CraftingLocation != CraftingLocation.Stay)
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
                returnLabel = configuration.CraftingLocation == CraftingLocation.InnRoom
                    ? "At the inn city aetheryte"
                    : "Home";
                EnterPhase(ProductionState.Teleporting, StepText($"Teleporting to the {LocationLabel()} before"));
                return;
            }

            // No such aetheryte, or the cast keeps being refused: craft where
            // we are (after gathering: at the zone aetheryte, as before 7.6).
            if (territory == 0 || homeTeleportAttempts >= 3)
            {
                homeTeleportPending = false;
                Log.Information(territory == 0
                    ? $"[Production] No {LocationLabel()} aetheryte to teleport to; crafting in place."
                    : $"[Production] The teleport to the {LocationLabel()} was refused {homeTeleportAttempts} times; crafting in place.");
                if (homeFallbackToZone)
                    ReturnToAetheryteThenCraft();
                else
                    EnterPhase(ProductionState.PreparingStep, StepText("Preparing"));
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
        // recorded area; nodes of another group can be visible on the way.
        if (NodeNearby() && NearNodeArea(task))
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
                gatherIndex++;
                areaDestination = null; // never reuse a previous task's area point
                EnterPreparing();
                if (StopAfterStep)
                {
                    StopGentlyNow($"after gather task {gatherIndex}/{gatherQueue.Count}");
                    break;
                }

                if (gatherIndex < gatherQueue.Count)
                    Transition(ProductionState.PreparingGather, GatherText("Preparing to gather"));
                else if (plan!.CraftSteps.Count == 0)
                    Transition(ProductionState.Completed, "Completed: materials gathered."); // gather-only plan (7.13 / 7.1)
                else
                    GoToCraftingSpotThenCraft(afterGathering: true);
                break;

            case Gathering.GatheringLoopState.Paused:
                Transition(ProductionState.Paused, $"Paused: {gatheringLoop.StatusText}");
                break;

            case Gathering.GatheringLoopState.Failed:
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
        return $"{verb} {gatherIndex + 1}/{gatherQueue.Count}: {recipeProvider.GetItemName(task.ItemId)} ×{task.Amount}.";
    }

    private void TickPreparing()
    {
        var step = plan!.CraftSteps[stepIndex];
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
        if (gameBridge.SelectedRecipeId != step.RecipeId || !gameBridge.IsReadyToStartCraft)
        {
            retry.Try(() => gameBridge.OpenRecipe(step.RecipeId));
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

        if (batchCrafter.Start(step.Crafts, quick, requireHq, targetQuality))
        {
            Log.Information(
                $"[Production] Step {stepIndex + 1}/{TotalSteps}: " +
                $"{recipeProvider.GetItemName(step.ItemId)} ×{step.TotalProduced} ({step.Crafts} crafts" +
                (quick ? ", quick synthesis" : "") + (requireHq ? ", HQ required" : "") +
                (collectable ? $", {step.CollectableTier} collectable" + (targetQuality > 0 ? $" ≥ {targetQuality / 10} collectability" : "") : "") + ").");
            Transition(ProductionState.RunningBatch, StepText("Crafting"));
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
                if (StopAfterStep && stepIndex < plan!.CraftSteps.Count)
                {
                    StopGentlyNow($"after step {stepIndex}/{TotalSteps}");
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

    /// <summary>Turns a plan's missing raw materials into gather tasks (spec §67/§38).</summary>
    private bool TryBuildGatherQueue(ProductionPlan productionPlan)
    {
        gatherQueue.Clear();
        gatherIndex = 0;
        areaDestination = null; // a new plan never inherits a previous area point
        returnTeleport = false;
        homeTeleportPending = false;
        if (productionPlan.RawMaterials.Count == 0)
            return true;

        if (!navigation.IsAvailable)
        {
            Transition(
                ProductionState.Idle,
                "Cannot start: raw materials are missing and vnavmesh is unavailable for gathering.");
            return false;
        }

        foreach (var material in productionPlan.RawMaterials)
        {
            var job = gatheringDatabase.GetGatheringJob(material.ItemId);
            if (job == null)
            {
                // A craftable-but-locked intermediate lands here as a raw
                // material (7.16): say which book is missing, not "not gatherable".
                var lockedRecipe = recipeProvider.FindRecipeForItem(material.ItemId);
                var reason = lockedRecipe != null && !capabilities.Current.IsRecipeUsable(lockedRecipe)
                    ? $"is only craftable from {recipeProvider.GetRecipeBookName(lockedRecipe.SecretRecipeBookId)}, which is not unlocked."
                    : "is missing and not gatherable by MIN/BTN.";
                Transition(
                    ProductionState.Idle,
                    $"Cannot start: {recipeProvider.GetItemName(material.ItemId)} ×{material.Amount} {reason}");
                return false;
            }

            // Known node area enables cross-territory travel (spec §68);
            // without one, gathering is attempted in the current zone.
            var location = gatheringDatabase.FindLocation(material.ItemId);

            // A collectable gather order (7.1) carries its tier on the plan's
            // target; plain materials (and normal gather orders) have none.
            var collectableOrder = productionPlan.Targets.FirstOrDefault(t =>
                t.Kind == OrderKind.Gather && t.ItemId == material.ItemId && t.Mode == ProductionMode.Collectable);

            gatherQueue.Add(new GatherTask(
                material.ItemId,
                material.Amount,
                location?.JobId ?? job.Value,
                location?.TerritoryId ?? 0,
                location?.Position ?? default,
                location?.Windows ?? [],
                collectableOrder?.CollectableTier));
        }

        // Timed materials go last, soonest window first, so untimed work
        // fills the waiting time (spec §38).
        var etNow = EorzeaClock.MinuteOfDay(new DateTimeOffset(Clock.UtcNow));
        gatherQueue.Sort((a, b) =>
            EorzeaClock.RealTimeUntilOpen(a.Windows, etNow)
                .CompareTo(EorzeaClock.RealTimeUntilOpen(b.Windows, etNow)));
        return true;
    }

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

        if (!TryBuildGatherQueue(newPlan))
        {
            Fail($"replanning found unobtainable materials ({StatusText})");
            return;
        }

        plan = newPlan;
        stepIndex = 0;
        EnterPreparing();
        if (gatherQueue.Count > 0)
            Transition(ProductionState.PreparingGather, GatherText("Preparing to gather"));
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
        var step = plan!.CraftSteps[stepIndex];
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

        if (gatherQueue.Count > 0)
        {
            yield return $"Gather queue {gatherIndex}/{gatherQueue.Count}:";
            for (var i = 0; i < gatherQueue.Count; i++)
            {
                var task = gatherQueue[i];
                yield return $"  {i}{(i == gatherIndex ? " (current)" : "")}: {recipeProvider.GetItemName(task.ItemId)} (item {task.ItemId}) ×{task.Amount}; job {task.JobId}; territory {task.TerritoryId}; area {task.AreaPosition.X:F0},{task.AreaPosition.Y:F0}; windows {task.Windows.Count}; tier {task.Tier?.ToString() ?? "-"}";
            }
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
