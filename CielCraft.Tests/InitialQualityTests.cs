using CielCraft.Core;
using CielCraft.Core.Crafting;
using Xunit;

namespace CielCraft.Tests;

/// <summary>The initial-quality formula and the HQ mix (roadmap 7.22); the formula's game validation is the coordinator's.</summary>
public class InitialQualityTests
{
    private const uint IronIngot = 5057;
    private const uint FireShard = 2;
    private const uint Lumber = 3;
    private const uint Cloth = 4;

    // Iron Rivets (recipe 51): max quality 360, MQF 50, Iron Ingot ×1 (ilvl 14, HQ-able) + Fire Shard ×1 (not).
    private static readonly RecipeInfo IronRivets = new(
        51, 100, 1, [(IronIngot, 1), (FireShard, 1)],
        MaterialQualityFactor: 50,
        Materials: [new MaterialInfo(IronIngot, 1, 14, true), new MaterialInfo(FireShard, 1, 1, false)]);

    // A made-up recipe with two HQ-able ingredients of different item levels.
    private static readonly RecipeInfo Mixed = new(
        60, 101, 1, [(Lumber, 2), (Cloth, 1), (FireShard, 3)],
        MaterialQualityFactor: 50,
        Materials:
        [
            new MaterialInfo(Lumber, 2, 10, true),
            new MaterialInfo(Cloth, 1, 30, true),
            new MaterialInfo(FireShard, 3, 1, false),
        ]);

    [Fact]
    public void IronRivetsWithAnHqIngotStartsAt180()
    {
        Assert.Equal(180, InitialQuality.For(IronRivets, 360, [(IronIngot, 1)]));
        Assert.Equal(0, InitialQuality.For(IronRivets, 360, []));
        // The shard cannot be HQ: asking for it changes nothing.
        Assert.Equal(180, InitialQuality.For(IronRivets, 360, [(IronIngot, 1), (FireShard, 1)]));
    }

    [Fact]
    public void ItemLevelWeightsTheShare()
    {
        // Σ(count × ilvl) over HQ-able = 2×10 + 1×30 = 50.
        Assert.Equal(300, InitialQuality.For(Mixed, 1000, [(Cloth, 1)]));   // 1000 × 0.5 × 30/50
        Assert.Equal(100, InitialQuality.For(Mixed, 1000, [(Lumber, 1)]));  // 1000 × 0.5 × 10/50
        Assert.Equal(500, InitialQuality.For(Mixed, 1000, [(Lumber, 2), (Cloth, 1)]));
    }

    [Fact]
    public void CountsAreClampedToThePerCraftAmount()
    {
        Assert.Equal(200, InitialQuality.For(Mixed, 1000, [(Lumber, 5)]));
        Assert.Equal(0, InitialQuality.For(Mixed, 1000, [(Lumber, -1)]));
    }

    [Fact]
    public void FloorsTheResult()
    {
        // Σ = 3 + 4 = 7; HQ 3 → 100 × 50 × 3 / 700 = 21.4 → 21.
        var recipe = new RecipeInfo(1, 2, 1, [(Lumber, 1), (Cloth, 1)], MaterialQualityFactor: 50,
            Materials: [new MaterialInfo(Lumber, 1, 3, true), new MaterialInfo(Cloth, 1, 4, true)]);
        Assert.Equal(21, InitialQuality.For(recipe, 100, [(Lumber, 1)]));
    }

    [Fact]
    public void NoMaterialDataOrNoFactorMeansZero()
    {
        var bare = new RecipeInfo(1, 2, 1, [(Lumber, 1)]);
        Assert.Equal(0, InitialQuality.For(bare, 1000, [(Lumber, 1)]));
        Assert.Null(InitialQuality.MixFor(bare, 1000, 10));

        var noHq = IronRivets with { MaterialQualityFactor = 0 };
        Assert.Equal(0, InitialQuality.For(noHq, 360, [(IronIngot, 1)]));
    }

    [Fact]
    public void MixTakesTheHighestItemLevelFirst()
    {
        // 300 needs one Cloth (30 ilvl) rather than two Lumber (200) plus more.
        var mix = InitialQuality.MixFor(Mixed, 1000, 300);
        Assert.NotNull(mix);
        Assert.Equal(300, mix.InitialQuality);
        Assert.Equal(1, mix.TotalHqItems);
        Assert.Equal(1, mix.HqCountOf(Cloth));

        // 350 needs the Cloth and one Lumber (400).
        var more = InitialQuality.MixFor(Mixed, 1000, 350);
        Assert.NotNull(more);
        Assert.Equal(400, more.InitialQuality);
        Assert.Equal(2, more.TotalHqItems);
        Assert.Equal(1, more.HqCountOf(Cloth));
        Assert.Equal(1, more.HqCountOf(Lumber));
    }

    [Fact]
    public void MixIsEmptyWhenNothingIsNeededAndNullWhenEverythingIsTooLittle()
    {
        Assert.Same(HqMix.None, InitialQuality.MixFor(IronRivets, 360, 0));
        Assert.Equal(1, InitialQuality.MixFor(IronRivets, 360, 180)!.TotalHqItems);
        Assert.Null(InitialQuality.MixFor(IronRivets, 360, 181));
    }

    [Fact]
    public void MixHonoursThePerIngredientCap()
    {
        // The Cloth cannot be seeded (gathered / bought): only the two Lumber (200) remain.
        var lumberOnly = InitialQuality.MixFor(Mixed, 1000, 150, id => id == Cloth ? 0 : 2);
        Assert.NotNull(lumberOnly);
        Assert.Equal(0, lumberOnly.HqCountOf(Cloth));
        Assert.Equal(2, lumberOnly.HqCountOf(Lumber));
        Assert.Null(InitialQuality.MixFor(Mixed, 1000, 300, id => id == Cloth ? 0 : 2));

        // A cap above the amount is the amount.
        Assert.Equal(1, InitialQuality.MixFor(Mixed, 1000, 300, _ => 9)!.HqCountOf(Cloth));
    }
}
