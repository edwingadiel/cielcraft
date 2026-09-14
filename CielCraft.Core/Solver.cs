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

/// <summary>Touch-combo state as the simulator models it.</summary>
public enum CraftCombo : byte
{
    None = 0,
    SynthesisBegin = 1,
    BasicTouch = 2,
    StandardTouch = 3,
}

/// <summary>
/// What a live snapshot cannot tell on its own: whether the once-per-craft
/// Trained Perfection is still available (read from the action's readiness).
/// </summary>
public sealed record CraftSolveContext(bool TrainedPerfectionAvailable = false)
{
    public static readonly CraftSolveContext None = new();
}

/// <summary>
/// The simulator's view of an in-progress craft, mapped from live statuses
/// (spec §17). Buff values are remaining steps (Inner Quiet: stacks) clamped
/// to the simulator's field widths. Built once per mid-craft solve.
/// </summary>
public sealed record CraftLiveEffects(
    int InnerQuiet,
    int WasteNot,
    int Innovation,
    int Veneration,
    int GreatStrides,
    int MuscleMemory,
    int Manipulation,
    bool TrainedPerfectionAvailable,
    bool HeartAndSoulAvailable,
    bool QuickInnovationAvailable,
    bool TrainedPerfectionActive,
    bool HeartAndSoulActive,
    CraftCombo Combo)
{
    public static CraftLiveEffects FromSnapshot(CraftSnapshot live, CraftSetup setup, CraftSolveContext context)
    {
        var trainedPerfectionActive = live.HasBuff(CraftBuffIds.TrainedPerfection);
        var heartAndSoulActive = live.HasBuff(CraftBuffIds.HeartAndSoul);

        return new CraftLiveEffects(
            InnerQuiet: Math.Clamp(live.FindBuff(CraftBuffIds.InnerQuiet)?.Stacks ?? 0, 0, 10),
            WasteNot: Math.Min(15, Math.Max(Remaining(live, CraftBuffIds.WasteNot), Remaining(live, CraftBuffIds.WasteNot2))),
            Innovation: Math.Min(7, Remaining(live, CraftBuffIds.Innovation)),
            Veneration: Math.Min(7, Remaining(live, CraftBuffIds.Veneration)),
            GreatStrides: Math.Min(3, Remaining(live, CraftBuffIds.GreatStrides)),
            MuscleMemory: Math.Min(7, Remaining(live, CraftBuffIds.MuscleMemory)),
            Manipulation: Math.Min(15, Remaining(live, CraftBuffIds.Manipulation)),
            TrainedPerfectionAvailable: context.TrainedPerfectionAvailable && !trainedPerfectionActive,
            // The setup's specialist flags are read from action readiness at
            // solve time, so a one-shot already spent this craft is false.
            HeartAndSoulAvailable: setup.HeartAndSoul && !heartAndSoulActive,
            QuickInnovationAvailable: setup.QuickInnovation,
            TrainedPerfectionActive: trainedPerfectionActive,
            HeartAndSoulActive: heartAndSoulActive,
            // A re-solve happens after something went wrong, so the last
            // action is not trusted: no touch combo is assumed. That only
            // forgoes a CP discount, never plans an action that cannot fire.
            Combo: live.Step <= 1 ? CraftCombo.SynthesisBegin : CraftCombo.None);
    }

    /// <summary>
    /// Remaining steps of a present buff; a present buff with an unreadable
    /// duration counts as 1 (it applies to at least the next action).
    /// </summary>
    private static int Remaining(CraftSnapshot live, uint statusId)
    {
        var buff = live.FindBuff(statusId);
        return buff == null ? 0 : Math.Max(1, buff.RemainingSteps);
    }
}

/// <summary>Solver provider boundary (spec §14): Raphael today, others later.</summary>
public interface ICraftSolver
{
    CraftSolution Solve(CraftSetup setup, CraftObjective objective);

    /// <summary>
    /// Solves the remainder of an in-progress craft from its live state
    /// (spec §17): the search starts at the current progress, quality,
    /// durability, CP and active effects (<see cref="CraftLiveEffects"/>),
    /// against the same recipe and stats as the original solve.
    /// </summary>
    CraftSolution SolveFromState(CraftSetup setup, CraftSnapshot live, int targetQuality, CraftSolveContext context);
}
