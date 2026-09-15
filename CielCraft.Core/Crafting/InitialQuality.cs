using System;
using System.Collections.Generic;
using System.Linq;

namespace CielCraft.Core.Crafting;

/// <summary>
/// HQ materials for one craft of a recipe: how many of each ingredient's
/// per-craft amount are HQ, and the initial quality they grant (roadmap 7.22).
/// </summary>
public sealed record HqMix(IReadOnlyList<(uint ItemId, int HqCount)> Counts, int InitialQuality)
{
    public static readonly HqMix None = new([], 0);

    public int TotalHqItems => Counts.Sum(c => c.HqCount);

    public int HqCountOf(uint itemId)
    {
        foreach (var (id, count) in Counts)
        {
            if (id == itemId)
                return count;
        }

        return 0;
    }
}

/// <summary>
/// The quality a craft starts with when some materials are HQ (roadmap 7.22).
/// The formula lives here and nowhere else:
///
///   initial = floor(MaxQuality × MaterialQualityFactor / 100
///                   × Σ(hqCount_i × ilvl_i) / Σ(amount_i × ilvl_i))
///
/// where both sums run over the ingredients that can be HQ (crystals and,
/// since patch 7.0, gathered materials cannot). MaterialQualityFactor is the
/// Recipe sheet's column (50 on every recipe with HQ-able ingredients), ilvl
/// is Item.LevelItem. Validated in game by the coordinator against Iron
/// Rivets (max quality 360, Iron Ingot ilvl 14 HQ, Fire Shard not HQ-able →
/// 180); if the game disagrees, this is the one place to fix.
/// </summary>
public static class InitialQuality
{
    /// <summary>Initial quality for the HQ counts (per craft) of a recipe; 0 without material data.</summary>
    public static int For(RecipeInfo recipe, int maxQuality, IReadOnlyList<(uint ItemId, int HqCount)> hqCounts) =>
        For(recipe, maxQuality, itemId =>
        {
            foreach (var (id, count) in hqCounts)
            {
                if (id == itemId)
                    return count;
            }

            return 0;
        });

    /// <summary>Initial quality for a per-ingredient HQ count lookup (clamped to the per-craft amount).</summary>
    public static int For(RecipeInfo recipe, int maxQuality, Func<uint, int> hqCountOf)
    {
        if (recipe.Materials == null || recipe.MaterialQualityFactor <= 0 || maxQuality <= 0)
            return 0;

        long total = 0, hq = 0;
        foreach (var material in recipe.Materials)
        {
            if (!material.CanBeHq || material.ItemLevel <= 0 || material.Amount <= 0)
                continue;

            total += (long)material.Amount * material.ItemLevel;
            hq += (long)Math.Clamp(hqCountOf(material.ItemId), 0, material.Amount) * material.ItemLevel;
        }

        if (total == 0 || hq == 0)
            return 0;

        // Integer arithmetic keeps the floor exact: every factor is an integer
        // and the division happens once, at the end.
        return (int)Math.Min(maxQuality, (long)maxQuality * recipe.MaterialQualityFactor * hq / (100 * total));
    }

    /// <summary>
    /// The cheapest HQ counts reaching <paramref name="needed"/> initial
    /// quality: fewest HQ items, taken from the ingredient with the highest
    /// item level first (each of its items grants the most quality), never
    /// more than <paramref name="maxHqPerCraft"/> allows per ingredient (0 =
    /// cannot be seeded, e.g. a gathered material; null = the per-craft
    /// amount). Null when even the allowed maximum falls short; an empty mix
    /// when nothing is needed. Rounding to whole crafts of the intermediate
    /// is the plan's business (<see cref="HqSeeding"/>).
    /// </summary>
    public static HqMix? MixFor(RecipeInfo recipe, int maxQuality, int needed, Func<uint, int>? maxHqPerCraft = null)
    {
        if (needed <= 0)
            return HqMix.None;

        if (recipe.Materials == null || recipe.MaterialQualityFactor <= 0 || maxQuality <= 0)
            return null;

        var candidates = recipe.Materials
            .Where(m => m.CanBeHq && m.ItemLevel > 0 && m.Amount > 0)
            .Select(m => (m.ItemId, Cap: Math.Clamp(maxHqPerCraft?.Invoke(m.ItemId) ?? m.Amount, 0, m.Amount), m.ItemLevel))
            .Where(c => c.Cap > 0)
            .OrderByDescending(c => c.ItemLevel)
            .ThenByDescending(c => c.Cap)
            .ToList();

        var counts = new Dictionary<uint, int>();
        foreach (var (itemId, cap, _) in candidates)
        {
            for (var k = 0; k < cap; k++)
            {
                counts[itemId] = counts.GetValueOrDefault(itemId) + 1;
                var initial = For(recipe, maxQuality, id => counts.GetValueOrDefault(id));
                if (initial >= needed)
                    return new HqMix(counts.Select(pair => (pair.Key, pair.Value)).ToList(), initial);
            }
        }

        return null;
    }
}
