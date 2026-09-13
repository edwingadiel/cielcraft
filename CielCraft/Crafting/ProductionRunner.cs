using System;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Plugin.Services;

namespace CielCraft.Crafting;

public enum ProductionState
{
    Idle,
    PreparingStep,
    RunningBatch,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// Executes a ProductionPlan's craft steps in dependency order (spec §62):
/// per step, switch to the recipe's job (via gearsets) when needed, open the
/// recipe in the crafting log, run a verified batch, then move on. Requires
/// all raw materials on hand — gathering arrives in later milestones.
/// </summary>
public sealed class ProductionRunner : IDisposable
{
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly IGameBridge gameBridge;
    private readonly BatchCrafter batchCrafter;
    private readonly DalamudRecipeProvider recipeProvider;

    private ProductionPlan? plan;
    private int stepIndex;
    private DateTime phaseStartedAt;
    private DateTime lastAttemptAt;
    private bool gearsetRequested;

    public ProductionState State { get; private set; } = ProductionState.Idle;
    public string StatusText { get; private set; } = "Idle.";
    public int CompletedSteps => stepIndex;
    public int TotalSteps => plan?.CraftSteps.Count ?? 0;

    public ProductionRunner(IGameBridge gameBridge, BatchCrafter batchCrafter, DalamudRecipeProvider recipeProvider)
    {
        this.gameBridge = gameBridge;
        this.batchCrafter = batchCrafter;
        this.recipeProvider = recipeProvider;

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

        if (productionPlan.RawMaterials.Count > 0)
        {
            Transition(
                ProductionState.Idle,
                $"Cannot start: {productionPlan.RawMaterials.Count} raw material(s) missing — gather them first.");
            return false;
        }

        if (gameBridge.IsCrafting)
        {
            Transition(ProductionState.Idle, "Cannot start while a craft is in progress.");
            return false;
        }

        plan = productionPlan;
        stepIndex = 0;
        EnterPreparing();
        Transition(ProductionState.PreparingStep, StepText("Preparing"));
        return true;
    }

    public void Pause(string reason)
    {
        if (State == ProductionState.RunningBatch)
            batchCrafter.Pause("production paused");

        if (State is ProductionState.PreparingStep or ProductionState.RunningBatch)
            Transition(ProductionState.Paused, $"Paused: {reason}.");
    }

    public void Resume()
    {
        if (State != ProductionState.Paused)
            return;

        if (batchCrafter.State == BatchState.Paused)
        {
            batchCrafter.Resume();
            Transition(ProductionState.RunningBatch, StepText("Crafting"));
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
        if (State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed))
            Transition(ProductionState.Idle, $"Stopped by user at step {stepIndex + 1}/{TotalSteps}.");
    }

    private void OnUpdate(IFramework framework)
    {
        switch (State)
        {
            case ProductionState.PreparingStep:
                TickPreparing();
                break;
            case ProductionState.RunningBatch:
                TickRunning();
                break;
        }
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
