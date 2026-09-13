using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class InventoryMathTests
{
    private static IngredientRequirement Req(uint id, int per, int owned) =>
        new(id, $"Item{id}", per, owned);

    [Fact]
    public void CraftableCountIsBoundByTheScarcestIngredient()
    {
        var requirements = new[]
        {
            Req(1, 4, 800), // 200 crafts
            Req(2, 2, 13),  // 6 crafts
            Req(3, 1, 50),  // 50 crafts
        };

        Assert.Equal(6, InventoryMath.CraftableCount(requirements));
    }

    [Fact]
    public void MissingIsRequiredMinusOwnedFlooredAtZero()
    {
        var scarce = Req(1, 4, 613);
        Assert.Equal(800, scarce.RequiredFor(200));
        Assert.Equal(187, scarce.MissingFor(200));

        var plentiful = Req(2, 1, 999);
        Assert.Equal(0, plentiful.MissingFor(200));
    }

    [Fact]
    public void EmptyRequirementsMeanNothingIsCraftable()
    {
        Assert.Equal(0, InventoryMath.CraftableCount([]));
        Assert.False(InventoryMath.CanCraft([], 1));
    }

    [Fact]
    public void CanCraftComparesAgainstTheRequestedQuantity()
    {
        var requirements = new[] { Req(1, 2, 10) };

        Assert.True(InventoryMath.CanCraft(requirements, 5));
        Assert.False(InventoryMath.CanCraft(requirements, 6));
    }
}
