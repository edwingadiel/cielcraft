namespace CielCraft.Core;

/// <summary>
/// The action chosen for the current step. ConsumeFromPlan is how many planned
/// actions this decision retires (1 = the planned action itself); a non-null
/// DeviationReason marks an intentional departure from the Raphael plan.
/// ActionId 0 is a request to stop and re-solve from the live state: the
/// remaining plan can no longer complete the craft.
/// </summary>
public sealed record AdaptiveDecision(uint ActionId, int ConsumeFromPlan, string? DeviationReason)
{
    public bool RequestsResolve => ActionId == 0;
}

/// <summary>
/// Live-state adaptation over a Raphael plan (spec §15/§16). Evaluated before
/// every action. v1 rules; progress estimates count the progress buffs
/// active for the next action (Veneration, Muscle Memory) and nothing
/// speculative, so real gains are never lower than estimated:
///
///  0. The remaining plan no longer fits the live craft (durability runs out
///     before it ends, or — once quality is met — its synthesis actions cannot
///     reach max progress): ask for a re-solve instead of walking into a
///     failed synthesis. Happens after a manual action during a pause shifted
///     every buff window.
///  0b. Poor step with a quality action planned: Observe first when the rest
///     of the plan still fits with one more step in it.
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
        var remainingProgress = state.MaxProgress - state.Progress;

        // Rule 0: durability must carry the whole remaining plan — checked only
        // when the plan is what we are about to follow (a finisher that ends the
        // craft right now makes the rest of the plan moot).
        if (!qualityCapped && remainingPlan.Count > 0 && baseProgress > 0)
        {
            var sim = Simulate(state, remainingPlan, level, baseProgress, observeFirst: false);
            if (!sim.Fits)
                return Resolve($"the remaining plan runs out of durability before it ends ({state.Durability} left)");
        }

        // Rule 0b: Excellent is always followed by Poor, and Poor halves quality
        // gains. Spend the Poor step on Observe when the rest still fits.
        if (state.Condition == CraftCondition.Poor
            && !qualityCapped
            && remainingPlan.Count > 0
            && CraftActionData.AffectsQuality(remainingPlan[0])
            && CanAffordObserve(state, remainingPlan, level, baseProgress))
        {
            return new AdaptiveDecision(
                CraftActionData.Observe,
                ConsumeFromPlan: 0,
                "Poor condition — observing so the planned quality action lands on a normal step");
        }

        if (!qualityCapped || baseProgress <= 0)
            return FollowPlan(remainingPlan);

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

        // Rule 2: drop quality-only actions from the plan — but only if what is
        // left can actually finish the craft.
        for (var i = 0; i < remainingPlan.Count; i++)
        {
            if (CraftActionData.IsQualityOnly(remainingPlan[i]))
                continue;

            var rest = remainingPlan.Skip(i).ToArray();
            var sim = Simulate(state, rest, level, baseProgress, observeFirst: false);
            if (sim.Progress < remainingProgress)
                return Resolve($"quality target reached but the remaining plan yields ~{sim.Progress} of the {remainingProgress} progress still needed");

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

    private static AdaptiveDecision Resolve(string reason) => new(0, 0, reason);

    private static int Gain(CraftActionData.SynthesisAction action, CraftSnapshot state, byte level, int baseProgress) =>
        action.ProgressGain(
            baseProgress,
            level,
            state.Durability,
            veneration: state.HasBuff(CraftBuffIds.Veneration),
            muscleMemory: state.HasBuff(CraftBuffIds.MuscleMemory),
            wasteNot: state.HasBuff(CraftBuffIds.WasteNot) || state.HasBuff(CraftBuffIds.WasteNot2),
            trainedPerfection: state.HasBuff(CraftBuffIds.TrainedPerfection));

    /// <summary>
    /// CP for the rest of the plan plus Observe, and the plan still fitting
    /// with one extra step in front of it (every buff window shifts by one).
    /// </summary>
    private static bool CanAffordObserve(CraftSnapshot state, IReadOnlyList<uint> remainingPlan, byte level, int baseProgress)
    {
        var cpNeeded = CraftActionData.CpCost(CraftActionData.Observe);
        foreach (var action in remainingPlan)
            cpNeeded += CraftActionData.CpCost(action);
        if (state.CurrentCp < cpNeeded)
            return false;

        return Simulate(state, remainingPlan, level, baseProgress, observeFirst: true).Fits;
    }

    private readonly record struct PlanSim(bool Fits, int Progress);

    /// <summary>
    /// Walks the remaining plan against the live craft: durability action by
    /// action (Waste Not halving, Manipulation +5 per step, Trained Perfection,
    /// mends — from the live buffs and from what the plan itself applies) and
    /// the progress its synthesis actions produce (Veneration and Muscle Memory
    /// windows tracked the same way; Groundwork halved below its durability
    /// cost). Fits is false when an action would be attempted at 0 durability.
    /// </summary>
    private static PlanSim Simulate(
        CraftSnapshot state, IReadOnlyList<uint> plan, byte level, int baseProgress, bool observeFirst)
    {
        var durability = state.Durability;
        var wasteNot = Math.Max(
            state.FindBuff(CraftBuffIds.WasteNot)?.RemainingSteps ?? 0,
            state.FindBuff(CraftBuffIds.WasteNot2)?.RemainingSteps ?? 0);
        var manipulation = state.FindBuff(CraftBuffIds.Manipulation)?.RemainingSteps ?? 0;
        var veneration = state.FindBuff(CraftBuffIds.Veneration)?.RemainingSteps ?? 0;
        var muscleMemory = state.FindBuff(CraftBuffIds.MuscleMemory)?.RemainingSteps ?? 0;
        var trainedPerfection = state.HasBuff(CraftBuffIds.TrainedPerfection);
        var progress = 0;

        if (observeFirst)
            Step(ref durability, ref wasteNot, ref manipulation, ref veneration, ref muscleMemory, cost: 0, state.MaxDurability);

        foreach (var action in plan)
        {
            if (durability <= 0)
                return new PlanSim(false, progress);

            var cost = CraftActionData.DurabilityCost(action);
            if (trainedPerfection && cost > 0)
            {
                cost = 0;
                trainedPerfection = false;
            }
            else if (wasteNot > 0)
            {
                cost /= 2;
            }

            var efficiency = SynthesisEfficiency(action, level, durability, cost);
            if (efficiency > 0)
            {
                var buffModifier = 10 + (muscleMemory > 0 ? 10 : 0) + (veneration > 0 ? 5 : 0);
                progress += baseProgress * efficiency * buffModifier / 1000;
                muscleMemory = 0; // consumed by the first synthesis
            }

            Step(ref durability, ref wasteNot, ref manipulation, ref veneration, ref muscleMemory, cost, state.MaxDurability);

            // Buffs and mends the plan itself applies take effect from the next step.
            switch (action)
            {
                case 4574: manipulation = 8; break;                                              // Manipulation
                case 4631: wasteNot = 4; break;                                                  // Waste Not
                case 4639: wasteNot = 8; break;                                                  // Waste Not II
                case 19297: veneration = 4; break;                                               // Veneration
                case 100379: muscleMemory = 5; break;                                            // Muscle Memory
                case 100475: trainedPerfection = true; break;                                    // Trained Perfection
                case 100003: durability = Math.Min(durability + 30, state.MaxDurability); break; // Master's Mend
                case 100467: durability = state.MaxDurability; break;                            // Immaculate Mend
            }
        }

        return new PlanSim(true, progress);
    }

    private static void Step(
        ref int durability, ref int wasteNot, ref int manipulation, ref int veneration, ref int muscleMemory,
        int cost, int maxDurability)
    {
        durability -= cost;
        if (wasteNot > 0)
            wasteNot--;
        if (veneration > 0)
            veneration--;
        if (muscleMemory > 0)
            muscleMemory--;
        if (manipulation > 0)
        {
            manipulation--;
            if (durability > 0)
                durability = Math.Min(durability + 5, maxDurability);
        }
    }

    /// <summary>
    /// Progress efficiency (percent) of a synthesis action; 0 for anything
    /// else. Groundwork halves when durability is below what the action will
    /// actually cost — under Waste Not that is 10, so 10 durability still
    /// gets the full hit (the Cobalt Tungsten Ingot finisher, 2026-09-15).
    /// </summary>
    private static int SynthesisEfficiency(uint actionId, byte level, int durability, int effectiveCost) => actionId switch
    {
        100001 => level < 31 ? 100 : 120,                          // Basic Synthesis
        100203 => level < 82 ? 150 : 180,                          // Careful Synthesis
        100403 => (level < 86 ? 300 : 360) / (durability < effectiveCost ? 2 : 1), // Groundwork
        100315 => 400,                                             // Intensive Synthesis
        100323 => level < 94 ? 100 : 150,                          // Delicate Synthesis
        100427 => 180,                                             // Prudent Synthesis
        100379 => 300,                                             // Muscle Memory (first step)
        _ => 0,
    };

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
