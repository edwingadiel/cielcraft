using CielCraft.Core;
using CielCraft.Raphael;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// Exercises the real native solver when the library is present (built with
/// cargo for the host platform); otherwise the tests pass trivially so the
/// managed suite stays runnable everywhere.
/// </summary>
public class RaphaelSolverTests
{
    // A level-100 crafter against a mid-tier recipe (rlvl 580-ish values).
    private static readonly CraftSetup Setup = new(
        RecipeLevel: 580,
        MaxProgress: 3900,
        MaxQuality: 10920,
        MaxDurability: 70,
        IsExpert: false,
        Craftsmanship: 4021,
        Control: 3998,
        Cp: 601,
        Level: 100,
        Manipulation: true,
        HeartAndSoul: false,
        QuickInnovation: false);

    [Fact]
    public void SolvesAKnownRecipe()
    {
        if (!RaphaelSolver.IsAvailable)
            return;

        var solution = new RaphaelSolver().Solve(Setup, new CraftObjective(TargetQuality: Setup.MaxQuality));

        Assert.True(solution.Success, solution.Error);
        Assert.NotEmpty(solution.ActionIds);

        // Every emitted action id must be one Raphael is known to produce.
        foreach (var actionId in solution.ActionIds)
            Assert.True(RaphaelActionNames.ById.ContainsKey(actionId), $"unexpected action id {actionId}");
    }

    [Fact]
    public void ReportsFailureForImpossibleCraft()
    {
        if (!RaphaelSolver.IsAvailable)
            return;

        // Level 10 crafter, endgame recipe, 1 CP: no valid rotation exists.
        var impossible = Setup with { Craftsmanship = 10, Control = 10, Cp = 1, Level = 10, Manipulation = false };
        var solution = new RaphaelSolver().Solve(impossible, new CraftObjective(TargetQuality: Setup.MaxQuality));

        Assert.False(solution.Success);
    }
}
