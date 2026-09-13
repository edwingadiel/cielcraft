using System.Collections.Generic;
using CielCraft.Core;

namespace CielCraft.Game;

/// <summary>
/// Provider boundary between CielCraft's logic and the game (spec §6/§8).
/// The only Dalamud-backed implementation is <see cref="DalamudGameBridge"/>;
/// tests use mocks.
/// </summary>
public interface IGameBridge
{
    bool IsLoggedIn { get; }

    /// <summary>True while a synthesis is in progress.</summary>
    bool IsCrafting { get; }

    /// <summary>True while the crafting log is open / a recipe is selected but no synthesis is running.</summary>
    bool IsPreparingToCraft { get; }

    bool IsGathering { get; }

    /// <summary>Null when no character is logged in.</summary>
    PlayerSnapshot? GetPlayerState();

    /// <summary>Null when no craft is active.</summary>
    CraftSnapshot? GetCraftState();

    /// <summary>True when the game reports the craft action as currently usable (CP, state, availability).</summary>
    bool IsCraftActionReady(uint craftActionId);

    /// <summary>Requests execution of a craft action. True if the game accepted the request.</summary>
    bool ExecuteCraftAction(uint craftActionId);

    /// <summary>True when the crafting log is open with a recipe selected and Synthesize is pressable.</summary>
    bool IsReadyToStartCraft { get; }

    /// <summary>Recipe currently selected in the crafting log; 0 when none.</summary>
    ushort SelectedRecipeId { get; }

    /// <summary>Presses Synthesize on the open crafting log. True if the request was issued.</summary>
    bool StartSynthesis();

    /// <summary>Result item id and per-craft yield of the active craft; null when not crafting.</summary>
    (uint ItemId, int Amount)? CurrentCraftResult { get; }

    /// <summary>Total NQ+HQ count of the item in the player inventory.</summary>
    int GetItemCount(uint itemId);

    /// <summary>
    /// Ingredient lines of a recipe with live inventory counts; empty when the
    /// recipe id is unknown.
    /// </summary>
    IReadOnlyList<IngredientRequirement> GetRecipeRequirements(ushort recipeId);
}
