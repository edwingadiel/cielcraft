using System;
using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>
/// One retainer as the client knows it (roadmap 7.17), read from
/// <c>RetainerManager</c>: the sorted index the retainer list shows, the
/// name, the class it ventures as, its level, and the venture in flight.
/// </summary>
/// <param name="Index">Sorted index; the same index <c>IGameBridge.SelectRetainer</c> takes.</param>
/// <param name="ClassJobId">ClassJob row id (16 = MIN, 17 = BTN, 18 = FSH, otherwise a combat class).</param>
/// <param name="VentureId">RetainerTask row id of the venture in flight; 0 when idle.</param>
/// <param name="VentureCompleteAt">When the venture in flight returns; null when idle.</param>
public sealed record RetainerSnapshot(
    int Index,
    string Name,
    uint ClassJobId,
    int Level,
    uint VentureId,
    DateTime? VentureCompleteAt,
    int ItemCount,
    bool Available)
{
    public bool OnVenture => VentureId != 0;

    /// <summary>The venture has come back and only needs collecting.</summary>
    public bool VentureReady(DateTime utcNow) => OnVenture && VentureCompleteAt is { } due && due <= utcNow;
}

/// <summary>A summoning bell in the object table right now: what to interact with and how far away it is.</summary>
public sealed record SummoningBellSnapshot(ulong ObjectId, System.Numerics.Vector3 Position, float Distance);

/// <summary>
/// A venture that brings one item (roadmap 7.17), from the RetainerTask /
/// RetainerTaskNormal sheets: what it brings, how much at each of the five
/// yield tiers, and what the retainer must be to run it.
/// </summary>
/// <param name="Quantities">
/// Yield by tier, lowest first. The tier is how many of the retainer's
/// <c>RetainerTaskParameter</c> thresholds its gathering stat clears — a stat
/// the client does not expose in <c>RetainerManager</c>, so only
/// <see cref="GuaranteedAmount"/> is used for planning.
/// </param>
public sealed record VentureOption(
    uint TaskId,
    uint ItemId,
    string ItemName,
    IReadOnlyList<int> Quantities,
    int RetainerLevel,
    int RequiredGathering,
    int RequiredItemLevel,
    int Minutes,
    uint ClassJobCategoryId)
{
    /// <summary>The lowest tier's yield: what the venture brings even at the worst stats.</summary>
    public int GuaranteedAmount => Quantities.Count > 0 ? Quantities[0] : 0;
}

/// <summary>What the retainer source decided to do for a material, before any window is opened.</summary>
public enum RetainerSupplyKind
{
    /// <summary>Withdraw stock a retainer already holds.</summary>
    Withdraw,

    /// <summary>Collect a venture that has already come back.</summary>
    CollectVenture,
}

/// <summary>
/// The retainer source's plan for one material: which retainer to summon and
/// what to do once it is there.
/// </summary>
public sealed record RetainerSupply(
    RetainerSupplyKind Kind,
    RetainerSnapshot Retainer,
    uint ItemId,
    int Amount,
    VentureOption? Venture,
    int EstimatedSeconds);

/// <summary>
/// The retainer source's offer maths (roadmap 7.17), kept pure so it can be
/// tested without a game: pick the retainer to visit for a material, or
/// answer that none can supply it.
/// </summary>
public static class RetainerOffers
{
    /// <summary>Teleport, walk to the bell, summon, withdraw, dismiss: the flat cost of one bell visit.</summary>
    public const int BellVisitSeconds = 120;

    /// <summary>
    /// A venture whose return time is within this much of now counts as
    /// "already returning" and may be offered: the run would otherwise stall
    /// the production for the rest of the venture's hour (roadmap 7.17; the
    /// "start ventures early" design is a follow-up, see the P3b report).
    /// </summary>
    public static readonly TimeSpan VentureGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The best retainer plan for <paramref name="amount"/> of the item, or
    /// null when no retainer can cover it. Only a full cover is offered: the
    /// production runner marks a source task done when its run completes, so
    /// a partial withdrawal would leave the craft short with no second chance.
    /// </summary>
    /// <param name="held">Item count on a retainer's cached pages, by sorted index.</param>
    /// <param name="ventureInFlight">The venture a retainer is out on; null when idle or unknown.</param>
    /// <param name="ventures">Whether <see cref="AutomationSettings.RetainerVentures"/> is on.</param>
    public static RetainerSupply? Choose(
        IReadOnlyList<RetainerSnapshot> retainers,
        uint itemId,
        int amount,
        Func<RetainerSnapshot, int> held,
        Func<RetainerSnapshot, VentureOption?> ventureInFlight,
        bool ventures,
        DateTime utcNow)
    {
        if (amount <= 0 || retainers.Count == 0)
            return null;

        // Stock first: it is there now, and collecting a venture costs the
        // same trip but only pays out once.
        RetainerSupply? best = null;
        foreach (var retainer in retainers)
        {
            if (!retainer.Available)
                continue;

            var count = held(retainer);
            if (count < amount)
                continue;

            if (best == null || count < held(best.Retainer))
                best = new RetainerSupply(RetainerSupplyKind.Withdraw, retainer, itemId, amount, null, BellVisitSeconds);
        }

        if (best != null || !ventures)
            return best;

        foreach (var retainer in retainers)
        {
            if (!retainer.Available || !retainer.OnVenture)
                continue;

            var venture = ventureInFlight(retainer);
            if (venture == null || venture.ItemId != itemId || venture.GuaranteedAmount < amount)
                continue;

            // Only a venture that is already back (or about to be) is worth
            // waiting on; anything else parks the run for up to an hour.
            if (retainer.VentureCompleteAt is not { } due || due > utcNow + VentureGrace)
                continue;

            var wait = (int)Math.Max(0, (due - utcNow).TotalSeconds);
            return new RetainerSupply(
                RetainerSupplyKind.CollectVenture, retainer, itemId, amount, venture, BellVisitSeconds + wait);
        }

        return null;
    }
}
