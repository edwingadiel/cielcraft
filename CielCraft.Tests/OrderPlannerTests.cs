using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class OrderPlannerTests
{
    private sealed class FakeProvider : IRecipeProvider
    {
        private readonly Dictionary<uint, RecipeInfo> byResult = new();

        public FakeProvider Add(uint recipeId, uint resultItem, int resultAmount, params (uint ItemId, int Amount)[] ingredients)
        {
            byResult[resultItem] = new RecipeInfo(recipeId, resultItem, resultAmount, ingredients);
            return this;
        }

        /// <summary>A collectable recipe (7.23): the item flag, not a required quality, marks it.</summary>
        public FakeProvider AddCollectable(uint recipeId, uint resultItem, params (uint ItemId, int Amount)[] ingredients)
        {
            byResult[resultItem] = new RecipeInfo(recipeId, resultItem, 1, ingredients, IsCollectable: true);
            return this;
        }

        public RecipeInfo? FindRecipeForItem(uint itemId) => byResult.GetValueOrDefault(itemId);

        public RecipeInfo? GetRecipeById(uint recipeId) => byResult.Values.FirstOrDefault(r => r.RecipeId == recipeId);

        public string GetItemName(uint itemId) => $"Item{itemId}";
    }

    private const uint Sword = 1;
    private const uint Shield = 2;
    private const uint Ingot = 3;
    private const uint Ore = 4;
    private const uint RarefiedSword = 5; // a collectable recipe
    private const uint Crystal = 6;       // gatherable, not craftable

    private static FakeProvider Recipes() => new FakeProvider()
        .Add(10, Sword, 1, (Ingot, 2))
        .Add(11, Shield, 1, (Ingot, 3))
        .Add(20, Ingot, 1, (Ore, 1))
        .AddCollectable(30, RarefiedSword, (Ingot, 1));

    /// <summary>The node data of the tests: ore and crystals can be gathered, nothing else.</summary>
    private static bool Gatherable(uint itemId) => itemId is Ore or Crystal;

    private static Func<uint, int> Owned(params (uint Id, int Count)[] entries)
    {
        var map = entries.ToDictionary(e => e.Id, e => e.Count);
        return id => map.GetValueOrDefault(id);
    }

    private static Order OrderFor(uint itemId, int amount, AmountMode amountMode = AmountMode.Absolute) =>
        new() { ItemId = itemId, Amount = amount, AmountMode = amountMode };

    [Fact]
    public void AbsoluteProducesTheAmountRegardlessOfStock()
    {
        var (quantity, skip) = OrderPlanner.Evaluate(OrderFor(Sword, 3), Recipes(), Owned((Sword, 50)));

        Assert.Equal(3, quantity);
        Assert.Null(skip);
    }

    [Fact]
    public void RestockProducesAmountMinusOwned()
    {
        var (quantity, skip) = OrderPlanner.Evaluate(OrderFor(Sword, 10, AmountMode.Restock), Recipes(), Owned((Sword, 4)));

        Assert.Equal(6, quantity);
        Assert.Null(skip);
    }

    [Fact]
    public void RestockAlreadySatisfiedIsSkipped()
    {
        var (quantity, skip) = OrderPlanner.Evaluate(OrderFor(Sword, 10, AmountMode.Restock), Recipes(), Owned((Sword, 12)));

        Assert.Equal(0, quantity);
        Assert.Equal("already stocked", skip);
    }

    [Fact]
    public void DisabledOrderIsSkipped()
    {
        var order = OrderFor(Sword, 1);
        order.Enabled = false;

        Assert.Equal("disabled", OrderPlanner.Evaluate(order, Recipes(), Owned()).SkipReason);
    }

    [Fact]
    public void CollectableModeIsRefusedForANonCollectableRecipe()
    {
        var order = OrderFor(Sword, 1);
        order.Mode = ProductionMode.Collectable;

        var (quantity, skip) = OrderPlanner.Evaluate(order, Recipes(), Owned());
        Assert.Equal(0, quantity);
        Assert.Equal("not a collectable recipe", skip);
    }

    [Fact]
    public void CollectableOrderIsPlannedWithItsTier()
    {
        var order = OrderFor(RarefiedSword, 3);
        order.Mode = ProductionMode.Collectable;
        order.CollectableTier = CollectableTier.Mid;

        var plan = OrderPlanner.PlanGroup(new OrderGroup { Orders = [order] }, Recipes(), Owned());

        Assert.NotNull(plan.Plan);
        var step = plan.Plan.CraftSteps.Single(s => s.ItemId == RarefiedSword);
        Assert.Equal(ProductionMode.Collectable, step.Mode);
        Assert.Equal(CollectableTier.Mid, step.CollectableTier);
        Assert.Equal(3, step.Crafts);
    }

    // Gather orders (roadmap 7.1).

    private static Order GatherOrder(uint itemId, int amount, AmountMode amountMode = AmountMode.Absolute) =>
        new() { Kind = OrderKind.Gather, ItemId = itemId, Amount = amount, AmountMode = amountMode };

    [Fact]
    public void GatherOrderNeedsTheNodeDataToKnowTheItem()
    {
        Assert.Equal("not gatherable", OrderPlanner.Evaluate(GatherOrder(Ore, 1), Recipes(), Owned()).SkipReason);
        Assert.Equal("not gatherable", OrderPlanner.Evaluate(GatherOrder(Sword, 1), Recipes(), Owned(), Gatherable).SkipReason);
        Assert.Null(OrderPlanner.Evaluate(GatherOrder(Crystal, 1), Recipes(), Owned(), Gatherable).SkipReason);
    }

    [Fact]
    public void GatherOrderBecomesARawMaterialWithoutACraftStep()
    {
        var group = new OrderGroup { Orders = [GatherOrder(Crystal, 12)] };

        var plan = OrderPlanner.PlanGroup(group, Recipes(), Owned((Crystal, 40)), isGatherable: Gatherable);

        Assert.NotNull(plan.Plan);
        Assert.False(plan.IsEmpty);
        Assert.Empty(plan.Plan.CraftSteps);
        var crystal = Assert.Single(plan.Plan.RawMaterials);
        Assert.Equal(Crystal, crystal.ItemId);
        Assert.Equal(12, crystal.Amount);
        Assert.Equal(OrderKind.Gather, Assert.Single(plan.Plan.Targets).Kind);
    }

    [Fact]
    public void GatherRestockSubtractsOwned()
    {
        Assert.Equal(4, OrderPlanner.Evaluate(GatherOrder(Ore, 10, AmountMode.Restock), Recipes(), Owned((Ore, 6)), Gatherable).Quantity);
        Assert.Equal("already stocked", OrderPlanner.Evaluate(GatherOrder(Ore, 10, AmountMode.Restock), Recipes(), Owned((Ore, 10)), Gatherable).SkipReason);
    }

    [Fact]
    public void GatherOrderIgnoresCraftingOnlyOptions()
    {
        // HQ, quick synth and materials-only are crafting notions; a gather order plans as plain gathering.
        var order = GatherOrder(Ore, 2);
        order.Mode = ProductionMode.ForceHq;
        order.MaterialsOnly = true;

        var plan = OrderPlanner.PlanGroup(new OrderGroup { Orders = [order] }, Recipes(), Owned(), isGatherable: Gatherable);

        var target = Assert.Single(plan.Plan!.Targets);
        Assert.Equal(ProductionMode.Any, target.Mode);
        Assert.False(target.MaterialsOnly);
        Assert.Equal(2, Assert.Single(plan.Plan.RawMaterials).Amount);
    }

    [Fact]
    public void CollectableGatherOrderCarriesItsTier()
    {
        var order = GatherOrder(Ore, 2);
        order.Mode = ProductionMode.Collectable;
        order.CollectableTier = CollectableTier.Low;

        var plan = OrderPlanner.PlanGroup(new OrderGroup { Orders = [order] }, Recipes(), Owned(), isGatherable: Gatherable);

        var target = Assert.Single(plan.Plan!.Targets);
        Assert.Equal(ProductionMode.Collectable, target.Mode);
        Assert.Equal(CollectableTier.Low, target.CollectableTier);
    }

    [Fact]
    public void GatherAndCraftOrdersShareOneGatherTrip()
    {
        // The sword needs 2 ore; the gather order wants 3 more of it.
        var group = new OrderGroup { Orders = [OrderFor(Sword, 1), GatherOrder(Ore, 3)] };

        var plan = OrderPlanner.PlanGroup(group, Recipes(), Owned(), isGatherable: Gatherable);

        Assert.Equal(2, plan.Orders.Count(o => o.Planned));
        Assert.Equal(5, Assert.Single(plan.Plan!.RawMaterials, m => m.ItemId == Ore).Amount);
    }

    [Fact]
    public void UncraftableItemIsSkipped()
    {
        Assert.Equal("not craftable", OrderPlanner.Evaluate(OrderFor(Ore, 1), Recipes(), Owned()).SkipReason);
    }

    [Fact]
    public void EmptyOrdersAreSkipped()
    {
        Assert.Equal("no item", OrderPlanner.Evaluate(OrderFor(0, 1), Recipes(), Owned()).SkipReason);
        Assert.Equal("amount is zero", OrderPlanner.Evaluate(OrderFor(Sword, 0), Recipes(), Owned()).SkipReason);
    }

    [Fact]
    public void PlanGroupMergesEnabledOrdersAndReportsEveryOrder()
    {
        var disabled = OrderFor(Shield, 5);
        disabled.Enabled = false;
        var group = new OrderGroup
        {
            Name = "Weapons",
            Orders = [OrderFor(Sword, 1), OrderFor(Shield, 1), disabled],
        };

        var plan = OrderPlanner.PlanGroup(group, Recipes(), Owned());

        Assert.Same(group, plan.Group);
        Assert.False(plan.IsEmpty);
        Assert.NotNull(plan.Plan);
        Assert.Equal(5, plan.Plan.CraftSteps.Single(s => s.ItemId == Ingot).Crafts);
        Assert.Equal(2, plan.Plan.Targets.Count);

        Assert.Equal(3, plan.Orders.Count);
        Assert.Equal(2, plan.Orders.Count(o => o.Planned));
        var skipped = Assert.Single(plan.Orders, o => !o.Planned);
        Assert.Same(disabled, skipped.Order);
        Assert.Equal("disabled", skipped.SkipReason);
    }

    [Fact]
    public void PlanGroupWithNothingToDoIsEmpty()
    {
        var group = new OrderGroup
        {
            Orders = [OrderFor(Sword, 5, AmountMode.Restock), OrderFor(Ore, 1)],
        };

        var plan = OrderPlanner.PlanGroup(group, Recipes(), Owned((Sword, 5)));

        Assert.Null(plan.Plan);
        Assert.True(plan.IsEmpty);
        Assert.All(plan.Orders, o => Assert.False(o.Planned));
    }

    [Fact]
    public void PlanGroupPassesModeAndMaterialsOnlyToTheResolver()
    {
        var hqSword = OrderFor(Sword, 1);
        hqSword.Mode = ProductionMode.ForceHq;
        var shieldMaterials = OrderFor(Shield, 1);
        shieldMaterials.MaterialsOnly = true;
        var group = new OrderGroup { Orders = [hqSword, shieldMaterials] };

        var plan = OrderPlanner.PlanGroup(group, Recipes(), Owned());

        Assert.NotNull(plan.Plan);
        Assert.Equal(ProductionMode.ForceHq, plan.Plan.CraftSteps.Single(s => s.ItemId == Sword).Mode);
        Assert.DoesNotContain(plan.Plan.CraftSteps, s => s.ItemId == Shield);
        Assert.Equal(5, plan.Plan.CraftSteps.Single(s => s.ItemId == Ingot).Crafts);
    }

    [Fact]
    public void RunnableGroupsKeepsRunOrderAndSkipsDisabledOrEmptyGroups()
    {
        var disabledOrder = OrderFor(Sword, 1);
        disabledOrder.Enabled = false;
        var first = new OrderGroup { Name = "first", Orders = [OrderFor(Sword, 1)] };
        var disabledGroup = new OrderGroup { Name = "off", Enabled = false, Orders = [OrderFor(Sword, 1)] };
        var allOff = new OrderGroup { Name = "all off", Orders = [disabledOrder] };
        var empty = new OrderGroup { Name = "empty" };
        var last = new OrderGroup { Name = "last", Orders = [disabledOrder, OrderFor(Shield, 1)] };
        var book = new OrderBook { Groups = [first, disabledGroup, allOff, empty, last] };

        Assert.Equal(["first", "last"], OrderPlanner.RunnableGroups(book).Select(g => g.Name));
    }
}
