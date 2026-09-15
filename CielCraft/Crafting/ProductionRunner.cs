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
    private bool returnTeleport;      // Teleporting phase is the post-gather return to the aetheryte
    private uint returnTerritoryId;
    private DateTime zoneArrivedAt = DateTime.MinValue;
    private DateTime gatherDoneAt = DateTime.MinValue;
    private DateTime lastNodeProbeAt = DateTime.MinValue;
    private bool lastNodeProbe;
    private int initialTargetCount;
    private int initialHqCount;
    private DateTime productionStartedAt;
    private int replanCount;
    private System.Numerics.Vector3? interferenceAnchor;

    private sealed record GatherTask(uint ItemId, int Amount, uint JobId, uint TerritoryId, System.Numerics.Vector2 AreaPosition, IReadOnlyList<EtWindow> Windows);

    public int CompletedSteps => stepIndex;

    /// <summary>"Stop gently" (roadmap 7.20): finish the current step or gather task, then stop with the run left resumable.</summary>
    public bool StopAfterStep { get; private set; }
    public int TotalSteps => plan?.CraftSteps.Count ?? 0;

    public ProductionRunner(
        IGameBridge gameBridge,
        BatchCrafter batchCrafter,
        DalamudRecipeProvider recipeProvider,
        Gathering.GatheringLoop gatheringLoop,
        GatheringDatabase gatheringDatabase,
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
        this.navigation = navigation;
    }

    public bool Start(ProductionPlan productionPlan)
    {
        if (State is ProductionState.PreparingStep or ProductionState.RunningBatch or ProductionState.Paused)
            return false;

        if (productionPlan.CraftSteps.Count == 0)
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
        initialTargetCount = gameBridge.GetItemCount(productionPlan.TargetItemId);
        initialHqCount = HqCountOfTarget();
        productionStartedAt = Clock.UtcNow;
        replanCount = 0;
        SaveProgress(active: true);
        EnterPreparing();

        if (gatherQueue.Count > 0)
            Transition(ProductionState.PreparingGather, GatherText("Preparing to gather"));
        else
            Transition(ProductionState.PreparingStep, StepText("Preparing"));
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

        if (gatheringLoop.Start(task.ItemId, task.Amount, AreaCenterFor(task)))
        {
            Log.Information(
                $"[Production] Gather task {gatherIndex + 1}/{gatherQueue.Count}: " +
                $"{recipeProvider.GetItemName(task.ItemId)} ×{task.Amount}.");
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
                Transition(ProductionState.PreparingStep, StepText("Back at the aetheryte; preparing"));
            }
            else
            {
                Transition(ProductionState.PreparingGather, GatherText("Arrived; preparing to gather"));
            }
        }
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
            EnterPhase(ProductionState.Teleporting, StepText("Returning to the aetheryte before"));
            return;
        }

        Transition(ProductionState.PreparingStep, StepText("Preparing"));
    }

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

                if (gatherIndex >= gatherQueue.Count)
                    ReturnToAetheryteThenCraft();
                else
                    Transition(ProductionState.PreparingGather, GatherText("Preparing to gather"));
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

        if (!EnsureJob(recipe.ClassJobId))
            return;

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

        // Quick synthesis for intermediates when enabled and offered (roadmap 1.4).
        var quick = configuration.QuickSynthIntermediates
                    && stepIndex < plan!.CraftSteps.Count - 1
                    && gameBridge.IsQuickSynthAvailable;

        if (batchCrafter.Start(step.Crafts, quick))
        {
            Log.Information(
                $"[Production] Step {stepIndex + 1}/{TotalSteps}: " +
                $"{recipeProvider.GetItemName(step.ItemId)} ×{step.TotalProduced} ({step.Crafts} crafts" +
                (quick ? ", quick synthesis" : "") + ").");
            Transition(ProductionState.RunningBatch, StepText("Crafting"));
        }
    }

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
            gatherQueue.Add(new GatherTask(
                material.ItemId,
                material.Amount,
                location?.JobId ?? job.Value,
                location?.TerritoryId ?? 0,
                location?.Position ?? default,
                location?.Windows ?? []));
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

        var produced = Math.Max(0, gameBridge.GetItemCount(plan.TargetItemId) - initialTargetCount);
        var remaining = plan.TargetQuantity - produced;
        if (remaining <= 0)
        {
            Transition(ProductionState.Completed, $"Completed: target already satisfied ({produced} produced).");
            return;
        }

        Log.Information($"[Production] Replanning ({reason}): {remaining} of the target still needed.");
        var newPlan = DependencyResolver.Resolve(
            plan.TargetItemId, remaining, recipeProvider, gameBridge.GetItemCount, capabilities.Current);

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

    /// <summary>End-of-run summary (roadmap 6.5).</summary>
    private string BuildSummary()
    {
        var produced = Math.Max(0, gameBridge.GetItemCount(plan!.TargetItemId) - initialTargetCount);
        var hq = Math.Max(0, HqCountOfTarget() - initialHqCount);
        var elapsed = Clock.UtcNow - productionStartedAt;
        var name = recipeProvider.GetItemName(plan.TargetItemId);
        return $"Production complete: {produced}× {name}" +
               (hq > 0 ? $" ({hq} HQ)" : "") +
               $" in {(int)elapsed.TotalMinutes}m {elapsed.Seconds}s.";
    }

    private int HqCountOfTarget() => plan == null ? 0 : gameBridge.GetHqItemCount(plan.TargetItemId);

    /// <summary>Persists the run so a reload/crash can offer resume (roadmap 6.3).</summary>
    private void SaveProgress(bool active)
    {
        if (plan == null)
            return;

        configuration.SavedProduction = new Configuration.SavedProductionState
        {
            Active = active,
            ItemId = plan.TargetItemId,
            Quantity = plan.TargetQuantity,
            InitialCount = initialTargetCount,
        };
        configuration.Save();
    }

    /// <summary>Resumes a persisted run by re-planning what is still missing.</summary>
    public bool TryResumeSaved()
    {
        var saved = configuration.SavedProduction;
        if (!saved.Active || saved.ItemId == 0)
            return false;

        var produced = Math.Max(0, gameBridge.GetItemCount(saved.ItemId) - saved.InitialCount);
        var remaining = saved.Quantity - produced;
        if (remaining <= 0)
        {
            DiscardSaved();
            Transition(ProductionState.Completed, "Saved production was already complete.");
            return true;
        }

        var resumedPlan = DependencyResolver.Resolve(
            saved.ItemId, remaining, recipeProvider, gameBridge.GetItemCount, capabilities.Current);
        Log.Information(
            $"[Production] Resuming saved production: {recipeProvider.GetItemName(saved.ItemId)} ×{remaining} remaining.");
        return Start(resumedPlan);
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
            yield return $"Plan: {recipeProvider.GetItemName(plan.TargetItemId)} (item {plan.TargetItemId}) ×{plan.TargetQuantity}; step {stepIndex + 1}/{plan.CraftSteps.Count}; replans {replanCount}; started {productionStartedAt:HH:mm:ss}Z";
            for (var i = 0; i < plan.CraftSteps.Count; i++)
            {
                var step = plan.CraftSteps[i];
                yield return $"  step {i + 1}{(i == stepIndex ? " (current)" : "")}: recipe {step.RecipeId} -> {recipeProvider.GetItemName(step.ItemId)} (item {step.ItemId}) ×{step.Crafts} crafts, yield {step.ResultAmount}";
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
                yield return $"  {i}{(i == gatherIndex ? " (current)" : "")}: {recipeProvider.GetItemName(task.ItemId)} (item {task.ItemId}) ×{task.Amount}; job {task.JobId}; territory {task.TerritoryId}; area {task.AreaPosition.X:F0},{task.AreaPosition.Y:F0}; windows {task.Windows.Count}";
            }
        }

        yield return $"Phase since {phaseStartedAt:HH:mm:ss}Z; last attempt {retry.LastAttempt:HH:mm:ss}Z; gearsetRequested {gearsetRequested}; sawLoadingScreen {sawLoadingScreen}; areaDestination {areaDestination?.ToString() ?? "-"}; lastNodeProbe {lastNodeProbe} at {lastNodeProbeAt:HH:mm:ss}Z";
        foreach (var line in travel.Describe())
            yield return line;
        yield return $"flight unlocked here {capabilities.Current.CanFlyIn(gameBridge.CurrentTerritoryId)}; interferenceAnchor {interferenceAnchor?.ToString() ?? "-"}";
        yield return $"Target count initial {initialTargetCount} (HQ {initialHqCount}), now {(plan != null ? gameBridge.GetItemCount(plan.TargetItemId) : 0)}";
        var saved = configuration.SavedProduction;
        yield return $"Saved production: active {saved.Active}; item {saved.ItemId} ×{saved.Quantity}; initial count {saved.InitialCount}";
    }
}
