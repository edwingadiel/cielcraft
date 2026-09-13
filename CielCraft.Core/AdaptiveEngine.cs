namespace CielCraft.Core;

/// <summary>
/// The action chosen for the current step. ConsumeFromPlan is how many planned
/// actions this decision retires (1 = the planned action itself); a non-null
/// DeviationReason marks an intentional departure from the Raphael plan.
/// </summary>
public sealed record AdaptiveDecision(uint ActionId, int ConsumeFromPlan, string? DeviationReason);

/// <summary>
/// Live-state adaptation over a Raphael plan (spec §15/§16). Evaluated before
/// every action. v1 rules, all safe underestimates (buffs are ignored, so real
/// gains are never lower than estimated):
///
///  1. Quality target reached and one synthesis action can finish the craft
///     (including Good-only Intensive Synthesis) — finish now.
///  2. Quality target reached otherwise — skip quality-only actions and run
///     the remaining progress portion of the plan.
///  3. Plan exhausted with the craft incomplete and quality capped — keep
///     synthesizing with the strongest affordable action instead of pausing.
///
/// Anything else follows the plan unchanged.
/// </summary>
public static class AdaptiveEngine
{
    public static AdaptiveDecision? Decide(
        CraftSnapshot state,
        IReadOnlyList<uint> remainingPlan,
        int baseProgress,
        byte level)
    {
        var qualityCapped = state.Quality >= state.MaxQuality;

        if (!qualityCapped || baseProgress <= 0)
            return FollowPlan(remainingPlan);

        var remainingProgress = state.MaxProgress - state.Progress;

        // Rule 1: one action to finish, cheapest sufficient finisher wins.
        var finisher = PickFinisher(state, level, baseProgress, requireSufficient: true, remainingProgress);
        if (finisher != null)
        {
            return new AdaptiveDecision(
                finisher.ActionId,
                ConsumeFromPlan: remainingPlan.Count,
                $"quality target reached — finishing with {finisher.Name} " +
                $"({finisher.ProgressGain(baseProgress, level, state.Durability)} progress covers the remaining {remainingProgress})");
        }

        // Rule 2: drop quality-only actions from the plan.
        for (var i = 0; i < remainingPlan.Count; i++)
        {
            if (CraftActionData.IsQualityOnly(remainingPlan[i]))
                continue;

            return new AdaptiveDecision(
                remainingPlan[i],
                ConsumeFromPlan: i + 1,
                i == 0 ? null : $"quality target reached — skipping {i} quality action(s)");
        }

        // Rule 3: nothing useful left in the plan; keep pushing progress.
        var fallback = PickFinisher(state, level, baseProgress, requireSufficient: false, remainingProgress);
        if (fallback != null)
        {
            return new AdaptiveDecision(
                fallback.ActionId,
                ConsumeFromPlan: remainingPlan.Count,
                $"plan exhausted with {remainingProgress} progress remaining — synthesizing with {fallback.Name}");
        }

        return FollowPlan(remainingPlan);
    }

    private static AdaptiveDecision? FollowPlan(IReadOnlyList<uint> remainingPlan) =>
        remainingPlan.Count > 0 ? new AdaptiveDecision(remainingPlan[0], 1, null) : null;

    private static CraftActionData.SynthesisAction? PickFinisher(
        CraftSnapshot state,
        byte level,
        int baseProgress,
        bool requireSufficient,
        int remainingProgress)
    {
        var goodCondition = state.Condition is CraftCondition.Good or CraftCondition.Excellent;

        CraftActionData.SynthesisAction? best = null;
        var bestGain = 0;

        foreach (var action in CraftActionData.Finishers)
        {
            if (level < action.LevelRequirement)
                continue;
            if (action.RequiresGoodCondition && !goodCondition)
                continue;
            if (state.CurrentCp < action.CpCost)
                continue;
            if (state.Durability <= 0)
                continue;

            var gain = action.ProgressGain(baseProgress, level, state.Durability);
            if (requireSufficient)
            {
                // Finishers are ordered cheapest-CP first; the first sufficient one wins.
                if (gain >= remainingProgress)
                    return action;
            }
            else if (gain > bestGain)
            {
                best = action;
                bestGain = gain;
            }
        }

        return requireSufficient ? null : best;
    }
}
