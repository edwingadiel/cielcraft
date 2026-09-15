using System;
using System.Collections.Generic;
using System.Linq;

namespace CielCraft.Core;

/// <summary>
/// Scrips an exchange needs but the character does not hold (roadmap 7.17).
/// The currency is an ordinary item (Purple / Orange Crafters' or Gatherers'
/// Scrip); the amount is the shortfall, not the whole price.
/// </summary>
public sealed record ScripNeed(uint CurrencyItemId, string CurrencyName, int Amount);

/// <summary>
/// One collectable the appraiser takes for scrips: what it is, which job
/// makes or gathers it and at what level, what each collectability tier pays,
/// and how expensive one unit is (ingredient count for a craft, 1 for a
/// gather) plus a rough duration. The plugin builds these from
/// CollectablesShopItem / CollectablesShopRefine / CollectablesShopRewardScrip
/// and hands them to the planner, so the planner itself stays pure.
/// </summary>
public sealed record ScripTurnIn(
    uint ItemId,
    string Name,
    uint CurrencyItemId,
    uint JobId,
    int RequiredLevel,
    bool IsGathered,
    int LowReward,
    int MidReward,
    int HighReward,
    int LowThreshold = 0,
    int MidThreshold = 0,
    int HighThreshold = 0,
    int MaterialCost = 1,
    int SecondsPerUnit = 60,
    uint TurnInNpcId = 0,
    string TurnInNpcName = "")
{
    public int RewardAt(CollectableTier tier) => tier switch
    {
        CollectableTier.Low => LowReward,
        CollectableTier.Mid => MidReward,
        _ => HighReward,
    };

    /// <summary>Collectability the item must reach for that tier, as the turn-in window shows it (quality / 10).</summary>
    public int CollectabilityAt(CollectableTier tier) => tier switch
    {
        CollectableTier.Low => LowThreshold,
        CollectableTier.Mid => MidThreshold,
        _ => HighThreshold,
    };

    /// <summary>The tiers that actually pay scrips, lowest first; a tier worth 0 is not a turn-in.</summary>
    public IEnumerable<CollectableTier> PayingTiers
    {
        get
        {
            if (LowReward > 0)
                yield return CollectableTier.Low;
            if (MidReward > 0)
                yield return CollectableTier.Mid;
            if (HighReward > 0)
                yield return CollectableTier.High;
        }
    }
}

/// <summary>One kind of collectable to produce for the scrips, at a tier, with the count that covers the need.</summary>
public sealed record ScripPlanStep(ScripTurnIn TurnIn, int Count, CollectableTier Tier)
{
    public int ScripsPerUnit => TurnIn.RewardAt(Tier);

    public int Scrips => Count * ScripsPerUnit;

    /// <summary>What the order runner prepends to the group: a collectable craft or gather order at this tier.</summary>
    public PlanTarget Target => new(
        TurnIn.ItemId,
        Count,
        ProductionMode.Collectable,
        MaterialsOnly: false,
        TurnIn.IsGathered ? OrderKind.Gather : OrderKind.Craft,
        Tier);

    public override string ToString() =>
        $"{(TurnIn.IsGathered ? "gather" : "craft")} {Count}× {TurnIn.Name} at {Tier} collectability ({Scrips} scrips)";
}

/// <summary>What to produce and where to hand it in so an exchange's scrips are covered (roadmap 7.17).</summary>
public sealed record ScripPlan(
    ScripNeed Need,
    IReadOnlyList<ScripPlanStep> Steps,
    uint TurnInNpcId,
    string TurnInNpcName)
{
    public IReadOnlyList<PlanTarget> Targets => Steps.Select(s => s.Target).ToList();

    public int Scrips => Steps.Sum(s => s.Scrips);

    public int TotalUnits => Steps.Sum(s => s.Count);

    public int EstimatedSeconds => Steps.Sum(s => s.Count * s.TurnIn.SecondsPerUnit);

    public int MaterialCost => Steps.Sum(s => s.Count * s.TurnIn.MaterialCost);

    public string Description =>
        $"{string.Join(", ", Steps)} → {Scrips} {Need.CurrencyName}" +
        (TurnInNpcName.Length > 0 ? $", hand in to {TurnInNpcName}" : "");
}

/// <summary>
/// Picks the collectables to make for a scrip shortfall (roadmap 7.17).
/// Pure: the caller supplies the turn-ins the appraiser accepts and the
/// character's DoH/DoL levels, so the whole decision is testable offline.
///
/// <para><see cref="ScripSourcePreference.Cheapest"/> spends the fewest
/// materials per scrip and hands in at the lowest tier that pays (the
/// solver then needs the least quality). <see cref="ScripSourcePreference
/// .Fastest"/> spends the fewest seconds per scrip and hands in at the
/// highest tier that pays, so fewer items cover the same amount — fewer
/// crafts, fewer node visits, one trip.</para>
/// </summary>
public static class ScripPlanner
{
    /// <summary>A plan nobody wants: more than this many collectables for one exchange.</summary>
    public const int DefaultMaxUnits = 99;

    /// <summary>
    /// The turn-ins to produce for <paramref name="need"/>, or null when no
    /// known turn-in pays that currency at the character's level (or every
    /// candidate would need more than <paramref name="maxUnits"/> items).
    /// <paramref name="jobLevels"/> is the capabilities' job-level map: a job
    /// missing from it counts as level 0, so an unread character plans
    /// nothing rather than something it cannot make.
    /// </summary>
    public static ScripPlan? Plan(
        ScripNeed need,
        IReadOnlyList<ScripTurnIn> turnIns,
        IReadOnlyDictionary<uint, int> jobLevels,
        ScripSourcePreference preference,
        int maxUnits = DefaultMaxUnits)
    {
        if (need.Amount <= 0 || turnIns.Count == 0)
            return null;

        ScripPlanStep? best = null;
        double bestScore = double.MaxValue;

        foreach (var turnIn in turnIns)
        {
            if (turnIn.CurrencyItemId != need.CurrencyItemId)
                continue;

            // Level gate: the character must already be able to make or gather it.
            if (jobLevels.GetValueOrDefault(turnIn.JobId) < turnIn.RequiredLevel)
                continue;

            var tiers = turnIn.PayingTiers.ToList();
            if (tiers.Count == 0)
                continue;

            var tier = preference == ScripSourcePreference.Cheapest ? tiers[0] : tiers[^1];
            var reward = turnIn.RewardAt(tier);
            var count = (need.Amount + reward - 1) / reward;
            if (count > maxUnits)
                continue;

            // Cheapest counts materials per scrip, Fastest counts seconds per
            // scrip; a cheaper item that needs three times as many crafts is
            // not faster, and a fast one that eats rare mats is not cheaper.
            var score = preference == ScripSourcePreference.Cheapest
                ? turnIn.MaterialCost / (double)reward
                : turnIn.SecondsPerUnit / (double)reward;

            var step = new ScripPlanStep(turnIn, count, tier);
            if (best == null || score < bestScore - 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && Beats(step, best, preference)))
            {
                best = step;
                bestScore = score;
            }
        }

        if (best == null)
            return null;

        return new ScripPlan(need, [best], best.TurnIn.TurnInNpcId, best.TurnIn.TurnInNpcName);
    }

    /// <summary>Tie-break between two equally scored steps: fewer items, then the lower item id so plans are stable.</summary>
    private static bool Beats(ScripPlanStep candidate, ScripPlanStep current, ScripSourcePreference preference)
    {
        if (candidate.Count != current.Count)
            return candidate.Count < current.Count;

        // With the same cost, more scrips per item still means fewer trips.
        if (preference == ScripSourcePreference.Fastest && candidate.ScripsPerUnit != current.ScripsPerUnit)
            return candidate.ScripsPerUnit > current.ScripsPerUnit;

        return candidate.TurnIn.ItemId < current.TurnIn.ItemId;
    }
}
