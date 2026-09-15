using System;
using CielCraft.Core;
using CielCraft.Game;
using System.Collections.Generic;

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
public sealed class BatchCrafter : AutomationMachine<BatchState>
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuickDialogTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Recipes the game refused to quick-synthesize this session (never crafted by this character).</summary>
    private readonly HashSet<uint> quickSynthRefused = [];

    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;
    private readonly CraftAutomator automator;
    private readonly SolverService solverService;
    private readonly Game.DalamudRecipeProvider recipeProvider;
    private readonly AutomationSettings configuration;
    private readonly Game.MaintenanceService maintenance;
    private readonly IActionResolver actionResolver;

    private int targetQuantity;
    private ushort recipeId;
    private uint resultItemId;
    private int resultAmount;
    private int baselineItemCount;
    private bool requireHq;            // ForceHq order (roadmap 7.13): only HQ results count toward the target
    private int qualityOverride;       // collectable tier threshold (roadmap 7.23); 0 = settings
    private int hqBaseline;            // HQ count of the result item when the batch started
    private int verifiedCrafts;        // every craft that landed in the bag, HQ or not
    private CraftSolution? solution;
    private bool solveRequested;
    private bool synthesisFired;
    private bool automatorStarted;
    private bool wasCrafting;
    private DateTime waitStartedAt;
    private bool quickMode;
    private bool quickDialogRequested;
    private DateTime quickLastProgressAt;
    private CraftSetup? solvedSetup;
    private int solveTargetQuality;
    private CraftSolution? midSolution;
    private bool midSolveTried;
    private bool midSolve;
    private DateTime lastRecipeOpenAttempt = DateTime.MinValue;
    private DateTime verifyUntil = DateTime.MinValue; // pending inventory verification of a finished craft
    private DateTime lastSynthesisPress = DateTime.MinValue;
    private DateTime lastFillAttempt = DateTime.MinValue;
    private DateTime craftStartedAt = DateTime.MinValue;   // pacing: first action waits for the start animation
    private DateTime lastCraftEndedAt = DateTime.MinValue; // pacing: next Synthesize waits for the end animation
    private static readonly TimeSpan SynthesisRetryInterval = TimeSpan.FromSeconds(3);

    public int CompletedCrafts { get; private set; }
    public int TargetQuantity => targetQuantity;

    public BatchCrafter(
        IGameBridge gameBridge,
        CraftStateMonitor craftMonitor,
        CraftAutomator automator,
        SolverService solverService,
        Game.DalamudRecipeProvider recipeProvider,
        AutomationSettings configuration,
        Game.MaintenanceService maintenance,
        IActionResolver actionResolver,
        ILog log,
        IClock clock)
        : base(log, clock, "[Production]", BatchState.Idle, "Idle.")
    {
        this.configuration = configuration;
        this.maintenance = maintenance;
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;
        this.automator = automator;
        this.solverService = solverService;
        this.recipeProvider = recipeProvider;
        this.actionResolver = actionResolver;
    }

    /// <summary>
    /// Starts a batch. Requires either the crafting log open with a recipe
    /// selected, or an active craft still on its first step (which then counts
    /// as craft #1 of the batch). With <paramref name="requireHq"/> (roadmap
    /// 7.13 ForceHq) the rotation is solved for full quality with HQ materials
    /// and the batch runs until the HQ count has risen by the quantity; NQ
    /// results are logged but do not count, and quick synthesis is never used.
    /// </summary>
    public bool Start(int quantity, bool quickSynth = false, bool requireHq = false, int targetQuality = 0)
    {
        if (State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting
            or BatchState.QuickStarting or BatchState.QuickRunning or BatchState.Paused)
            return false;

        if (quantity < 1)
            return false;

        // A craft already in progress is adopted as craft #1: on step 1 it is
        // solved like a fresh craft, later it is solved from the live state
        // (the way a paused batch resumes), so a run interrupted by a plugin
        // reload can be picked up without quitting the synthesis.
        var attached = gameBridge.IsCrafting && craftMonitor.Current != null;
        if (!attached && !gameBridge.IsReadyToStartCraft)
        {
            Transition(BatchState.Idle, "Cannot start: open the crafting log on a recipe, or be in a craft.");
            return false;
        }

        // Inventory awareness (spec §60): refuse quantities the materials
        // cannot cover instead of failing mid-batch.
        if (!attached)
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
        verifiedCrafts = 0;
        recipeId = gameBridge.SelectedRecipeId;
        resultItemId = 0;
        resultAmount = 0;
        baselineItemCount = 0;
        this.requireHq = requireHq;
        qualityOverride = targetQuality;
        hqBaseline = 0;
        solution = null;
        solveRequested = false;
        solvedSetup = null;
        solveTargetQuality = 0;
        midSolution = null;
        midSolveTried = false;
        midSolve = attached && craftMonitor.Current is { Step: > 1 };
        synthesisFired = false;
        automatorStarted = false;
        wasCrafting = gameBridge.IsCrafting;
        waitStartedAt = Clock.UtcNow;
        verifyUntil = DateTime.MinValue;

        // Quick synthesis only ever yields NQ, so an HQ batch never uses it.
        quickMode = quickSynth && !requireHq && !gameBridge.IsCrafting && gameBridge.IsQuickSynthAvailable
                    && !quickSynthRefused.Contains(gameBridge.SelectedRecipeId);
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
            waitStartedAt = Clock.UtcNow;
            Transition(BatchState.QuickStarting, $"Quick synthesis batch of {quantity} started.");
            return true;
        }

        Transition(
            gameBridge.IsCrafting ? BatchState.Solving : BatchState.StartingCraft,
            requireHq ? $"HQ batch of {quantity} started." : $"Batch of {quantity} started.");
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

        waitStartedAt = Clock.UtcNow;
        if (quickMode && !gameBridge.IsCrafting)
        {
            quickDialogRequested = false;
            Transition(BatchState.QuickStarting, ProgressText());
        }
        else if (gameBridge.IsCrafting)
        {
            if (automator.State == AutomationState.Paused && automator.Resume())
            {
                // The automator holds a consistent position in its rotation.
                Transition(BatchState.Crafting, ProgressText());
                return;
            }

            // No resumable automation. Mid-craft the only sound continuation
            // is a rotation solved from the live state; a fresh craft (or a
            // pause taken during Solving) just needs the solve (re)run. An
            // automator that refused to resume (the craft moved while paused)
            // also invalidates any earlier mid-craft solution.
            if (automator.State == AutomationState.Failed)
                midSolution = null;

            var craft = craftMonitor.Current;
            if (solution == null || (craft is { Step: > 1 } && midSolution == null))
            {
                midSolve = craft is { Step: > 1 };
                solveRequested = false;
                automatorStarted = false;
                Transition(BatchState.Solving, "Re-solving after resume...");
                return;
            }

            automatorStarted = false;
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
        maintenance.Abort();
        verifyUntil = DateTime.MinValue;
        if (State is BatchState.QuickStarting or BatchState.QuickRunning)
            gameBridge.CancelQuickSynthesis();
        if (State is not (BatchState.Idle or BatchState.Completed or BatchState.Failed))
            Transition(BatchState.Idle, $"Stopped by user after {CompletedCrafts}/{targetQuantity} crafts.");
    }

    protected override void OnTick()
    {
        var isCrafting = gameBridge.IsCrafting;
        var craftJustEnded = wasCrafting && !isCrafting;
        wasCrafting = isCrafting;

        // A finished craft whose inventory update has not landed yet.
        if (verifyUntil != DateTime.MinValue)
        {
            TryVerifyCraft();
            return;
        }

        // A craft can end while the batch is paused (in-flight last action);
        // its verification must not be lost or the count drifts by one.
        if (craftJustEnded && State == BatchState.Paused && automatorStarted)
        {
            HandleCraftEnded();
            return;
        }

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
            quickLastProgressAt = Clock.UtcNow;
            Transition(BatchState.QuickRunning, $"Quick synthesizing {CompletedCrafts}/{targetQuantity}...");
            return;
        }

        if (Clock.UtcNow - waitStartedAt > StartTimeout)
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

        // The game refuses quick synthesis for a recipe this character has
        // never crafted ("not available until after successfully crafting an
        // item") and the dialog never opens. Fall back to a normal synthesis
        // for this step and remember the recipe for the session.
        if (!gameBridge.IsAddonVisible("SynthesisSimpleDialog"))
        {
            if (Clock.UtcNow - waitStartedAt > QuickDialogTimeout)
            {
                recipeId = gameBridge.SelectedRecipeId;
                quickSynthRefused.Add(recipeId);
                Log.Information(
                    $"[Production] Quick synthesis dialog did not open for recipe {recipeId} " +
                    "(recipe never crafted?); switching this batch to normal synthesis.");
                quickMode = false;
                quickDialogRequested = false;
                synthesisFired = false;
                waitStartedAt = Clock.UtcNow;
                Transition(BatchState.StartingCraft, ProgressText());
            }

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
            quickLastProgressAt = Clock.UtcNow;
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
            waitStartedAt = Clock.UtcNow;
            Transition(BatchState.QuickStarting, $"Quick synthesis round done ({CompletedCrafts}/{targetQuantity}); continuing.");
            return;
        }

        if (Clock.UtcNow - quickLastProgressAt > TimeSpan.FromSeconds(30))
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

            // The Synthesis window's numbers fill in a frame or two after the
            // craft starts; solving from zeros only yields "no solution".
            if (craft.MaxProgress <= 0 || craft.MaxQuality <= 0 || craft.MaxDurability <= 0)
                return;

            // Specialist one-shots: usable at craft start only when the job is
            // an equipped specialist with charges (roadmap 3.5).
            var jobId = player.ClassJobId;
            var heartAndSoul = IsSpecialistActionReady(CraftActionData.HeartAndSoul, jobId);
            var quickInnovation = IsSpecialistActionReady(CraftActionData.QuickInnovation, jobId);

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
            // required quality. A mid-craft recovery keeps the original target
            // — recomputing from live quality would inflate it for the rest of
            // the batch. An HQ batch (7.13) always solves for full quality.
            if (!midSolve || solveTargetQuality == 0)
            {
                var percent = requireHq ? 100 : Math.Clamp(configuration.TargetQualityPercent, 1, 100);
                // A collectable tier threshold (7.23) replaces the percentage.
                var wanted = qualityOverride > 0 ? qualityOverride : craft.MaxQuality * percent / 100;
                solveTargetQuality = Math.Max(
                    Math.Max((int)craft.Quality, craft.RequiredQuality),
                    Math.Min(wanted, craft.MaxQuality));
            }

            if (midSolve)
            {
                var context = new CraftSolveContext(
                    TrainedPerfectionAvailable: IsSpecialistActionReady(CraftActionData.TrainedPerfection, jobId));
                if (!solverService.BeginSolveFromState(setup, craft, solveTargetQuality, context))
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
        var resolved = actionResolver.ResolveForJob(raphaelActionId, jobId);
        return resolved != null && gameBridge.IsCraftActionReady(resolved.Value);
    }

    private void TickStartingCraft(bool isCrafting)
    {
        if (!isCrafting && !synthesisFired)
        {
            // Repair gear / refresh food between crafts (roadmap 6.1/6.2).
            if (maintenance.Tick())
            {
                StatusText = maintenance.StatusText;
                waitStartedAt = Clock.UtcNow;
                return;
            }

            if (maintenance.BlockedReason != null)
            {
                Pause(maintenance.BlockedReason);
                return;
            }

            // Maintenance may have closed the crafting log; reopen our recipe —
            // but only when the window is actually gone. Right after a craft the
            // log is briefly "not ready" while it reopens on its own, and
            // reopening it through the agent drops the crafting stance and
            // re-enters it, which looks nothing like a person crafting again.
            if (!gameBridge.IsAddonVisible("RecipeNote") && recipeId != 0
                && Clock.UtcNow - lastRecipeOpenAttempt > TimeSpan.FromSeconds(2))
            {
                lastRecipeOpenAttempt = Clock.UtcNow;
                gameBridge.OpenRecipe(recipeId);
            }
        }

        if (isCrafting)
        {
            // Craft #(CompletedCrafts+1) has begun.
            synthesisFired = false;
            automatorStarted = false;
            craftStartedAt = Clock.UtcNow;
            Transition(solution == null ? BatchState.Solving : BatchState.Crafting, ProgressText());
            return;
        }

        if (!synthesisFired)
        {
            // Breathe between crafts (pacing): the completion animation is
            // still playing and back-to-back presses are what got us kicked.
            if (Clock.UtcNow - lastCraftEndedAt < Core.Pacing.BetweenCrafts)
                return;

            if (gameBridge.IsReadyToStartCraft)
            {
                if (recipeId != 0 && gameBridge.SelectedRecipeId != recipeId)
                {
                    Fail($"selected recipe changed ({gameBridge.SelectedRecipeId} != {recipeId})");
                    return;
                }

                recipeId = gameBridge.SelectedRecipeId;
                if (configuration.PreferHqMaterials || requireHq)
                    gameBridge.FillIngredients(preferHq: true);

                if (gameBridge.StartSynthesis())
                {
                    synthesisFired = true;
                    waitStartedAt = Clock.UtcNow;
                    lastSynthesisPress = Clock.UtcNow;
                    Log.Information($"[Production] Starting synthesis of recipe {recipeId}.");
                }

                return;
            }

            // An HQ batch keeps crafting past its nominal count while NQ
            // results land (7.13); the materials are what ends it, so say so
            // instead of timing out on a log that can never become ready.
            if (requireHq && gameBridge.IsAddonVisible("RecipeNote") && gameBridge.SelectedRecipeId != 0)
            {
                var requirements = gameBridge.GetRecipeRequirements(gameBridge.SelectedRecipeId);
                if (requirements.Count > 0 && InventoryMath.CraftableCount(requirements) < 1)
                {
                    Fail($"materials ran out with {CompletedCrafts}/{targetQuantity} HQ made ({verifiedCrafts} crafts)");
                    return;
                }
            }

            // The log is open on our recipe but the game has not assigned the
            // materials (ingredients owned only as HQ stay at 0 until the HQ
            // column is selected): press the log's own fill button.
            if (gameBridge.IsAddonVisible("RecipeNote") && gameBridge.SelectedRecipeId != 0
                && !gameBridge.AreIngredientsAssigned()
                && Clock.UtcNow - lastFillAttempt > TimeSpan.FromSeconds(2))
            {
                lastFillAttempt = Clock.UtcNow;
                var preferHq = configuration.PreferHqMaterials || requireHq;
                var assigned = gameBridge.FillIngredients(preferHq);
                Log.Information($"[Production] Assigning materials via the crafting log's {(preferHq ? "HQ" : "NQ")} fill button: {(assigned ? "all assigned" : "still incomplete")}.");
            }

            if (Clock.UtcNow - waitStartedAt > StartTimeout)
                Fail("crafting log did not become ready");

            return;
        }

        if (Clock.UtcNow - waitStartedAt > StartTimeout)
        {
            Fail("synthesis did not start after pressing Synthesize");
            return;
        }

        // A press made right after the previous craft is swallowed by the
        // completion animation (observed: the log was ready, nothing started).
        // Press again every few seconds until the craft begins or we time out.
        if (Clock.UtcNow - lastSynthesisPress > SynthesisRetryInterval && gameBridge.IsReadyToStartCraft)
        {
            lastSynthesisPress = Clock.UtcNow;
            if (gameBridge.StartSynthesis())
                Log.Information($"[Production] Synthesis has not started yet; pressing Synthesize again.");
        }
    }

    private void TickCrafting(bool isCrafting, bool craftJustEnded)
    {
        if (isCrafting)
        {
            CaptureCraftResult();

            // A batch attached mid-craft has only the live-state solution.
            if (!automatorStarted && (solution != null || midSolution != null) && automator.State != AutomationState.Running)
            {
                // Let the synthesis start animation play before the first action (pacing).
                if (Clock.UtcNow - craftStartedAt < Core.Pacing.AfterCraftStart)
                    return;

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
                    Log.Information("[Raphael] Crafter stats changed (food/potion?); re-solving.");
                    solution = null;
                    solveRequested = false;
                    Transition(BatchState.Solving, "Stats changed; re-solving...");
                    return;
                }

                var active = midSolution ?? solution;
                if (active == null)
                    return;

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
                    Log.Information($"[Adaptive] Automation paused ({automator.StatusText}); re-solving the remainder.");
                    Transition(BatchState.Solving, "Recovering: re-solving the remaining craft...");
                    return;
                }

                Pause($"craft automation stopped ({automator.StatusText})");
            }

            return;
        }

        if (!craftJustEnded)
            return;

        HandleCraftEnded();
    }

    /// <summary>Verifies and counts a finished craft (spec §18); safe to run while paused.</summary>
    private void HandleCraftEnded()
    {
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

        // The inventory update can land a few frames after the Synthesis
        // window closes; keep checking briefly before calling it a failure.
        verifyUntil = Clock.UtcNow + TimeSpan.FromSeconds(3);
        TryVerifyCraft();
    }

    private void TryVerifyCraft()
    {
        if (resultItemId != 0)
        {
            var expected = baselineItemCount + (verifiedCrafts + 1) * Math.Max(resultAmount, 1);
            var actual = gameBridge.GetItemCount(resultItemId);
            if (actual < expected)
            {
                if (Clock.UtcNow < verifyUntil)
                    return;

                verifyUntil = DateTime.MinValue;
                Pause($"inventory verification failed (item {resultItemId}: have {actual}, expected {expected})");
                return;
            }
        }

        verifyUntil = DateTime.MinValue;
        lastCraftEndedAt = Clock.UtcNow;
        verifiedCrafts++;

        if (requireHq)
        {
            // Only the HQ gain counts (7.13 ForceHq); an NQ result is a craft
            // spent, not progress, and the batch keeps going while materials last.
            var hqGain = Math.Max(0, gameBridge.GetHqItemCount(resultItemId) - hqBaseline) / Math.Max(resultAmount, 1);
            if (hqGain > CompletedCrafts)
            {
                CompletedCrafts = hqGain;
                Log.Information($"[Production] Craft {verifiedCrafts} verified HQ ({CompletedCrafts}/{targetQuantity} HQ).");
            }
            else
            {
                Log.Information($"[Production] Craft {verifiedCrafts} landed NQ; not counted ({CompletedCrafts}/{targetQuantity} HQ).");
            }
        }
        else
        {
            CompletedCrafts++;
            Log.Information($"[Production] Craft {CompletedCrafts}/{targetQuantity} verified.");
        }

        if (CompletedCrafts >= targetQuantity)
        {
            Transition(BatchState.Completed, requireHq
                ? $"Completed: {CompletedCrafts}/{targetQuantity} HQ in {verifiedCrafts} crafts."
                : $"Completed: {CompletedCrafts}/{targetQuantity} crafts.");
            return;
        }

        synthesisFired = false;
        waitStartedAt = Clock.UtcNow;
        if (State == BatchState.Paused)
            StatusText = $"Paused ({CompletedCrafts}/{targetQuantity} crafts verified).";
        else
            Transition(BatchState.StartingCraft, ProgressText());
    }

    private void CaptureCraftResult()
    {
        if (resultItemId != 0)
            return;

        // The recipe we pressed Synthesize on is the authority. The craft
        // event handler's result item is only read when no recipe is known
        // (a batch attached to a craft already in progress): right after a
        // synthesis starts it still holds the *previous* craft's item, which
        // made a White Gold Ingot batch verify against Titanium Gold Nuggets.
        var recipe = recipeId != 0 ? recipeProvider.GetRecipeById(recipeId) : null;
        if (recipe != null)
        {
            resultItemId = recipe.ResultItemId;
            resultAmount = recipe.ResultAmount;
        }
        else
        {
            var result = gameBridge.CurrentCraftResult;
            if (result == null)
                return;

            resultItemId = result.Value.ItemId;
            resultAmount = result.Value.Amount;
        }
        baselineItemCount = gameBridge.GetItemCount(resultItemId) - verifiedCrafts * Math.Max(resultAmount, 1);
        hqBaseline = gameBridge.GetHqItemCount(resultItemId) - CompletedCrafts * Math.Max(resultAmount, 1);
        Log.Information(
            $"[Production] Batch target item {resultItemId} x{resultAmount} per craft; " +
            $"inventory baseline {baselineItemCount}" + (requireHq ? $" (HQ {hqBaseline})" : "") + ".");
    }

    private string ProgressText() => requireHq
        ? $"Crafting for HQ {CompletedCrafts}/{targetQuantity} (craft #{verifiedCrafts + 1})..."
        : $"Crafting {CompletedCrafts + 1}/{targetQuantity}...";

    private void Fail(string reason) => Transition(BatchState.Failed, $"Failed: {reason}.");

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"Crafts {CompletedCrafts}/{targetQuantity} (verified {verifiedCrafts}, requireHq {requireHq}); recipe {recipeId}; result item {resultItemId} ×{resultAmount}; baseline count {baselineItemCount} (HQ {hqBaseline}), now {(resultItemId != 0 ? gameBridge.GetItemCount(resultItemId) : 0)}";
        yield return $"solveRequested {solveRequested}; synthesisFired {synthesisFired}; automatorStarted {automatorStarted}; wasCrafting {wasCrafting}; quickMode {quickMode}; quickDialogRequested {quickDialogRequested}; midSolve {midSolve}; midSolveTried {midSolveTried}";
        yield return $"Target quality {solveTargetQuality}; wait started {waitStartedAt:HH:mm:ss}Z; quick last progress {quickLastProgressAt:HH:mm:ss}Z; last recipe open attempt {lastRecipeOpenAttempt:HH:mm:ss}Z";
        yield return $"Solved setup: {solvedSetup?.ToString() ?? "none"}";
        yield return $"Solution: {DescribeSolution(solution)}; mid-craft solution: {DescribeSolution(midSolution)}";
    }

    private static string DescribeSolution(CraftSolution? candidate) =>
        candidate == null ? "none"
        : candidate.Success ? $"{candidate.ActionIds.Count} actions (base {candidate.BaseProgress}/{candidate.BaseQuality})"
        : $"failed: {candidate.Error}";
}
