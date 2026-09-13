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

            info = new RecipeInfo(recipeId, row.ItemResult.RowId, Math.Max((int)row.AmountResult, 1), ingredients);
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
