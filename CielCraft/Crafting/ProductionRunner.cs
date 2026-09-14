using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Plugin.Services;

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
/// </summary>
public sealed class ProductionRunner : IDisposable
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

    private ProductionPlan? plan;
    private int stepIndex;
    private readonly List<GatherTask> gatherQueue = [];
    private int gatherIndex;
    private DateTime phaseStartedAt;
    private DateTime lastAttemptAt;
    private bool gearsetRequested;
    private bool sawLoadingScreen;
    private System.Numerics.Vector3? areaDestination;

    private sealed record GatherTask(uint ItemId, int Amount, uint JobId, uint TerritoryId, System.Numerics.Vector2 AreaPosition, IReadOnlyList<EtWindow> Windows);

    public ProductionState State { get; private set; } = ProductionState.Idle;
    public string StatusText { get; private set; } = "Idle.";
    public int CompletedSteps => stepIndex;
    public int TotalSteps => plan?.CraftSteps.Count ?? 0;

    public ProductionRunner(
        IGameBridge gameBridge,
        BatchCrafter batchCrafter,
        DalamudRecipeProvider recipeProvider,
        Gathering.GatheringLoop gatheringLoop,
        GatheringDatabase gatheringDatabase,
        INavigationProvider navigation)
    {
        this.gameBridge = gameBridge;
        this.batchCrafter = batchCrafter;
        this.recipeProvider = recipeProvider;
        this.gatheringLoop = gatheringLoop;
        this.gatheringDatabase = gatheringDatabase;
        this.navigation = navigation;

        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
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

        // Missing raw materials become gather tasks (spec §67) when they are
        // gatherable and navigation is up; otherwise starting is refused.
        gatherQueue.Clear();
        gatherIndex = 0;
        if (productionPlan.RawMaterials.Count > 0)
        {
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
                    Transition(
                        ProductionState.Idle,
                        $"Cannot start: {recipeProvider.GetItemName(material.ItemId)} ×{material.Amount} " +
                        "is missing and not gatherable by MIN/BTN.");
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
            var etNow = EorzeaClock.MinuteOfDay(DateTimeOffset.UtcNow);
            gatherQueue.Sort((a, b) =>
                EorzeaClock.RealTimeUntilOpen(a.Windows, etNow)
                    .CompareTo(EorzeaClock.RealTimeUntilOpen(b.Windows, etNow)));
        }

        if (gameBridge.IsCrafting)
        {
            Transition(ProductionState.Idle, "Cannot start while a craft is in progress.");
            return false;
        }

        plan = productionPlan;
        stepIndex = 0;
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
            navigation.Stop();

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

    public void Stop()
    {
        batchCrafter.Stop();
        gatheringLoop.Stop();
        navigation.Stop();
        if (State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed))
            Transition(ProductionState.Idle, $"Stopped by user at step {stepIndex + 1}/{TotalSteps}.");
    }

    private void OnUpdate(IFramework framework)
    {
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

        if (DateTime.UtcNow - phaseStartedAt > PrepareTimeout)
        {
            Fail($"could not prepare gathering for {recipeProvider.GetItemName(task.ItemId)}" +
                 (gearsetRequested ? " — is there a MIN/BTN gearset?" : ""));
            return;
        }

        if (gameBridge.IsCrafting)
            return;

        if (gameBridge.CurrentClassJobId != task.JobId)
        {
            if (gameBridge.IsPreparingToCraft || gameBridge.SelectedRecipeId != 0)
            {
                Throttled(gameBridge.CloseRecipeNote);
                return;
            }

            Throttled(() =>
            {
                gearsetRequested = true;
                if (!gameBridge.EquipGearsetForJob(task.JobId))
                    Fail($"no gearset found for gathering job {task.JobId}");
            });
            return;
        }

        // Timed node not up yet: hold until shortly before the window opens
        // (travel starts ~2 real minutes early so we arrive as it pops).
        var untilOpen = EorzeaClock.RealTimeUntilOpen(task.Windows, EorzeaClock.MinuteOfDay(DateTimeOffset.UtcNow));
        if (untilOpen > TimeSpan.FromMinutes(2))
        {
            EnterPhase(ProductionState.WaitingForWindow, GatherText("Waiting for the ET window for"));
            return;
        }

        // Wrong zone: teleport there first (spec §68).
        if (task.TerritoryId != 0 && gameBridge.CurrentTerritoryId != task.TerritoryId)
        {
            Throttled(() =>
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
        // appear in the object table.
        if (task.AreaPosition != default
            && gameBridge.FindNearestGatheringNode() == null)
        {
            areaDestination = null;
            EnterPhase(ProductionState.MovingToArea, GatherText("Traveling to the node area for"));
            return;
        }

        if (gatheringLoop.Start(task.ItemId, task.Amount))
        {
            Plugin.Log.Information(
                $"[Production] Gather task {gatherIndex + 1}/{gatherQueue.Count}: " +
                $"{recipeProvider.GetItemName(task.ItemId)} ×{task.Amount}.");
            Transition(ProductionState.RunningGather, GatherText("Gathering"));
        }
    }

    private void TickWaitingForWindow()
    {
        var task = gatherQueue[gatherIndex];
        var untilOpen = EorzeaClock.RealTimeUntilOpen(task.Windows, EorzeaClock.MinuteOfDay(DateTimeOffset.UtcNow));

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
        var task = gatherQueue[gatherIndex];

        if (DateTime.UtcNow - phaseStartedAt > TeleportTimeout)
        {
            Fail("teleport did not complete (cast interrupted or loading took too long)");
            return;
        }

        if (gameBridge.IsBetweenAreas)
        {
            sawLoadingScreen = true;
            return;
        }

        if (sawLoadingScreen
            && gameBridge.CurrentTerritoryId == task.TerritoryId
            && gameBridge.GetPlayerState() != null)
        {
            EnterPreparing();
            Transition(ProductionState.PreparingGather, GatherText("Arrived; preparing to gather"));
        }
    }

    private void TickMovingToArea()
    {
        var task = gatherQueue[gatherIndex];

        if (DateTime.UtcNow - phaseStartedAt > AreaTimeout)
        {
            Fail("could not reach the node area in time");
            return;
        }

        // A targetable node in the object table means we are close enough.
        if (gameBridge.FindNearestGatheringNode() != null)
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
        }

        var distance = System.Numerics.Vector3.Distance(player.Position, areaDestination.Value);
        if (distance <= NodeAreaArrivalRange)
            return; // nodes should appear as they spawn into the object table

        if (!navigation.IsMoving)
            Throttled(() => navigation.MoveCloseTo(areaDestination.Value, 10f, fly: false));
    }

    private void TickRunningGather()
    {
        switch (gatheringLoop.State)
        {
            case Gathering.GatheringLoopState.Completed:
                gatherIndex++;
                EnterPreparing();
                if (gatherIndex >= gatherQueue.Count)
                    Transition(ProductionState.PreparingStep, StepText("Preparing"));
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

        if (DateTime.UtcNow - phaseStartedAt > PrepareTimeout)
        {
            Fail($"could not prepare step {stepIndex + 1} ({recipeProvider.GetItemName(step.ItemId)}) in time" +
                 (gearsetRequested ? " — is there a gearset for the job?" : ""));
            return;
        }

        if (gameBridge.IsCrafting)
            return;

        // Wrong job: close the crafting log (class changes are blocked while
        // it is open), then equip a gearset for the recipe's job.
        if (gameBridge.CurrentClassJobId != recipe.ClassJobId)
        {
            if (gameBridge.IsPreparingToCraft || gameBridge.SelectedRecipeId != 0)
            {
                Throttled(gameBridge.CloseRecipeNote);
                return;
            }

            Throttled(() =>
            {
                gearsetRequested = true;
                if (!gameBridge.EquipGearsetForJob(recipe.ClassJobId))
                    Fail($"no gearset found for job {recipe.ClassJobId}");
            });
            return;
        }

        // Right job: get the crafting log onto this step's recipe.
        if (gameBridge.SelectedRecipeId != step.RecipeId || !gameBridge.IsReadyToStartCraft)
        {
            Throttled(() => gameBridge.OpenRecipe(step.RecipeId));
            return;
        }

        if (batchCrafter.Start(step.Crafts))
        {
            Plugin.Log.Information(
                $"[Production] Step {stepIndex + 1}/{TotalSteps}: " +
                $"{recipeProvider.GetItemName(step.ItemId)} ×{step.TotalProduced} ({step.Crafts} crafts).");
            Transition(ProductionState.RunningBatch, StepText("Crafting"));
        }
    }

    private void TickRunning()
    {
        switch (batchCrafter.State)
        {
            case BatchState.Completed:
                stepIndex++;
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

    private void EnterPhase(ProductionState state, string statusText)
    {
        EnterPreparing();
        Transition(state, statusText);
    }

    private void EnterPreparing()
    {
        phaseStartedAt = DateTime.UtcNow;
        lastAttemptAt = DateTime.MinValue;
        gearsetRequested = false;
    }

    private void Throttled(Action action)
    {
        if (DateTime.UtcNow - lastAttemptAt < RetryInterval)
            return;

        lastAttemptAt = DateTime.UtcNow;
        action();
    }

    private string StepText(string verb)
    {
        var step = plan!.CraftSteps[stepIndex];
        return $"{verb} step {stepIndex + 1}/{TotalSteps}: " +
               $"{recipeProvider.GetItemName(step.ItemId)} ×{step.TotalProduced}.";
    }

    private void Fail(string reason) => Transition(ProductionState.Failed, $"Failed: {reason}.");

    private void Transition(ProductionState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Plugin.Log.Information($"[Production] {statusText}");
    }
}
