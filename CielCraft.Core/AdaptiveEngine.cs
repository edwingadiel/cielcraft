namespace CielCraft.Core;

/// <summary>
/// The action chosen for the current step. ConsumeFromPlan is how many planned
/// actions this decision retires (1 = the planned action itself); a non-null
/// DeviationReason marks an intentional departure from the Raphael plan.
/// </summary>
public sealed record AdaptiveDecision(uint ActionId, int ConsumeFromPlan, string? DeviationReason);

/// <summary>
/// Live-state adaptation over a Raphael plan (spec §15/§16). Evaluated before
/// every action. v1 rules; progress estimates count the progress buffs
/// active for the next action (Veneration, Muscle Memory) and nothing
/// speculative, so real gains are never lower than estimated:
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
        byte level,
        int targetQuality = 0)
    {
        var target = targetQuality > 0 ? Math.Min(targetQuality, state.MaxQuality) : state.MaxQuality;
        var qualityCapped = state.Quality >= target;

        // Rule 0: Excellent is always followed by Poor, and Poor halves quality
        // gains. When the planned action on a Poor step is a quality action,
        // spend the step on Observe so it lands on a Normal step instead — but
        // only when CP and durability can absorb the extra step, since every
        // buff window shifts by one.
        if (state.Condition == CraftCondition.Poor
            && !qualityCapped
            && remainingPlan.Count > 0
            && CraftActionData.AffectsQuality(remainingPlan[0])
            && CanAffordObserve(state, remainingPlan))
        {
            return new AdaptiveDecision(
                CraftActionData.Observe,
                ConsumeFromPlan: 0,
                "Poor condition — observing so the planned quality action lands on a normal step");
        }

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
                $"({Gain(finisher, state, level, baseProgress)} progress covers the remaining {remainingProgress})");
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

    private static int Gain(CraftActionData.SynthesisAction action, CraftSnapshot state, byte level, int baseProgress) =>
        action.ProgressGain(
            baseProgress,
            level,
            state.Durability,
            veneration: state.HasBuff(CraftBuffIds.Veneration),
            muscleMemory: state.HasBuff(CraftBuffIds.MuscleMemory));

    /// <summary>
    /// The rest of the plan still fits after inserting an Observe step: CP for
    /// everything including Observe, and durability walked action by action
    /// with Waste Not halving costs and Manipulation restoring 5 per step for
    /// as long as they last (Trained Perfection is ignored, which only makes
    /// the check stricter).
    /// </summary>
    private static bool CanAffordObserve(CraftSnapshot state, IReadOnlyList<uint> remainingPlan)
    {
        var cpNeeded = CraftActionData.CpCost(CraftActionData.Observe);
        foreach (var action in remainingPlan)
            cpNeeded += CraftActionData.CpCost(action);
        if (state.CurrentCp < cpNeeded)
            return false;

        var durability = state.Durability;
        var wasteNot = Math.Max(
            state.FindBuff(CraftBuffIds.WasteNot)?.RemainingSteps ?? 0,
            state.FindBuff(CraftBuffIds.WasteNot2)?.RemainingSteps ?? 0);
        var manipulation = state.FindBuff(CraftBuffIds.Manipulation)?.RemainingSteps ?? 0;

        StepDurability(ref durability, ref wasteNot, ref manipulation, cost: 0, state.MaxDurability); // Observe

        foreach (var action in remainingPlan)
        {
            if (durability <= 0)
                return false;

            var cost = CraftActionData.DurabilityCost(action);
            if (wasteNot > 0)
                cost /= 2;
            StepDurability(ref durability, ref wasteNot, ref manipulation, cost, state.MaxDurability);

            // Buffs and mends the plan itself applies take effect from the next step.
            switch (action)
            {
                case 4574: manipulation = 8; break;                                   // Manipulation
                case 4631: wasteNot = 4; break;                                       // Waste Not
                case 4639: wasteNot = 8; break;                                       // Waste Not II
                case 100003: durability = Math.Min(durability + 30, state.MaxDurability); break; // Master's Mend
                case 100467: durability = state.MaxDurability; break;                 // Immaculate Mend
            }
        }

        return true;
    }

    private static void StepDurability(ref int durability, ref int wasteNot, ref int manipulation, int cost, int maxDurability)
    {
        durability -= cost;
        if (wasteNot > 0)
            wasteNot--;
        if (manipulation > 0)
        {
            manipulation--;
            if (durability > 0)
                durability = Math.Min(durability + 5, maxDurability);
        }
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

            var gain = Gain(action, state, level, baseProgress);
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
