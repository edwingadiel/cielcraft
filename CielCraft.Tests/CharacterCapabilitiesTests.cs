using System.Numerics;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

/// <summary>Core-side capability decisions (roadmap 7.16) with fake data.</summary>
public class CharacterCapabilitiesTests
{
    private const uint FlyZone = 100;
    private const uint WalkZone = 200;
    private const uint OpenBook = 5;
    private const uint LockedBook = 6;
    private const uint Crp = 8;
    private const uint Bsm = 9;

    private static CharacterCapabilities Known(params uint[] flightZones) => new(
        true,
        new HashSet<uint>(flightZones),
        new HashSet<uint> { OpenBook },
        new Dictionary<uint, int>(),
        [],
        new Dictionary<uint, int> { [Crp] = 90 },
        DateTime.UtcNow);

    private static RecipeInfo Recipe(uint id, uint job, uint book = 0) =>
        new(id, ResultItemId: 1, ResultAmount: 1, Ingredients: [], ClassJobId: job, SecretRecipeBookId: book);

    private static GatheringLocation Location(uint territory, byte level, bool timed = false) =>
        new(1, GatheringActions.MinerJobId, level, territory, Vector2.Zero, 10f,
            timed ? [new EtWindow(0, 120)] : []);

    private sealed class FakeProvider : IRecipeProvider
    {
        private readonly Dictionary<uint, RecipeInfo> byResult = new();

        public FakeProvider Add(uint recipeId, uint resultItem, uint book, params (uint ItemId, int Amount)[] ingredients)
        {
            byResult[resultItem] = new RecipeInfo(recipeId, resultItem, 1, ingredients, SecretRecipeBookId: book);
            return this;
        }

        public RecipeInfo? FindRecipeForItem(uint itemId) => byResult.GetValueOrDefault(itemId);

        public RecipeInfo? GetRecipeById(uint recipeId) => byResult.Values.FirstOrDefault(r => r.RecipeId == recipeId);

        public string GetItemName(uint itemId) => $"Item{itemId}";
    }

    [Fact]
    public void UnknownSnapshotIsPermissive()
    {
        var caps = CharacterCapabilities.Unknown;

        Assert.True(caps.CanFlyIn(WalkZone));
        Assert.True(caps.HasRecipeBook(LockedBook));
        Assert.Equal(0, caps.LevelOf(Crp));
    }

    [Fact]
    public void KnownSnapshotAnswersFromItsSets()
    {
        var caps = Known(FlyZone);

        Assert.True(caps.CanFlyIn(FlyZone));
        Assert.False(caps.CanFlyIn(WalkZone));
        Assert.True(caps.HasRecipeBook(0));
        Assert.True(caps.HasRecipeBook(OpenBook));
        Assert.False(caps.HasRecipeBook(LockedBook));
        Assert.Equal(90, caps.LevelOf(Crp));
    }

    [Fact]
    public void ChooseRecipePrefersAnUnlockedBookOverTheCurrentJobsLockedOne()
    {
        var current = Recipe(10, Crp, LockedBook);
        var other = Recipe(11, Bsm, OpenBook);

        var chosen = CapabilityRules.ChooseRecipe([current, other], currentJob: Crp, _ => false, Known());

        Assert.Same(other, chosen);
    }

    [Fact]
    public void ChooseRecipeKeepsTheJobOrderWhenEveryBookIsUsable()
    {
        var lowestId = Recipe(10, 12);
        var withGearset = Recipe(11, Bsm);
        var current = Recipe(12, Crp);

        Assert.Same(current, CapabilityRules.ChooseRecipe([lowestId, withGearset, current], Crp, _ => false, Known()));
        Assert.Same(withGearset, CapabilityRules.ChooseRecipe([lowestId, withGearset], 0, job => job == Bsm, Known()));
        Assert.Same(lowestId, CapabilityRules.ChooseRecipe([lowestId, withGearset], 0, _ => false, Known()));
    }

    [Fact]
    public void ChooseRecipeStillReturnsTheOnlyRecipeWhenItsBookIsLocked()
    {
        var locked = Recipe(10, Crp, LockedBook);

        // The runner names the missing book; dropping it would read as "not craftable".
        Assert.Same(locked, CapabilityRules.ChooseRecipe([locked], Crp, _ => false, Known()));
    }

    [Fact]
    public void ResolverTreatsALockedBookIngredientAsRawMaterial()
    {
        const uint final = 1, ingot = 2, ore = 3;
        var recipes = new FakeProvider()
            .Add(10, final, 0, (ingot, 2))
            .Add(20, ingot, LockedBook, (ore, 4));

        var plan = DependencyResolver.Resolve(final, 1, recipes, _ => 0, Known());

        Assert.Single(plan.CraftSteps);
        Assert.Equal(final, plan.CraftSteps[0].ItemId);
        Assert.Equal(2, Assert.Single(plan.RawMaterials, m => m.ItemId == ingot).Amount);
    }

    [Fact]
    public void ResolverExpandsAnUnlockedBookIngredientAndIgnoresBooksWithoutCapabilities()
    {
        const uint final = 1, ingot = 2, ore = 3;
        var recipes = new FakeProvider()
            .Add(10, final, 0, (ingot, 2))
            .Add(20, ingot, LockedBook, (ore, 4));

        var withOpenBook = DependencyResolver.Resolve(final, 1, new FakeProvider()
            .Add(10, final, 0, (ingot, 2))
            .Add(20, ingot, OpenBook, (ore, 4)), _ => 0, Known());
        Assert.Equal(2, withOpenBook.CraftSteps.Count);

        var noCapabilities = DependencyResolver.Resolve(final, 1, recipes, _ => 0);
        Assert.Equal(2, noCapabilities.CraftSteps.Count);
    }

    [Fact]
    public void ResolverKeepsTheTargetRecipeEvenWhenItsBookIsLocked()
    {
        const uint final = 1, ore = 3;
        var recipes = new FakeProvider().Add(10, final, LockedBook, (ore, 4));

        var plan = DependencyResolver.Resolve(final, 1, recipes, _ => 0, Known());

        Assert.Equal(final, Assert.Single(plan.CraftSteps).ItemId);
        Assert.Equal(4, Assert.Single(plan.RawMaterials).Amount);
    }

    [Fact]
    public void ChooseSourcePrefersAZoneWithFlight()
    {
        var walk = Location(WalkZone, level: 10);
        var fly = Location(FlyZone, level: 50);

        Assert.Same(fly, CapabilityRules.ChooseSource([walk, fly], Known(FlyZone)));
    }

    [Fact]
    public void ChooseSourceKeepsTheOnlySourceWithoutFlight()
    {
        var walk = Location(WalkZone, level: 10);

        Assert.Same(walk, CapabilityRules.ChooseSource([walk], Known(FlyZone)));
        Assert.Null(CapabilityRules.ChooseSource([], Known(FlyZone)));
    }

    [Fact]
    public void ChooseSourceRanksUntimedAboveFlightAndFlightAboveLevel()
    {
        var timedFly = Location(FlyZone, level: 10, timed: true);
        var untimedWalk = Location(WalkZone, level: 50);
        Assert.Same(untimedWalk, CapabilityRules.ChooseSource([timedFly, untimedWalk], Known(FlyZone)));

        var flyHigh = Location(FlyZone, level: 50);
        var flyLow = Location(FlyZone, level: 20);
        Assert.Same(flyLow, CapabilityRules.ChooseSource([flyHigh, flyLow], Known(FlyZone)));
    }

    [Fact]
    public void ChooseSourceFallsBackToTheLevelRuleWhileCapabilitiesAreUnknown()
    {
        var lowWalk = Location(WalkZone, level: 10);
        var highFly = Location(FlyZone, level: 50);

        Assert.Same(lowWalk, CapabilityRules.ChooseSource([highFly, lowWalk], CharacterCapabilities.Unknown));
    }
}
