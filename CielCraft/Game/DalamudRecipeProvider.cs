using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>IRecipeProvider over the game's Recipe sheet (spec §19: no redundant static data).</summary>
public sealed class DalamudRecipeProvider : IRecipeProvider
{
    private readonly Dictionary<uint, RecipeInfo?> byRecipeId = new();
    private readonly Dictionary<uint, string> names = new();
    private Dictionary<uint, uint>? itemToRecipeId;

    public RecipeInfo? FindRecipeForItem(uint itemId)
    {
        EnsureIndex();
        return itemToRecipeId!.TryGetValue(itemId, out var recipeId) ? GetRecipeById(recipeId) : null;
    }

    public RecipeInfo? GetRecipeById(uint recipeId)
    {
        if (byRecipeId.TryGetValue(recipeId, out var cached))
            return cached;

        RecipeInfo? info = null;
        if (Plugin.DataManager.GetExcelSheet<Recipe>().TryGetRow(recipeId, out var row) && row.ItemResult.RowId != 0)
        {
            var ingredients = new List<(uint ItemId, int Amount)>();
            for (var i = 0; i < row.Ingredient.Count; i++)
            {
                var itemId = row.Ingredient[i].RowId;
                var amount = (int)row.AmountIngredient[i];
                if (itemId != 0 && amount > 0)
                    ingredients.Add((itemId, amount));
            }

            // CraftType rows 0..7 map to ClassJob rows 8..15 (CRP..CUL).
            info = new RecipeInfo(
                recipeId,
                row.ItemResult.RowId,
                Math.Max((int)row.AmountResult, 1),
                ingredients,
                ClassJobId: row.CraftType.RowId + 8);
        }

        byRecipeId[recipeId] = info;
        return info;
    }

    public string GetItemName(uint itemId)
    {
        if (names.TryGetValue(itemId, out var cached))
            return cached;

        var name = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)
            ? item.Name.ExtractText()
            : $"Item {itemId}";

        names[itemId] = name;
        return name;
    }

    /// <summary>Case-insensitive substring search over craftable item names (roadmap 1.2).</summary>
    public IReadOnlyList<(uint RecipeId, uint ItemId, string Name)> SearchCraftable(string query, int maxResults = 10)
    {
        EnsureIndex();
        var needle = query.Trim();
        if (needle.Length < 2)
            return [];

        var results = new List<(uint RecipeId, uint ItemId, string Name)>();
        foreach (var (itemId, recipeId) in itemToRecipeId!)
        {
            var name = GetItemName(itemId);
            if (!name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add((recipeId, itemId, name));
            if (results.Count >= maxResults * 4)
                break;
        }

        // Prefer names that start with the query, then shorter names.
        results.Sort((a, b) =>
        {
            var aStarts = a.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
            var bStarts = b.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
            if (aStarts != bStarts)
                return aStarts ? -1 : 1;

            return a.Name.Length.CompareTo(b.Name.Length);
        });

        return results.Count > maxResults ? results.GetRange(0, maxResults) : results;
    }

    /// <summary>Item-to-recipe index, built once; the lowest recipe id wins for multi-recipe items.</summary>
    private void EnsureIndex()
    {
        if (itemToRecipeId != null)
            return;

        itemToRecipeId = new Dictionary<uint, uint>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Recipe>())
        {
            var resultId = row.ItemResult.RowId;
            if (resultId != 0 && !itemToRecipeId.ContainsKey(resultId))
                itemToRecipeId[resultId] = row.RowId;
        }
    }
}
