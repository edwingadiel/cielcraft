using System;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Plugin.Services;

namespace CielCraft.Crafting;

public enum BatchState
{
    Idle,
    Solving,
    StartingCraft,
    Crafting,
    QuickStarting,
    QuickRunning,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// Batch crafting (spec §18/§58): repeat the solved rotation until the target
/// quantity is produced. Every craft is verified against the player inventory
/// — completion is never assumed. Fails safe: anything unexpected pauses or
/// fails with a reason.
/// </summary>
public sealed class BatchCrafter : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;
    private readonly CraftAutomator automator;
    private readonly SolverService solverService;
    private readonly Game.DalamudRecipeProvider recipeProvider;
    private readonly Configuration configuration;
    private readonly Game.MaintenanceService maintenance;

    private int targetQuantity;
    private ushort recipeId;
    private uint resultItemId;
    private int resultAmount;
    private int baselineItemCount;
    private CraftSolution? solution;
    private bool solveRequested;
    private bool synthesisFired;
    private bool automatorStarted;
    private bool wasCrafting;
    private DateTime waitStartedAt;

    public BatchState State { get; private set; } = BatchState.Idle;
    public string StatusText { get; private set; } = "Idle.";
    public int CompletedCrafts { get; private set; }
    public int TargetQuantity => targetQuantity;

    public BatchCrafter(
        IGameBridge gameBridge,
        CraftStateMonitor craftMonitor,
        CraftAutomator automator,
        SolverService solverService,
        Game.DalamudRecipeProvider recipeProvider,
        Configuration configuration,
        Game.MaintenanceService maintenance)
    {
        this.configuration = configuration;
        this.maintenance = maintenance;
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;
        this.automator = automator;
        this.solverService = solverService;
        this.recipeProvider = recipeProvider;

        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
    }

    /// <summary>
    /// Starts a batch. Requires either the crafting log open with a recipe
    /// selected, or an active craft still on its first step (which then counts
    /// as craft #1 of the batch).
    /// </summary>
    private bool quickMode;
    private bool quickDialogRequested;
    private DateTime quickLastProgressAt;
    private CraftSetup? solvedSetup;
    private int solveTargetQuality;
    private CraftSolution? midSolution;
    private bool midSolveTried;
    private bool midSolve;

    public bool Start(int quantity, bool quickSynth = false)
    {
        if (State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting
            or BatchState.QuickStarting or BatchState.QuickRunning or BatchState.Paused)
            return false;

        if (quantity < 1)
            return false;

        var freshCraft = gameBridge.IsCrafting && craftMonitor.Current is { Step: <= 1, Quality: 0 };
        if (!freshCraft && !gameBridge.IsReadyToStartCraft)
        {
            Transition(BatchState.Idle, "Cannot start: open the crafting log on a recipe (or be on step 1 of a craft).");
            return false;
        }

        // Inventory awareness (spec §60): refuse quantities the materials
        // cannot cover instead of failing mid-batch.
        if (!freshCraft)
        {
            var requirements = gameBridge.GetRecipeRequirements(gameBridge.SelectedRecipeId);
            var craftable = InventoryMath.CraftableCount(requirements);
            if (requirements.Count > 0 && craftable < quantity)
            {
                Transition(
                    BatchState.Idle,
                    $"Cannot start: materials cover only {craftable} of {quantity} crafts.");
                return false;
            }
        }

        if (gameBridge.GetFreeInventorySlots() < 1)
        {
            Transition(BatchState.Idle, "Cannot start: inventory is full.");
            return false;
        }

        targetQuantity = quantity;
        CompletedCrafts = 0;
        recipeId = gameBridge.IsCrafting ? (ushort)0 : gameBridge.SelectedRecipeId;
        resultItemId = 0;
        resultAmount = 0;
        baselineItemCount = 0;
        solution = null;
        solveRequested = false;
        solvedSetup = null;
        midSolution = null;
        midSolveTried = false;
        midSolve = false;
        synthesisFired = false;
        automatorStarted = false;
        wasCrafting = gameBridge.IsCrafting;
        waitStartedAt = DateTime.UtcNow;

        quickMode = quickSynth && !gameBridge.IsCrafting && gameBridge.IsQuickSynthAvailable;
        if (quickMode)
        {
            var recipe = recipeProvider.GetRecipeById(gameBridge.SelectedRecipeId);
            if (recipe == null)
            {
                Transition(BatchState.Idle, "Cannot quick synth: recipe could not be read.");
                return false;
            }

            resultItemId = recipe.ResultItemId;
            resultAmount = recipe.ResultAmount;
            baselineItemCount = gameBridge.GetItemCount(resultItemId);
            quickDialogRequested = false;
            waitStartedAt = DateTime.UtcNow;
            Transition(BatchState.QuickStarting, $"Quick synthesis batch of {quantity} started.");
            return true;
        }

        Transition(
            gameBridge.IsCrafting ? BatchState.Solving : BatchState.StartingCraft,
            $"Batch of {quantity} started.");
        return true;
    }

    public void Pause(string reason)
    {
        if (State is BatchState.QuickStarting or BatchState.QuickRunning)
        {
            gameBridge.CancelQuickSynthesis();
            Transition(BatchState.Paused, $"Paused: {reason}.");
            return;
        }

        if (State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting)
        {
            if (automator.State == AutomationState.Running)
                automator.Pause("batch paused");

            Transition(BatchState.Paused, $"Paused: {reason}.");
        }
    }

    public void Resume()
    {
        if (State != BatchState.Paused)
            return;

        waitStartedAt = DateTime.UtcNow;
        if (quickMode && !gameBridge.IsCrafting)
        {
            quickDialogRequested = false;
            Transition(BatchState.QuickStarting, ProgressText());
        }
        else if (gameBridge.IsCrafting)
        {
            if (automator.State == AutomationState.Paused)
                automator.Resume();

            Transition(BatchState.Crafting, ProgressText());
        }
        else
        {
            synthesisFired = false;
            Transition(BatchState.StartingCraft, ProgressText());
        }
    }

    public void Stop()
    {
        automator.Stop();
        if (State is BatchState.QuickStarting or BatchState.QuickRunning)
            gameBridge.CancelQuickSynthesis();
        if (State is not (BatchState.Idle or BatchState.Completed or BatchState.Failed))
            Transition(BatchState.Idle, $"Stopped by user after {CompletedCrafts}/{targetQuantity} crafts.");
    }

    private void OnUpdate(IFramework framework)
    {
        var isCrafting = gameBridge.IsCrafting;
        var craftJustEnded = wasCrafting && !isCrafting;
        wasCrafting = isCrafting;

        switch (State)
        {
            case BatchState.Solving:
                TickSolving(isCrafting);
                break;
            case BatchState.StartingCraft:
                TickStartingCraft(isCrafting);
                break;
            case BatchState.Crafting:
                TickCrafting(isCrafting, craftJustEnded);
                break;
            case BatchState.QuickStarting:
                TickQuickStarting();
                break;
            case BatchState.QuickRunning:
                TickQuickRunning();
                break;
        }
    }

    private void TickQuickStarting()
    {
        if (gameBridge.IsQuickSynthesisActive)
        {
            quickLastProgressAt = DateTime.UtcNow;
            Transition(BatchState.QuickRunning, $"Quick synthesizing {CompletedCrafts}/{targetQuantity}...");
            return;
        }

        if (DateTime.UtcNow - waitStartedAt > StartTimeout)
        {
            Fail("quick synthesis did not start (out of materials, or the dialog did not respond)");
            return;
        }

        if (!quickDialogRequested)
        {
            if (gameBridge.OpenQuickSynthesisDialog())
                quickDialogRequested = true;
            return;
        }

        // The dialog takes the count; batches are capped at 99 by the game and
        // re-entered here for larger targets.
        gameBridge.ConfirmQuickSynthesisDialog(targetQuantity - CompletedCrafts);
    }

    private void TickQuickRunning()
    {
        var produced = (gameBridge.GetItemCount(resultItemId) - baselineItemCount) / Math.Max(resultAmount, 1);
        if (produced > CompletedCrafts)
        {
            CompletedCrafts = produced;
            quickLastProgressAt = DateTime.UtcNow;
            StatusText = $"Quick synthesizing {CompletedCrafts}/{targetQuantity}...";
        }

        if (CompletedCrafts >= targetQuantity)
        {
            if (gameBridge.IsQuickSynthesisActive)
                gameBridge.CancelQuickSynthesis();
            else
                Transition(BatchState.Completed, $"Completed: {CompletedCrafts}/{targetQuantity} quick synths.");
            return;
        }

        if (!gameBridge.IsQuickSynthesisActive)
        {
            // A 99-batch finished (or materials/space ran out): start the next
            // round; QuickStarting fails cleanly if nothing can be crafted.
            quickDialogRequested = false;
            waitStartedAt = DateTime.UtcNow;
            Transition(BatchState.QuickStarting, $"Quick synthesis round done ({CompletedCrafts}/{targetQuantity}); continuing.");
            return;
        }

        if (DateTime.UtcNow - quickLastProgressAt > TimeSpan.FromSeconds(30))
            Pause("quick synthesis made no progress for 30s");
    }

    private void TickSolving(bool isCrafting)
    {
        if (!isCrafting)
        {
            Fail("craft ended while the solver was preparing");
            return;
        }

        CaptureCraftResult();

        if (!solveRequested)
        {
            var craft = craftMonitor.Current;
            var player = gameBridge.GetPlayerState();
            if (craft == null || player == null)
                return;

            // Specialist one-shots: usable at craft start only when the job is
            // an equipped specialist with charges (roadmap 3.5).
            var jobId = player.ClassJobId;
            var heartAndSoul = IsSpecialistActionReady(100419, jobId);
            var quickInnovation = IsSpecialistActionReady(100459, jobId);

            var recipeInfo = recipeId != 0 ? recipeProvider.GetRecipeById(recipeId) : null;

            var setup = new CraftSetup(
                RecipeLevel: craft.RecipeLevel,
                MaxProgress: (ushort)craft.MaxProgress,
                MaxQuality: (ushort)craft.MaxQuality,
                MaxDurability: (ushort)craft.MaxDurability,
                IsExpert: recipeInfo?.IsExpert ?? false,
                Craftsmanship: (ushort)player.Craftsmanship,
                Control: (ushort)player.Control,
                Cp: (ushort)player.MaxCp,
                Level: (byte)player.Level,
                Manipulation: player.Level >= 65,
                HeartAndSoul: heartAndSoul,
                QuickInnovation: quickInnovation);

            solvedSetup = setup;
            // Collectables (roadmap 4.1): never solve below the recipe's
            // required quality.
            solveTargetQuality = Math.Max(
                Math.Max((int)craft.Quality, craft.RequiredQuality),
                craft.MaxQuality * Math.Clamp(configuration.TargetQualityPercent, 1, 100) / 100);

            if (midSolve)
            {
                if (!solverService.BeginSolveFromState(setup, craft, solveTargetQuality))
                    return;
            }
            else if (!solverService.BeginSolve(setup, new CraftObjective(
                    // Live quality at solve time reflects HQ materials, so the
                    // rotation accounts for them (every craft of the batch
                    // shares the same HQ fill).
                    TargetQuality: (ushort)solveTargetQuality,
                    InitialQuality: (ushort)craft.Quality)))
            {
                return;
            }

            solveRequested = true;
            StatusText = midSolve ? "Re-solving from the current state..." : "Solving rotation...";
            return;
        }

        switch (solverService.Status)
        {
            case SolverStatus.Done when solverService.Solution is { Success: true } solved:
                if (midSolve)
                {
                    midSolution = solved;
                    midSolve = false;
                }
                else
                {
                    solution = solved;
                }

                automatorStarted = false;
                Transition(BatchState.Crafting, ProgressText());
                break;
            case SolverStatus.Failed:
                if (midSolve)
                {
                    midSolve = false;
                    Pause($"mid-craft re-solve failed ({solverService.Solution?.Error})");
                }
                else
                {
                    Fail($"solver failed ({solverService.Solution?.Error})");
                }

                break;
        }
    }

    private bool IsSpecialistActionReady(uint raphaelActionId, uint jobId)
    {
        var resolved = Game.CraftActionResolver.ResolveForJob(raphaelActionId, jobId);
        return resolved != null && gameBridge.IsCraftActionReady(resolved.Value);
    }

    private DateTime lastRecipeOpenAttempt = DateTime.MinValue;

    private void TickStartingCraft(bool isCrafting)
    {
        if (!isCrafting && !synthesisFired)
        {
            // Repair gear / refresh food between crafts (roadmap 6.1/6.2).
            if (maintenance.Tick())
            {
                StatusText = maintenance.StatusText;
                waitStartedAt = DateTime.UtcNow;
                return;
            }

            if (maintenance.BlockedReason != null)
            {
                Pause(maintenance.BlockedReason);
                return;
            }

            // Maintenance may have closed the crafting log; reopen our recipe.
            if (!gameBridge.IsReadyToStartCraft && recipeId != 0
                && DateTime.UtcNow - lastRecipeOpenAttempt > TimeSpan.FromSeconds(2))
            {
                lastRecipeOpenAttempt = DateTime.UtcNow;
                gameBridge.OpenRecipe(recipeId);
            }
        }

        if (isCrafting)
        {
            // Craft #(CompletedCrafts+1) has begun.
            synthesisFired = false;
            automatorStarted = false;
            Transition(solution == null ? BatchState.Solving : BatchState.Crafting, ProgressText());
            return;
        }

        if (!synthesisFired)
        {
            if (gameBridge.IsReadyToStartCraft)
            {
                if (recipeId != 0 && gameBridge.SelectedRecipeId != recipeId)
                {
                    Fail($"selected recipe changed ({gameBridge.SelectedRecipeId} != {recipeId})");
                    return;
                }

                recipeId = gameBridge.SelectedRecipeId;
                if (configuration.PreferHqMaterials)
                    gameBridge.FillHqIngredients();

                if (gameBridge.StartSynthesis())
                {
                    synthesisFired = true;
                    waitStartedAt = DateTime.UtcNow;
                    Plugin.Log.Information($"[Production] Starting synthesis of recipe {recipeId}.");
                }

                return;
            }

            if (DateTime.UtcNow - waitStartedAt > StartTimeout)
                Fail("crafting log did not become ready");

            return;
        }

        if (DateTime.UtcNow - waitStartedAt > StartTimeout)
            Fail("synthesis did not start after pressing Synthesize");
    }

    private void TickCrafting(bool isCrafting, bool craftJustEnded)
    {
        if (isCrafting)
        {
            CaptureCraftResult();

            if (!automatorStarted && solution != null && automator.State != AutomationState.Running)
            {
                var player = gameBridge.GetPlayerState();
                if (player == null)
                    return;

                // Food/potion expiry between crafts (roadmap 3.3): stats that
                // no longer match the solve invalidate the rotation.
                if (midSolution == null && solvedSetup is { } solved
                    && ((ushort)player.Craftsmanship != solved.Craftsmanship
                        || (ushort)player.Control != solved.Control
                        || (ushort)player.MaxCp != solved.Cp))
                {
                    Plugin.Log.Information("[Raphael] Crafter stats changed (food/potion?); re-solving.");
                    solution = null;
                    solveRequested = false;
                    Transition(BatchState.Solving, "Stats changed; re-solving...");
                    return;
                }

                var active = midSolution ?? solution;
                if (automator.Start(active.ActionIds, player.ClassJobId, active.BaseProgress, solveTargetQuality))
                {
                    automatorStarted = true;
                    StatusText = ProgressText();
                }

                return;
            }

            if (automatorStarted && automator.State is AutomationState.Paused or AutomationState.Failed)
            {
                // One recovery attempt per craft (roadmap 3.2): re-solve the
                // remainder from the live state instead of giving up.
                if (!midSolveTried && automator.State == AutomationState.Paused
                    && craftMonitor.Current != null && solvedSetup != null)
                {
                    midSolveTried = true;
                    midSolve = true;
                    solveRequested = false;
                    automatorStarted = false;
                    automator.Stop();
                    Plugin.Log.Information($"[Adaptive] Automation paused ({automator.StatusText}); re-solving the remainder.");
                    Transition(BatchState.Solving, "Recovering: re-solving the remaining craft...");
                    return;
                }

                Pause($"craft automation stopped ({automator.StatusText})");
            }

            return;
        }

        if (!craftJustEnded)
            return;

        // A craft ended: verify against inventory before counting it (spec §18).
        automatorStarted = false;
        midSolution = null;
        midSolveTried = false;
        midSolve = false;

        if (automator.State != AutomationState.Completed)
        {
            Pause($"craft ended without completing the rotation ({automator.StatusText})");
            return;
        }

        if (resultItemId != 0)
        {
            var expected = baselineItemCount + (CompletedCrafts + 1) * Math.Max(resultAmount, 1);
            var actual = gameBridge.GetItemCount(resultItemId);
            if (actual < expected)
            {
                Pause($"inventory verification failed (item {resultItemId}: have {actual}, expected {expected})");
                return;
            }
        }

        CompletedCrafts++;
        Plugin.Log.Information($"[Production] Craft {CompletedCrafts}/{targetQuantity} verified.");

        if (CompletedCrafts >= targetQuantity)
        {
            Transition(BatchState.Completed, $"Completed: {CompletedCrafts}/{targetQuantity} crafts.");
            return;
        }

        synthesisFired = false;
        waitStartedAt = DateTime.UtcNow;
        Transition(BatchState.StartingCraft, ProgressText());
    }

    private void CaptureCraftResult()
    {
        if (resultItemId != 0)
            return;

        var result = gameBridge.CurrentCraftResult;
        if (result == null)
            return;

        resultItemId = result.Value.ItemId;
        resultAmount = result.Value.Amount;
        baselineItemCount = gameBridge.GetItemCount(resultItemId) - CompletedCrafts * Math.Max(resultAmount, 1);
        Plugin.Log.Information(
            $"[Production] Batch target item {resultItemId} x{resultAmount} per craft; " +
            $"inventory baseline {baselineItemCount}.");
    }

    private string ProgressText() => $"Crafting {CompletedCrafts + 1}/{targetQuantity}...";

    private void Fail(string reason) => Transition(BatchState.Failed, $"Failed: {reason}.");

    private void Transition(BatchState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Plugin.Log.Information($"[Production] {statusText}");
    }
}
