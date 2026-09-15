namespace CielCraft.Core;

/// <summary>A recipe as the planner sees it (spec §19).</summary>
public sealed record RecipeInfo(
    uint RecipeId,
    uint ResultItemId,
    int ResultAmount,
    IReadOnlyList<(uint ItemId, int Amount)> Ingredients,
    uint ClassJobId = 0,
    bool IsExpert = false,
    uint RequiredQuality = 0,
    uint SecretRecipeBookId = 0);

/// <summary>Recipe lookup boundary (spec §6): game data in the plugin, fakes in tests.</summary>
public interface IRecipeProvider
{
    /// <summary>The recipe that produces the item, or null when it is not craftable.</summary>
    RecipeInfo? FindRecipeForItem(uint itemId);

    RecipeInfo? GetRecipeById(uint recipeId);

    string GetItemName(uint itemId);
}

/// <summary>
/// One aggregated craft step; total output is Crafts * ResultAmount. Mode is
/// the order's production mode for a target step (roadmap 7.13) and Any for
/// intermediates, which the runner quick-synthesizes per the settings.
/// </summary>
public sealed record PlannedCraft(
    uint RecipeId,
    uint ItemId,
    int Crafts,
    int ResultAmount,
    ProductionMode Mode = ProductionMode.Any)
{
    public int TotalProduced => Crafts * ResultAmount;
}

public sealed record MissingMaterial(uint ItemId, int Amount);

/// <summary>
/// The resolved production graph, flattened to executable order: CraftSteps is
/// dependency-ordered (every step's ingredients are produced by earlier steps
/// or covered by inventory/raw materials), target steps last. A plan can
/// carry several targets (an order group planned together, roadmap 7.13).
/// </summary>
public sealed record ProductionPlan(
    IReadOnlyList<PlanTarget> Targets,
    IReadOnlyList<PlannedCraft> CraftSteps,
    IReadOnlyList<MissingMaterial> RawMaterials)
{
    /// <summary>Single-target plan.</summary>
    public ProductionPlan(uint targetItemId, int targetQuantity, IReadOnlyList<PlannedCraft> craftSteps, IReadOnlyList<MissingMaterial> rawMaterials)
        : this([new PlanTarget(targetItemId, targetQuantity)], craftSteps, rawMaterials)
    {
    }

    /// <summary>The first target; what single-target callers report on.</summary>
    public uint TargetItemId => Targets.Count > 0 ? Targets[0].ItemId : 0;

    public int TargetQuantity => Targets.Count > 0 ? Targets[0].Quantity : 0;
}

/// <summary>
/// Recursive dependency expansion (spec §20/§21). Craftable ingredients expand
/// into sub-crafts; inventory stock is consumed once across the whole graph;
/// craft counts honor recipe yields (ceil(needed / yield)) and surplus output
/// feeds later branches. The target itself is always produced, never taken
/// from inventory. With capabilities given, ingredient recipes whose master
/// book is locked are not planned (roadmap 7.16); the target keeps its recipe
/// so the runner can refuse with the book's name.
/// </summary>
public static class DependencyResolver
{
    private const int MaxDepth = 10;

    public static ProductionPlan Resolve(
        uint targetItemId,
        int quantity,
        IRecipeProvider recipes,
        Func<uint, int> ownedOf,
        CharacterCapabilities? capabilities = null) =>
        Resolve([new PlanTarget(targetItemId, quantity)], recipes, ownedOf, capabilities);

    /// <summary>
    /// Several targets planned as one graph (roadmap 7.13): stock is consumed
    /// once across all of them, shared intermediates merge into one step, and
    /// a materials-only target expands its ingredients without its own craft.
    /// Targets are expanded in order, so an earlier target's surplus feeds a
    /// later one.
    /// </summary>
    public static ProductionPlan Resolve(
        IReadOnlyList<PlanTarget> targets,
        IRecipeProvider recipes,
        Func<uint, int> ownedOf,
        CharacterCapabilities? capabilities = null)
    {
        var state = new State(recipes, ownedOf, capabilities);
        foreach (var target in targets)
        {
            if (target.MaterialsOnly)
                state.ExpandIngredientsOnly(target.ItemId, target.Quantity);
            else
                state.Expand(target.ItemId, target.Quantity, useStock: false, depth: 0);
        }

        // A target's mode belongs to its own step; a step that is both a
        // target and someone's intermediate keeps the target's mode.
        foreach (var target in targets)
        {
            if (!target.MaterialsOnly && state.Crafts.TryGetValue(target.ItemId, out var step))
                state.Crafts[target.ItemId] = step with { Mode = target.Mode };
        }

        return new ProductionPlan(
            targets,
            state.CraftOrder.Select(itemId => state.Crafts[itemId]).ToList(),
            state.Raw.Select(pair => new MissingMaterial(pair.Key, pair.Value)).ToList());
    }

    private sealed class State(IRecipeProvider recipes, Func<uint, int> ownedOf, CharacterCapabilities? capabilities)
    {
        public readonly Dictionary<uint, PlannedCraft> Crafts = new();
        public readonly List<uint> CraftOrder = [];
        public readonly Dictionary<uint, int> Raw = new();

        private readonly Dictionary<uint, int> stock = new();
        private readonly HashSet<uint> expanding = [];

        /// <summary>Materials-only target: what its crafts would consume, without the crafts themselves.</summary>
        public void ExpandIngredientsOnly(uint itemId, int needed)
        {
            var recipe = recipes.FindRecipeForItem(itemId);
            if (recipe == null || needed <= 0)
                return;

            var crafts = (needed + recipe.ResultAmount - 1) / recipe.ResultAmount;
            foreach (var (ingredientId, amount) in recipe.Ingredients)
                Expand(ingredientId, crafts * amount, useStock: true, depth: 1);
        }

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

            // A locked master-book recipe cannot be crafted: treat the
            // ingredient as raw (gather/buy) instead of planning it (7.16).
            if (recipe != null && depth > 0 && capabilities != null && !capabilities.IsRecipeUsable(recipe))
                recipe = null;

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
