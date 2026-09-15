using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using Dalamud.Game.ClientState.Conditions;

namespace CielCraft.Game;

/// <summary>Dalamud-backed implementation of <see cref="IGameBridge"/> (spec §8).</summary>
public sealed class DalamudGameBridge : IGameBridge
{
    public bool IsLoggedIn => Plugin.ClientState.IsLoggedIn;

    // ConditionFlag.Crafting is the crafting *stance*: it stays set after a
    // synthesis finishes and the log reopens, so a batch would never see the
    // craft end. A synthesis is running exactly while the Synthesis window is up.
    public bool IsCrafting =>
        IsAddonVisible("Synthesis") || Plugin.Condition[ConditionFlag.ExecutingCraftingAction];

    public bool IsPreparingToCraft => Plugin.Condition[ConditionFlag.PreparingToCraft];

    public bool IsGathering =>
        Plugin.Condition[ConditionFlag.Gathering] || Plugin.Condition[ConditionFlag.ExecutingGatheringAction];

    /// <summary>ITravelBridge: where the character is; null when not logged in.</summary>
    public System.Numerics.Vector3? PlayerPosition => GetPlayerState()?.Position;

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

    public GatheringNodeSnapshot? FindNearestGatheringNode(
        IReadOnlyCollection<ulong>? excludedObjectIds = null, System.Numerics.Vector3? origin = null)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        var rankFrom = origin ?? player.Position;
        GatheringNodeSnapshot? nearest = null;
        var nearestRank = float.MaxValue;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.GatheringPoint || !obj.IsTargetable)
                continue;

            if (excludedObjectIds != null && excludedObjectIds.Contains(obj.GameObjectId))
                continue;

            var rank = System.Numerics.Vector3.Distance(rankFrom, obj.Position);
            if (nearest == null || rank < nearestRank)
            {
                nearestRank = rank;
                var distance = System.Numerics.Vector3.Distance(player.Position, obj.Position);
                nearest = new GatheringNodeSnapshot(obj.GameObjectId, obj.Name.TextValue, obj.Position, distance);
            }
        }

        return nearest;
    }

    public unsafe void CloseGatheringWindow()
    {
        // Callback -1 is the window's own close/cancel: the client tells the
        // server we left the node and the Gathering condition clears ("You
        // finish mining."). AtkUnitBase.Close only hides the window and leaves
        // the character pinned in gathering mode — so fire the callback even
        // when the addon is hidden, to recover from exactly that state.
        var ptr = Plugin.GameGui.GetAddonByName("Gathering");
        if (ptr.IsNull)
            return;

        ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address)->FireCallbackInt(-1);
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
        var addon = GetGatheringAddon();
        if (addon == null || slotIndex < 0 || slotIndex >= addon->GatheredItemComponentCheckbox.Length)
            return false;

        var checkbox = addon->GatheredItemComponentCheckbox[slotIndex].Value;
        if (checkbox == null || !checkbox->IsEnabled || !checkbox->AtkResNode->IsVisible())
            return false;

        return ReplayCheckboxClick(&addon->AtkUnitBase, checkbox);
    }

    /// <summary>
    /// Replays a checkbox's own click event back into its addon. Null-guarded
    /// end to end: a missing owner node or unattached event listener (possible
    /// on the addon's first visible frame) would otherwise be a native null
    /// dereference — a client crash, not an exception.
    /// </summary>
    private static unsafe bool ReplayCheckboxClick(
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon,
        FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentCheckBox* checkbox)
    {
        if (checkbox == null)
            return false;

        var node = checkbox->OwnerNode;
        if (node == null)
            return false;

        var evt = node->AtkResNode.AtkEventManager.Event;
        if (evt == null)
            return false;

        var data = default(FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData);
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, &data);
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
                   && addon->SynthesizeButton->IsEnabled
                   && AreIngredientsAssigned();
        }
    }

    public unsafe ushort SelectedRecipeId
    {
        get
        {
            // RecipeNote.ActiveCraftRecipeId is the recipe of the synthesis in
            // progress — and it keeps that value after the craft ends, so it
            // is only trustworthy while a synthesis is actually running.
            // (Observed: it still read Iron Ingot while the log sat on Titanium
            // Gold Nugget, and the runner kept reopening the recipe forever.)
            var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
            if (IsCrafting && recipeNote != null && recipeNote->ActiveCraftRecipeId != 0)
                return recipeNote->ActiveCraftRecipeId;

            // Otherwise the answer is whatever the open crafting log shows.
            var addon = GetRecipeNote();
            if (addon == null)
                return 0;

            // The recipe list keeps the selected entry with its recipe id — the
            // direct source, when the list is populated.
            if (recipeNote != null && recipeNote->IsRecipeListReady && recipeNote->RecipeList != null)
            {
                var selected = recipeNote->RecipeList->SelectedRecipe;
                if (selected != null && selected->RecipeId != 0)
                    return selected->RecipeId;
            }

            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRecipeNote.Instance();
            if (agent != null && agent->AgentInterface.IsAgentActive() && agent->ActiveCraftRecipeId != 0)
                return (ushort)agent->ActiveCraftRecipeId;

            // On this client that agent field reads 0 while the log merely sits
            // on a recipe, so resolve from the displayed result name + craft
            // type, preferring the recipe this plugin last asked the log to open.
            if (agent == null || addon->SelectedRecipeName == null)
                return 0;

            var name = Dalamud.Utility.Utf8StringExtensions.ExtractText(addon->SelectedRecipeName->NodeText).Trim();
            if (name.Length == 0)
                return 0;

            var craftType = (uint)agent->SelectedCraftType;
            var recipes = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Recipe>();
            if (lastOpenedRecipeId != 0
                && recipes.TryGetRow(lastOpenedRecipeId, out var opened)
                && opened.CraftType.RowId == craftType
                && opened.ItemResult.RowId != 0
                && opened.ItemResult.Value.Name.ExtractText() == name)
                return (ushort)lastOpenedRecipeId;

            recipeByTypeAndName ??= BuildRecipeNameIndex(recipes);
            return recipeByTypeAndName.TryGetValue((craftType, name), out var id) ? id : (ushort)0;
        }
    }

    private uint lastOpenedRecipeId;
    private Dictionary<(uint CraftType, string Name), ushort>? recipeByTypeAndName;

    /// <summary>(craft type, result item name) → lowest recipe id; resolves the crafting log's displayed recipe.</summary>
    private static Dictionary<(uint CraftType, string Name), ushort> BuildRecipeNameIndex(
        Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Recipe> recipes)
    {
        var index = new Dictionary<(uint, string), ushort>();
        foreach (var row in recipes)
        {
            if (row.ItemResult.RowId == 0)
                continue;

            index.TryAdd((row.CraftType.RowId, row.ItemResult.Value.Name.ExtractText()), (ushort)row.RowId);
        }

        return index;
    }

    public unsafe string DescribeRecipeSelection()
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRecipeNote.Instance();
        var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        var addon = GetRecipeNote();
        var agentPart = agent == null
            ? "agent null"
            : $"agent active {agent->AgentInterface.IsAgentActive()}, agent.ActiveCraftRecipeId {agent->ActiveCraftRecipeId}, " +
              $"SelectedCraftType {agent->SelectedCraftType}, SelectedRecipeCategory {agent->SelectedRecipeCategory}, " +
              $"SelectedRecipeIndex {agent->SelectedRecipeIndex}";
        var notePart = recipeNote == null
            ? "recipeNote null"
            : $"recipeNote.ActiveCraftRecipeId {recipeNote->ActiveCraftRecipeId}, CraftingRecipeId {recipeNote->CraftingRecipeId}, " +
              $"ActiveCraftItemRequired {recipeNote->ActiveCraftItemRequired}";
        var addonPart = addon == null
            ? "addon hidden"
            : $"addon name '{(addon->SelectedRecipeName != null ? Dalamud.Utility.Utf8StringExtensions.ExtractText(addon->SelectedRecipeName->NodeText) : "-")}', " +
              $"synthesize button {(addon->SynthesizeButton == null ? "null" : addon->SynthesizeButton->IsEnabled ? "enabled" : "disabled")}, " +
              $"ingredients assigned {AreIngredientsAssigned()}";
        return $"{agentPart}; {notePart}; {addonPart}; {DescribeIngredientAssignment()}";
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

    public unsafe int GetHqItemCount(uint itemId)
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetInventoryItemCount(itemId, true);
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
        if (agent == null)
            return;

        // Re-issuing the request while the log already shows the recipe
        // toggles it closed (observed 2026-09-15); leave it alone then.
        if (lastOpenedRecipeId == recipeId && agent->AgentInterface.IsAgentActive() && IsAddonVisible("RecipeNote")
            && SelectedRecipeId == recipeId)
            return;

        lastOpenedRecipeId = recipeId;
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
        var best = FindBestGearsetForJob(classJobId);
        if (best < 0)
            return false;

        FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance()->EquipGearset(best, 0);
        return true;
    }

    /// <summary>Highest-item-level gearset slot for the job; -1 when none exists.</summary>
    private static unsafe int FindBestGearsetForJob(uint classJobId)
    {
        var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
        if (module == null)
            return -1;

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

        return best;
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

    // Aetheryte sheet rows of the housing entries in the teleport list: the
    // "Estate Hall (Free Company)" rows are 56/57/58 (Mist, Lavender Beds,
    // Goblet), 96 (Shirogane), 164 (Empyreum); "Estate Hall (Private)" rows
    // are 59/60/61, 97, 165. Apartments and shared estates carry flags.
    private static readonly uint[] PrivateEstateAetherytes = [59, 60, 61, 97, 165];
    private static readonly uint[] FreeCompanyEstateAetherytes = [56, 57, 58, 96, 164];

    // City aetherytes with an inn: New Gridania, Limsa Lower Decks, Ul'dah
    // Steps of Nald, Foundation, Kugane, the Crystarium, Old Sharlayan, Tuliyollal.
    private static readonly uint[] InnCityAetherytes = [2, 8, 9, 70, 111, 133, 182, 216];

    public unsafe bool TeleportHome(CraftingLocation location, out uint territoryId)
    {
        territoryId = 0;
        var entries = Plugin.AetheryteList.ToList();
        Dalamud.Game.ClientState.Aetherytes.IAetheryteEntry? entry;
        string label;
        switch (location)
        {
            case CraftingLocation.EstateHall:
                // Own house first, then the free company's, then a shared estate.
                entry = entries.FirstOrDefault(e => PrivateEstateAetherytes.Contains(e.AetheryteId))
                        ?? entries.FirstOrDefault(e => FreeCompanyEstateAetherytes.Contains(e.AetheryteId))
                        ?? entries.FirstOrDefault(e => e.IsSharedHouse);
                label = "estate hall";
                break;

            case CraftingLocation.Apartment:
                entry = entries.FirstOrDefault(e => e.IsApartment);
                label = "apartment";
                break;

            case CraftingLocation.InnRoom:
                // Telepo cannot enter an inn; the cheapest inn city is the
                // nearest one, and the character idles at its aetheryte.
                entry = entries.Where(e => InnCityAetherytes.Contains(e.AetheryteId))
                    .OrderBy(e => e.GilCost)
                    .FirstOrDefault();
                label = "inn city";
                break;

            default:
                return false;
        }

        if (entry == null)
        {
            Plugin.Log.Information($"[Travel] No {label} aetheryte in the teleport list (roadmap 7.6).");
            return false;
        }

        var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
        if (telepo == null)
            return false;

        territoryId = entry.TerritoryId;
        var where = entry.IsApartment ? "apartment"
            : entry.IsSharedHouse ? $"shared estate (ward {entry.Ward}, plot {entry.Plot})"
            : location == CraftingLocation.InnRoom ? "inn city aetheryte (the inn room itself is not entered)"
            : "estate hall";
        Plugin.Log.Information(
            $"[Travel] Teleporting home: {where}, aetheryte {entry.AetheryteId}/{entry.SubIndex} (territory {territoryId}, {entry.GilCost} gil).");
        return telepo->Teleport(entry.AetheryteId, entry.SubIndex);
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

    public bool IsQuickSynthesisActive => IsAddonVisible("SynthesisSimple");

    public void CancelQuickSynthesis() => FireAddonCallbackInt("SynthesisSimple", -1);

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

        ReplayCheckboxClick(&addon->AtkUnitBase, addon->QuickGatheringComponentCheckBox);
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

    public bool HasGearsetForJob(uint classJobId) => FindBestGearsetForJob(classJobId) >= 0;

    private static readonly FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] StorageContainers =
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

    public unsafe int GetStoredItemCount(uint itemId)
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inventory == null)
            return 0;

        var total = 0;
        foreach (var container in StorageContainers)
        {
            // Unvisited containers are simply not loaded and count as zero.
            total += inventory->GetItemCountInContainer(itemId, container, false);
            total += inventory->GetItemCountInContainer(itemId, container, true);
        }

        return total;
    }

    public unsafe float GetLowestEquipmentConditionPercent()
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inventory == null)
            return 100f;

        var container = inventory->GetInventoryContainer(
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems);
        if (container == null)
            return 100f;

        var lowest = 100f;
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0)
                continue;

            // Condition is stored as 0..30000 (= 0..100%).
            lowest = System.Math.Min(lowest, item->Condition / 300f);
        }

        return lowest;
    }

    public unsafe void OpenRepairWindow()
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        // General action 6 = Repair.
        actionManager->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 6);
    }

    public unsafe bool PlaySoundEffect(int soundEffectNumber)
    {
        if (soundEffectNumber is < 1 or > 16)
            return false;

        // The sound function hangs off any addon; the chat log is up whenever
        // a character is logged in. <se.N> is system sound id 36 + N.
        var ptr = Plugin.GameGui.GetAddonByName("ChatLog");
        if (ptr.IsNull)
            return false;

        ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address)->PlaySoundEffect(36 + soundEffectNumber);
        return true;
    }

    public unsafe void ExecuteChatCommand(string command)
    {
        var uiModule = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->GetUIModule();
        var text = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString(command);
        try
        {
            uiModule->GetRaptureShellModule()->ExecuteCommandInner(text, uiModule);
        }
        finally
        {
            text->Dtor(true);
        }
    }

    public bool IsAddonVisible(string addonName)
    {
        var ptr = Plugin.GameGui.GetAddonByName(addonName);
        return !ptr.IsNull && ptr.IsVisible;
    }

    public unsafe bool FireAddonCallbackInt(string addonName, int value)
    {
        var ptr = Plugin.GameGui.GetAddonByName(addonName);
        if (ptr.IsNull || !ptr.IsVisible)
            return false;

        ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address)->FireCallbackInt(value);
        return true;
    }

    public unsafe IReadOnlyList<string> ReadAddonStrings(string addonName)
    {
        var ptr = Plugin.GameGui.GetAddonByName(addonName);
        if (ptr.IsNull)
            return [];

        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address;
        var strings = new List<string>();
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = addon->AtkValues[i];
            if (value.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String || value.String.Value == null)
                continue;

            var text = value.String.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                strings.Add(text);
        }

        return strings;
    }

    public bool IsPartyOrFreeCompanyMember(string playerName)
    {
        for (var i = 0; i < Plugin.PartyList.Length; i++)
        {
            var member = Plugin.PartyList[i];
            if (member != null && member.Name.TextValue == playerName)
                return true;
        }

        // FC membership has no cheap query; a nearby player wearing our tag is
        // the best local answer (roadmap 7.10: tells from FC mates are not noted).
        var localTag = Plugin.ObjectTable.LocalPlayer?.CompanyTag.TextValue;
        if (string.IsNullOrEmpty(localTag))
            return false;

        foreach (var obj in Plugin.ObjectTable.PlayerObjects)
        {
            if (obj is Dalamud.Game.ClientState.Objects.Types.ICharacter character && character.Name.TextValue == playerName)
                return character.CompanyTag.TextValue == localTag;
        }

        return false;
    }

    // 48 = Well Fed, 49 = Medicated: food and potions are tracked by their own
    // buff (roadmap 7.11).
    public float GetFoodBuffRemainingSeconds() => GetStatusRemainingSeconds(48);

    public float GetMedicatedRemainingSeconds() => GetStatusRemainingSeconds(49);

    public float GetStatusRemainingSeconds(uint statusId)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return 0f;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == statusId)
                return System.Math.Max(0f, status.RemainingTime);
        }

        return 0f;
    }

    private static readonly FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] BagContainers =
    [
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4,
    ];

    public unsafe IReadOnlyList<ConsumableItem> ListConsumables()
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inventory == null)
            return [];

        // Stacks of the same item and quality may sit in several slots; the
        // picker wants one line per (item, HQ) with the summed count.
        var counts = new Dictionary<(uint ItemId, bool Hq), int>();
        var order = new List<(uint ItemId, bool Hq)>();
        foreach (var type in BagContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var key = (slot->GetBaseItemId(), slot->IsHighQuality());
                if (!counts.ContainsKey(key))
                    order.Add(key);
                counts[key] = counts.GetValueOrDefault(key) + (int)slot->GetQuantity();
            }
        }

        var items = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var result = new List<ConsumableItem>();
        foreach (var key in order)
        {
            if (!items.TryGetRow(key.ItemId, out var item))
                continue;

            // ItemUICategory 46 = Meal, 44 = Medicine.
            var kind = item.ItemUICategory.RowId switch
            {
                46 => ConsumableKind.Food,
                44 => ConsumableKind.Medicine,
                _ => (ConsumableKind?)null,
            };
            if (kind == null)
                continue;

            result.Add(new ConsumableItem(
                key.ItemId, item.Name.ExtractText(), key.Hq, counts[key], DescribeItemFood(item, key.Hq), kind.Value));
        }

        return result;
    }

    /// <summary>
    /// The bonus text of the item's ItemFood row (Item.ItemAction → Data[1] is
    /// the ItemFood id for meals and medicine); "n/a" when the item has none.
    /// </summary>
    private static string DescribeItemFood(Lumina.Excel.Sheets.Item item, bool hq)
    {
        var action = item.ItemAction.ValueNullable;
        if (action == null || action.Value.Data.Count < 2)
            return "n/a";

        var foods = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ItemFood>();
        if (!foods.TryGetRow(action.Value.Data[1], out var food))
            return "n/a";

        var parts = new List<string>();
        foreach (var param in food.Params)
        {
            var baseParam = param.BaseParam.ValueNullable;
            if (param.BaseParam.RowId == 0 || baseParam == null)
                continue;

            var name = baseParam.Value.Name.ExtractText();
            var value = hq ? param.ValueHQ : param.Value;
            var max = hq ? param.MaxHQ : param.Max;
            parts.Add(param.IsRelative ? $"{name} +{value}% (max {max})" : $"{name} +{value}");
        }

        return parts.Count == 0 ? "n/a" : string.Join(", ", parts);
    }

    public unsafe bool FillHqIngredients()
    {
        var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        var recipeId = SelectedRecipeId;
        if (recipeNote == null || recipeId == 0 || !IsAddonVisible("RecipeNote"))
            return false;

        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Recipe>()
                .TryGetRow(recipeId, out var recipe))
            return false;

        return FillIngredients(preferHq: true);
    }

    public unsafe bool FillIngredients(bool preferHq)
    {
        // Writing the RecipeNote amount arrays directly does not register as
        // a material selection: an ingredient owned only as HQ stayed at 0 and
        // Synthesize did nothing (Titanium Gold Shield with HQ ingots). The
        // crafting log has its own "use NQ / use HQ materials" buttons; press
        // those and let the game assign everything. With NQ preferred, HQ is
        // still pressed afterwards for ingredients that only exist as HQ.
        var addon = GetRecipeNote();
        if (addon == null)
            return false;

        var first = preferHq ? addon->HqFillButton : addon->NqFillButton;
        if (first != null)
            ReplayButtonClick(&addon->AtkUnitBase, first);

        if (!preferHq && !AreIngredientsAssigned() && addon->HqFillButton != null)
            ReplayButtonClick(&addon->AtkUnitBase, addon->HqFillButton);

        return AreIngredientsAssigned();
    }

    public unsafe bool AreIngredientsAssigned()
    {
        // The selected entry lists each ingredient's required amount and the
        // NQ/HQ counts the log has selected for it. RecipeNote's
        // CraftIngredient*Amounts arrays describe the *active* craft instead:
        // they kept the previous craft's selection and made a freshly opened
        // recipe look unassigned forever (Cobalt Tungsten Ingot, 2026-09-15).
        var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        if (recipeNote == null || !recipeNote->IsRecipeListReady || recipeNote->RecipeList == null)
            return true; // cannot tell; do not block

        var selected = recipeNote->RecipeList->SelectedRecipe;
        if (selected == null)
            return true;

        var ingredients = selected->Ingredients;
        for (var i = 0; i < ingredients.Length; i++)
        {
            var ingredient = ingredients[i];
            if (ingredient.ItemId == 0 || ingredient.Amount == 0)
                continue;

            if (ingredient.NQCount + ingredient.HQCount < ingredient.Amount)
                return false;
        }

        return true;
    }

    // ---- Materia extraction (roadmap 7.2) ----

    public unsafe IReadOnlyList<EquippedSpiritbond> GetEquipmentSpiritbond()
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inventory == null)
            return [];

        var container = inventory->GetInventoryContainer(
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems);
        if (container == null)
            return [];

        var result = new List<EquippedSpiritbond>();
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0)
                continue;

            // The same field carries collectability on collectables; equipped
            // gear only ever holds spiritbond (0..10000 = 0..100%).
            result.Add(new EquippedSpiritbond(i, item->ItemId, item->GetSpiritbondOrCollectability()));
        }

        return result;
    }

    public IReadOnlyList<int> GetSpiritbondReadySlots()
    {
        var ready = new List<int>();
        foreach (var piece in GetEquipmentSpiritbond())
        {
            if (piece.IsFull)
                ready.Add(piece.Slot);
        }

        return ready;
    }

    private uint? materiaExtractionActionId;

    /// <summary>
    /// GeneralAction row of "Materia Extraction", resolved by name once (the
    /// sheet says 14; Repair above is row 6). 0 when the name is not found.
    /// </summary>
    private uint MateriaExtractionActionId
    {
        get
        {
            if (materiaExtractionActionId is { } cached)
                return cached;

            uint found = 0;
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.GeneralAction>())
            {
                if (row.Name.ExtractText() == "Materia Extraction")
                {
                    found = row.RowId;
                    break;
                }
            }

            if (found == 0)
                Plugin.Log.Warning("[Maintenance] General action \"Materia Extraction\" not found in the GeneralAction sheet.");
            materiaExtractionActionId = found;
            return found;
        }
    }

    public unsafe bool OpenMaterialize()
    {
        var id = MateriaExtractionActionId;
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (id == 0 || actionManager == null)
            return false;

        actionManager->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, id);
        return true;
    }

    public unsafe bool ExtractMateria(int slot)
    {
        var ptr = Plugin.GameGui.GetAddonByName("Materialize");
        if (ptr.IsNull || !ptr.IsVisible)
            return false;

        // The row-select command Artisan fires on the Materialize list:
        // (2, index). The index is taken as the equipment slot; the
        // maintenance phase verifies the outcome by the spiritbond dropping.
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address;
        var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[2];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
        values[0].Int = 2;
        values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt;
        values[1].UInt = (uint)System.Math.Max(0, slot);
        addon->FireCallback(2, values, false);
        return true;
    }

    private bool confirmMaterializeByButton;

    public unsafe bool ConfirmMaterializeDialog()
    {
        var ptr = Plugin.GameGui.GetAddonByName("MaterializeDialog");
        if (ptr.IsNull || !ptr.IsVisible)
            return false;

        // Two known ways to say Yes: the dialog's callback 0 (older Artisan)
        // and its YesButton (ECommons' AddonMaster; the button field is in the
        // installed ClientStructs). Alternate between them on retries so a
        // wrong guess costs one retry interval, not the whole phase.
        var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonMaterializeDialog*)ptr.Address;
        confirmMaterializeByButton = !confirmMaterializeByButton;
        if (confirmMaterializeByButton && addon->YesButton != null && addon->YesButton->IsEnabled)
            return ReplayButtonClick(&addon->AtkUnitBase, addon->YesButton);

        addon->AtkUnitBase.FireCallbackInt(0);
        return true;
    }

    // No ConditionFlag names the extraction; Artisan treats Occupied39 as
    // "extracting materia" and that is what the wait phase keys off.
    public bool IsMaterializing => Plugin.Condition[ConditionFlag.Occupied39];

    public unsafe void CloseMaterialize()
    {
        var dialog = Plugin.GameGui.GetAddonByName("MaterializeDialog");
        if (!dialog.IsNull && dialog.IsVisible)
        {
            var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonMaterializeDialog*)dialog.Address;
            if (addon->NoButton == null || !ReplayButtonClick(&addon->AtkUnitBase, addon->NoButton))
                addon->AtkUnitBase.FireCallbackInt(-1);
        }

        FireAddonCallbackInt("Materialize", -1);
    }

    /// <summary>Per-slot required / NQ / HQ selection of the selected recipe, for reports about "did not become ready".</summary>
    private unsafe string DescribeIngredientAssignment()
    {
        var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        if (recipeNote == null || !recipeNote->IsRecipeListReady || recipeNote->RecipeList == null
            || recipeNote->RecipeList->SelectedRecipe == null)
            return "ingredient slots: n/a";

        var selected = recipeNote->RecipeList->SelectedRecipe;
        var parts = new List<string>();
        for (var i = 0; i < selected->Ingredients.Length; i++)
        {
            var ingredient = selected->Ingredients[i];
            if (ingredient.ItemId == 0)
                continue;
            parts.Add($"[{i}] item {ingredient.ItemId} need {ingredient.Amount} nq {ingredient.NQCount} hq {ingredient.HQCount} owned {GetItemCount(ingredient.ItemId)}/{GetHqItemCount(ingredient.ItemId)}hq");
        }

        return $"selected entry recipe {selected->RecipeId}; ingredient slots: {string.Join(", ", parts)}";
    }

    /// <summary>Replays a button's own click event into its addon (same null-guarding as the checkbox variant).</summary>
    private static unsafe bool ReplayButtonClick(
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon,
        FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentButton* button)
    {
        if (button == null)
            return false;

        var node = button->OwnerNode;
        if (node == null)
            return false;

        var evt = node->AtkResNode.AtkEventManager.Event;
        if (evt == null)
            return false;

        var data = default(FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData);
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, &data);
        return true;
    }

    // ------------------------------------------------ rotation engine (7.14)

    private sealed record PointBonus(GatheringBonusKind Kind, uint ConditionId, int ConditionValue, string Text);

    private sealed record PointInfo(IReadOnlyList<PointBonus> Bonuses, bool IsTimed);

    private readonly Dictionary<uint, PointInfo> pointCache = new();
    private static Dictionary<uint, GatherStatus>? statusByIdCache;

    public GatheringNodeFacts GetGatheringNodeFacts(ulong nodeObjectId, int slotIndex)
    {
        // A gathering point object's data id is its GatheringPoint row, which
        // carries the point's bonus conditions and (via the transient sheet)
        // its pop windows.
        var pointId = Plugin.ObjectTable.SearchById(nodeObjectId)?.BaseId ?? 0;
        var point = pointId != 0 ? GetPointInfo(pointId) : null;

        var bonuses = new List<GatheringBonusCondition>();
        if (point != null)
        {
            foreach (var bonus in point.Bonuses)
                bonuses.Add(new GatheringBonusCondition(bonus.Kind, bonus.Text, IsConditionMet(bonus.ConditionId, bonus.ConditionValue)));
        }

        var statuses = new HashSet<GatherStatus>();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null)
        {
            var byId = StatusById();
            foreach (var status in player.StatusList)
            {
                if (byId.TryGetValue(status.StatusId, out var known))
                    statuses.Add(known);
            }
        }

        var slotTexts = slotIndex >= 0 ? GatheringStateReader.ReadSlotTexts(slotIndex) : [];
        var boon = slotIndex >= 0 ? GatheringStateReader.ReadBoonChance(slotIndex) : -1;
        return new GatheringNodeFacts(boon, bonuses, statuses, point?.IsTimed ?? false, pointId) { SlotTexts = slotTexts };
    }

    private PointInfo GetPointInfo(uint pointId)
    {
        if (pointCache.TryGetValue(pointId, out var cached))
            return cached;

        var bonuses = new List<PointBonus>();
        var timed = false;
        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.GatheringPoint>().TryGetRow(pointId, out var point))
        {
            foreach (var bonusRef in point.GatheringPointBonus)
            {
                if (bonusRef.RowId == 0 || !bonusRef.IsValid)
                    continue;

                var bonus = bonusRef.Value;
                var conditionText = bonus.Condition.IsValid ? bonus.Condition.Value.Text.ExtractText().Trim() : "";
                var bonusText = bonus.BonusType.IsValid ? bonus.BonusType.Value.Text.ExtractText().Trim() : "";
                bonuses.Add(new PointBonus(
                    KindOf(bonus.BonusType.RowId),
                    bonus.Condition.RowId,
                    (int)bonus.ConditionValue,
                    $"{conditionText} {bonus.ConditionValue} → {bonusText} {bonus.BonusValue}"));
            }
        }

        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.GatheringPointTransient>().TryGetRow(pointId, out var transient))
        {
            timed = transient.GatheringRarePopTimeTable.RowId != 0
                    || (transient.EphemeralStartTime < 2400 && transient.EphemeralEndTime < 2400
                        && transient.EphemeralStartTime != transient.EphemeralEndTime);
        }

        var info = new PointInfo(bonuses, timed);
        pointCache[pointId] = info;
        return info;
    }

    // GatheringPointBonusType rows (2026-09-15): 1–9 gathering rate, 14–17
    // yield, 18–19 attempts/integrity, 22–23 Gatherer's Boon chance, 24–33
    // collectability effects.
    private static GatheringBonusKind KindOf(uint bonusTypeId) => bonusTypeId switch
    {
        22 or 23 => GatheringBonusKind.Boon,
        >= 14 and <= 17 => GatheringBonusKind.Yield,
        18 or 19 => GatheringBonusKind.Attempts,
        >= 1 and <= 9 => GatheringBonusKind.GatheringRate,
        >= 24 and <= 33 => GatheringBonusKind.Collectability,
        _ => GatheringBonusKind.Other,
    };

    // GatheringCondition rows: 14 Gathering ≥, 15 Perception ≥, 16 Gathering <,
    // 19 Max GP ≥ (BaseParam 72 = Gathering, 73 = Perception). The chain
    // condition (1) depends on the swings of this node and counts as unmet.
    private static bool IsConditionMet(uint conditionId, int value) => conditionId switch
    {
        14 => GetAttribute(72) >= value,
        15 => GetAttribute(73) >= value,
        16 => GetAttribute(72) < value,
        19 => (Plugin.ObjectTable.LocalPlayer?.MaxGp ?? 0) >= value,
        _ => false,
    };

    private static Dictionary<uint, GatherStatus> StatusById()
    {
        if (statusByIdCache != null)
            return statusByIdCache;

        var map = new Dictionary<uint, GatherStatus>();
        foreach (var status in System.Enum.GetValues<GatherStatus>())
        {
            foreach (var id in GatheringActions.StatusIds(status))
                map.TryAdd(id, status);
        }

        return statusByIdCache = map;
    }

    public unsafe bool IsItemOnCooldown(uint itemId)
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        return actionManager != null
               && actionManager->IsRecastTimerActive(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, itemId);
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

    // ---- Fishing (7.4) ----

    /// <summary>
    /// The game's fishing event handler, or null when it is not up. Path read
    /// from the installed ClientStructs on 2026-09-15:
    /// <c>EventFramework.Instance()->EventHandlerModule.FishingEventHandler</c>.
    /// </summary>
    private static unsafe FFXIVClientStructs.FFXIV.Client.Game.Event.FishingEventHandler* GetFishingHandler()
    {
        var framework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
        return framework == null ? null : framework->EventHandlerModule.FishingEventHandler;
    }

    public unsafe FishingSnapshot? GetFishingState()
    {
        var handler = GetFishingHandler();
        if (handler == null)
            return null;

        var bait = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return new FishingSnapshot(
            PhaseOf(handler->State),
            handler->CanFish,
            handler->CanMoochPreviousCatch,
            handler->CanMooch2PreviousCatch,
            bait == null ? 0 : bait->FishingBait,
            // The tug ("!", "!!", "!!!") is not stored on any struct in the
            // installed ClientStructs — FishingHookStrength is a bare enum with
            // no field referencing it — so it can only be observed by hooking
            // the bite event, as AutoHook does. Unknown means "plain Hook".
            FishingTug.Unknown);
    }

    private static FishingPhase PhaseOf(FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState state) => state switch
    {
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.None => FishingPhase.None,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.CastingOut => FishingPhase.CastingOut,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.PullingPoleIn => FishingPhase.PullingPoleIn,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.Quitting => FishingPhase.Quitting,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.PoleReady => FishingPhase.PoleReady,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.Bite => FishingPhase.Bite,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.Hooking => FishingPhase.Hooking,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.ReleasingCatch => FishingPhase.ReleasingCatch,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.ConfirmingCollectable => FishingPhase.ConfirmingCollectable,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.AmbitiousLure => FishingPhase.Lure,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.ModestLure => FishingPhase.Lure,
        FFXIVClientStructs.FFXIV.Client.Game.Event.FishingState.LineInWater => FishingPhase.LineInWater,
        _ => FishingPhase.Unknown,
    };

    public bool IsFishing => Plugin.Condition[ConditionFlag.Fishing];

    public unsafe bool SelectBait(uint baitItemId)
    {
        if (baitItemId == 0)
            return false;

        var handler = GetFishingHandler();
        if (handler == null)
        {
            // The fishing event handler is only created once the character has
            // fished this session; until then, using the bait item from the bag
            // is the same thing the Fishing Log's "Apply" does.
            return UseItem(baitItemId);
        }

        // ChangeBait takes the bait's item id (ClientStructs doc comment).
        handler->ChangeBait((int)baitItemId);
        return true;
    }

    public unsafe float GetMainHandConditionPercent()
    {
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        var container = inventory == null
            ? null
            : inventory->GetInventoryContainer(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems);
        if (container == null || container->Size == 0)
            return 100f;

        // Slot 0 of EquippedItems is the main hand (the rod).
        var item = container->GetInventorySlot(0);
        return item == null || item->ItemId == 0 ? 100f : item->Condition / 300f;
    }
}
