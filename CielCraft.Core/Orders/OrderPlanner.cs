using System;
using System.Collections.Generic;
using System.Linq;

namespace CielCraft.Core;

/// <summary>
/// Turns an order group into one production plan (roadmap 7.13): restock
/// orders become "amount − owned", disabled and satisfied orders are skipped
/// with a reason, and the remaining targets are resolved together so
/// sub-crafts and raw materials are shared across the group. Gather orders
/// (7.1) become raw materials of the plan; collectable orders (7.23 / 7.1)
/// carry their tier to the craft step or gather task.
/// </summary>
public static class OrderPlanner
{
    /// <param name="isGatherable">Whether the gathering database knows the item (7.1); null = no gather orders can be planned.</param>
    public static GroupPlan PlanGroup(
        OrderGroup group,
        IRecipeProvider recipes,
        Func<uint, int> ownedOf,
        CharacterCapabilities? capabilities = null,
        Func<uint, bool>? isGatherable = null)
    {
        var outcomes = new List<OrderOutcome>();
        var targets = new List<PlanTarget>();

        foreach (var order in group.Orders)
        {
            var (quantity, skip) = Evaluate(order, recipes, ownedOf, isGatherable);
            outcomes.Add(new OrderOutcome(order, quantity, skip));
            if (skip == null && quantity > 0)
                targets.Add(TargetFor(order, quantity));
        }

        var plan = targets.Count == 0
            ? null
            : DependencyResolver.Resolve(targets, recipes, ownedOf, capabilities);
        return new GroupPlan(group, plan, outcomes);
    }

    /// <summary>
    /// The resolver target for a planned order. A gather order has no craft,
    /// so materials-only is meaningless and only Any / Collectable apply as
    /// modes (HQ and quick synthesis are crafting notions).
    /// </summary>
    private static PlanTarget TargetFor(Order order, int quantity)
    {
        if (order.Kind == OrderKind.Gather)
        {
            var mode = order.Mode == ProductionMode.Collectable ? ProductionMode.Collectable : ProductionMode.Any;
            return new PlanTarget(order.ItemId, quantity, mode, false, OrderKind.Gather, order.CollectableTier);
        }

        return new PlanTarget(order.ItemId, quantity, order.Mode, order.MaterialsOnly, OrderKind.Craft, order.CollectableTier);
    }

    /// <summary>Quantity to produce for one order and why it is skipped, if it is.</summary>
    public static (int Quantity, string? SkipReason) Evaluate(
        Order order, IRecipeProvider recipes, Func<uint, int> ownedOf, Func<uint, bool>? isGatherable = null)
    {
        if (!order.Enabled)
            return (0, "disabled");

        if (order.ItemId == 0)
            return (0, "no item");

        if (order.Kind == OrderKind.Gather)
        {
            if (isGatherable == null || !isGatherable(order.ItemId))
                return (0, "not gatherable");
        }
        else
        {
            var recipe = recipes.FindRecipeForItem(order.ItemId);
            if (recipe == null)
                return (0, "not craftable");

            // Collectable synthesis is only offered for collectable items (7.23).
            if (order.Mode == ProductionMode.Collectable && !recipe.IsCollectable)
                return (0, "not a collectable recipe");
        }

        var quantity = order.AmountMode == AmountMode.Restock
            ? order.Amount - ownedOf(order.ItemId)
            : order.Amount;

        if (quantity <= 0)
            return (0, order.AmountMode == AmountMode.Restock ? "already stocked" : "amount is zero");

        return (quantity, null);
    }

    /// <summary>Enabled groups with at least one enabled order, in run order.</summary>
    public static IEnumerable<OrderGroup> RunnableGroups(OrderBook book) =>
        book.Groups.Where(g => g.Enabled && g.Orders.Any(o => o.Enabled));
}
