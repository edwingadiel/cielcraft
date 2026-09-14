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
    public void TargetQualityBelowMaxTriggersCompletionEarly()
    {
        // Quality 6000 of 10000 max, but the target is 50% -> capped; finish.
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 2720, quality: 6000),
            [Innovation, PreparatoryTouch, Groundwork],
            BaseProgress, Level, targetQuality: 5000);

        Assert.NotNull(decision);
        Assert.Equal(BasicSynthesis, decision.ActionId);
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

    [Fact]
    public void ProgressBuffsMakeACheaperFinisherSufficient()
    {
        // 450 remaining: unbuffed Careful (450) is the cheapest sufficient
        // finisher; under Veneration Basic Synthesis reaches 300 × 1.5 = 450.
        var plain = Snapshot(progress: 2550, quality: 10000);
        var venerated = plain with { Buffs = [new CraftBuff(CraftBuffIds.Veneration, 0, 2)] };

        Assert.Equal(CarefulSynthesis, AdaptiveEngine.Decide(plain, [PreparatoryTouch], BaseProgress, Level)!.ActionId);
        Assert.Equal(BasicSynthesis, AdaptiveEngine.Decide(venerated, [PreparatoryTouch], BaseProgress, Level)!.ActionId);
    }

    [Fact]
    public void MuscleMemoryDoublesTheEstimate()
    {
        var action = CraftActionData.Finishers[0]; // Basic Synthesis, 300 at Lv100 with base 250
        Assert.Equal(300, action.ProgressGain(BaseProgress, Level, 40));
        Assert.Equal(450, action.ProgressGain(BaseProgress, Level, 40, veneration: true));
        Assert.Equal(600, action.ProgressGain(BaseProgress, Level, 40, muscleMemory: true));
        Assert.Equal(750, action.ProgressGain(BaseProgress, Level, 40, veneration: true, muscleMemory: true));
    }

    private const uint Observe = 100010;

    [Fact]
    public void ObservesOnPoorWhenTheNextQualityActionStillFitsAfterwards()
    {
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 0, quality: 500, durability: 40, cp: 300, condition: CraftCondition.Poor),
            [PreparatoryTouch, ByregotsBlessing, CarefulSynthesis],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(Observe, decision.ActionId);
        Assert.Equal(0, decision.ConsumeFromPlan);
        Assert.NotNull(decision.DeviationReason);
    }

    [Fact]
    public void FollowsPlanOnPoorWhenDurabilityCannotAbsorbAnExtraStep()
    {
        // 30 durability: Preparatory (20) + Byregot (10) leaves nothing for Careful Synthesis.
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 0, quality: 500, durability: 30, cp: 300, condition: CraftCondition.Poor),
            [PreparatoryTouch, ByregotsBlessing, CarefulSynthesis],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(PreparatoryTouch, decision.ActionId);
    }

    [Fact]
    public void FollowsPlanOnPoorWhenCpCannotCoverObserve()
    {
        // Plan needs 40 + 24 + 7 = 71 CP; Observe would make it 78.
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 0, quality: 500, durability: 40, cp: 75, condition: CraftCondition.Poor),
            [PreparatoryTouch, ByregotsBlessing, CarefulSynthesis],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(PreparatoryTouch, decision.ActionId);
    }

    [Fact]
    public void FollowsPlanOnPoorWhenTheNextActionDoesNotTouchQuality()
    {
        var decision = AdaptiveEngine.Decide(
            Snapshot(progress: 0, quality: 500, condition: CraftCondition.Poor),
            [Veneration, PreparatoryTouch],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(Veneration, decision.ActionId);
    }

    [Fact]
    public void ManipulationAndWasteNotCountTowardDurabilityWhenObserving()
    {
        // 25 durability would not cover Preparatory (20) + Byregot (10) + Careful (10),
        // but Waste Not halves the costs and Manipulation gives 5 back per step.
        var snapshot = Snapshot(progress: 0, quality: 500, durability: 25, cp: 300, condition: CraftCondition.Poor) with
        {
            Buffs = [new CraftBuff(CraftBuffIds.WasteNot2, 0, 6), new CraftBuff(CraftBuffIds.Manipulation, 0, 6)],
        };

        var decision = AdaptiveEngine.Decide(
            snapshot,
            [PreparatoryTouch, ByregotsBlessing, CarefulSynthesis],
            BaseProgress, Level);

        Assert.NotNull(decision);
        Assert.Equal(Observe, decision.ActionId);
    }
}
