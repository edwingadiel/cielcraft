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

    /// <summary>Null when no gathering node is open.</summary>
    GatheringSnapshot? GetGatheringState();

    /// <summary>True while a gather swing/action is animating.</summary>
    bool IsGatheringActionInProgress { get; }

    /// <summary>Nearest targetable gathering point not in the excluded set, or null.</summary>
    GatheringNodeSnapshot? FindNearestGatheringNode(IReadOnlyCollection<ulong>? excludedObjectIds = null);

    /// <summary>Targets and interacts with the object. False when it is gone.</summary>
    bool InteractWithObject(ulong objectId);

    /// <summary>Clicks an item slot in the open gathering window. False when not clickable.</summary>
    bool GatherSlot(int slotIndex);

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

    /// <summary>HQ-only count of the item in the player inventory.</summary>
    int GetHqItemCount(uint itemId);

    /// <summary>
    /// Ingredient lines of a recipe with live inventory counts; empty when the
    /// recipe id is unknown.
    /// </summary>
    IReadOnlyList<IngredientRequirement> GetRecipeRequirements(ushort recipeId);

    /// <summary>ClassJob row id of the current job; 0 when not logged in.</summary>
    uint CurrentClassJobId { get; }

    /// <summary>Opens the crafting log on the given recipe.</summary>
    void OpenRecipe(uint recipeId);

    /// <summary>Closes the crafting log if it is open.</summary>
    void CloseRecipeNote();

    /// <summary>Equips the best gearset for the job. False when none exists.</summary>
    bool EquipGearsetForJob(uint classJobId);

    /// <summary>Current territory row id.</summary>
    uint CurrentTerritoryId { get; }

    /// <summary>True during zone transitions/loading screens.</summary>
    bool IsBetweenAreas { get; }

    /// <summary>Teleports to an attuned aetheryte in the territory. False when none is attuned.</summary>
    bool TeleportToTerritory(uint territoryId);

    /// <summary>True while riding a mount.</summary>
    bool IsMounted { get; }

    /// <summary>Requests mount roulette. The game rejects it where mounting is not allowed.</summary>
    void TryMount();

    void TryDismount();

    /// <summary>Free bag slots in the main inventory.</summary>
    int GetFreeInventorySlots();

    /// <summary>Quick Synthesis is offered for the selected recipe.</summary>
    bool IsQuickSynthAvailable { get; }

    /// <summary>Opens the quick-synthesis dialog from the crafting log.</summary>
    bool OpenQuickSynthesisDialog();

    /// <summary>Confirms the quick-synthesis dialog for the given count (uses HQ materials).</summary>
    bool ConfirmQuickSynthesisDialog(int count);

    /// <summary>The quick-synthesis progress window is up.</summary>
    bool IsQuickSynthesisActive { get; }

    /// <summary>Cancels a quick synthesis in progress.</summary>
    void CancelQuickSynthesis();

    /// <summary>Uses an inventory item (e.g. a cordial). False when unusable or absent.</summary>
    bool UseItem(uint itemId);

    /// <summary>The open gathering node has quick gathering toggled on.</summary>
    bool IsQuickGatheringEnabled { get; }

    /// <summary>Toggles quick gathering off on the open node.</summary>
    void DisableQuickGathering();

    /// <summary>Live state of the collectable gathering window; null when closed.</summary>
    CollectableGatheringSnapshot? GetCollectableGatheringState();

    /// <summary>A gearset exists for the job.</summary>
    bool HasGearsetForJob(uint classJobId);

    /// <summary>Item count across saddlebags and cached retainer pages (read-only awareness).</summary>
    int GetStoredItemCount(uint itemId);

    /// <summary>Lowest condition of equipped gear, 0-100.</summary>
    float GetLowestEquipmentConditionPercent();

    /// <summary>Opens the self-repair window (general action).</summary>
    void OpenRepairWindow();

    bool IsAddonVisible(string addonName);

    /// <summary>Fires an integer callback on a visible addon. False when it is not open.</summary>
    bool FireAddonCallbackInt(string addonName, int value);

    /// <summary>Seconds left on the Well Fed buff; 0 when not fed.</summary>
    float GetFoodBuffRemainingSeconds();

    /// <summary>
    /// Fills the selected recipe's ingredient slots with as many HQ materials
    /// as owned (NQ for the remainder). False when no recipe is selected.
    /// </summary>
    bool FillHqIngredients();
}
