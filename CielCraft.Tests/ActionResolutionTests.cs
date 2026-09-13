using CielCraft.Core;

using Xunit;

namespace CielCraft.Tests;

public class ActionResolutionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    private static CraftSnapshot Snapshot(int step, int progress = 0, int durability = 40) =>
        new(
            RecipeLevel: 1,
            Step: step,
            Progress: progress,
            MaxProgress: 100,
            Quality: 0,
            MaxQuality: 500,
            Durability: durability,
            MaxDurability: 40,
            CurrentCp: 200,
            MaxCp: 200,
            Condition: CraftCondition.Normal);

    [Fact]
    public void PendingWhileNothingChangedAndWithinTimeout()
    {
        var outcome = ActionResolution.Evaluate(
            Snapshot(step: 1), Snapshot(step: 1),
            isCrafting: true, elapsed: TimeSpan.FromSeconds(1), Timeout);

        Assert.Equal(ActionOutcome.Pending, outcome);
    }

    [Fact]
    public void StepAdvanceResolvesTheAction()
    {
        var outcome = ActionResolution.Evaluate(
            Snapshot(step: 1), Snapshot(step: 2, progress: 25, durability: 30),
            isCrafting: true, elapsed: TimeSpan.FromSeconds(2), Timeout);

        Assert.Equal(ActionOutcome.StepAdvanced, outcome);
    }

    [Fact]
    public void CraftEndingWinsOverStepAdvance()
    {
        // The final action both advances the step and ends the craft; once the
        // craft is gone the addon closes, so the executor must treat it as ended.
        var outcome = ActionResolution.Evaluate(
            Snapshot(step: 8), current: null,
            isCrafting: false, elapsed: TimeSpan.FromSeconds(2), Timeout);

        Assert.Equal(ActionOutcome.CraftEnded, outcome);
    }

    [Fact]
    public void UnreadableStateWhileCraftingCountsAsEnded()
    {
        var outcome = ActionResolution.Evaluate(
            Snapshot(step: 1), current: null,
            isCrafting: true, elapsed: TimeSpan.FromSeconds(1), Timeout);

        Assert.Equal(ActionOutcome.CraftEnded, outcome);
    }

    [Fact]
    public void TimesOutAfterDeadlineWithNoTransition()
    {
        var outcome = ActionResolution.Evaluate(
            Snapshot(step: 1), Snapshot(step: 1),
            isCrafting: true, elapsed: TimeSpan.FromSeconds(7), Timeout);

        Assert.Equal(ActionOutcome.TimedOut, outcome);
    }
}
