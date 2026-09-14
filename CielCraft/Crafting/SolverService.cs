using System;
using System.Threading.Tasks;
using CielCraft.Core;

namespace CielCraft.Crafting;

public enum SolverStatus
{
    Idle,
    Solving,
    Done,
    Failed,
}

/// <summary>
/// Runs the craft solver off the framework thread and exposes the result for
/// the UI. One solve at a time; a solve can take several seconds.
/// </summary>
public sealed class SolverService
{
    private readonly ICraftSolver solver;
    private readonly object gate = new();

    private DateTime startedAt;

    public SolverStatus Status { get; private set; } = SolverStatus.Idle;
    public CraftSolution? Solution { get; private set; }
    public string StatusText { get; private set; } = "No solve requested yet.";

    public SolverService(ICraftSolver solver)
    {
        this.solver = solver;
    }

    public bool BeginSolveFromState(CraftSetup setup, CraftSnapshot live, int targetQuality, CraftSolveContext context)
    {
        lock (gate)
        {
            if (Status == SolverStatus.Solving)
                return false;

            Status = SolverStatus.Solving;
            Solution = null;
            startedAt = DateTime.UtcNow;
            StatusText = "Re-solving from the current craft state...";
        }

        var effects = CraftLiveEffects.FromSnapshot(live, setup, context);
        Plugin.Log.Information(
            $"[Raphael] Mid-craft re-solve: step {live.Step}, progress {live.Progress}/{live.MaxProgress}, " +
            $"quality {live.Quality}/{targetQuality}, durability {live.Durability}, CP {live.CurrentCp}; " +
            $"effects {effects}.");

        Task.Run(() => Finish(() => solver.SolveFromState(setup, live, targetQuality, context)));
        return true;
    }

    private void Finish(Func<CraftSolution> run)
    {
        CraftSolution result;
        try
        {
            result = run();
        }
        catch (Exception e)
        {
            result = CraftSolution.Failed(e.Message);
            Plugin.Log.Error(e, "[Raphael] Solve threw.");
        }

        var elapsed = DateTime.UtcNow - startedAt;
        lock (gate)
        {
            Solution = result;
            Status = result.Success ? SolverStatus.Done : SolverStatus.Failed;
            StatusText = result.Success
                ? $"Solved in {elapsed.TotalSeconds:F1}s: {result.ActionIds.Count} actions."
                : $"Failed after {elapsed.TotalSeconds:F1}s: {result.Error}.";
        }

        Plugin.Log.Information($"[Raphael] {StatusText}");
    }

    public bool BeginSolve(CraftSetup setup, CraftObjective objective)
    {
        lock (gate)
        {
            if (Status == SolverStatus.Solving)
                return false;

            Status = SolverStatus.Solving;
            Solution = null;
            startedAt = DateTime.UtcNow;
            StatusText = "Solving...";
        }

        Plugin.Log.Information(
            $"[Raphael] Solve requested: rlvl {setup.RecipeLevel}, " +
            $"progress {setup.MaxProgress}, quality {setup.MaxQuality}, durability {setup.MaxDurability}, " +
            $"stats {setup.Craftsmanship}/{setup.Control}/{setup.Cp} @ Lv{setup.Level}, " +
            $"target quality {objective.TargetQuality} (initial {objective.InitialQuality}).");

        Task.Run(() => Finish(() => solver.Solve(setup, objective)));
        return true;
    }
}
