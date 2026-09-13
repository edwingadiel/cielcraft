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
        SolverService solverService)
    {
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;
        this.automator = automator;
        this.solverService = solverService;

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
    public bool Start(int quantity)
    {
        if (State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting or BatchState.Paused)
            return false;

        if (quantity < 1)
            return false;

        var freshCraft = gameBridge.IsCrafting && craftMonitor.Current is { Step: <= 1, Quality: 0 };
        if (!freshCraft && !gameBridge.IsReadyToStartCraft)
        {
            Transition(BatchState.Idle, "Cannot start: open the crafting log on a recipe (or be on step 1 of a craft).");
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
        synthesisFired = false;
        automatorStarted = false;
        wasCrafting = gameBridge.IsCrafting;
        waitStartedAt = DateTime.UtcNow;

        Transition(
            gameBridge.IsCrafting ? BatchState.Solving : BatchState.StartingCraft,
            $"Batch of {quantity} started.");
        return true;
    }

    public void Pause(string reason)
    {
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
        if (gameBridge.IsCrafting)
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
        }
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

            var setup = new CraftSetup(
                RecipeLevel: craft.RecipeLevel,
                MaxProgress: (ushort)craft.MaxProgress,
                MaxQuality: (ushort)craft.MaxQuality,
                MaxDurability: (ushort)craft.MaxDurability,
                IsExpert: false,
                Craftsmanship: (ushort)player.Craftsmanship,
                Control: (ushort)player.Control,
                Cp: (ushort)player.MaxCp,
                Level: (byte)player.Level,
                Manipulation: player.Level >= 65,
                HeartAndSoul: false,
                QuickInnovation: false);

            if (!solverService.BeginSolve(setup, new CraftObjective(TargetQuality: (ushort)craft.MaxQuality)))
                return;

            solveRequested = true;
            StatusText = "Solving rotation...";
            return;
        }

        switch (solverService.Status)
        {
            case SolverStatus.Done when solverService.Solution is { Success: true } solved:
                solution = solved;
                Transition(BatchState.Crafting, ProgressText());
                break;
            case SolverStatus.Failed:
                Fail($"solver failed ({solverService.Solution?.Error})");
                break;
        }
    }

    private void TickStartingCraft(bool isCrafting)
    {
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
                if (player != null && automator.Start(solution.ActionIds, player.ClassJobId))
                {
                    automatorStarted = true;
                    StatusText = ProgressText();
                }

                return;
            }

            if (automatorStarted && automator.State is AutomationState.Paused or AutomationState.Failed)
                Pause($"craft automation stopped ({automator.StatusText})");

            return;
        }

        if (!craftJustEnded)
            return;

        // A craft ended: verify against inventory before counting it (spec §18).
        automatorStarted = false;

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
