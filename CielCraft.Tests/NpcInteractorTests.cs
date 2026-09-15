using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Game;
using CielCraft.Npc;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// Drives <see cref="NpcInteractor"/> (roadmap 7.3) against a scripted bridge:
/// the teleport shows a loading screen, the NPC appears in the object table,
/// the interaction raises a menu and the menu raises the window the script
/// waits for. Also the mender repair on top of it (roadmap 7.3a): no Dark
/// Matter sends <see cref="MaintenanceService"/> to a mender instead of
/// blocking the run.
/// </summary>
public class NpcInteractorTests
{
    private const uint MenderZone = 819;
    private const uint FieldZone = 814;
    private const uint MenderNpcId = 1027851;
    private static readonly Vector3 MenderSpot = new(10, 0, 10);

    private static readonly NpcTarget Mender =
        new(MenderNpcId, "Axel", MenderZone, MenderSpot, MenderNpcId);

    private sealed class FakeNavigation : INavigationProvider
    {
        public bool IsAvailable => true;
        public bool IsReady => true;
        public bool IsMoving { get; set; }
        public int Stops;

        public bool MoveTo(Vector3 destination, bool fly)
        {
            IsMoving = true;
            return true;
        }

        public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly)
        {
            IsMoving = true;
            return true;
        }

        public void Stop()
        {
            Stops++;
            IsMoving = false;
        }

        public Vector3? FindNearestMeshPoint(Vector3 approximate, float halfExtentXZ, float halfExtentY) => approximate;

        public Vector3? FindPointOnFloor(Vector3 near, float halfExtentXZ) => near;
    }

    /// <summary>
    /// The slice of the bridge the interactor and the mender repair touch;
    /// everything else throws, so neither may wander into unscripted state.
    /// </summary>
    private sealed class FakeBridge : IGameBridge
    {
        public readonly HashSet<string> Visible = [];
        public readonly List<string> Calls = [];
        public readonly List<string> Options = [];
        public readonly Dictionary<uint, int> ItemCounts = new();

        public uint Territory = FieldZone;
        public bool BetweenAreas;
        public bool Teleports = true;
        public Vector3? Position = Vector3.Zero;
        public Vector3? NpcPosition = MenderSpot;
        public float Condition = 100f;

        /// <summary>What the interaction raises: the addon and the options it lists.</summary>
        public string InteractionRaises = "SelectString";

        public Vector3? PlayerPosition => Position;

        public bool IsMounted { get; set; }

        public void TryMount() => Calls.Add("Mount");

        public void TryDismount()
        {
            Calls.Add("Dismount");
            IsMounted = false;
        }

        public uint CurrentTerritoryId => Territory;

        public bool IsBetweenAreas => BetweenAreas;

        public bool CanTeleportTo(uint territoryId) => Teleports;

        public bool TeleportToTerritory(uint territoryId)
        {
            Calls.Add($"Teleport({territoryId})");
            if (!Teleports)
                return false;

            BetweenAreas = true;
            Territory = territoryId;
            return true;
        }

        public (ulong ObjectId, Vector3 Position)? FindNpcObject(uint dataId) =>
            NpcPosition is { } position && dataId == MenderNpcId ? (42UL, position) : null;

        public bool InteractWithObject(ulong objectId)
        {
            Calls.Add($"Interact({objectId})");
            if (InteractionRaises.Length > 0)
                Visible.Add(InteractionRaises);
            return true;
        }

        public IReadOnlyList<string> ReadDialogOptions() =>
            Visible.Contains("SelectString") || Visible.Contains("SelectIconString") ? Options : [];

        public bool SelectDialogOption(string textContains)
        {
            Calls.Add($"Option({textContains})");
            if (!Visible.Contains("SelectString") && !Visible.Contains("SelectIconString"))
                return false;

            if (!Options.Any(o => o.Contains(textContains, StringComparison.OrdinalIgnoreCase)))
                return false;

            Visible.Remove("SelectString");
            Visible.Remove("SelectIconString");
            Visible.Add("Repair");
            return true;
        }

        public bool AdvanceTalk()
        {
            Calls.Add("AdvanceTalk");
            if (!Visible.Contains("Talk"))
                return false;

            Visible.Remove("Talk");
            return true;
        }

        public bool IsAddonVisible(string addonName) => Visible.Contains(addonName);

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
                case ("SelectString", -1):
                case ("SelectIconString", -1):
                    Visible.Remove(addonName);
                    break;
            }

            return true;
        }

        // ---- the maintenance service's own slice ----

        public bool IsCrafting => false;
        public bool IsGathering => false;

        public float GetLowestEquipmentConditionPercent() => Condition;

        public int GetItemCount(uint itemId) => ItemCounts.GetValueOrDefault(itemId);

        public int GetHqItemCount(uint itemId) => 0;

        public int GetFreeInventorySlots() => 10;

        public float GetFoodBuffRemainingSeconds() => 3600f;

        public float GetMedicatedRemainingSeconds() => 3600f;

        public IReadOnlyList<int> GetSpiritbondReadySlots() => [];

        public IReadOnlyList<EquippedSpiritbond> GetEquipmentSpiritbond() => [];

        public void CloseRecipeNote() => Calls.Add("CloseRecipeNote");

        public void OpenRepairWindow()
        {
            Calls.Add("OpenRepairWindow");
            Visible.Add("Repair");
        }

        // ---- not reached ----

        private static NotImplementedException Unexpected() => new("the NPC layer must not touch this");

        public bool IsLoggedIn => throw Unexpected();
        public bool IsPreparingToCraft => throw Unexpected();
        public PlayerSnapshot? GetPlayerState() => throw Unexpected();
        public CraftSnapshot? GetCraftState() => throw Unexpected();
        public GatheringSnapshot? GetGatheringState() => throw Unexpected();
        public bool IsGatheringActionInProgress => throw Unexpected();
        public GatheringNodeSnapshot? FindNearestGatheringNode(IReadOnlyCollection<ulong>? excludedObjectIds = null, Vector3? origin = null) => throw Unexpected();
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
        public bool OpenMaterialize() => throw Unexpected();
        public bool ExtractMateria(int slot) => throw Unexpected();
        public bool ConfirmMaterializeDialog() => throw Unexpected();
        public bool IsMaterializing => throw Unexpected();
        public void CloseMaterialize() => throw Unexpected();
        public GatheringNodeFacts GetGatheringNodeFacts(ulong nodeObjectId, int slotIndex) => throw Unexpected();
        public float GetStatusRemainingSeconds(uint statusId) => throw Unexpected();
        public bool IsItemOnCooldown(uint itemId) => throw Unexpected();
    }

    private static (NpcInteractor Interactor, FakeBridge Bridge, FakeNavigation Nav, FakeClock Clock, ListLog Log) Build()
    {
        var bridge = new FakeBridge();
        bridge.Options.AddRange(["Repair Gear", "Cancel"]);
        var nav = new FakeNavigation();
        var clock = new FakeClock();
        var log = new ListLog();
        var interactor = new NpcInteractor(bridge, nav, log, clock, null, id => $"zone {id}");
        return (interactor, bridge, nav, clock, log);
    }

    private static readonly DialogStep[] RepairScript = [new SelectOption("Repair"), new WaitForAddon("Repair")];

    /// <summary>Teleport → travel → interact → menu → the window the script waits for.</summary>
    private static void RunToCompletion(NpcInteractor interactor, FakeBridge bridge, FakeClock clock)
    {
        interactor.Tick();                       // issues the teleport
        Assert.Contains($"Teleport({MenderZone})", bridge.Calls);
        interactor.Tick();                       // the loading screen
        bridge.BetweenAreas = false;
        interactor.Tick();                       // landed: the zone settles first
        Assert.Equal(NpcInteractionState.Teleporting, interactor.State);
        clock.Advance(Pacing.AfterZoneChange.TotalSeconds + 0.1);
        interactor.Tick();                       // → Traveling
        Assert.Equal(NpcInteractionState.Traveling, interactor.State);

        bridge.Position = MenderSpot;            // already at the NPC: the leg ends at once
        interactor.Tick();
        Assert.Equal(NpcInteractionState.Interacting, interactor.State);

        interactor.Tick();                       // a beat before clicking
        Assert.DoesNotContain("Interact(42)", bridge.Calls);
        clock.Advance(Pacing.BeforeInteract.TotalSeconds + 0.1);
        interactor.Tick();                       // interacts → the menu opens
        Assert.Contains("Interact(42)", bridge.Calls);

        interactor.Tick();                       // → InDialog
        Assert.Equal(NpcInteractionState.InDialog, interactor.State);

        interactor.Tick();                       // the menu just opened: not clicked yet
        Assert.DoesNotContain("Option(Repair)", bridge.Calls);
        clock.Advance(Pacing.AfterNodeOpen.TotalSeconds + 0.1);
        interactor.Tick();                       // picks "Repair Gear"
        Assert.Contains("Option(Repair)", bridge.Calls);

        interactor.Tick();                       // the menu closed → next step
        clock.Advance(Pacing.AfterNodeOpen.TotalSeconds + 0.1);
        interactor.Tick();                       // the Repair window has settled → done
    }

    [Fact]
    public void TeleportsTravelsInteractsAndDrivesTheScript()
    {
        var (interactor, bridge, _, clock, log) = Build();

        Assert.True(interactor.Start(Mender, RepairScript, "repairs at Axel"));
        RunToCompletion(interactor, bridge, clock);

        Assert.Equal(NpcInteractionState.Completed, interactor.State);
        Assert.Contains("Repair", bridge.Visible);
        Assert.Equal("", interactor.FailureReason);
        Assert.Contains(log.Lines, l => l.StartsWith("INF [Npc] Chose \"Repair\" at Axel"));
        Assert.Contains(interactor.Describe(), l => l.Contains("Target Axel"));
    }

    [Fact]
    public void ASecondInteractionIsRefusedWhileOneIsInFlight()
    {
        var (interactor, _, _, _, _) = Build();

        Assert.True(interactor.Start(Mender, RepairScript, "repairs"));
        Assert.False(interactor.Start(Mender, RepairScript, "repairs again"));
    }

    [Fact]
    public void AStepThatTimesOutFailsWithTheReasonAndClosesTheDialog()
    {
        var (interactor, bridge, _, clock, _) = Build();
        bridge.Territory = MenderZone;           // no teleport needed
        bridge.Position = MenderSpot;
        bridge.InteractionRaises = "Talk";       // the NPC only talks; no menu ever opens

        Assert.True(interactor.Start(Mender, [new SelectOption("Purchase"), new WaitForAddon("Shop")], "purchases"));
        interactor.Tick();                       // arrives
        clock.Advance(Pacing.BeforeInteract.TotalSeconds + 0.1);
        interactor.Tick();                       // interacts → Talk
        interactor.Tick();                       // → InDialog
        Assert.Equal(NpcInteractionState.InDialog, interactor.State);

        interactor.Tick();                       // clicks through the talk box while waiting
        Assert.Contains("AdvanceTalk", bridge.Calls);

        clock.Advance(11);
        interactor.Tick();
        Assert.Equal(NpcInteractionState.Failed, interactor.State);
        Assert.Contains("Purchase", interactor.FailureReason);
        Assert.Contains("Callback(SelectString,-1)", bridge.Calls);
    }

    [Fact]
    public void AWindowThatIsAlreadyOpenSkipsTheStepsBeforeIt()
    {
        var (interactor, bridge, _, clock, log) = Build();
        bridge.Territory = MenderZone;
        bridge.Position = MenderSpot;
        bridge.InteractionRaises = "Repair";     // a mender with nothing else to offer

        Assert.True(interactor.Start(Mender, RepairScript, "repairs"));
        interactor.Tick();                       // arrives
        clock.Advance(Pacing.BeforeInteract.TotalSeconds + 0.1);
        interactor.Tick();                       // interacts → the Repair window
        interactor.Tick();                       // → InDialog
        interactor.Tick();                       // skips the menu step; the window settles
        clock.Advance(Pacing.AfterNodeOpen.TotalSeconds + 0.1);
        interactor.Tick();

        Assert.Equal(NpcInteractionState.Completed, interactor.State);
        Assert.DoesNotContain("Option(Repair)", bridge.Calls);
        Assert.Contains(log.Lines, l => l.Contains("Repair is already open; skipping"));
    }

    [Fact]
    public void ManualMovementPausesTheInteractionAndResumeRestartsIt()
    {
        var (interactor, bridge, _, clock, _) = Build();
        bridge.Territory = MenderZone;
        bridge.Position = MenderSpot;

        Assert.True(interactor.Start(Mender, RepairScript, "repairs"));
        interactor.Tick();                       // arrives → Interacting
        Assert.Equal(NpcInteractionState.Interacting, interactor.State);
        interactor.Tick();                       // anchors where the character stands

        bridge.Position = MenderSpot + new Vector3(8, 0, 0);
        interactor.Tick();
        Assert.Equal(NpcInteractionState.Paused, interactor.State);

        interactor.Resume();
        Assert.Equal(NpcInteractionState.Interacting, interactor.State);
        bridge.Position = MenderSpot;            // the user stepped back to the NPC
        clock.Advance(Pacing.BeforeInteract.TotalSeconds + 0.1);
        interactor.Tick();
        Assert.Contains("Interact(42)", bridge.Calls);
    }

    [Fact]
    public void StopClosesWhateverDialogIsOpen()
    {
        var (interactor, bridge, nav, clock, _) = Build();
        bridge.Territory = MenderZone;
        bridge.Position = MenderSpot;

        Assert.True(interactor.Start(Mender, RepairScript, "repairs"));
        interactor.Tick();
        clock.Advance(Pacing.BeforeInteract.TotalSeconds + 0.1);
        interactor.Tick();                       // the menu is up
        Assert.Contains("SelectString", bridge.Visible);

        interactor.Stop();
        Assert.Equal(NpcInteractionState.Idle, interactor.State);
        Assert.DoesNotContain("SelectString", bridge.Visible);
        Assert.Contains("Callback(SelectYesno,1)", bridge.Calls);
        Assert.True(nav.Stops > 0);
    }

    [Fact]
    public void ATeleportThatIsRefusedEveryTimeFails()
    {
        var (interactor, bridge, _, clock, _) = Build();
        bridge.Teleports = false;

        Assert.True(interactor.Start(Mender, RepairScript, "repairs"));
        for (var i = 0; i < 3; i++)
        {
            interactor.Tick();
            clock.Advance(2.1);
        }

        Assert.Equal(NpcInteractionState.Failed, interactor.State);
        Assert.Contains("no attuned aetheryte in zone 819", interactor.FailureReason);
    }

    // ---- mender repair (roadmap 7.3a) ----

    private sealed class FakeMenders : IMenderLocator
    {
        public NpcTarget? Mender = NpcInteractorTests.Mender;

        public NpcTarget? NearestMender(uint territoryId, Vector3 position) => Mender;

        public string TerritoryName(uint territoryId) => $"zone {territoryId}";
    }

    private static (MaintenanceService Service, NpcInteractor Interactor, FakeBridge Bridge, FakeClock Clock, ListLog Log, FakeMenders Menders)
        BuildMaintenance(bool menderRepair = true)
    {
        var (interactor, bridge, _, clock, log) = Build();
        var menders = new FakeMenders();
        var settings = new AutomationSettings
        {
            AutoRepair = true,
            AutoExtractMateria = false,
            MenderRepair = menderRepair,
            RepairThresholdPercent = 50,
        };
        var service = new MaintenanceService(
            bridge, settings, log, clock, id => $"item {id}", interactor, menders);
        bridge.Condition = 20f;
        return (service, interactor, bridge, clock, log, menders);
    }

    [Fact]
    public void NoDarkMatterSendsTheCharacterToTheMenderAndRepairsThere()
    {
        var (service, interactor, bridge, clock, log, _) = BuildMaintenance();

        Assert.True(service.Tick());             // no Dark Matter → the mender trip starts
        Assert.Null(service.BlockedReason);
        Assert.Contains("CloseRecipeNote", bridge.Calls);
        Assert.Contains(log.Lines, l => l.Contains("no Dark Matter in the bag; going to Axel in zone 819"));

        // The service ticks the interactor itself while it owns the trip.
        RunToCompletion(interactor, bridge, clock);
        Assert.Equal(NpcInteractionState.Completed, interactor.State);

        Assert.True(service.Tick());             // → RepairingAll at the mender's window
        Assert.Contains(log.Lines, l => l.Contains("At Axel in zone 819; repairing all equipment."));

        for (var i = 0; i < 8; i++)
        {
            service.Tick();
            clock.Advance(2);
        }

        Assert.Equal(100f, bridge.Condition);
        Assert.DoesNotContain("Repair", bridge.Visible);
        Assert.Equal(NpcInteractionState.Idle, interactor.State);
        Assert.Contains(log.Lines, l => l.Contains("Repaired at Axel in zone 819"));
        Assert.False(service.Tick());
        Assert.Null(service.BlockedReason);
    }

    [Fact]
    public void NoMenderReachableBlocksWithTodaysReason()
    {
        var (service, _, _, _, _, menders) = BuildMaintenance();
        menders.Mender = null;

        Assert.False(service.Tick());
        Assert.Equal(
            "equipment needs repair but there is no Dark Matter in the inventory (no mender is reachable from here)",
            service.BlockedReason);
    }

    [Fact]
    public void MenderRepairOffKeepsTheOldBlockedReason()
    {
        var (service, _, _, _, _, _) = BuildMaintenance(menderRepair: false);

        Assert.False(service.Tick());
        Assert.StartsWith("equipment needs repair but there is no Dark Matter in the inventory", service.BlockedReason);
        Assert.Contains("mender repair is off", service.BlockedReason);
    }

    [Fact]
    public void AFailedMenderTripBlocksWithTheReasonAndStopsTheInteraction()
    {
        var (service, interactor, bridge, clock, _, _) = BuildMaintenance();
        bridge.Teleports = false;

        Assert.True(service.Tick());             // the trip starts
        for (var i = 0; i < 4; i++)
        {
            service.Tick();
            clock.Advance(2.1);
        }

        Assert.Equal(NpcInteractionState.Idle, interactor.State);
        Assert.NotNull(service.BlockedReason);
        Assert.Contains("the mender trip failed", service.BlockedReason);
    }

    [Fact]
    public void DarkMatterStillSelfRepairs()
    {
        var (service, _, bridge, _, log, _) = BuildMaintenance();
        bridge.ItemCounts[33916] = 3;

        Assert.True(service.Tick());
        Assert.Contains(log.Lines, l => l.Contains("self-repairing"));
        Assert.DoesNotContain(bridge.Calls, c => c.StartsWith("Teleport("));
    }
}
