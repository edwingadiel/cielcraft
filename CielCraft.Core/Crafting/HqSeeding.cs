using System;
using System.Collections.Generic;
using System.Linq;

namespace CielCraft.Core.Crafting;

/// <summary>What a quality-wanting craft step asks of the seeding pass: the recipe's max quality and the quality its crafts must reach.</summary>
public sealed record HqSeedRequest(int MaxQuality, int TargetQuality);

/// <summary>
/// "Reachable from zero?" (roadmap 7.22). For every craft step whose crafts
/// want quality — a target whose order mode asks for it, or an intermediate
/// that an earlier decision made HQ — the pass asks the oracle what quality
/// the character's rotation reaches from NQ materials; when that falls short
/// of the target, <see cref="InitialQuality.MixFor"/> picks the HQ materials
/// that seed the difference, the oracle confirms the mix, and the plan steps
/// that craft those materials get <see cref="PlannedCraft.HqCrafts"/> (whole
/// crafts of the intermediate). Only crafted intermediates can be seeded: a
/// gathered or bought ingredient has no step to mark, and gathered materials
/// cannot be HQ anyway. Steps are visited last to first, so a step's HQ count
/// is final before its own ingredients are considered — a seeded
/// intermediate's ingredients are seeded in turn if it cannot reach 100%
/// from NQ.
/// </summary>
public static class HqSeeding
{
    /// <summary>How many times a mix the confirm solve found short is raised before giving up.</summary>
    private const int ConfirmRounds = 3;

    /// <summary>
    /// The quality a rotation reaches for the recipe from an initial quality,
    /// solving for the target; null when no answer is available (solver
    /// failure, or a cache-only caller with nothing cached).
    /// </summary>
    public delegate int? ReachableQuality(uint recipeId, int initialQuality, int targetQuality);

    /// <summary>
    /// The plan with HqCrafts set. <paramref name="requestFor"/> says what a
    /// step wants: for a target step its order's target (null = the mode does
    /// not care about quality), for a seeded intermediate 100%.
    /// </summary>
    public static ProductionPlan Apply(
        ProductionPlan plan,
        IRecipeProvider recipes,
        Func<PlannedCraft, bool, HqSeedRequest?> requestFor,
        ReachableQuality reachable,
        ILog log)
    {
        if (plan.CraftSteps.Count == 0)
            return plan;

        var steps = plan.CraftSteps.ToList();
        var indexOf = new Dictionary<uint, int>();
        for (var i = 0; i < steps.Count; i++)
            indexOf[steps[i].ItemId] = i;

        // HQ items of an intermediate already promised to a later consumer.
        var promised = new Dictionary<uint, int>();
        var changed = false;

        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var step = steps[i];
            var isTarget = plan.Targets.Any(t => !t.MaterialsOnly && t.Kind == OrderKind.Craft && t.ItemId == step.ItemId);
            var qualityCrafts = isTarget ? step.Crafts : step.HqCrafts;
            if (qualityCrafts <= 0)
                continue;

            var request = requestFor(step, isTarget);
            if (request == null || request.TargetQuality <= 0 || request.MaxQuality <= 0)
                continue;

            var recipe = recipes.GetRecipeById(step.RecipeId);
            var name = recipes.GetItemName(step.ItemId);
            if (recipe?.Materials == null)
            {
                log.Warning($"[Production] HQ intermediates: no material data for {name}; cannot tell whether HQ materials are needed.");
                continue;
            }

            // Only crafted-earlier, HQ-able ingredients can be seeded; with
            // none there is nothing to decide and no solve to spend.
            var seedable = recipe.Materials
                .Where(m => m.CanBeHq && indexOf.TryGetValue(m.ItemId, out var index) && index < i)
                .ToDictionary(m => m.ItemId, m => m);
            if (seedable.Count == 0)
                continue;

            var target = Math.Min(request.TargetQuality, request.MaxQuality);
            var fromZero = reachable(recipe.RecipeId, 0, target);
            if (fromZero == null)
            {
                log.Information($"[Production] HQ intermediates: no rotation answer for {name}; keeping quick synthesis for its intermediates.");
                continue;
            }

            if (fromZero.Value >= target)
            {
                log.Information($"[Production] HQ intermediates: {name} reaches {fromZero}/{request.MaxQuality} quality from NQ materials (target {target}); no HQ materials needed.");
                continue;
            }

            // Per-craft HQ an ingredient can supply: what its step produces,
            // minus what later consumers already claimed, spread over the
            // crafts that want quality.
            int CapOf(uint itemId)
            {
                if (!seedable.TryGetValue(itemId, out var material))
                    return 0;
                var supplier = steps[indexOf[itemId]];
                var available = supplier.TotalProduced - promised.GetValueOrDefault(itemId);
                return Math.Clamp(available / qualityCrafts, 0, material.Amount);
            }

            var needed = target - fromZero.Value;
            HqMix? accepted = null;
            int? confirmed = null;
            for (var round = 0; round < ConfirmRounds; round++)
            {
                var mix = InitialQuality.MixFor(recipe, request.MaxQuality, needed, CapOf);
                if (mix == null)
                    break;

                // The crafting log's HQ fill takes as many HQ as the bag
                // holds for every slot, so a partial count would be spent by
                // the first crafts of a batch and missing for the last ones.
                // With more than one craft, seed whole ingredients.
                if (qualityCrafts > 1)
                    mix = WholeIngredients(mix, recipe, request.MaxQuality, CapOf);

                confirmed = reachable(recipe.RecipeId, mix.InitialQuality, target);
                if (confirmed == null || confirmed.Value >= target)
                {
                    accepted = mix;
                    break;
                }

                // The rotation from that start still falls short: ask for
                // the shortfall on top and try again.
                needed = mix.InitialQuality + (target - confirmed.Value);
            }

            if (accepted == null)
            {
                log.Information(
                    $"[Production] HQ intermediates: {name} reaches {fromZero}/{request.MaxQuality} quality from NQ materials (target {target}) " +
                    "and no HQ mix of its crafted intermediates gets it there; keeping quick synthesis.");
                continue;
            }

            var parts = new List<string>();
            foreach (var (itemId, hqPerCraft) in accepted.Counts)
            {
                if (hqPerCraft <= 0)
                    continue;

                var index = indexOf[itemId];
                var supplier = steps[index];
                var hqCrafts = (hqPerCraft * qualityCrafts + supplier.ResultAmount - 1) / supplier.ResultAmount;
                var total = Math.Min(supplier.Crafts, supplier.HqCrafts + hqCrafts);
                steps[index] = supplier with { HqCrafts = total };
                promised[itemId] = promised.GetValueOrDefault(itemId) + hqCrafts * supplier.ResultAmount;
                changed = true;
                parts.Add($"{recipes.GetItemName(itemId)} ×{hqPerCraft} HQ per craft → {total} HQ of {supplier.Crafts} crafts");
            }

            log.Information(
                $"[Production] HQ intermediates: {name} reaches {fromZero}/{request.MaxQuality} quality from NQ materials (target {target}); " +
                $"seeding {accepted.InitialQuality} initial quality" +
                (confirmed == null ? " (unconfirmed)" : $" (rotation then reaches {confirmed})") +
                $": {string.Join(", ", parts)}.");
        }

        return changed ? plan with { CraftSteps = steps } : plan;
    }

    /// <summary>Raises every partial HQ count of the mix to the ingredient's per-craft amount (within its cap).</summary>
    private static HqMix WholeIngredients(HqMix mix, RecipeInfo recipe, int maxQuality, Func<uint, int> capOf)
    {
        var counts = new List<(uint ItemId, int HqCount)>();
        foreach (var (itemId, count) in mix.Counts)
        {
            var amount = recipe.Materials!.First(m => m.ItemId == itemId).Amount;
            counts.Add((itemId, count > 0 && count < amount ? Math.Max(count, Math.Min(amount, capOf(itemId))) : count));
        }

        return new HqMix(counts, InitialQuality.For(recipe, maxQuality, counts));
    }
}
