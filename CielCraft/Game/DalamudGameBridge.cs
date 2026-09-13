using CielCraft.Core;
using Dalamud.Game.ClientState.Conditions;

namespace CielCraft.Game;

/// <summary>Dalamud-backed implementation of <see cref="IGameBridge"/> (spec §8).</summary>
public sealed class DalamudGameBridge : IGameBridge
{
    public bool IsLoggedIn => Plugin.ClientState.IsLoggedIn;

    public bool IsCrafting =>
        Plugin.Condition[ConditionFlag.Crafting] || Plugin.Condition[ConditionFlag.ExecutingCraftingAction];

    public bool IsPreparingToCraft => Plugin.Condition[ConditionFlag.PreparingToCraft];

    public bool IsGathering =>
        Plugin.Condition[ConditionFlag.Gathering] || Plugin.Condition[ConditionFlag.ExecutingGatheringAction];

    public PlayerSnapshot? GetPlayerState()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        return new PlayerSnapshot(
            Name: player.Name.TextValue,
            TerritoryId: Plugin.ClientState.TerritoryType,
            Position: player.Position,
            ClassJobId: player.ClassJob.RowId,
            ClassJobAbbreviation: player.ClassJob.Value.Abbreviation.ExtractText(),
            Level: player.Level,
            CurrentCp: player.CurrentCp,
            MaxCp: player.MaxCp,
            Craftsmanship: GetAttribute(70),
            Control: GetAttribute(71));
    }

    public CraftSnapshot? GetCraftState() => CraftStateReader.Read();

    public unsafe bool IsCraftActionReady(uint craftActionId)
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (actionManager == null)
            return false;

        return actionManager->GetActionStatus(TypeFor(craftActionId), craftActionId) == 0;
    }

    public unsafe bool ExecuteCraftAction(uint craftActionId)
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (actionManager == null)
            return false;

        return actionManager->UseAction(TypeFor(craftActionId), craftActionId);
    }

    // Craft actions occupy ids >= 100000; crafting buffs (Veneration, Great
    // Strides, Manipulation, ...) are ordinary shared actions below that.
    private static FFXIVClientStructs.FFXIV.Client.Game.ActionType TypeFor(uint actionId) =>
        actionId >= 100000
            ? FFXIVClientStructs.FFXIV.Client.Game.ActionType.CraftAction
            : FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action;

    public unsafe bool IsReadyToStartCraft
    {
        get
        {
            var addon = GetRecipeNote();
            return addon != null
                   && SelectedRecipeId != 0
                   && addon->SynthesizeButton != null
                   && addon->SynthesizeButton->IsEnabled;
        }
    }

    public unsafe ushort SelectedRecipeId
    {
        get
        {
            var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
            return recipeNote != null ? recipeNote->ActiveCraftRecipeId : (ushort)0;
        }
    }

    public unsafe bool StartSynthesis()
    {
        if (!IsReadyToStartCraft)
            return false;

        // Callback value 8 is the crafting log's Synthesize command.
        var addon = GetRecipeNote();
        addon->AtkUnitBase.FireCallbackInt(8);
        return true;
    }

    public unsafe (uint ItemId, int Amount)? CurrentCraftResult
    {
        get
        {
            if (!IsCrafting)
                return null;

            var handler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance()->GetCraftEventHandler();
            if (handler == null || handler->ItemResult <= 0)
                return null;

            return ((uint)handler->ItemResult, handler->AmountResult);
        }
    }

    public unsafe int GetItemCount(uint itemId)
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inventory == null)
            return 0;

        return inventory->GetInventoryItemCount(itemId, false)
               + inventory->GetInventoryItemCount(itemId, true);
    }

    private static unsafe FFXIVClientStructs.FFXIV.Client.UI.AddonRecipeNote* GetRecipeNote()
    {
        var ptr = Plugin.GameGui.GetAddonByName("RecipeNote");
        if (ptr.IsNull || !ptr.IsVisible)
            return null;

        return (FFXIVClientStructs.FFXIV.Client.UI.AddonRecipeNote*)ptr.Address;
    }

    private static unsafe uint GetAttribute(int baseParamId)
    {
        var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (playerState == null)
            return 0;

        return (uint)playerState->Attributes[baseParamId];
    }
}
