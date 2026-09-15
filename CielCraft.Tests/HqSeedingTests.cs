using CielCraft.Core;
using CielCraft.Core.Crafting;
using CielCraft.Core.Planning;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// The "reachable from zero?" pass over resolver-built plans (roadmap 7.22):
/// which intermediate steps get HqCrafts, and how many.
/// </summary>
public class HqSeedingTests
{
    private sealed class FakeProvider : IRecipeProvider
    {
        private readonly Dictionary<uint, RecipeInfo> byResult = new();

        /// <summary>Adds a recipe with MQF 50; each ingredient carries (id, amount, ilvl, HQ-able).</summary>
        public FakeProvider Add(uint recipeId, uint resultItem, int resultAmount, params (uint ItemId, int Amount, int ItemLevel, bool CanBeHq)[] ingredients)
        {
            byResult[resultItem] = new RecipeInfo(
                recipeId, resultItem, resultAmount,
                ingredients.Select(i => (i.ItemId, i.Amount)).ToList(),
                MaterialQualityFactor: 50,
                Materials: ingredients.Select(i => new MaterialInfo(i.ItemId, i.Amount, i.ItemLevel, i.CanBeHq)).ToList());
            return this;
        }

        public RecipeInfo? FindRecipeForItem(uint itemId) => byResult.GetValueOrDefault(itemId);

        public RecipeInfo? GetRecipeById(uint recipeId) => byResult.Values.FirstOrDefault(r => r.RecipeId == recipeId);

        public string GetItemName(uint itemId) => $"Item{itemId}";
    }

    /// <summary>An oracle scripted per recipe: the quality reached from zero, plus the initial quality (additive).</summary>
    private sealed class Oracle
    {
        private readonly Dictionary<uint, int> fromZero = new();
        public readonly List<(uint RecipeId, int Initial, int Target)> Calls = [];

        public Oracle Reaches(uint recipeId, int quality)
        {
            fromZero[recipeId] = quality;
            return this;
        }

        public int? Answer(uint recipeId, int initial, int target)
        {
            Calls.Add((recipeId, initial, target));
            return fromZero.TryGetValue(recipeId, out var reached) ? reached + initial : null;
        }
    }

    private const uint Rivets = 1;
    private const uint Ingot = 2;
    private const uint Ore = 3;
    private const uint Shard = 4;
    private const uint Nugget = 5;
    private const uint Cloth = 6;

    private const uint RivetsRecipe = 10;
    private const uint IngotRecipe = 20;
    private const uint NuggetRecipe = 30;

    private const int MaxQuality = 360;

    private static Func<uint, int> Owned(params (uint Id, int Count)[] entries)
    {
        var map = entries.ToDictionary(e => e.Id, e => e.Count);
        return id => map.GetValueOrDefault(id);
    }

    /// <summary>Rivets ← Ingot (crafted, ilvl 14) + Shard; Ingot ← Ore (gathered).</summary>
    private static FakeProvider Recipes(int ingotYield = 1, int ingotsPerRivet = 1) => new FakeProvider()
        .Add(RivetsRecipe, Rivets, 1, (Ingot, ingotsPerRivet, 14, true), (Shard, 1, 1, false))
        .Add(IngotRecipe, Ingot, ingotYield, (Ore, 3, 10, false), (Shard, 1, 1, false));

    private static ProductionPlan Plan(IRecipeProvider recipes, int quantity, ProductionMode mode = ProductionMode.Any, Func<uint, int>? owned = null) =>
        DependencyResolver.Resolve([new PlanTarget(Rivets, quantity, mode)], recipes, owned ?? Owned());

    /// <summary>Every target wants the full quality; a seeded intermediate wants 100% too.</summary>
    private static HqSeedRequest? WantsFull(PlannedCraft step, bool isTarget) => new(MaxQuality, MaxQuality);

    private static PlannedCraft Step(ProductionPlan plan, uint itemId) => plan.CraftSteps.Single(s => s.ItemId == itemId);

    [Fact]
    public void SeedsTheCraftedIntermediateWhenTheTargetIsShortFromZero()
    {
        var recipes = Recipes();
        var plan = Plan(recipes, 3);
        // Rivets reach 200/360 from NQ; an HQ ingot adds 180 (14/14 of the HQ-able weight).
        var oracle = new Oracle().Reaches(RivetsRecipe, 200).Reaches(IngotRecipe, MaxQuality);
        var log = new ListLog();

        var seeded = HqSeeding.Apply(plan, recipes, WantsFull, oracle.Answer, log);

        Assert.Equal(3, Step(seeded, Ingot).HqCrafts);
        Assert.Equal(3, Step(seeded, Ingot).Crafts);
        Assert.Equal(0, Step(seeded, Rivets).HqCrafts);
        Assert.Contains(log.Lines, l => l.Contains("seeding 180 initial quality") && l.Contains("3 HQ of 3 crafts"));
        // Solved from zero, then confirmed at the mix's initial quality.
        Assert.Contains((RivetsRecipe, 0, MaxQuality), oracle.Calls);
        Assert.Contains((RivetsRecipe, 180, MaxQuality), oracle.Calls);
        // The raw materials and the original plan are untouched.
        Assert.Equal(plan.RawMaterials, seeded.RawMaterials);
        Assert.All(plan.CraftSteps, s => Assert.Equal(0, s.HqCrafts));
    }

    [Fact]
    public void LeavesThePlanAloneWhenReachableFromZero()
    {
        var recipes = Recipes();
        var plan = Plan(recipes, 3);
        var oracle = new Oracle().Reaches(RivetsRecipe, MaxQuality);
        var log = new ListLog();

        var result = HqSeeding.Apply(plan, recipes, WantsFull, oracle.Answer, log);

        Assert.Same(plan, result);
        Assert.Contains(log.Lines, l => l.Contains("no HQ materials needed"));
        Assert.Single(oracle.Calls);
    }

    [Fact]
    public void OnlyCraftedIntermediatesCanBeSeeded()
    {
        // Ingots come from the bag: nothing to mark, so not even a solve is spent.
        var recipes = Recipes();
        var plan = Plan(recipes, 3, owned: Owned((Ingot, 10)));
        Assert.DoesNotContain(plan.CraftSteps, s => s.ItemId == Ingot);
        var oracle = new Oracle().Reaches(RivetsRecipe, 100);

        var result = HqSeeding.Apply(plan, recipes, WantsFull, oracle.Answer, new ListLog());

        Assert.Same(plan, result);
        Assert.Empty(oracle.Calls);
    }

    [Fact]
    public void StepsThatDoNotWantQualityAreSkipped()
    {
        var recipes = Recipes();
        var plan = Plan(recipes, 3, ProductionMode.QuickSynth);
        var oracle = new Oracle().Reaches(RivetsRecipe, 100);

        var result = HqSeeding.Apply(plan, recipes, (_, _) => null, oracle.Answer, new ListLog());

        Assert.Same(plan, result);
        Assert.Empty(oracle.Calls);
    }

    [Fact]
    public void RoundsUpToWholeCraftsOfTheIntermediate()
    {
        // Ingots come 3 per craft; 2 rivets need 2 HQ ingots → 1 HQ craft.
        var recipes = Recipes(ingotYield: 3);
        var plan = Plan(recipes, 2);
        Assert.Equal(1, Step(plan, Ingot).Crafts);
        var oracle = new Oracle().Reaches(RivetsRecipe, 200).Reaches(IngotRecipe, MaxQuality);

        var seeded = HqSeeding.Apply(plan, recipes, WantsFull, oracle.Answer, new ListLog());

        Assert.Equal(1, Step(seeded, Ingot).HqCrafts);
    }

    [Fact]
    public void SeedsWholeIngredientsForABatchButKeepsAPartialMixForASingleCraft()
    {
        // Two ingots per rivet; one HQ ingot (90) would do for a target of 290,
        // but the crafting log's HQ fill spends HQ slot by slot, so a batch of
        // 3 seeds both ingots of every craft: 6 HQ of 6.
        var recipes = Recipes(ingotsPerRivet: 2);
        var oracle = new Oracle().Reaches(RivetsRecipe, 200).Reaches(IngotRecipe, MaxQuality);
        HqSeedRequest Target(PlannedCraft step, bool isTarget) => new(MaxQuality, isTarget ? 290 : MaxQuality);

        var batch = HqSeeding.Apply(Plan(recipes, 3), recipes, Target, oracle.Answer, new ListLog());
        Assert.Equal(6, Step(batch, Ingot).HqCrafts);
        Assert.Contains((RivetsRecipe, 180, 290), oracle.Calls);

        var single = HqSeeding.Apply(Plan(recipes, 1), recipes, Target, oracle.Answer, new ListLog());
        Assert.Equal(1, Step(single, Ingot).HqCrafts);
        Assert.Equal(2, Step(single, Ingot).Crafts);
        Assert.Contains((RivetsRecipe, 90, 290), oracle.Calls);
    }

    [Fact]
    public void RaisesTheMixWhenTheConfirmSolveFallsShort()
    {
        // Two HQ-able crafted ingredients: Cloth (ilvl 30) and Ingot (ilvl 10).
        var recipes = new FakeProvider()
            .Add(RivetsRecipe, Rivets, 1, (Cloth, 1, 30, true), (Ingot, 2, 10, true))
            .Add(IngotRecipe, Ingot, 1, (Ore, 1, 10, false))
            .Add(NuggetRecipe, Cloth, 1, (Ore, 1, 10, false));
        var plan = DependencyResolver.Resolve([new PlanTarget(Rivets, 1)], recipes, Owned());
        // From zero the rotation reaches 700 of 1000; the first mix (Cloth: 300)
        // gets confirmed at only 950 because the rotation from 300 is worse
        // than "700 + 300" — the oracle scripts that dip.
        var calls = new List<(uint, int, int)>();
        int? Answer(uint recipeId, int initial, int target)
        {
            calls.Add((recipeId, initial, target));
            if (recipeId != RivetsRecipe)
                return 1000;
            return initial switch { 0 => 700, 300 => 950, _ => 700 + initial };
        }

        var seeded = HqSeeding.Apply(plan, recipes, (_, _) => new HqSeedRequest(1000, 1000), Answer, new ListLog());

        // Second round asks for 300 + 50 → Cloth + one Ingot = 400.
        Assert.Contains((RivetsRecipe, 300, 1000), calls);
        Assert.Contains((RivetsRecipe, 400, 1000), calls);
        Assert.Equal(1, Step(seeded, Cloth).HqCrafts);
        Assert.Equal(1, Step(seeded, Ingot).HqCrafts);
    }

    [Fact]
    public void KeepsQuickWhenNoMixReachesTheTarget()
    {
        var recipes = Recipes();
        var plan = Plan(recipes, 3);
        // 100 from zero + at most 180 from the ingot never reaches 360.
        var oracle = new Oracle().Reaches(RivetsRecipe, 100);
        var log = new ListLog();

        var result = HqSeeding.Apply(plan, recipes, WantsFull, oracle.Answer, log);

        Assert.Same(plan, result);
        Assert.Contains(log.Lines, l => l.Contains("no HQ mix"));
    }

    [Fact]
    public void KeepsQuickWithoutARotationAnswer()
    {
        var recipes = Recipes();
        var plan = Plan(recipes, 3);
        var log = new ListLog();

        var result = HqSeeding.Apply(plan, recipes, WantsFull, (_, _, _) => null, log);

        Assert.Same(plan, result);
        Assert.Contains(log.Lines, l => l.Contains("no rotation answer"));
    }

    [Fact]
    public void ASeededIntermediateThatCannotReachFullQualitySeedsItsOwnIngredients()
    {
        // Rivets ← Ingot ← Nugget, all crafted and HQ-able.
        var recipes = new FakeProvider()
            .Add(RivetsRecipe, Rivets, 1, (Ingot, 1, 14, true), (Shard, 1, 1, false))
            .Add(IngotRecipe, Ingot, 1, (Nugget, 1, 12, true), (Shard, 1, 1, false))
            .Add(NuggetRecipe, Nugget, 1, (Ore, 2, 10, false));
        var plan = DependencyResolver.Resolve([new PlanTarget(Rivets, 2)], recipes, Owned());
        var oracle = new Oracle().Reaches(RivetsRecipe, 200).Reaches(IngotRecipe, 200).Reaches(NuggetRecipe, MaxQuality);

        var seeded = HqSeeding.Apply(plan, recipes, WantsFull, oracle.Answer, new ListLog());

        Assert.Equal(2, Step(seeded, Ingot).HqCrafts);
        Assert.Equal(2, Step(seeded, Nugget).HqCrafts);
        // The intermediate was checked for 100% of its own quality.
        Assert.Contains((IngotRecipe, 0, MaxQuality), oracle.Calls);
    }

    [Fact]
    public void CapacityIsLimitedByWhatTheIntermediateStepProduces()
    {
        // Two owned NQ ingots leave one craft of ingots for three rivets: no
        // per-craft HQ ingot for every rivet, so the mix is refused.
        var recipes = Recipes();
        var plan = Plan(recipes, 3, owned: Owned((Ingot, 2)));
        Assert.Equal(1, Step(plan, Ingot).Crafts);
        var oracle = new Oracle().Reaches(RivetsRecipe, 200).Reaches(IngotRecipe, MaxQuality);
        var log = new ListLog();

        var result = HqSeeding.Apply(plan, recipes, WantsFull, oracle.Answer, log);

        Assert.Same(plan, result);
        Assert.Contains(log.Lines, l => l.Contains("no HQ mix"));
    }

    [Fact]
    public void PlanTreeShowsTheHqCrafts()
    {
        var recipes = Recipes();
        var plan = Plan(recipes, 3);
        var oracle = new Oracle().Reaches(RivetsRecipe, 200).Reaches(IngotRecipe, MaxQuality);
        var seeded = HqSeeding.Apply(plan, recipes, WantsFull, oracle.Answer, new ListLog());

        var tree = PlanTree.Build(seeded, recipes, Owned(), Owned(), _ => null);
        var text = tree.Render(recipes, job => $"job{job}", zone => $"zone{zone}");

        Assert.Equal(3, tree.TotalHqCrafts);
        Assert.Contains("(3 HQ intermediate craft(s))", text);
        Assert.Contains("Item2 ×3: 3 craft(s) × 1 = 3; 3 HQ of 3 crafts", text);
        Assert.DoesNotContain("Item1 ×3: 3 craft(s) × 1 = 3;", text);
    }
}
