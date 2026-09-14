using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class CraftLiveEffectsTests
{
    private static readonly CraftSetup Setup = new(
        RecipeLevel: 580, MaxProgress: 3900, MaxQuality: 10920, MaxDurability: 70, IsExpert: false,
        Craftsmanship: 4021, Control: 3998, Cp: 601, Level: 100,
        Manipulation: true, HeartAndSoul: true, QuickInnovation: true);

    private static CraftSnapshot Snapshot(int step, params CraftBuff[] buffs) =>
        new(580, step, 1000, 3900, 2000, 10920, 40, 70, 300, 601, CraftCondition.Normal) { Buffs = buffs };

    [Fact]
    public void MapsStacksAndRemainingSteps()
    {
        var live = Snapshot(
            5,
            new CraftBuff(CraftBuffIds.InnerQuiet, 6),
            new CraftBuff(CraftBuffIds.Innovation, 0, 3),
            new CraftBuff(CraftBuffIds.Manipulation, 0, 7),
            new CraftBuff(CraftBuffIds.WasteNot2, 0, 6));

        var effects = CraftLiveEffects.FromSnapshot(live, Setup, new CraftSolveContext(TrainedPerfectionAvailable: true));

        Assert.Equal(6, effects.InnerQuiet);
        Assert.Equal(3, effects.Innovation);
        Assert.Equal(7, effects.Manipulation);
        Assert.Equal(6, effects.WasteNot);
        Assert.Equal(0, effects.Veneration);
        Assert.True(effects.TrainedPerfectionAvailable);
        Assert.True(effects.HeartAndSoulAvailable);
        Assert.True(effects.QuickInnovationAvailable);
        Assert.Equal(CraftCombo.None, effects.Combo);
    }

    [Fact]
    public void PresentBuffWithUnknownDurationCountsAsOneStep()
    {
        var live = Snapshot(3, new CraftBuff(CraftBuffIds.Veneration, 0));

        var effects = CraftLiveEffects.FromSnapshot(live, Setup, CraftSolveContext.None);

        Assert.Equal(1, effects.Veneration);
    }

    [Fact]
    public void ClampsToSimulatorFieldWidths()
    {
        var live = Snapshot(
            3,
            new CraftBuff(CraftBuffIds.InnerQuiet, 14),
            new CraftBuff(CraftBuffIds.GreatStrides, 0, 9),
            new CraftBuff(CraftBuffIds.WasteNot, 0, 40));

        var effects = CraftLiveEffects.FromSnapshot(live, Setup, CraftSolveContext.None);

        Assert.Equal(10, effects.InnerQuiet);
        Assert.Equal(3, effects.GreatStrides);
        Assert.Equal(15, effects.WasteNot);
    }

    [Fact]
    public void ActiveOneShotsAreNotAvailable()
    {
        var live = Snapshot(
            4,
            new CraftBuff(CraftBuffIds.HeartAndSoul, 0),
            new CraftBuff(CraftBuffIds.TrainedPerfection, 0));

        var effects = CraftLiveEffects.FromSnapshot(live, Setup, new CraftSolveContext(TrainedPerfectionAvailable: true));

        Assert.True(effects.HeartAndSoulActive);
        Assert.False(effects.HeartAndSoulAvailable);
        Assert.True(effects.TrainedPerfectionActive);
        Assert.False(effects.TrainedPerfectionAvailable);
    }

    [Fact]
    public void SpentSpecialistFlagsFollowTheSetup()
    {
        var spent = Setup with { HeartAndSoul = false, QuickInnovation = false };

        var effects = CraftLiveEffects.FromSnapshot(Snapshot(4), spent, CraftSolveContext.None);

        Assert.False(effects.HeartAndSoulAvailable);
        Assert.False(effects.QuickInnovationAvailable);
    }

    [Fact]
    public void FirstStepKeepsTheSynthesisBeginCombo()
    {
        Assert.Equal(CraftCombo.SynthesisBegin, CraftLiveEffects.FromSnapshot(Snapshot(1), Setup, CraftSolveContext.None).Combo);
        Assert.Equal(CraftCombo.None, CraftLiveEffects.FromSnapshot(Snapshot(2), Setup, CraftSolveContext.None).Combo);
    }

    [Fact]
    public void BuffEqualityIgnoresRemainingSteps()
    {
        CraftBuff[] a = [new(CraftBuffIds.Innovation, 0, 4)];
        CraftBuff[] b = [new(CraftBuffIds.Innovation, 0, 3)];
        CraftBuff[] c = [new(CraftBuffIds.InnerQuiet, 3, 0)];
        CraftBuff[] d = [new(CraftBuffIds.InnerQuiet, 4, 0)];

        Assert.True(CraftSnapshot.BuffsEqual(a, b));
        Assert.False(CraftSnapshot.BuffsEqual(c, d));
    }
}
