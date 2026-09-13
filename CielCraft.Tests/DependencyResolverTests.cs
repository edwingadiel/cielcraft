using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class DependencyResolverTests
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

    private const uint Final = 1;
    private const uint Ingot = 2;
    private const uint Ore = 3;
    private const uint Lumber = 4;
    private const uint Logs = 5;

    private static Func<uint, int> Owned(params (uint Id, int Count)[] entries)
    {
        var map = entries.ToDictionary(e => e.Id, e => e.Count);
        return id => map.GetValueOrDefault(id);
    }

    [Fact]
    public void CraftCountsHonorRecipeYield()
    {
        // Need 10 ingots, recipe produces 3 -> ceil(10/3) = 4 crafts (spec §20).
        var recipes = new FakeProvider()
            .Add(10, Final, 1, (Ingot, 10))
            .Add(20, Ingot, 3, (Ore, 4));

        var plan = DependencyResolver.Resolve(Final, 1, recipes, Owned());

        var ingotStep = Assert.Single(plan.CraftSteps, s => s.ItemId == Ingot);
        Assert.Equal(4, ingotStep.Crafts);
        Assert.Equal(16, Assert.Single(plan.RawMaterials, m => m.ItemId == Ore).Amount);
    }

    [Fact]
    public void InventoryIsSubtractedBeforeExpandingSubRecipes()
    {
        var recipes = new FakeProvider()
            .Add(10, Final, 1, (Ingot, 4))
            .Add(20, Ingot, 1, (Ore, 3));

        // Own 3 of the 8 ingots needed for 2 finals -> craft 5, needing 15 ore; own 4 ore -> 11 missing.
        var plan = DependencyResolver.Resolve(Final, 2, recipes, Owned((Ingot, 3), (Ore, 4)));

        Assert.Equal(5, Assert.Single(plan.CraftSteps, s => s.ItemId == Ingot).Crafts);
        Assert.Equal(11, Assert.Single(plan.RawMaterials).Amount);
    }

    [Fact]
    public void SharedStockIsConsumedOnlyOnce()
    {
        // Both intermediates consume Ore; the 5 owned Ore must not double-count.
        var recipes = new FakeProvider()
            .Add(10, Final, 1, (Ingot, 1), (Lumber, 1))
            .Add(20, Ingot, 1, (Ore, 4))
            .Add(30, Lumber, 1, (Ore, 4));

        var plan = DependencyResolver.Resolve(Final, 1, recipes, Owned((Ore, 5)));

        Assert.Equal(3, Assert.Single(plan.RawMaterials, m => m.ItemId == Ore).Amount);
    }

    [Fact]
    public void YieldSurplusFeedsLaterBranches()
    {
        // Each branch needs 2 ingots; the first craft batch (yield 3) leaves a
        // surplus the second branch reuses: total crafts = ceil(2/3) + ceil(1/3) = 2.
        var recipes = new FakeProvider()
            .Add(10, Final, 1, (Ingot, 2), (Lumber, 1))
            .Add(20, Ingot, 3, (Ore, 1))
            .Add(30, Lumber, 1, (Ingot, 2));

        var plan = DependencyResolver.Resolve(Final, 1, recipes, Owned());

        Assert.Equal(2, Assert.Single(plan.CraftSteps, s => s.ItemId == Ingot).Crafts);
    }

    [Fact]
    public void CraftStepsAreDependencyOrdered()
    {
        var recipes = new FakeProvider()
            .Add(10, Final, 1, (Ingot, 2), (Lumber, 2))
            .Add(20, Ingot, 1, (Ore, 2))
            .Add(30, Lumber, 1, (Logs, 2));

        var plan = DependencyResolver.Resolve(Final, 3, recipes, Owned());

        var order = plan.CraftSteps.Select(s => s.ItemId).ToList();
        Assert.True(order.IndexOf(Ingot) < order.IndexOf(Final));
        Assert.True(order.IndexOf(Lumber) < order.IndexOf(Final));
        Assert.Equal(Final, order[^1]);
        Assert.Equal(3, plan.CraftSteps.Single(s => s.ItemId == Final).Crafts);
    }

    [Fact]
    public void TargetItemIsNeverTakenFromInventory()
    {
        var recipes = new FakeProvider().Add(10, Final, 1, (Ore, 1));

        // Owning finished items must not reduce the requested crafts.
        var plan = DependencyResolver.Resolve(Final, 5, recipes, Owned((Final, 99)));

        Assert.Equal(5, plan.CraftSteps.Single(s => s.ItemId == Final).Crafts);
    }

    [Fact]
    public void NonCraftableTargetBecomesRawMaterial()
    {
        var plan = DependencyResolver.Resolve(Ore, 7, new FakeProvider(), Owned());

        Assert.Empty(plan.CraftSteps);
        Assert.Equal(7, Assert.Single(plan.RawMaterials).Amount);
    }
}
