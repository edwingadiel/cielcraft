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

/// <summary>Several targets planned as one graph (roadmap 7.13).</summary>
public class MultiTargetResolverTests
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
    private const uint Lumber = 5;

    private static Func<uint, int> Owned(params (uint Id, int Count)[] entries)
    {
        var map = entries.ToDictionary(e => e.Id, e => e.Count);
        return id => map.GetValueOrDefault(id);
    }

    /// <summary>Sword needs 2 ingots, Shield needs 3; ingots come from ore 1:1.</summary>
    private static FakeProvider TwoFinals() => new FakeProvider()
        .Add(10, Sword, 1, (Ingot, 2))
        .Add(11, Shield, 1, (Ingot, 3))
        .Add(20, Ingot, 1, (Ore, 1));

    [Fact]
    public void SharedIntermediateMergesIntoOneStep()
    {
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1), new PlanTarget(Shield, 1)], TwoFinals(), Owned());

        var ingot = Assert.Single(plan.CraftSteps, s => s.ItemId == Ingot);
        Assert.Equal(5, ingot.Crafts);
        Assert.Equal(5, Assert.Single(plan.RawMaterials).Amount);

        // Ingots are listed before either final; both finals are steps.
        var order = plan.CraftSteps.Select(s => s.ItemId).ToList();
        Assert.True(order.IndexOf(Ingot) < order.IndexOf(Sword));
        Assert.True(order.IndexOf(Ingot) < order.IndexOf(Shield));
        Assert.Equal(2, plan.Targets.Count);
        Assert.Equal(Sword, plan.TargetItemId);
    }

    [Fact]
    public void StockIsConsumedOnceAcrossTargets()
    {
        // Both finals need ore through ingots; 4 owned ore covers 4 of the 5 ingots, once.
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1), new PlanTarget(Shield, 1)], TwoFinals(), Owned((Ore, 4)));

        Assert.Equal(1, Assert.Single(plan.RawMaterials, m => m.ItemId == Ore).Amount);
        Assert.Equal(5, plan.CraftSteps.Single(s => s.ItemId == Ingot).Crafts);
    }

    [Fact]
    public void SurplusFromFirstTargetFeedsSecond()
    {
        // Ingot yields 3: ordering 2 crafts one batch with 1 spare, which the
        // sword's single ingot takes instead of a second batch.
        var recipes = new FakeProvider()
            .Add(10, Sword, 1, (Ingot, 1))
            .Add(20, Ingot, 3, (Ore, 1));

        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Ingot, 2), new PlanTarget(Sword, 1)], recipes, Owned());

        Assert.Equal(1, plan.CraftSteps.Single(s => s.ItemId == Ingot).Crafts);
        Assert.Equal(1, Assert.Single(plan.RawMaterials).Amount);
    }

    [Fact]
    public void MaterialsOnlyExpandsIngredientsWithoutTheTargetStep()
    {
        var recipes = new FakeProvider()
            .Add(10, Sword, 1, (Ingot, 2), (Lumber, 1))
            .Add(20, Ingot, 1, (Ore, 2));

        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 2, MaterialsOnly: true)], recipes, Owned());

        Assert.DoesNotContain(plan.CraftSteps, s => s.ItemId == Sword);
        Assert.Equal(4, Assert.Single(plan.CraftSteps).Crafts);
        Assert.Equal(8, plan.RawMaterials.Single(m => m.ItemId == Ore).Amount);
        Assert.Equal(2, plan.RawMaterials.Single(m => m.ItemId == Lumber).Amount);
    }

    [Fact]
    public void MaterialsOnlyUsesOwnedIngredients()
    {
        var recipes = new FakeProvider()
            .Add(10, Sword, 1, (Ingot, 2))
            .Add(20, Ingot, 1, (Ore, 1));

        // Materials for 1 sword = 2 ingots; owning 2 leaves nothing to do.
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1, MaterialsOnly: true)], recipes, Owned((Ingot, 2)));

        Assert.Empty(plan.CraftSteps);
        Assert.Empty(plan.RawMaterials);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModeLandsOnTargetStepAndSurvivesBeingAnIntermediate(bool ingotFirst)
    {
        var ingotOrder = new PlanTarget(Ingot, 2, ProductionMode.ForceHq);
        var swordOrder = new PlanTarget(Sword, 1, ProductionMode.QuickSynth);
        PlanTarget[] targets = ingotFirst ? [ingotOrder, swordOrder] : [swordOrder, ingotOrder];

        var plan = DependencyResolver.Resolve(targets, TwoFinals(), Owned());

        var ingot = plan.CraftSteps.Single(s => s.ItemId == Ingot);
        Assert.Equal(ProductionMode.ForceHq, ingot.Mode);
        Assert.Equal(4, ingot.Crafts);
        Assert.Equal(ProductionMode.QuickSynth, plan.CraftSteps.Single(s => s.ItemId == Sword).Mode);
        Assert.Equal(Ingot, plan.CraftSteps[0].ItemId);
    }

    [Fact]
    public void IntermediatesKeepModeAny()
    {
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1, ProductionMode.ForceHq)], TwoFinals(), Owned());

        Assert.Equal(ProductionMode.Any, plan.CraftSteps.Single(s => s.ItemId == Ingot).Mode);
        Assert.Equal(ProductionMode.ForceHq, plan.CraftSteps.Single(s => s.ItemId == Sword).Mode);
    }

    [Fact]
    public void SameItemOrderedTwiceSharesOneStepAndKeepsTheStricterMode()
    {
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Ingot, 2), new PlanTarget(Ingot, 3, ProductionMode.ForceHq)], TwoFinals(), Owned());

        var ingot = Assert.Single(plan.CraftSteps);
        Assert.Equal(5, ingot.Crafts);
        Assert.Equal(ProductionMode.ForceHq, ingot.Mode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnedStockOfATargetItemIsNotSpentByASibling(bool ingotFirst)
    {
        // Restock "ingots to 5" with 3 owned plans 2; the sword's 2 ingots must
        // be crafted too, otherwise the bag ends at 3 instead of 5.
        var ingotOrder = new PlanTarget(Ingot, 2);
        var swordOrder = new PlanTarget(Sword, 1);
        PlanTarget[] targets = ingotFirst ? [ingotOrder, swordOrder] : [swordOrder, ingotOrder];

        var plan = DependencyResolver.Resolve(targets, TwoFinals(), Owned((Ingot, 3)));

        Assert.Equal(4, plan.CraftSteps.Single(s => s.ItemId == Ingot).Crafts);
        Assert.Equal(4, Assert.Single(plan.RawMaterials).Amount);
    }

    [Fact]
    public void DepthGuardTurnsDeepChainsIntoRawMaterials()
    {
        // A 12-deep chain: the resolver stops expanding at MaxDepth (10) and
        // reports the rest as raw instead of recursing forever.
        var recipes = new FakeProvider();
        for (uint i = 100; i < 112; i++)
            recipes.Add(i, i, 1, (i + 1, 1));

        var plan = DependencyResolver.Resolve([new PlanTarget(100, 1)], recipes, Owned());

        Assert.Equal(10, plan.CraftSteps.Count);
        Assert.Equal(110u, Assert.Single(plan.RawMaterials).ItemId);
    }

    // Gather targets (roadmap 7.1): raw materials of the plan, never crafted, never taken from stock.

    [Fact]
    public void GatherTargetIsARawMaterialWithoutACraftStep()
    {
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Ore, 7, Kind: OrderKind.Gather)], TwoFinals(), Owned((Ore, 100)));

        Assert.Empty(plan.CraftSteps);
        var ore = Assert.Single(plan.RawMaterials);
        Assert.Equal(Ore, ore.ItemId);
        Assert.Equal(7, ore.Amount); // owned stock is not a substitute for gathering
        Assert.Equal(OrderKind.Gather, Assert.Single(plan.Targets).Kind);
    }

    [Fact]
    public void GatherTargetOfACraftableItemIsNotExpanded()
    {
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Ingot, 3, Kind: OrderKind.Gather)], TwoFinals(), Owned());

        Assert.Empty(plan.CraftSteps);
        Assert.Equal(3, Assert.Single(plan.RawMaterials, m => m.ItemId == Ingot).Amount);
    }

    [Fact]
    public void GatherTargetMergesWithACraftsRawNeedIntoOneTrip()
    {
        // The sword needs 2 ore through its ingots; the gather order adds 5 more of the same ore.
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1), new PlanTarget(Ore, 5, Kind: OrderKind.Gather)], TwoFinals(), Owned());

        Assert.Equal(7, Assert.Single(plan.RawMaterials, m => m.ItemId == Ore).Amount);
        Assert.Equal(2, plan.CraftSteps.Count);
    }

    [Fact]
    public void CollectableGatherTargetKeepsItsTierOnTheTarget()
    {
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Ore, 2, ProductionMode.Collectable, Kind: OrderKind.Gather, CollectableTier: CollectableTier.Mid)],
            TwoFinals(), Owned());

        var target = Assert.Single(plan.Targets);
        Assert.Equal(ProductionMode.Collectable, target.Mode);
        Assert.Equal(CollectableTier.Mid, target.CollectableTier);
        Assert.Equal(2, Assert.Single(plan.RawMaterials).Amount);
    }
}
