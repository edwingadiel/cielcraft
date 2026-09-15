using CielCraft.Core;
using CielCraft.Core.Rotations;
using CielCraft.Raphael;
using Xunit;

namespace CielCraft.Tests;

public class RotationSimulatorTests
{
    private const uint BasicSynthesis = 100001, BasicTouch = 100002, MastersMend = 100003, WasteNot = 4631,
        Veneration = 19297, StandardTouch = 100004, GreatStrides = 260, Innovation = 19004, ByregotsBlessing = 100339,
        MuscleMemory = 100379, Manipulation = 4574, AdvancedTouch = 100411, Reflect = 100387, Groundwork = 100403,
        TrainedPerfection = 100475, HeartAndSoul = 100419, PreciseTouch = 100128, HastyTouch = 100355;

    // Round base numbers so the expected values can be checked by hand.
    private static readonly CraftSetup Setup = new(
        RecipeLevel: 580, MaxProgress: 1000, MaxQuality: 5000, MaxDurability: 70, IsExpert: false,
        Craftsmanship: 4000, Control: 4000, Cp: 600, Level: 100,
        Manipulation: true, HeartAndSoul: false, QuickInnovation: false);

    private static RotationSimulation Run(params uint[] actions) => RotationSimulator.Run(Setup, 100, 100, actions);

    [Fact]
    public void ProgressBuffsAndFinishing()
    {
        // Muscle Memory 300 → 300; Veneration; Groundwork 360 × (1 + MM + Vene) = 900 → finished at 1200.
        var sim = Run(MuscleMemory, Veneration, Groundwork, BasicSynthesis);

        Assert.Null(sim.Error);
        Assert.True(sim.Finished);
        Assert.Equal(1200, sim.Progress);
        Assert.Equal(3, sim.Steps.Count); // nothing runs after the craft finishes
        Assert.Contains("1 more action", sim.Steps[2].Note);
        Assert.Equal(300, sim.Steps[0].Progress);
        Assert.Equal(60, sim.Steps[0].Durability);
        Assert.Equal(600 - 6 - 18 - 18, sim.Cp);
    }

    [Fact]
    public void QualityCombosAndInnerQuiet()
    {
        var sim = Run(Reflect, BasicTouch, StandardTouch, AdvancedTouch);

        Assert.Null(sim.Error);
        // Reflect: 100 × 300 × (0+10)×10 × 2 / 20000 = 300, IQ 2 afterwards.
        Assert.Equal(300, sim.Steps[0].Quality);
        // Basic Touch at IQ 2: 100 × 100 × 12×10 × 2 / 20000 = 120.
        Assert.Equal(420, sim.Steps[1].Quality);
        // Standard Touch (combo, 18 CP) at IQ 3: 100 × 125 × 13×10 × 2 / 20000 = 162.
        Assert.Equal(582, sim.Steps[2].Quality);
        // Advanced Touch (combo, 18 CP) at IQ 4: 100 × 150 × 14×10 × 2 / 20000 = 210.
        Assert.Equal(792, sim.Quality);
        Assert.Equal(600 - 6 - 18 - 18 - 18, sim.Cp);
        Assert.Equal(30, sim.Durability);
        Assert.False(sim.Finished);
    }

    [Fact]
    public void InnovationGreatStridesAndByregots()
    {
        var sim = Run(Reflect, Innovation, GreatStrides, ByregotsBlessing, ByregotsBlessing);

        // Byregot's at IQ 2 under Innovation + Great Strides: 100 × 140 × 12×25 × 2 / 20000 = 420.
        Assert.Equal(300 + 420, sim.Steps[3].Quality);
        // Inner Quiet is spent, so a second Byregot's is refused.
        Assert.NotNull(sim.Error);
        Assert.Contains("Inner Quiet", sim.Steps[4].Error);
        Assert.Contains("step 5", sim.Error);
    }

    [Fact]
    public void DurabilityEffects()
    {
        // Manipulation restores 5 after each step; Waste Not halves costs (rounded up); Trained Perfection makes one action free.
        var manipulation = Run(Manipulation, BasicSynthesis);
        Assert.Equal(65, manipulation.Durability);

        var wasteNot = Run(WasteNot, Groundwork);
        Assert.Equal(60, wasteNot.Durability);

        var perfection = Run(TrainedPerfection, Groundwork, Groundwork);
        Assert.Equal(70, perfection.Steps[1].Durability);
        Assert.Equal(50, perfection.Steps[2].Durability);

        var mend = Run(Groundwork, Groundwork, MastersMend);
        Assert.Equal(60, mend.Durability);

        // A long craft: 70 → 50 → 30 → 10 → the fourth Groundwork empties the bar and ends the craft.
        var exhausted = RotationSimulator.Run(Setup with { MaxProgress = 5000 }, 100, 100, [Groundwork, Groundwork, Groundwork, Groundwork, BasicTouch]);
        Assert.Equal(0, exhausted.Durability);
        Assert.Equal(4, exhausted.Steps.Count);
        Assert.Null(exhausted.Error);
    }

    [Fact]
    public void GroundworkHalvesBelowItsCost()
    {
        var sim = RotationSimulator.Run(Setup with { MaxProgress = 5000 }, 100, 100, [Groundwork, Groundwork, Groundwork, BasicSynthesis, Groundwork]);

        // 70 → 50 → 30 → 10 → Basic Synthesis leaves 0: the craft ends.
        Assert.Equal(4, sim.Steps.Count);
        Assert.Equal(360 * 3 + 120, sim.Progress);

        var halved = RotationSimulator.Run(Setup with { MaxDurability = 10 }, 100, 100, [Groundwork]);
        Assert.Equal(180, halved.Progress);
    }

    [Fact]
    public void RefusesWhatTheGameWouldRefuse()
    {
        Assert.Contains("first step", Run(BasicTouch, MuscleMemory).Error);
        Assert.Contains("level 100", RotationSimulator.Run(Setup with { Level = 90 }, 100, 100, [TrainedPerfection]).Error);
        Assert.Contains("CP", RotationSimulator.Run(Setup with { Cp = 10 }, 100, 100, [BasicTouch]).Error);
        Assert.Contains("Good", Run(PreciseTouch).Error);
        Assert.Contains("not available", Run(HeartAndSoul).Error);
        Assert.Contains("unknown", RotationSimulator.Run(Setup, 100, 100, [12345u]).Error);

        // A specialist with Heart and Soul may use a Good-only action on a Normal step.
        var specialist = RotationSimulator.Run(Setup with { HeartAndSoul = true }, 100, 100, [HeartAndSoul, PreciseTouch]);
        Assert.Null(specialist.Error);
        Assert.Equal(150, specialist.Quality);
    }

    [Fact]
    public void ChanceBasedActionsAreNotedNotRefused()
    {
        var sim = Run(HastyTouch);

        Assert.Null(sim.Error);
        Assert.Equal(100, sim.Quality);
        Assert.Contains("60%", sim.Steps[0].Note);
    }

    [Fact]
    public void ContinuesFromALiveState()
    {
        var live = new CraftSnapshot(
            580, Step: 5, Progress: 400, MaxProgress: 1000, Quality: 1000, MaxQuality: 5000,
            Durability: 40, MaxDurability: 70, CurrentCp: 300, MaxCp: 600, CraftCondition.Normal)
        {
            Buffs = [new CraftBuff(CraftBuffIds.InnerQuiet, 4), new CraftBuff(CraftBuffIds.Veneration, 0, 2)],
        };
        var effects = CraftLiveEffects.FromSnapshot(live, Setup, CraftSolveContext.None);

        var sim = RotationSimulator.Run(Setup, 100, 100, [BasicTouch, BasicSynthesis], live, effects);

        Assert.Null(sim.Error);
        // Basic Touch at IQ 4: 100 × 100 × 14×10 × 2 / 20000 = 140.
        Assert.Equal(1140, sim.Quality);
        // Basic Synthesis under Veneration (2 steps left, still up after one action): 120 × 1.5 = 180.
        Assert.Equal(580, sim.Progress);
        Assert.Equal(20, sim.Durability);
    }

    [Fact]
    public void HqChanceTableAnchors()
    {
        Assert.Equal(1, HqChance.ForQualityPercent(0));
        Assert.Equal(2, HqChance.ForQualityPercent(5));
        Assert.Equal(15, HqChance.ForQualityPercent(50));
        Assert.Equal(47, HqChance.ForQualityPercent(75));
        Assert.Equal(91, HqChance.ForQualityPercent(95));
        Assert.Equal(100, HqChance.ForQualityPercent(100));
        Assert.Equal(100, HqChance.ForQualityPercent(150));
        Assert.Equal(1, HqChance.ForQualityPercent(-3));

        Assert.Equal(15, HqChance.For(2500, 5000));
        Assert.Equal(98, HqChance.For(4999, 5000)); // floors to 99%
        Assert.Equal(100, HqChance.For(10, 0));
    }

    [Fact]
    public void BaseValuesFollowTheRecipeLevelTable()
    {
        // rlvl 15 (no level penalty): 400 × 10 / 50 + 2 = 82; 400 × 10 / 30 + 35 = 168.
        Assert.Equal((82, 168), CraftMath.BaseValues(400, 400, 20, new RecipeLevelInfo(15, 50, 30, 100, 100)));
        // rlvl 580 at level 90 (at the recipe's level: the modifiers apply): (4021 × 10 / 130 + 2) × 0.8 = 249, (3998 × 10 / 115 + 35) × 0.7 = 267.
        Assert.Equal((249, 267), CraftMath.BaseValues(4021, 3998, 90, new RecipeLevelInfo(90, 130, 115, 80, 70)));
        Assert.Equal((0, 0), CraftMath.BaseValues(400, 400, 20, new RecipeLevelInfo(1, 0, 0, 100, 100)));
    }

    [Fact]
    public void AgreesWithTheNativeSolver()
    {
        if (!RaphaelSolver.IsAvailable)
            return;

        // The same rlvl 580 craft RaphaelSolverTests uses; the native base values must match the managed formula,
        // and the replayed rotation must reach what the solver promised.
        var setup = new CraftSetup(580, 3900, 10920, 70, false, 4021, 3998, 601, 100, true, false, false);
        var solution = new RaphaelSolver().Solve(setup, new CraftObjective(TargetQuality: setup.MaxQuality));
        Assert.True(solution.Success, solution.Error);

        var (baseProgress, baseQuality) = CraftMath.BaseValues(4021, 3998, 100, new RecipeLevelInfo(90, 130, 115, 80, 70));
        Assert.Equal(solution.BaseProgress, baseProgress);
        Assert.Equal(solution.BaseQuality, baseQuality);

        var sim = RotationSimulator.Run(setup, solution.BaseProgress, solution.BaseQuality, solution.ActionIds);
        Assert.Null(sim.Error);
        Assert.True(sim.Finished);
        Assert.True(sim.Quality >= setup.MaxQuality, $"quality {sim.Quality} < {setup.MaxQuality}");
    }
}
