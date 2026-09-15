using System.Numerics;
using CielCraft.Core;
using CielCraft.Core.Planning;
using Xunit;

namespace CielCraft.Tests;

public class PlanTreeTests
{
    private sealed class FakeProvider : IRecipeProvider
    {
        private readonly Dictionary<uint, RecipeInfo> byResult = new();

        public FakeProvider Add(uint recipeId, uint resultItem, int resultAmount, uint job, params (uint ItemId, int Amount)[] ingredients)
        {
            byResult[resultItem] = new RecipeInfo(recipeId, resultItem, resultAmount, ingredients, ClassJobId: job);
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
    private const uint Logs = 6;

    private const uint Bsm = 10;
    private const uint Crp = 11;
    private const uint Min = 16;
    private const uint Btn = 17;

    private const uint Thanalan = 140;
    private const uint Shroud = 148;

    private static FakeProvider Recipes() => new FakeProvider()
        .Add(10, Sword, 1, Bsm, (Ingot, 2), (Lumber, 1))
        .Add(11, Shield, 1, Crp, (Lumber, 3))
        .Add(20, Ingot, 3, Bsm, (Ore, 4))
        .Add(30, Lumber, 1, Crp, (Logs, 2));

    private static Func<uint, int> Owned(params (uint Id, int Count)[] entries)
    {
        var map = entries.ToDictionary(e => e.Id, e => e.Count);
        return id => map.GetValueOrDefault(id);
    }

    private static GatheringLocation? Zones(uint itemId) => itemId switch
    {
        Ore => new GatheringLocation(Ore, Min, 10, Thanalan, Vector2.Zero, 50f, []),
        Logs => new GatheringLocation(Logs, Btn, 10, Shroud, Vector2.Zero, 50f, [new EtWindow(1, 3)]),
        _ => null,
    };

    private static string Job(uint id) => id switch { Bsm => "BSM", Crp => "CRP", Min => "MIN", Btn => "BTN", _ => $"job {id}" };

    private static string Zone(uint id) => id switch { Thanalan => "Central Thanalan", Shroud => "Central Shroud", _ => $"zone {id}" };

    private static PlanTree Build(ProductionPlan plan, Func<uint, int> owned, Func<uint, int>? hq = null) =>
        PlanTree.Build(plan, Recipes(), owned, hq ?? (_ => 0), Zones);

    [Fact]
    public void TargetExpandsToSubCraftsAndRawLeaves()
    {
        var owned = Owned();
        var plan = DependencyResolver.Resolve(Sword, 1, Recipes(), owned);
        var tree = Build(plan, owned);

        var sword = Assert.Single(tree.Targets);
        Assert.True(sword.IsTarget);
        Assert.Equal(1, sword.Crafts);
        Assert.Equal(Bsm, sword.JobId);
        Assert.Equal(2, sword.Children.Count);

        var ingot = sword.Children[0];
        Assert.Equal(Ingot, ingot.ItemId);
        Assert.Equal(2, ingot.Need);
        Assert.Equal(1, ingot.Crafts);   // ceil(2 / 3)
        Assert.Equal(3, ingot.Yield);
        Assert.Equal(0, ingot.StepIndex);

        var ore = Assert.Single(ingot.Children);
        Assert.Equal(4, ore.Need);
        Assert.Equal(4, ore.Missing);
        Assert.Equal(-1, ore.StepIndex);
        Assert.Equal(Min, ore.JobId);
        Assert.Equal(Thanalan, ore.TerritoryId);

        var logs = Assert.Single(sword.Children[1].Children);
        Assert.True(logs.Timed);
    }

    [Fact]
    public void StockIsConsumedOnceAcrossBranchesAndMatchesThePlan()
    {
        // Sword needs 1 lumber, Shield 3; the 2 owned lumber go to the sword's
        // branch first, the shield still crafts 2 — exactly what the plan says.
        var owned = Owned((Lumber, 2));
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1), new PlanTarget(Shield, 1)], Recipes(), owned);
        var tree = Build(plan, owned);

        var swordLumber = tree.Targets[0].Children[1];
        Assert.Equal(1, swordLumber.FromStock);
        Assert.Equal(0, swordLumber.Crafts);
        Assert.Equal(0, swordLumber.Missing);
        Assert.Empty(swordLumber.Children);

        var shieldLumber = Assert.Single(tree.Targets[1].Children);
        Assert.Equal(1, shieldLumber.FromStock);
        Assert.Equal(2, shieldLumber.Crafts);
        Assert.Equal(2, shieldLumber.Owned);

        var lumberStep = Assert.Single(plan.CraftSteps, s => s.ItemId == Lumber);
        Assert.Equal(lumberStep.Crafts, swordLumber.Crafts + shieldLumber.Crafts);
        Assert.Equal(
            Assert.Single(plan.RawMaterials, m => m.ItemId == Logs).Amount,
            tree.AllNodes().Where(n => n.ItemId == Logs).Sum(n => n.Missing));
    }

    [Fact]
    public void SharedStepShowsPerBranchCraftsAndTheStepTotal()
    {
        var owned = Owned();
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1), new PlanTarget(Shield, 1)], Recipes(), owned);
        var tree = Build(plan, owned);

        var swordLumber = tree.Targets[0].Children[1];
        var shieldLumber = Assert.Single(tree.Targets[1].Children);
        Assert.Equal(1, swordLumber.Crafts);
        Assert.Equal(3, shieldLumber.Crafts);
        Assert.Equal(4, swordLumber.StepCrafts);
        Assert.True(swordLumber.SharedStep);
        Assert.True(shieldLumber.SharedStep);
        Assert.Equal(swordLumber.StepIndex, shieldLumber.StepIndex);
    }

    [Fact]
    public void YieldSurplusFeedsLaterBranches()
    {
        // Sword ×1 needs 2 ingots (1 craft of 3 → 1 spare); Sword ×2 as a
        // second target needs 4: 1 from the surplus, one more craft.
        var owned = Owned();
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1), new PlanTarget(Sword, 2)], Recipes(), owned);
        var tree = Build(plan, owned);

        var second = tree.Targets[1].Children[0];
        Assert.Equal(1, second.FromStock);
        Assert.Equal(1, second.Crafts);
        Assert.Equal(Assert.Single(plan.CraftSteps, s => s.ItemId == Ingot).Crafts, tree.Targets[0].Children[0].Crafts + second.Crafts);
    }

    [Fact]
    public void MaterialsOnlyTargetHasNoStepButKeepsItsIngredients()
    {
        var owned = Owned();
        var plan = DependencyResolver.Resolve([new PlanTarget(Sword, 2, MaterialsOnly: true)], Recipes(), owned);
        var tree = Build(plan, owned);

        var sword = Assert.Single(tree.Targets);
        Assert.True(sword.MaterialsOnly);
        Assert.Equal(-1, sword.StepIndex);
        Assert.Equal(2, sword.Crafts);
        Assert.Equal(4, sword.Children[0].Need);   // 2 crafts × 2 ingots
        Assert.DoesNotContain(plan.CraftSteps, s => s.ItemId == Sword);
    }

    [Fact]
    public void GatherOnlyTargetIsARawLeaf()
    {
        var owned = Owned((Ore, 3));
        var plan = DependencyResolver.Resolve([new PlanTarget(Ore, 10, Kind: OrderKind.Gather)], Recipes(), owned);
        var tree = Build(plan, owned);

        var ore = Assert.Single(tree.Targets);
        Assert.True(ore.IsTarget);
        Assert.False(ore.IsCraft);
        Assert.Equal(10, ore.Missing);   // the target's own stock is reserved, not consumed
        Assert.Equal(Thanalan, ore.TerritoryId);
    }

    [Fact]
    public void TreeFollowsThePlanNotTheProviderForRawDecisions()
    {
        // The plan treats Ingot as raw (a locked book, 7.16) although the
        // provider knows a recipe: the tree must not expand it.
        var plan = new ProductionPlan(Sword, 1, [new PlannedCraft(10, Sword, 1, 1)], [new MissingMaterial(Ingot, 2), new MissingMaterial(Lumber, 1)]);
        var tree = Build(plan, Owned());

        var ingot = tree.Targets[0].Children[0];
        Assert.Equal(-1, ingot.StepIndex);
        Assert.Equal(2, ingot.Missing);
        Assert.Empty(ingot.Children);
    }

    [Fact]
    public void RollupsGroupByZoneAndJobFromThePlanTotals()
    {
        var owned = Owned();
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1), new PlanTarget(Shield, 1)], Recipes(), owned);
        var tree = Build(plan, owned);

        Assert.Equal(2, tree.Zones.Count);
        var thanalan = Assert.Single(tree.Zones, z => z.TerritoryId == Thanalan);
        var ore = Assert.Single(thanalan.Items);
        Assert.Equal(4, ore.Amount);
        Assert.Equal(Min, ore.JobId);
        var shroud = Assert.Single(tree.Zones, z => z.TerritoryId == Shroud);
        Assert.True(Assert.Single(shroud.Items).Timed);
        Assert.Equal(8, shroud.TotalAmount);   // 4 lumber × 2 logs

        var bsm = Assert.Single(tree.Jobs, j => j.JobId == Bsm);
        Assert.Equal(2, bsm.Steps);            // sword + ingot
        Assert.Equal(2, bsm.Crafts);
        var crp = Assert.Single(tree.Jobs, j => j.JobId == Crp);
        Assert.Equal(5, crp.Crafts);           // 4 lumber + shield
        Assert.Equal(plan.CraftSteps.Sum(s => s.Crafts), tree.TotalCrafts);
    }

    [Fact]
    public void UnknownZoneIsListedLast()
    {
        var plan = new ProductionPlan(Sword, 1, [], [new MissingMaterial(Ingot, 1), new MissingMaterial(Ore, 1)]);
        var tree = Build(plan, Owned());

        Assert.Equal([Thanalan, 0u], tree.Zones.Select(z => z.TerritoryId).ToArray());
    }

    [Fact]
    public void HqStockIsAttributedToTheCraftThatConsumesIt()
    {
        // 5 lumber owned, 3 of them HQ: the sword takes 1 (HQ), the shield 3
        // (2 HQ + 1 NQ); none is left to craft.
        var owned = Owned((Lumber, 5));
        var hq = Owned((Lumber, 3));
        var plan = DependencyResolver.Resolve(
            [new PlanTarget(Sword, 1), new PlanTarget(Shield, 1)], Recipes(), owned);
        var tree = Build(plan, owned, hq);

        Assert.Equal(1, tree.Targets[0].Children[1].HqFromStock);
        Assert.Equal(2, Assert.Single(tree.Targets[1].Children).HqFromStock);
        Assert.Equal(2, tree.HqMaterials.Count);
        Assert.Contains(tree.HqMaterials, u => u.ItemId == Lumber && u.ConsumerItemId == Sword && u.Amount == 1);
        Assert.Contains(tree.HqMaterials, u => u.ItemId == Lumber && u.ConsumerItemId == Shield && u.Amount == 2);
    }

    [Fact]
    public void RenderListsTreeAndRollupsWithNames()
    {
        var owned = Owned();
        var plan = DependencyResolver.Resolve(Sword, 1, Recipes(), owned);
        var lines = Build(plan, owned).RenderLines(Recipes(), Job, Zone).ToList();

        Assert.StartsWith("Plan: 1 target(s); 3 craft step(s), 3 craft(s); 2 raw material(s)", lines[0]);
        Assert.Equal("Item1 ×1: 1 craft(s) × 1 = 1 [BSM]", lines[1]);
        Assert.Equal("  Item3 ×2: 1 craft(s) × 3 = 3 [BSM]", lines[2]);
        Assert.Equal("    Item4 ×4: need 4, owned 0, missing 4 [MIN] Central Thanalan", lines[3]);
        Assert.Equal("  Item5 ×1: 1 craft(s) × 1 = 1 [CRP]", lines[4]);
        Assert.Equal("    Item6 ×2: need 2, owned 0, missing 2 [BTN] Central Shroud (timed)", lines[5]);
        Assert.Contains("Gathering by zone:", lines);
        Assert.Contains("  Central Thanalan: Item4 ×4 (MIN)", lines);
        Assert.Contains("  Central Shroud: Item6 ×2 (BTN, timed)", lines);
        Assert.Contains("Crafts by job:", lines);
        Assert.Contains("  BSM: 2 craft(s) in 2 step(s)", lines);
        Assert.Contains("  CRP: 1 craft(s) in 1 step(s)", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("HQ materials"));
    }

    [Fact]
    public void ProgressMarksDoneInFlightAndPendingNodes()
    {
        var owned = Owned();
        var plan = DependencyResolver.Resolve(Sword, 1, Recipes(), owned);
        var tree = Build(plan, owned);

        // Gathering: raws in flight, every step pending.
        var gathering = new PlanProgress(0, 0, 0, Gathering: true, GatheringDone: false);
        Assert.Equal("▶", PlanTree.Mark(tree.Targets[0].Children[0].Children[0], gathering));
        Assert.Equal("·", PlanTree.Mark(tree.Targets[0].Children[0], gathering));

        // Step 2 of 3 in flight: step 1 done, raws done, the target pending.
        var crafting = new PlanProgress(1, 2, 3, Gathering: false, GatheringDone: true);
        var lines = tree.RenderLines(Recipes(), Job, Zone, crafting).ToList();
        Assert.Contains("step 2/3 in flight", lines[0]);
        Assert.StartsWith("· Item1", lines[1]);
        Assert.StartsWith("  ✓ Item3", lines[2]);
        Assert.StartsWith("    ✓ Item4", lines[3]);
        Assert.StartsWith("  ▶ 2/3 Item5", lines[4]);

        // A materials-only target is done when all of its branches are.
        var materials = Build(DependencyResolver.Resolve([new PlanTarget(Sword, 1, MaterialsOnly: true)], Recipes(), owned), owned);
        Assert.Equal("✓", PlanTree.Mark(materials.Targets[0], new PlanProgress(2, 0, 0, false, true)));
        Assert.Equal("▶", PlanTree.Mark(materials.Targets[0], new PlanProgress(0, 0, 0, false, true)));
        Assert.Equal("·", PlanTree.Mark(materials.Targets[0], new PlanProgress(0, 0, 0, false, false)));
    }
}
