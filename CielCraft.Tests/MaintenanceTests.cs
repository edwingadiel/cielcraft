using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Game;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// Drives the materia extraction phases of <see cref="MaintenanceService"/>
/// (roadmap 7.2) against a scripted bridge: the Materialize window and its
/// dialog appear on request, Yes drops the piece's spiritbond, and the
/// occupied flag models the extraction animation. The repair phases are
/// exercised only far enough to show that materia follows repair.
/// </summary>
public class MaintenanceTests
{
    /// <summary>
    /// The slice of the bridge maintenance touches. Every other member throws:
    /// the service must not wander into game state a test did not script.
    /// </summary>
    private sealed class FakeBridge : IGameBridge
    {
        public readonly Dictionary<int, (uint ItemId, int Spiritbond)> Spiritbond = new();
        public readonly HashSet<string> Visible = [];
        public readonly List<string> Calls = [];
        public readonly Dictionary<uint, int> ItemCounts = new();

        public float Condition = 100f;
        public int FreeSlots = 10;
        public bool MaterializeOpens = true;
        public bool Materializing;
        private int extractingSlot = -1;

        public bool IsLoggedIn => true;
        public bool IsCrafting => false;
        public bool IsPreparingToCraft => false;
        public bool IsGathering => false;

        public float GetLowestEquipmentConditionPercent() => Condition;

        public int GetItemCount(uint itemId) => ItemCounts.GetValueOrDefault(itemId);

        public int GetHqItemCount(uint itemId) => 0;

        public int GetFreeInventorySlots() => FreeSlots;

        public float GetFoodBuffRemainingSeconds() => 0f;

        public float GetMedicatedRemainingSeconds() => 0f;

        public void CloseRecipeNote() => Calls.Add("CloseRecipeNote");

        public bool IsAddonVisible(string addonName) => Visible.Contains(addonName);

        public void OpenRepairWindow()
        {
            Calls.Add("OpenRepairWindow");
            Visible.Add("Repair");
        }

        public bool FireAddonCallbackInt(string addonName, int value)
        {
            Calls.Add($"Callback({addonName},{value})");
            if (!Visible.Contains(addonName))
                return false;

            switch (addonName, value)
            {
                case ("Repair", 0):
                    Visible.Add("SelectYesno");
                    break;
                case ("SelectYesno", 0):
                    Visible.Remove("SelectYesno");
                    Condition = 100f;
                    break;
                case ("SelectYesno", 1):
                    Visible.Remove("SelectYesno");
                    break;
                case ("Repair", -1):
                    Visible.Remove("Repair");
                    break;
            }

            return true;
        }

        public IReadOnlyList<EquippedSpiritbond> GetEquipmentSpiritbond() =>
            Spiritbond.OrderBy(p => p.Key).Select(p => new EquippedSpiritbond(p.Key, p.Value.ItemId, p.Value.Spiritbond)).ToList();

        public IReadOnlyList<int> GetSpiritbondReadySlots() =>
            GetEquipmentSpiritbond().Where(p => p.IsFull).Select(p => p.Slot).ToList();

        public bool OpenMaterialize()
        {
            Calls.Add("OpenMaterialize");
            if (MaterializeOpens)
                Visible.Add("Materialize");
            return true;
        }

        public bool ExtractMateria(int slot)
        {
            Calls.Add($"Extract({slot})");
            if (!Visible.Contains("Materialize"))
                return false;

            extractingSlot = slot;
            Visible.Add("MaterializeDialog");
            return true;
        }

        public bool ConfirmMaterializeDialog()
        {
            Calls.Add("Confirm");
            if (!Visible.Contains("MaterializeDialog"))
                return false;

            Visible.Remove("MaterializeDialog");
            Materializing = true;
            if (Spiritbond.TryGetValue(extractingSlot, out var piece))
                Spiritbond[extractingSlot] = (piece.ItemId, 0);
            return true;
        }

        public bool IsMaterializing => Materializing;

        public void CloseMaterialize()
        {
            Calls.Add("CloseMaterialize");
            Visible.Remove("MaterializeDialog");
            Visible.Remove("Materialize");
        }

        // ---- not reached by maintenance ----

        private static NotImplementedException Unexpected() => new("the maintenance service must not touch this");

        public System.Numerics.Vector3? PlayerPosition => throw Unexpected();
        public bool IsMounted => throw Unexpected();
        public void TryMount() => throw Unexpected();
        public void TryDismount() => throw Unexpected();
        public PlayerSnapshot? GetPlayerState() => throw Unexpected();
        public CraftSnapshot? GetCraftState() => throw Unexpected();
        public GatheringSnapshot? GetGatheringState() => throw Unexpected();
        public bool IsGatheringActionInProgress => throw Unexpected();
        public GatheringNodeSnapshot? FindNearestGatheringNode(IReadOnlyCollection<ulong>? excludedObjectIds = null, System.Numerics.Vector3? origin = null) => throw Unexpected();
        public bool InteractWithObject(ulong objectId) => throw Unexpected();
        public bool GatherSlot(int slotIndex) => throw Unexpected();
        public void CloseGatheringWindow() => throw Unexpected();
        public bool IsCraftActionReady(uint craftActionId) => throw Unexpected();
        public bool ExecuteCraftAction(uint craftActionId) => throw Unexpected();
        public bool IsReadyToStartCraft => throw Unexpected();
        public ushort SelectedRecipeId => throw Unexpected();
        public string DescribeRecipeSelection() => throw Unexpected();
        public bool StartSynthesis() => throw Unexpected();
        public (uint ItemId, int Amount)? CurrentCraftResult => throw Unexpected();
        public IReadOnlyList<IngredientRequirement> GetRecipeRequirements(ushort recipeId) => throw Unexpected();
        public uint CurrentClassJobId => throw Unexpected();
        public void OpenRecipe(uint recipeId) => throw Unexpected();
        public bool EquipGearsetForJob(uint classJobId) => throw Unexpected();
        public uint CurrentTerritoryId => throw Unexpected();
        public bool IsBetweenAreas => throw Unexpected();
        public bool TeleportToTerritory(uint territoryId) => throw Unexpected();
        public bool TeleportHome(CraftingLocation location, out uint territoryId) => throw Unexpected();
        public bool IsQuickSynthAvailable => throw Unexpected();
        public bool OpenQuickSynthesisDialog() => throw Unexpected();
        public bool ConfirmQuickSynthesisDialog(int count) => throw Unexpected();
        public bool IsQuickSynthesisActive => throw Unexpected();
        public void CancelQuickSynthesis() => throw Unexpected();
        public bool UseItem(uint itemId) => throw Unexpected();
        public bool IsQuickGatheringEnabled => throw Unexpected();
        public void DisableQuickGathering() => throw Unexpected();
        public CollectableGatheringSnapshot? GetCollectableGatheringState() => throw Unexpected();
        public bool HasGearsetForJob(uint classJobId) => throw Unexpected();
        public int GetStoredItemCount(uint itemId) => throw Unexpected();
        public bool PlaySoundEffect(int soundEffectNumber) => throw Unexpected();
        public void ExecuteChatCommand(string command) => throw Unexpected();
        public IReadOnlyList<string> ReadAddonStrings(string addonName) => throw Unexpected();
        public bool IsPartyOrFreeCompanyMember(string playerName) => throw Unexpected();
        public IReadOnlyList<ConsumableItem> ListConsumables() => throw Unexpected();
        public bool FillHqIngredients() => throw Unexpected();
        public bool FillIngredients(bool preferHq) => throw Unexpected();
        public bool AreIngredientsAssigned() => throw Unexpected();
    }

    private const uint Gloves = 1001;
    private const uint DarkMatterGrade8 = 33916;

    private static (MaintenanceService Service, FakeBridge Bridge, FakeClock Clock, ListLog Log) Build(
        bool autoExtract = true, bool autoRepair = false)
    {
        var clock = new FakeClock();
        var log = new ListLog();
        var bridge = new FakeBridge();
        bridge.Spiritbond[0] = (1000, 5000);
        bridge.Spiritbond[3] = (Gloves, EquippedSpiritbond.Full);
        var settings = new AutomationSettings { AutoRepair = autoRepair, AutoExtractMateria = autoExtract };
        var service = new MaintenanceService(bridge, settings, log, clock, id => $"item {id}");
        return (service, bridge, clock, log);
    }

    [Fact]
    public void ExtractsOnePieceAtFullSpiritbondAndCountsIt()
    {
        var (service, bridge, clock, log) = Build();

        Assert.True(service.Tick());                     // Idle → Extracting
        Assert.Equal("Extracting materia...", service.StatusText);
        Assert.True(service.Tick());                     // opens Materialize (closing the crafting log first)
        Assert.Equal(["CloseRecipeNote", "OpenMaterialize"], bridge.Calls);

        Assert.True(service.Tick());                     // retry interval not elapsed: no row select yet
        Assert.DoesNotContain("Extract(3)", bridge.Calls);
        clock.Advance(2);
        Assert.True(service.Tick());                     // selects the ready slot's row → dialog
        Assert.Contains("Extract(3)", bridge.Calls);
        Assert.Contains("MaterializeDialog", bridge.Visible);

        Assert.True(service.Tick());                     // → ConfirmingExtract
        Assert.True(service.Tick());                     // Yes: dialog gone, spiritbond drops, extraction animates
        Assert.Contains("Confirm", bridge.Calls);
        Assert.True(service.Tick());                     // → WaitingForExtract
        Assert.True(service.Tick());                     // still occupied by the extraction: keep waiting
        Assert.Equal(0, service.MateriaExtracted);

        bridge.Materializing = false;
        Assert.True(service.Tick());                     // landed → ClosingMaterialize
        Assert.Equal(1, service.MateriaExtracted);
        Assert.Contains(log.Lines, l => l == $"INF [Maintenance] Materia extracted from item {Gloves} (1 this session).");

        Assert.True(service.Tick());                     // closes the window
        Assert.Contains("CloseMaterialize", bridge.Calls);
        Assert.False(service.Tick());                    // Idle again, nothing due
        Assert.Null(service.BlockedReason);

        // Nothing else is at 100%: the next idle check does not reopen the window.
        clock.Advance(2);
        Assert.False(service.Tick());
        Assert.Equal(1, bridge.Calls.Count(c => c == "OpenMaterialize"));
    }

    [Fact]
    public void ExtractionIsSkippedWhenTheSettingIsOff()
    {
        var (service, bridge, _, _) = Build(autoExtract: false);

        Assert.False(service.Tick());
        Assert.Empty(bridge.Calls);
        Assert.Null(service.BlockedReason);
    }

    [Fact]
    public void ExtractionTimeoutIsNonFatalAndDisablesItForTheSession()
    {
        var (service, bridge, clock, log) = Build();
        bridge.MaterializeOpens = false;

        Assert.True(service.Tick());
        Assert.True(service.Tick());                     // the open request that never lands
        clock.Advance(11);
        Assert.False(service.Tick());                    // timed out: the run goes on
        Assert.Null(service.BlockedReason);
        Assert.Contains(log.Lines, l => l.StartsWith("WRN [Maintenance] the materia extraction window did not respond; materia extraction is off for this session"));
        Assert.Contains("CloseMaterialize", bridge.Calls);
        Assert.Equal(0, service.MateriaExtracted);

        // The piece is still at 100%, but extraction stays off until the next load.
        clock.Advance(2);
        Assert.False(service.Tick());
        Assert.Equal(1, bridge.Calls.Count(c => c == "OpenMaterialize"));
        Assert.Contains(service.Describe(), line => line.Contains("disabled True"));
    }

    [Fact]
    public void ExtractionFollowsRepairInTheSamePass()
    {
        var (service, bridge, _, _) = Build(autoRepair: true);
        bridge.Condition = 20f;
        bridge.ItemCounts[DarkMatterGrade8] = 3;

        // Repair: open, repair all, confirm, wait for condition, close.
        for (var i = 0; i < 9; i++)
            Assert.True(service.Tick());
        Assert.Equal(100f, bridge.Condition);
        Assert.DoesNotContain("Repair", bridge.Visible);

        // The closing tick chains straight into extraction instead of returning to the caller.
        Assert.True(service.Tick());
        Assert.Equal("Extracting materia...", service.StatusText);
        Assert.True(service.Tick());
        Assert.True(bridge.Calls.IndexOf("OpenRepairWindow") < bridge.Calls.IndexOf("OpenMaterialize"));
    }

    [Fact]
    public void FullBagSkipsExtractionWithoutDisablingIt()
    {
        var (service, bridge, clock, log) = Build();
        bridge.FreeSlots = 0;

        Assert.False(service.Tick());
        Assert.DoesNotContain("OpenMaterialize", bridge.Calls);
        Assert.Contains(log.Lines, l => l.Contains("the bag is full; skipping materia extraction"));
        Assert.Null(service.BlockedReason);

        bridge.FreeSlots = 5;
        clock.Advance(2);
        Assert.True(service.Tick());
        Assert.Equal("Extracting materia...", service.StatusText);
    }

    [Fact]
    public void AbortDuringExtractionDismissesTheMateriaWindow()
    {
        var (service, bridge, _, _) = Build();

        Assert.True(service.Tick());
        Assert.True(service.Tick());
        Assert.Contains("Materialize", bridge.Visible);

        service.Abort();
        Assert.Contains("CloseMaterialize", bridge.Calls);
        Assert.DoesNotContain("Materialize", bridge.Visible);
        Assert.Equal("", service.StatusText);
    }

    [Fact]
    public void DescribeReportsTheSessionCountAndReadySlots()
    {
        var (service, _, _, _) = Build();

        Assert.Contains(service.Describe(), line =>
            line.Contains("Materia extracted this session 0") && line.Contains("spiritbond-ready slots [3]"));
    }
}
