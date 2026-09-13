namespace CielCraft.Core;

/// <summary>One ingredient line of a recipe, with current ownership.</summary>
public sealed record IngredientRequirement(uint ItemId, string Name, int AmountPerCraft, int Owned)
{
    public int RequiredFor(int quantity) => AmountPerCraft * quantity;

    /// <summary>Required − owned, floored at zero (spec §22).</summary>
    public int MissingFor(int quantity) => Math.Max(0, RequiredFor(quantity) - Owned);
}

/// <summary>Pure inventory arithmetic (spec §22/§60).</summary>
public static class InventoryMath
{
    /// <summary>How many crafts the owned materials cover right now.</summary>
    public static int CraftableCount(IReadOnlyList<IngredientRequirement> requirements)
    {
        if (requirements.Count == 0)
            return 0;

        var possible = int.MaxValue;
        foreach (var requirement in requirements)
        {
            if (requirement.AmountPerCraft <= 0)
                continue;

            possible = Math.Min(possible, requirement.Owned / requirement.AmountPerCraft);
        }

        return possible == int.MaxValue ? 0 : possible;
    }

    /// <summary>True when every ingredient covers the requested quantity.</summary>
    public static bool CanCraft(IReadOnlyList<IngredientRequirement> requirements, int quantity) =>
        requirements.Count > 0 && CraftableCount(requirements) >= quantity;
}
