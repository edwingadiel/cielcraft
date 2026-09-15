using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Core.Rotations;
using Lumina.Excel.Sheets;

namespace CielCraft.Crafting;

/// <summary>
/// Recipe numbers from the game sheets for solves that happen outside a live
/// craft: the craft test (roadmap 7.18) and the base values a manual rotation
/// needs (7.8). The Synthesis window stays the authority during a craft; this
/// only reproduces what it would show.
/// </summary>
internal static class RecipeSheet
{
    /// <summary>What a recipe row and its level table say about a craft.</summary>
    public sealed record RecipeParameters(
        uint RecipeId,
        ushort RecipeLevel,
        int ClassJobLevel,
        int Stars,
        ushort MaxProgress,
        ushort MaxQuality,
        ushort MaxDurability,
        bool IsExpert,
        bool CanHq,
        int SuggestedCraftsmanship,
        RecipeLevelInfo Level);

    private static readonly Dictionary<ushort, RecipeLevelInfo?> Levels = new();
    private static readonly Dictionary<uint, RecipeParameters?> Recipes = new();

    /// <summary>The RecipeLevelTable row; null when the rlvl is unknown.</summary>
    public static RecipeLevelInfo? Level(ushort recipeLevel)
    {
        if (Levels.TryGetValue(recipeLevel, out var cached))
            return cached;

        RecipeLevelInfo? info = null;
        if (Plugin.DataManager.GetExcelSheet<RecipeLevelTable>().TryGetRow(recipeLevel, out var row))
            info = new RecipeLevelInfo(row.ClassJobLevel, row.ProgressDivider, row.QualityDivider, row.ProgressModifier, row.QualityModifier);

        Levels[recipeLevel] = info;
        return info;
    }

    /// <summary>Recipe parameters the way raphael-data derives them (level table × the recipe's factors).</summary>
    public static RecipeParameters? Read(uint recipeId)
    {
        if (Recipes.TryGetValue(recipeId, out var cached))
            return cached;

        RecipeParameters? parameters = null;
        if (Plugin.DataManager.GetExcelSheet<Recipe>().TryGetRow(recipeId, out var row)
            && row.ItemResult.RowId != 0 && row.RecipeLevelTable.IsValid)
        {
            var rlvl = row.RecipeLevelTable.Value;
            var level = new RecipeLevelInfo(rlvl.ClassJobLevel, rlvl.ProgressDivider, rlvl.QualityDivider, rlvl.ProgressModifier, rlvl.QualityModifier);
            parameters = new RecipeParameters(
                recipeId,
                (ushort)row.RecipeLevelTable.RowId,
                rlvl.ClassJobLevel,
                rlvl.Stars,
                (ushort)Math.Min(ushort.MaxValue, (long)rlvl.Difficulty * row.DifficultyFactor / 100),
                (ushort)Math.Min(ushort.MaxValue, (long)rlvl.Quality * row.QualityFactor / 100),
                (ushort)Math.Min(ushort.MaxValue, (long)rlvl.Durability * row.DurabilityFactor / 100),
                row.IsExpert,
                row.CanHq,
                rlvl.SuggestedCraftsmanship,
                level);
        }

        Recipes[recipeId] = parameters;
        return parameters;
    }

    /// <summary>Base progress/quality for a setup, from its rlvl row; null when the row is unknown.</summary>
    public static (int BaseProgress, int BaseQuality)? BaseValues(CraftSetup setup) =>
        Level(setup.RecipeLevel) is { } rlvl
            ? CraftMath.BaseValues(setup.Craftsmanship, setup.Control, setup.Level, rlvl)
            : null;

    /// <summary>A solver setup for a recipe and a set of crafter stats.</summary>
    public static CraftSetup SetupFor(
        RecipeParameters recipe, int craftsmanship, int control, int cp, int level,
        bool manipulation, bool heartAndSoul, bool quickInnovation) => new(
        RecipeLevel: recipe.RecipeLevel,
        MaxProgress: recipe.MaxProgress,
        MaxQuality: recipe.MaxQuality,
        MaxDurability: recipe.MaxDurability,
        IsExpert: recipe.IsExpert,
        Craftsmanship: (ushort)Math.Clamp(craftsmanship, 0, ushort.MaxValue),
        Control: (ushort)Math.Clamp(control, 0, ushort.MaxValue),
        Cp: (ushort)Math.Clamp(cp, 0, ushort.MaxValue),
        Level: (byte)Math.Clamp(level, 1, 255),
        Manipulation: manipulation,
        HeartAndSoul: heartAndSoul,
        QuickInnovation: quickInnovation);

    /// <summary>The character's setup for a recipe, the way the batch builds one at synthesis start (no specialist one-shots).</summary>
    public static CraftSetup? SetupForPlayer(RecipeParameters recipe, PlayerSnapshot? player) =>
        player == null
            ? null
            : SetupFor(recipe, (int)player.Craftsmanship, (int)player.Control, (int)player.MaxCp, player.Level,
                manipulation: player.Level >= 65, heartAndSoul: false, quickInnovation: false);
}
