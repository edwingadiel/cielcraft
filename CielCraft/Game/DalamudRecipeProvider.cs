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
    private Dictionary<uint, List<uint>>? itemToRecipeIds;
    private readonly Func<uint> currentJob;
    private readonly Func<uint, bool> hasGearsetForJob;
    private readonly Func<CharacterCapabilities> capabilities;

    public DalamudRecipeProvider(
        Func<uint>? currentJobProvider = null,
        Func<uint, bool>? gearsetLookup = null,
        Func<CharacterCapabilities>? capabilities = null)
    {
        currentJob = currentJobProvider ?? (() => 0);
        hasGearsetForJob = gearsetLookup ?? (_ => false);
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
    }

    public RecipeInfo? FindRecipeForItem(uint itemId)
    {
        EnsureIndex();
        if (!itemToRecipeIds!.TryGetValue(itemId, out var recipeIds))
            return null;

        // Multi-job items (roadmap 4.5) and locked master books (7.16): the
        // ranking lives in Core so it is testable; ids are in ascending order.
        var candidates = new List<RecipeInfo>();
        foreach (var recipeId in recipeIds)
        {
            var info = GetRecipeById(recipeId);
            if (info != null)
                candidates.Add(info);
        }

        return CapabilityRules.ChooseRecipe(candidates, currentJob(), hasGearsetForJob, capabilities());
    }

    /// <summary>Name of a master recipe book (SecretRecipeBook row); empty for 0.</summary>
    public string GetRecipeBookName(uint bookId)
    {
        if (bookId == 0)
            return "";

        return Plugin.DataManager.GetExcelSheet<SecretRecipeBook>().TryGetRow(bookId, out var row)
            ? row.Name.ExtractText()
            : $"master book {bookId}";
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
                ClassJobId: row.CraftType.RowId + 8,
                IsExpert: row.IsExpert,
                RequiredQuality: row.RequiredQuality,
                SecretRecipeBookId: row.SecretRecipeBook.RowId);
        }

        byRecipeId[recipeId] = info;
        return info;
    }

    private readonly Dictionary<uint, ushort> icons = new();

    /// <summary>The item's game icon id; 0 when unknown.</summary>
    public ushort GetItemIconId(uint itemId)
    {
        if (icons.TryGetValue(itemId, out var cached))
            return cached;

        var icon = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)
            ? item.Icon
            : (ushort)0;
        icons[itemId] = icon;
        return icon;
    }

    /// <summary>Job abbreviation (CRP, MIN, ...) for user-facing messages; "job N" when unknown.</summary>
    public string GetJobAbbreviation(uint jobId) =>
        Plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(jobId, out var row)
            ? row.Abbreviation.ExtractText()
            : $"job {jobId}";

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
        foreach (var (itemId, recipeIds) in itemToRecipeIds!)
        {
            var recipeId = recipeIds[0];
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

    /// <summary>Exact (case-insensitive) craftable item name, for list imports where a substring hit would be a wrong item.</summary>
    public (uint RecipeId, uint ItemId, string Name)? FindCraftableByName(string name)
    {
        EnsureIndex();
        var needle = name.Trim();
        if (needle.Length == 0)
            return null;

        foreach (var (itemId, recipeIds) in itemToRecipeIds!)
        {
            var itemName = GetItemName(itemId);
            if (string.Equals(itemName, needle, StringComparison.OrdinalIgnoreCase))
                return (recipeIds[0], itemId, itemName);
        }

        return null;
    }

    private List<(uint ItemId, string Name)>? mealIndex;

    /// <summary>Case-insensitive name search over food items (ItemUICategory Meal).</summary>
    public IReadOnlyList<(uint ItemId, string Name)> SearchFood(string query, int maxResults = 8)
    {
        var needle = query.Trim();
        if (needle.Length < 2)
            return [];

        if (mealIndex == null)
        {
            mealIndex = [];
            foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
            {
                // ItemUICategory 46 = Meal.
                if (item.ItemUICategory.RowId == 46)
                    mealIndex.Add((item.RowId, item.Name.ExtractText()));
            }
        }

        var results = new List<(uint ItemId, string Name)>();
        foreach (var entry in mealIndex)
        {
            if (!entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(entry);
            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    /// <summary>Item-to-recipe index, built once; all recipes per item are kept.</summary>
    private void EnsureIndex()
    {
        if (itemToRecipeIds != null)
            return;

        itemToRecipeIds = new Dictionary<uint, List<uint>>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Recipe>())
        {
            var resultId = row.ItemResult.RowId;
            if (resultId == 0)
                continue;

            if (!itemToRecipeIds.TryGetValue(resultId, out var list))
                itemToRecipeIds[resultId] = list = [];

            list.Add(row.RowId);
        }
    }
}
