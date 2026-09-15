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

        public RecipeInfo? FindRecipeForItem(uint itemId) => byResult.GetValueOrDefault(itemId);

        public RecipeInfo? GetRecipeById(uint recipeId) => byResult.Values.FirstOrDefault(r => r.RecipeId == recipeId);

        public string GetItemName(uint itemId) => $"Item{itemId}";
    }

    private const uint Sword = 1;
    private const uint Shield = 2;
    private const uint Ingot = 3;
    private const uint Ore = 4;

    private static FakeProvider Recipes() => new FakeProvider()
        .Add(10, Sword, 1, (Ingot, 2))
        .Add(11, Shield, 1, (Ingot, 3))
        .Add(20, Ingot, 1, (Ore, 1));

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
    public void CollectableModeIsRefused()
    {
        var order = OrderFor(Sword, 1);
        order.Mode = ProductionMode.Collectable;

        var (quantity, skip) = OrderPlanner.Evaluate(order, Recipes(), Owned());
        Assert.Equal(0, quantity);
        Assert.Contains("7.23", skip);
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
