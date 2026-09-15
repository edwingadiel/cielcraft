using System;
using System.Collections.Generic;
using System.Linq;

namespace CielCraft.Core;

/// <summary>
/// Turns an order group into one production plan (roadmap 7.13): restock
/// orders become "amount − owned", disabled and satisfied orders are skipped
/// with a reason, and the remaining targets are resolved together so
/// sub-crafts and raw materials are shared across the group.
/// </summary>
public static class OrderPlanner
{
    public static GroupPlan PlanGroup(
        OrderGroup group,
        IRecipeProvider recipes,
        Func<uint, int> ownedOf,
        CharacterCapabilities? capabilities = null)
    {
        var outcomes = new List<OrderOutcome>();
        var targets = new List<PlanTarget>();

        foreach (var order in group.Orders)
        {
            var (quantity, skip) = Evaluate(order, recipes, ownedOf);
            outcomes.Add(new OrderOutcome(order, quantity, skip));
            if (skip == null && quantity > 0)
                targets.Add(new PlanTarget(order.ItemId, quantity, order.Mode, order.MaterialsOnly));
        }

        var plan = targets.Count == 0
            ? null
            : DependencyResolver.Resolve(targets, recipes, ownedOf, capabilities);
        return new GroupPlan(group, plan, outcomes);
    }

    /// <summary>Quantity to produce for one order and why it is skipped, if it is.</summary>
    public static (int Quantity, string? SkipReason) Evaluate(Order order, IRecipeProvider recipes, Func<uint, int> ownedOf)
    {
        if (!order.Enabled)
            return (0, "disabled");

        if (order.ItemId == 0)
            return (0, "no item");

        if (order.Mode == ProductionMode.Collectable)
            return (0, "collectable crafting is not supported yet (roadmap 7.23)");

        if (recipes.FindRecipeForItem(order.ItemId) == null)
            return (0, "not craftable");

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
