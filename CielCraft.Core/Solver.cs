namespace CielCraft.Core;

/// <summary>Crafter stats and recipe parameters a solve runs against (spec §13).</summary>
public sealed record CraftSetup(
    ushort RecipeLevel,
    ushort MaxProgress,
    ushort MaxQuality,
    ushort MaxDurability,
    bool IsExpert,
    ushort Craftsmanship,
    ushort Control,
    ushort Cp,
    byte Level,
    bool Manipulation,
    bool HeartAndSoul,
    bool QuickInnovation);

public sealed record CraftObjective(
    ushort TargetQuality,
    ushort InitialQuality = 0,
    bool Adversarial = false,
    bool BackloadProgress = false,
    bool ExcludeFirstStepActions = false,
    bool ExcludePrudent = false);

/// <summary>
/// An ordered rotation. Action ids are FFXIV ids as Raphael emits them:
/// shared ids for buff-type actions, CRP-flavored ids for per-job craft
/// actions (translated to the active job at execution time).
/// </summary>
public sealed record CraftSolution(
    IReadOnlyList<uint> ActionIds,
    string? Error = null,
    int BaseProgress = 0,
    int BaseQuality = 0)
{
    public bool Success => Error == null;

    public static CraftSolution Failed(string error) => new([], error);
}

/// <summary>Solver provider boundary (spec §14): Raphael today, others later.</summary>
public interface ICraftSolver
{
    CraftSolution Solve(CraftSetup setup, CraftObjective objective);

    /// <summary>
    /// Solves the remainder of an in-progress craft (spec §17), encoded as a
    /// fresh craft: remaining progress/quality as the goals, current
    /// durability and CP as the caps. Active buffs are dropped — they only
    /// increase gains, so the plan stays valid — except Waste Not, which
    /// masks out Prudent actions; first-step-only actions are masked past
    /// step 1. Conservative, never unsound.
    /// </summary>
    CraftSolution SolveFromState(CraftSetup setup, CraftSnapshot live, int targetQuality);
}
