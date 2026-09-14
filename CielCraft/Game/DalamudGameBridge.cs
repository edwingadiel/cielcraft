using System.Collections.Generic;
using System.Linq;
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
            Control: GetAttribute(71),
            CurrentGp: player.CurrentGp,
            MaxGp: player.MaxGp);
    }

    public CraftSnapshot? GetCraftState() => CraftStateReader.Read();

    public GatheringSnapshot? GetGatheringState() => GatheringStateReader.Read();

    public bool IsGatheringActionInProgress => Plugin.Condition[ConditionFlag.ExecutingGatheringAction];

    public GatheringNodeSnapshot? FindNearestGatheringNode(IReadOnlyCollection<ulong>? excludedObjectIds = null)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        GatheringNodeSnapshot? nearest = null;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint || !obj.IsTargetable)
                continue;

            if (excludedObjectIds != null && excludedObjectIds.Contains(obj.GameObjectId))
                continue;

            var distance = System.Numerics.Vector3.Distance(player.Position, obj.Position);
            if (nearest == null || distance < nearest.Distance)
                nearest = new GatheringNodeSnapshot(obj.GameObjectId, obj.Name.TextValue, obj.Position, distance);
        }

        return nearest;
    }

    public unsafe bool InteractWithObject(ulong objectId)
    {
        var obj = Plugin.ObjectTable.SearchById(objectId);
        if (obj == null || !obj.IsTargetable)
            return false;

        Plugin.TargetManager.Target = obj;
        FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address, false);
        return true;
    }

    public unsafe bool GatherSlot(int slotIndex)
    {
        var addonPtr = Plugin.GameGui.GetAddonByName("Gathering");
        if (addonPtr.IsNull || !addonPtr.IsVisible)
            return false;

        var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonGathering*)addonPtr.Address;
        if (slotIndex < 0 || slotIndex >= addon->GatheredItemComponentCheckbox.Length)
            return false;

        var checkbox = addon->GatheredItemComponentCheckbox[slotIndex].Value;
        if (checkbox == null || !checkbox->IsEnabled || !checkbox->AtkResNode->IsVisible())
            return false;

        // Replay the checkbox's own click event back into the addon.
        var node = checkbox->OwnerNode;
        var evt = node->AtkResNode.AtkEventManager.Event;
        var data = default(FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData);
        addon->AtkUnitBase.ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, &data);
        return true;
    }

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

    public IReadOnlyList<IngredientRequirement> GetRecipeRequirements(ushort recipeId)
    {
        var recipes = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Recipe>();
        if (!recipes.TryGetRow(recipeId, out var recipe))
            return [];

        var items = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var requirements = new List<IngredientRequirement>();

        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            var itemId = recipe.Ingredient[i].RowId;
            var amount = (int)recipe.AmountIngredient[i];
            if (itemId == 0 || amount <= 0)
                continue;

            var name = items.TryGetRow(itemId, out var item) ? item.Name.ExtractText() : $"Item {itemId}";
            requirements.Add(new IngredientRequirement(itemId, name, amount, GetItemCount(itemId)));
        }

        return requirements;
    }

    public uint CurrentClassJobId => Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;

    public unsafe void OpenRecipe(uint recipeId)
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRecipeNote.Instance();
        if (agent != null)
            agent->OpenRecipeByRecipeId(recipeId);
    }

    public unsafe void CloseRecipeNote()
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRecipeNote.Instance();
        if (agent != null && agent->AgentInterface.IsAgentActive())
            agent->AgentInterface.Hide();
    }

    public unsafe bool EquipGearsetForJob(uint classJobId)
    {
        var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
        if (module == null)
            return false;

        var best = -1;
        short bestItemLevel = -1;
        for (var i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i))
                continue;

            var gearset = module->GetGearset(i);
            if (gearset == null || gearset->ClassJob != classJobId)
                continue;

            if (gearset->ItemLevel > bestItemLevel)
            {
                best = i;
                bestItemLevel = gearset->ItemLevel;
            }
        }

        if (best < 0)
            return false;

        module->EquipGearset(best, 0);
        return true;
    }

    public uint CurrentTerritoryId => Plugin.ClientState.TerritoryType;

    public bool IsBetweenAreas =>
        Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];

    public unsafe bool TeleportToTerritory(uint territoryId)
    {
        foreach (var entry in Plugin.AetheryteList)
        {
            if (entry.TerritoryId != territoryId)
                continue;

            var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
            if (telepo == null)
                return false;

            Plugin.Log.Information($"[Travel] Teleporting to aetheryte {entry.AetheryteId} (territory {territoryId}).");
            return telepo->Teleport(entry.AetheryteId, entry.SubIndex);
        }

        return false;
    }

    public bool IsMounted => Plugin.Condition[ConditionFlag.Mounted];

    public unsafe void TryMount()
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        // General action 9 = mount roulette.
        actionManager->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 9);
    }

    public unsafe void TryDismount()
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        // General action 23 = dismount.
        actionManager->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 23);
    }

    public unsafe int GetFreeInventorySlots()
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        return inventory == null ? 0 : (int)inventory->GetEmptySlotsInBag();
    }

    public unsafe bool IsQuickSynthAvailable
    {
        get
        {
            var addon = GetRecipeNote();
            return addon != null
                   && SelectedRecipeId != 0
                   && addon->QuickSynthesisButton != null
                   && addon->QuickSynthesisButton->IsEnabled;
        }
    }

    public unsafe bool OpenQuickSynthesisDialog()
    {
        if (!IsQuickSynthAvailable)
            return false;

        // Callback value 9 is the crafting log's Quick Synthesis command.
        GetRecipeNote()->AtkUnitBase.FireCallbackInt(9);
        return true;
    }

    public unsafe bool ConfirmQuickSynthesisDialog(int count)
    {
        var ptr = Plugin.GameGui.GetAddonByName("SynthesisSimpleDialog");
        if (ptr.IsNull || !ptr.IsVisible)
            return false;

        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address;
        var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[3];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
        values[0].Int = System.Math.Clamp(count, 1, 99);
        values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool;
        values[1].Byte = 1;
        values[2].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool;
        values[2].Byte = 1;
        addon->FireCallback(3, values, true);
        return true;
    }

    public unsafe bool IsQuickSynthesisActive
    {
        get
        {
            var ptr = Plugin.GameGui.GetAddonByName("SynthesisSimple");
            return !ptr.IsNull && ptr.IsVisible;
        }
    }

    public unsafe void CancelQuickSynthesis()
    {
        var ptr = Plugin.GameGui.GetAddonByName("SynthesisSimple");
        if (!ptr.IsNull && ptr.IsVisible)
            ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address)->FireCallbackInt(-1);
    }

    public unsafe bool UseItem(uint itemId)
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (actionManager == null)
            return false;

        if (actionManager->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, itemId) != 0)
            return false;

        return actionManager->UseAction(
            FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, itemId, 0xE0000000, 65535);
    }

    public unsafe bool IsQuickGatheringEnabled
    {
        get
        {
            var addon = GetGatheringAddon();
            return addon != null
                   && addon->QuickGatheringComponentCheckBox != null
                   && addon->QuickGatheringComponentCheckBox->IsChecked;
        }
    }

    public unsafe void DisableQuickGathering()
    {
        var addon = GetGatheringAddon();
        if (addon == null || addon->QuickGatheringComponentCheckBox == null
            || !addon->QuickGatheringComponentCheckBox->IsChecked)
            return;

        // Replay the checkbox's own click event to toggle it off.
        var checkbox = addon->QuickGatheringComponentCheckBox;
        var node = checkbox->OwnerNode;
        var evt = node->AtkResNode.AtkEventManager.Event;
        var data = default(FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData);
        addon->AtkUnitBase.ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, &data);
    }

    public unsafe CollectableGatheringSnapshot? GetCollectableGatheringState()
    {
        var ptr = Plugin.GameGui.GetAddonByName("GatheringMasterpiece");
        if (ptr.IsNull || !ptr.IsVisible)
            return null;

        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address;
        return new CollectableGatheringSnapshot(
            Collectability: ReadAtkInt(addon, 13),
            CollectabilityMax: ReadAtkInt(addon, 14),
            IntegrityRemaining: ReadAtkInt(addon, 62),
            IntegrityTotal: ReadAtkInt(addon, 63),
            LowThreshold: ReadAtkInt(addon, 65),
            MidThreshold: ReadAtkInt(addon, 66),
            HighThreshold: ReadAtkInt(addon, 67));
    }

    private static unsafe int ReadAtkInt(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
    {
        if (index >= addon->AtkValuesCount)
            return 0;

        var value = addon->AtkValues[index];
        return value.Type switch
        {
            FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int => value.Int,
            FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt => (int)value.UInt,
            _ => 0,
        };
    }

    public unsafe bool HasGearsetForJob(uint classJobId)
    {
        var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
        if (module == null)
            return false;

        for (var i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i))
                continue;

            var gearset = module->GetGearset(i);
            if (gearset != null && gearset->ClassJob == classJobId)
                return true;
        }

        return false;
    }

    public unsafe int GetStoredItemCount(uint itemId)
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inventory == null)
            return 0;

        FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] containers =
        [
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.SaddleBag1,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.SaddleBag2,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.PremiumSaddleBag1,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.PremiumSaddleBag2,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage2,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage3,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage4,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage5,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage6,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7,
        ];

        var total = 0;
        foreach (var container in containers)
        {
            // Unvisited containers are simply not loaded and count as zero.
            total += inventory->GetItemCountInContainer(itemId, container, false);
            total += inventory->GetItemCountInContainer(itemId, container, true);
        }

        return total;
    }

    private static unsafe FFXIVClientStructs.FFXIV.Client.UI.AddonGathering* GetGatheringAddon()
    {
        var ptr = Plugin.GameGui.GetAddonByName("Gathering");
        if (ptr.IsNull || !ptr.IsVisible)
            return null;

        return (FFXIVClientStructs.FFXIV.Client.UI.AddonGathering*)ptr.Address;
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
