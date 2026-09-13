using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class AdaptiveEngineTests
{
    private const uint BasicSynthesis = 100001;
    private const uint CarefulSynthesis = 100203;
    private const uint Groundwork = 100403;
    private const uint IntensiveSynthesis = 100315;
    private const uint PreparatoryTouch = 100299;
    private const uint ByregotsBlessing = 100339;
    private const uint Innovation = 19004;
    private const uint GreatStrides = 260;
    private const uint Veneration = 19297;

    private const int BaseProgress = 250; // Basic 300, Intensive 1000, Careful 450, Groundwork 900 at Lv100
    private const byte Level = 100;

    private static CraftSnapshot Snapshot(
        int progress,
        int maxProgress = 3000,
        int quality = 0,
        int maxQuality = 10000,
        int durability = 40,
        uint cp = 300,
        CraftCondition condition = CraftCondition.Normal) =>
        new(1, 5, progress, maxProgress, quality, maxQuality, durability, 70, cp, 700, condition);

    [Fact]
    public void FollowsPlanWhileQualityBelowTarget()
    {
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 0, quality: 500),
            [Innovation, PreparatoryTouch, CarefulSynthesis],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(Innovation, decision.ActionId);
        Assert.Equal(1, decision.ConsumeFromPlan);
        Assert.Null(decision.DeviationReason);
    }

    [Fact]
    public void FinishesWithCheapestSufficientActionWhenQualityCapped()
    {
        // 280 progress remaining: Basic Synthesis (300) suffices.
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 2720, quality: 10000),
            [Innovation, PreparatoryTouch, GreatStrides, ByregotsBlessing, Veneration, Groundwork],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(BasicSynthesis, decision.ActionId);
        Assert.Equal(6, decision.ConsumeFromPlan);
        Assert.NotNull(decision.DeviationReason);
    }

    [Fact]
    public void UsesIntensiveSynthesisOnGoodWhenCheaperOnesFallShort()
    {
        // 600 remaining: Basic 300 and Careful 450 fall short, Intensive 1000 finishes.
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 2400, quality: 10000, durability: 15, condition: CraftCondition.Good),
            [Veneration, Groundwork, Groundwork],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(IntensiveSynthesis, decision.ActionId);
        Assert.NotNull(decision.DeviationReason);
    }

    [Fact]
    public void SkipsQualityActionsWhenNoSingleFinisherSuffices()
    {
        // 2000 remaining: no single unbuffed action covers it -> skip quality
        // actions and continue the plan's progress portion (Veneration).
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 1000, quality: 10000),
            [Innovation, PreparatoryTouch, GreatStrides, ByregotsBlessing, Veneration, Groundwork, Groundwork],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(Veneration, decision.ActionId);
        Assert.Equal(5, decision.ConsumeFromPlan);
        Assert.NotNull(decision.DeviationReason);
    }

    [Fact]
    public void KeepsSynthesizingWhenPlanIsExhausted()
    {
        // Plan empty, 2000 remaining, quality capped: strongest affordable
        // action (Groundwork at full durability) keeps the craft moving.
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 1000, quality: 10000),
            [],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(Groundwork, decision.ActionId);
        Assert.NotNull(decision.DeviationReason);
    }

    [Fact]
    public void GroundworkIsHalvedBelowItsDurabilityCost()
    {
        // 500 remaining at 10 durability: Groundwork halves to 450 and fails,
        // Careful (450) fails too, Basic (300) fails -> on Normal condition no
        // single finisher, so the quality-skip path runs instead.
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 2500, quality: 10000, durability: 10),
            [Innovation, Veneration, Groundwork],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(Veneration, decision.ActionId);
        Assert.Equal(2, decision.ConsumeFromPlan);
    }
}
