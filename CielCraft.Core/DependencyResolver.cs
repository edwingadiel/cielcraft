namespace CielCraft.Core;

/// <summary>A recipe as the planner sees it (spec §19).</summary>
public sealed record RecipeInfo(
    uint RecipeId,
    uint ResultItemId,
    int ResultAmount,
    IReadOnlyList<(uint ItemId, int Amount)> Ingredients);

/// <summary>Recipe lookup boundary (spec §6): game data in the plugin, fakes in tests.</summary>
public interface IRecipeProvider
{
    /// <summary>The recipe that produces the item, or null when it is not craftable.</summary>
    RecipeInfo? FindRecipeForItem(uint itemId);

    RecipeInfo? GetRecipeById(uint recipeId);

    string GetItemName(uint itemId);
}

/// <summary>One aggregated craft step; total output is Crafts * ResultAmount.</summary>
public sealed record PlannedCraft(uint RecipeId, uint ItemId, int Crafts, int ResultAmount)
{
    public int TotalProduced => Crafts * ResultAmount;
}

public sealed record MissingMaterial(uint ItemId, int Amount);

/// <summary>
/// The resolved production graph, flattened to executable order: CraftSteps is
/// dependency-ordered (every step's ingredients are produced by earlier steps
/// or covered by inventory/raw materials), the target step last.
/// </summary>
public sealed record ProductionPlan(
    uint TargetItemId,
    int TargetQuantity,
    IReadOnlyList<PlannedCraft> CraftSteps,
    IReadOnlyList<MissingMaterial> RawMaterials);

/// <summary>
/// Recursive dependency expansion (spec §20/§21). Craftable ingredients expand
/// into sub-crafts; inventory stock is consumed once across the whole graph;
/// craft counts honor recipe yields (ceil(needed / yield)) and surplus output
/// feeds later branches. The target itself is always produced, never taken
/// from inventory.
/// </summary>
public static class DependencyResolver
{
    private const int MaxDepth = 10;

    public static ProductionPlan Resolve(
        uint targetItemId,
        int quantity,
        IRecipeProvider recipes,
        Func<uint, int> ownedOf)
    {
        var state = new State(recipes, ownedOf);
        state.Expand(targetItemId, quantity, useStock: false, depth: 0);

        return new ProductionPlan(
            targetItemId,
            quantity,
            state.CraftOrder.Select(itemId => state.Crafts[itemId]).ToList(),
            state.Raw.Select(pair => new MissingMaterial(pair.Key, pair.Value)).ToList());
    }

    private sealed class State(IRecipeProvider recipes, Func<uint, int> ownedOf)
    {
        public readonly Dictionary<uint, PlannedCraft> Crafts = new();
        public readonly List<uint> CraftOrder = [];
        public readonly Dictionary<uint, int> Raw = new();

        private readonly Dictionary<uint, int> stock = new();
        private readonly HashSet<uint> expanding = [];

        public void Expand(uint itemId, int needed, bool useStock, int depth)
        {
            if (needed <= 0)
                return;

            if (useStock)
            {
                var available = StockOf(itemId);
                var taken = Math.Min(available, needed);
                stock[itemId] = available - taken;
                needed -= taken;
                if (needed == 0)
                    return;
            }

            var recipe = recipes.FindRecipeForItem(itemId);
            if (recipe == null || depth >= MaxDepth || !expanding.Add(itemId))
            {
                // Not craftable (or a cycle/depth guard tripped): raw material.
                Raw[itemId] = Raw.GetValueOrDefault(itemId) + needed;
                return;
            }

            var crafts = (needed + recipe.ResultAmount - 1) / recipe.ResultAmount;

            foreach (var (ingredientId, amount) in recipe.Ingredients)
                Expand(ingredientId, crafts * amount, useStock: true, depth + 1);

            expanding.Remove(itemId);

            // Post-order merge: children are already listed before this step,
            // so repeat visits only bump counts and the order stays valid.
            if (Crafts.TryGetValue(itemId, out var existing))
            {
                Crafts[itemId] = existing with { Crafts = existing.Crafts + crafts };
            }
            else
            {
                Crafts[itemId] = new PlannedCraft(recipe.RecipeId, itemId, crafts, recipe.ResultAmount);
                CraftOrder.Add(itemId);
            }

            // Yield surplus is real stock for later branches.
            var surplus = crafts * recipe.ResultAmount - needed;
            if (surplus > 0)
                stock[itemId] = StockOf(itemId) + surplus;
        }

        private int StockOf(uint itemId)
        {
            if (!stock.TryGetValue(itemId, out var value))
            {
                value = Math.Max(0, ownedOf(itemId));
                stock[itemId] = value;
            }

            return value;
        }
    }
}
