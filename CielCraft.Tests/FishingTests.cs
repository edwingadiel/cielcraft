using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Fishing;
using CielCraft.Game;
using CielCraft.Sourcing;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// Fishing (roadmap 7.4): the database's lookups against fake sheet rows, and
/// the controller / source state flow against a scripted bridge that models
/// the game's fishing phases — cast puts the line in the water, a bite comes,
/// hooking puts a fish in the bag, Quit stows the rod.
/// </summary>
public class FishingTests
{
    // Item ids from the Item sheet (2026-09-15); the bait pairings are the
    // bundled table's, so these fish must stay in FishingBaitTable.
    private const uint BlackEel = 4958;      // <- Rat Tail
    private const uint RatTail = 2591;
    private const uint MoatCarp = 4947;      // <- Crayfish Ball
    private const uint CrayfishBall = 2588;
    private const uint Wentletrap = 20144;   // spearfishing only
    private const uint Mythril = 5065;       // not a fish at all

    private const uint UpperHathoeva = 153;  // The Black Shroud zones of the fake rows
    private const uint MiddleHathoeva = 153;
    private const uint FarAwayZone = 999;

    // ------------------------------------------------------------- fake sheets

    private sealed class FakeSheets : IFishingSheetReader
    {
        public readonly List<FishingSpotRow> Spots = [];
        public readonly List<FishRow> Fish = [];
        public readonly List<SpearfishRow> Spearfish = [];
        public readonly List<BaitRow> Baits = [];

        public IEnumerable<FishingSpotRow> ReadSpots() => Spots;
        public IEnumerable<FishRow> ReadFish() => Fish;
        public IEnumerable<SpearfishRow> ReadSpearfish() => Spearfish;
        public IEnumerable<BaitRow> ReadBaits() => Baits;
    }

    private static FishingSpotRow Spot(uint id, string name, uint territory, byte level, params uint[] items) =>
        new(id, name, territory, new Vector3(100, 0, 100), 25f, level, 2, false, items);

    private static FakeSheets Sheets()
    {
        var sheets = new FakeSheets();
        sheets.Spots.Add(Spot(11, "Upper Hathoeva River", UpperHathoeva, 20, BlackEel, MoatCarp));
        sheets.Spots.Add(Spot(12, "Middle Hathoeva River", MiddleHathoeva, 30, BlackEel));
        sheets.Spots.Add(Spot(99, "Faraway Pond", FarAwayZone, 5, BlackEel));
        sheets.Fish.Add(new FishRow(BlackEel, "Black Eel", "flavour", TimeRestricted: true, false, false, false));
        sheets.Fish.Add(new FishRow(MoatCarp, "Moat Carp", "flavour", false, false, false, false));
        sheets.Spearfish.Add(new SpearfishRow(Wentletrap, "Wentletrap", 0));
        sheets.Baits.Add(new BaitRow(RatTail, "Rat Tail", 15, IsTackle: true));
        sheets.Baits.Add(new BaitRow(CrayfishBall, "Crayfish Ball", 5, IsTackle: true));
        return sheets;
    }

    private static FishingDatabase Database(
        FakeSheets? sheets = null,
        int fisherLevel = 90,
        Func<uint, bool>? attuned = null,
        Dictionary<uint, FishingBaitChoice>? baitOverrides = null) =>
        new(
            sheets ?? Sheets(),
            () => CharacterCapabilities.Unknown with
            {
                IsKnown = true,
                JobLevels = new Dictionary<uint, int> { [GatheringActions.FisherJobId] = fisherLevel },
            },
            attuned,
            baitOverrides);

    // ------------------------------------------------------------- fake bridge

    /// <summary>
    /// The slice of the bridge fishing touches, modelling the game's fishing
    /// phases. Every other member throws: the controller must not wander into
    /// state a test did not script.
    /// </summary>
    private sealed class FakeBridge : IGameBridge
    {
        public readonly Dictionary<uint, int> ItemCounts = new();
        public readonly List<string> Calls = [];

        public uint Territory = UpperHathoeva;
        public uint JobId = GatheringActions.FisherJobId;
        public bool Gearset = true;
        public int FreeSlots = 20;
        public float RodCondition = 100f;
        public Vector3 Position = new(100, 0, 100);
        public bool Fishing;
        public FishingPhase Phase = FishingPhase.None;
        public bool CanFish = true;
        public bool CanMooch;
        public uint Bait;
        public bool CastReady = true;
        public FishingTug Tug = FishingTug.Unknown;

        /// <summary>An action the game refuses (0 = none), e.g. a hookset the character cannot afford.</summary>
        public uint RefuseAction;

        /// <summary>The fish a Hook puts in the bag; 0 = the hook catches nothing.</summary>
        public uint CatchItemId = BlackEel;

        public bool IsLoggedIn => true;
        public bool IsCrafting => false;
        public bool IsPreparingToCraft => false;
        public bool IsGathering => false;
        public uint CurrentTerritoryId => Territory;
        public uint CurrentClassJobId => JobId;
        public bool IsBetweenAreas => false;
        public Vector3? PlayerPosition => Position;
        public bool IsMounted => false;
        public void TryMount() => Calls.Add("Mount");
        public void TryDismount() => Calls.Add("Dismount");

        public uint Gp = 600;

        public PlayerSnapshot? GetPlayerState() =>
            new("Tester", Territory, Position, JobId, "FSH", 90, 0, 0, 0, 0, Gp, 800);

        public int GetItemCount(uint itemId) => ItemCounts.GetValueOrDefault(itemId);
        public int GetHqItemCount(uint itemId) => 0;
        public int GetFreeInventorySlots() => FreeSlots;
        public bool HasGearsetForJob(uint classJobId) => Gearset;
        public float GetMainHandConditionPercent() => RodCondition;
        public bool IsItemOnCooldown(uint itemId) => false;
        public bool UseItem(uint itemId) => false;

        public bool EquipGearsetForJob(uint classJobId)
        {
            Calls.Add($"Gearset({classJobId})");
            if (!Gearset)
                return false;

            JobId = classJobId;
            return true;
        }

        public bool TeleportToTerritory(uint territoryId)
        {
            Calls.Add($"Teleport({territoryId})");
            Territory = territoryId;
            return true;
        }

        /// <summary>The game only builds the fishing event handler once the character has fished.</summary>
        public bool HandlerUp = true;

        public FishingSnapshot? GetFishingState() =>
            HandlerUp ? new FishingSnapshot(Phase, CanFish, CanMooch, false, Bait, Tug) : null;

        public bool IsFishing => Fishing;

        public bool SelectBait(uint baitItemId)
        {
            Calls.Add($"Bait({baitItemId})");
            Bait = baitItemId;
            return true;
        }

        public bool IsCraftActionReady(uint craftActionId) =>
            craftActionId != RefuseAction && (craftActionId != 289 || CastReady);

        public bool ExecuteCraftAction(uint craftActionId)
        {
            Calls.Add($"Action({craftActionId})");
            if (craftActionId == RefuseAction)
                return false;

            switch (craftActionId)
            {
                case 289: // Cast
                    Fishing = true;
                    Phase = FishingPhase.LineInWater;
                    break;
                case 296: // Hook
                case 4103:
                case 4179:
                    Phase = FishingPhase.Hooking;
                    if (CatchItemId != 0)
                        ItemCounts[CatchItemId] = ItemCounts.GetValueOrDefault(CatchItemId) + 1;
                    break;
                case 297: // Mooch
                    Phase = FishingPhase.LineInWater;
                    CanMooch = false;
                    break;
                case 299: // Quit
                    Fishing = false;
                    Phase = FishingPhase.None;
                    break;
            }

            return true;
        }

        /// <summary>The fish takes the bait.</summary>
        public void Bite() => Phase = FishingPhase.Bite;

        /// <summary>The reeling-in animation ends and the pole is ready again.</summary>
        public void PoleReady() => Phase = FishingPhase.PoleReady;

        // ---- not reached by fishing ----

        private static NotImplementedException Unexpected() => new("the fishing run must not touch this");

        public CraftSnapshot? GetCraftState() => throw Unexpected();
        public GatheringSnapshot? GetGatheringState() => throw Unexpected();
        public bool IsGatheringActionInProgress => throw Unexpected();
        public GatheringNodeSnapshot? FindNearestGatheringNode(IReadOnlyCollection<ulong>? excludedObjectIds = null, Vector3? origin = null) => throw Unexpected();
        public bool InteractWithObject(ulong objectId) => throw Unexpected();
        public bool GatherSlot(int slotIndex) => throw Unexpected();
        public void CloseGatheringWindow() => throw Unexpected();
        public bool IsReadyToStartCraft => throw Unexpected();
        public ushort SelectedRecipeId => throw Unexpected();
        public string DescribeRecipeSelection() => throw Unexpected();
        public bool StartSynthesis() => throw Unexpected();
        public (uint ItemId, int Amount)? CurrentCraftResult => throw Unexpected();
        public IReadOnlyList<IngredientRequirement> GetRecipeRequirements(ushort recipeId) => throw Unexpected();
        public void OpenRecipe(uint recipeId) => throw Unexpected();
        public void CloseRecipeNote() => throw Unexpected();
        public bool TeleportHome(CraftingLocation location, out uint territoryId) => throw Unexpected();
        public bool IsQuickSynthAvailable => throw Unexpected();
        public bool OpenQuickSynthesisDialog() => throw Unexpected();
        public bool ConfirmQuickSynthesisDialog(int count) => throw Unexpected();
        public bool IsQuickSynthesisActive => throw Unexpected();
        public void CancelQuickSynthesis() => throw Unexpected();
        public bool IsQuickGatheringEnabled => throw Unexpected();
        public void DisableQuickGathering() => throw Unexpected();
        public CollectableGatheringSnapshot? GetCollectableGatheringState() => throw Unexpected();
        public int GetStoredItemCount(uint itemId) => throw Unexpected();
        public float GetLowestEquipmentConditionPercent() => throw Unexpected();
        public void OpenRepairWindow() => throw Unexpected();
        public bool PlaySoundEffect(int soundEffectNumber) => throw Unexpected();
        public void ExecuteChatCommand(string command) => throw Unexpected();
        public bool IsAddonVisible(string addonName) => throw Unexpected();
        public bool FireAddonCallbackInt(string addonName, int value) => throw Unexpected();
        public IReadOnlyList<string> ReadAddonStrings(string addonName) => throw Unexpected();
        public bool IsPartyOrFreeCompanyMember(string playerName) => throw Unexpected();
        public float GetFoodBuffRemainingSeconds() => throw Unexpected();
        public float GetMedicatedRemainingSeconds() => throw Unexpected();
        public IReadOnlyList<ConsumableItem> ListConsumables() => throw Unexpected();
        public bool FillHqIngredients() => throw Unexpected();
        public bool FillIngredients(bool preferHq) => throw Unexpected();
        public bool AreIngredientsAssigned() => throw Unexpected();
        public IReadOnlyList<EquippedSpiritbond> GetEquipmentSpiritbond() => throw Unexpected();
        public IReadOnlyList<int> GetSpiritbondReadySlots() => throw Unexpected();
        public bool OpenMaterialize() => throw Unexpected();
        public bool ExtractMateria(int slot) => throw Unexpected();
        public bool ConfirmMaterializeDialog() => throw Unexpected();
        public bool IsMaterializing => throw Unexpected();
        public void CloseMaterialize() => throw Unexpected();
        public GatheringNodeFacts GetGatheringNodeFacts(ulong nodeObjectId, int slotIndex) => throw Unexpected();
        public float GetStatusRemainingSeconds(uint statusId) => throw Unexpected();
    }

    private sealed class FakeNavigation : INavigationProvider
    {
        public bool IsMoving { get; set; }
        public bool IsAvailable => true;
        public bool IsReady => true;
        public bool MoveTo(Vector3 destination, bool fly) => true;
        public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly) => true;
        public void Stop() => IsMoving = false;
        public Vector3? FindNearestMeshPoint(Vector3 approximate, float halfExtentXZ, float halfExtentY) => approximate;
        public Vector3? FindPointOnFloor(Vector3 near, float halfExtentXZ) => near;
    }

    private sealed class Harness
    {
        public required FakeBridge Bridge { get; init; }
        public required FakeClock Clock { get; init; }
        public required ListLog Log { get; init; }
        public required FishingController Controller { get; init; }
        public required FishingDatabase Database { get; init; }
        public required AutomationSettings Settings { get; init; }

        /// <summary>Ticks with a second of game time per tick until the predicate holds; false when it never does.</summary>
        public bool PumpUntil(Func<bool> done, int maxTicks = 60)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                if (done())
                    return true;

                Controller.Tick();
                Clock.Advance(1);
            }

            return done();
        }
    }

    private static Harness Build(
        int fisherLevel = 90,
        FakeSheets? sheets = null,
        Dictionary<uint, FishingBaitChoice>? baitOverrides = null)
    {
        var clock = new FakeClock();
        var log = new ListLog();
        var bridge = new FakeBridge();
        bridge.ItemCounts[RatTail] = 50;
        bridge.ItemCounts[CrayfishBall] = 50;
        var settings = new AutomationSettings { UseCordials = false, FishingPreferAutoHook = false };
        var database = Database(sheets, fisherLevel, baitOverrides: baitOverrides);
        var controller = new FishingController(
            bridge, new FakeNavigation(), settings, database, log, clock,
            capabilities: () => CharacterCapabilities.Unknown with
            {
                IsKnown = true,
                JobLevels = new Dictionary<uint, int> { [GatheringActions.FisherJobId] = fisherLevel },
            });

        return new Harness
        {
            Bridge = bridge, Clock = clock, Log = log,
            Controller = controller, Database = database, Settings = settings,
        };
    }

    // ---------------------------------------------------------------- database

    [Fact]
    public void FindSpotPrefersTheCurrentZoneThenAnAttunedOneThenTheEasiestWater()
    {
        var database = Database();

        // Faraway Pond is the lowest-level hole, but the character is standing
        // in the Hathoeva zone: the hole here wins.
        var here = database.FindSpot(BlackEel, UpperHathoeva);
        Assert.NotNull(here);
        Assert.Equal(UpperHathoeva, here!.Spot.TerritoryId);
        Assert.Equal(RatTail, here.BaitItemId);
        Assert.Equal("Rat Tail", here.BaitName);

        // Somewhere else entirely: the lowest-level hole with an attuned
        // aetheryte wins, and an unattuned zone loses to an attuned one.
        var anywhere = Database(attuned: t => t != FarAwayZone).FindSpot(BlackEel, currentTerritoryId: 0);
        Assert.Equal(UpperHathoeva, anywhere!.Spot.TerritoryId);
        Assert.Equal(11u, anywhere.Spot.SpotId);

        var farAllowed = Database().FindSpot(BlackEel, currentTerritoryId: 0);
        Assert.Equal(FarAwayZone, farAllowed!.Spot.TerritoryId);
    }

    [Fact]
    public void FindSpotSkipsHolesAboveTheFisherLevel()
    {
        var sheets = Sheets();
        sheets.Spots.RemoveAll(s => s.TerritoryId == FarAwayZone);
        var database = Database(sheets, fisherLevel: 25);

        // Level 25 reaches the level-20 hole but not the level-30 one.
        Assert.Equal(11u, database.FindSpot(BlackEel, 0, fisherLevel: 25)!.Spot.SpotId);
        Assert.Null(database.FindSpot(BlackEel, 0, fisherLevel: 10));
    }

    [Fact]
    public void SpearfishingIsIndexedAndRefusedWithAReason()
    {
        var database = Database();

        Assert.True(database.IsSpearfish(Wentletrap));
        Assert.False(database.IsFish(Wentletrap));
        Assert.Contains("spear fishing", database.RefusalReason(Wentletrap));
        Assert.Null(database.FindSpot(Wentletrap));
    }

    [Fact]
    public void AFishWithNoBundledBaitIsRefusedRatherThanCastForBlindly()
    {
        var sheets = Sheets();
        const uint unknownFish = 4999;
        sheets.Spots.Add(Spot(200, "Nameless Pool", UpperHathoeva, 10, unknownFish));
        sheets.Fish.Add(new FishRow(unknownFish, "Nameless Fish", "", false, false, false, false));
        var database = Database(sheets);

        Assert.True(database.IsFish(unknownFish));
        Assert.Null(database.FindSpot(unknownFish, UpperHathoeva));
        Assert.Contains("no bait is known", database.RefusalReason(unknownFish));

        // An item that is not a fish is not this database's business at all.
        Assert.False(database.IsFish(Mythril));
        Assert.Null(database.RefusalReason(Mythril));
    }

    [Fact]
    public void ADatabaseWithoutSpotsKnowsNothingAndSaysSo()
    {
        var database = Database(new FakeSheets());

        Assert.False(database.IsFish(BlackEel));
        Assert.Contains(database.Describe(), l => l.Contains("0 fish at known holes"));
    }

    private sealed class BrokenSheets : IFishingSheetReader
    {
        public IEnumerable<FishingSpotRow> ReadSpots() => throw new InvalidOperationException("sheet missing");
        public IEnumerable<FishRow> ReadFish() => [];
        public IEnumerable<SpearfishRow> ReadSpearfish() => [];
        public IEnumerable<BaitRow> ReadBaits() => [];
    }

    [Fact]
    public void SheetsThatCannotBeReadLeaveTheRestOfThePluginWorking()
    {
        var database = new FishingDatabase(new BrokenSheets());

        Assert.False(database.IsFish(BlackEel));
        Assert.Null(database.FindSpot(BlackEel));
        Assert.Null(database.RefusalReason(BlackEel));
        Assert.Contains(database.Describe(), l => l.Contains("THE SHEETS COULD NOT BE READ: sheet missing"));
    }

    // -------------------------------------------------------------- controller

    [Fact]
    public void ARunTravelsEquipsBaitsCastsHooksAndStopsAtTheTarget()
    {
        var harness = Build();
        harness.Bridge.JobId = 8; // on a crafter: the gearset has to change

        Assert.True(harness.Controller.Start(BlackEel, 1));
        Assert.Equal(FishingRunState.PreparingJob, harness.Controller.State);

        // Gearset, then travel (the player already stands at the hole), then bait.
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));
        Assert.Contains($"Gearset({GatheringActions.FisherJobId})", harness.Bridge.Calls);
        Assert.Contains($"Bait({RatTail})", harness.Bridge.Calls);
        Assert.Equal(RatTail, harness.Bridge.Bait);

        // The pole is ready: one cast, confirmed by the line going into the water.
        harness.Bridge.PoleReady();
        Assert.True(harness.PumpUntil(() => harness.Bridge.Calls.Contains("Action(289)")));
        Assert.Equal(FishingPhase.LineInWater, harness.Bridge.Phase);

        // A bite: one hook, and the fish lands in the bag.
        harness.Bridge.Bite();
        Assert.True(harness.PumpUntil(() => harness.Bridge.Calls.Contains("Action(296)")));
        Assert.Equal(1, harness.Controller.Caught);

        // The pole comes back and the target is met: the rod goes away.
        harness.Bridge.PoleReady();
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Completed));
        Assert.Contains("Action(299)", harness.Bridge.Calls);
        Assert.False(harness.Bridge.Fishing);
        Assert.Contains(harness.Log.Lines, l => l.Contains("[Fishing] Caught 1× Black Eel (1/1"));
    }

    [Fact]
    public void ACastIsFiredOnceAndOnlyRepeatedAfterTheStateMovesOn()
    {
        var harness = Build();
        Assert.True(harness.Controller.Start(BlackEel, 5));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));

        harness.Bridge.PoleReady();
        Assert.True(harness.PumpUntil(() => harness.Bridge.Calls.Contains("Action(289)")));

        // The line is out: ten more ticks must not produce a second cast.
        for (var i = 0; i < 10; i++)
        {
            harness.Controller.Tick();
            harness.Clock.Advance(1);
        }

        Assert.Equal(1, harness.Bridge.Calls.Count(c => c == "Action(289)"));
    }

    [Fact]
    public void AFullBagAndABrokenRodPauseTheRun()
    {
        var harness = Build();
        Assert.True(harness.Controller.Start(BlackEel, 5));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));

        harness.Bridge.FreeSlots = 0;
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Paused));
        Assert.Equal("inventory is full", harness.Controller.FailureReason);

        harness.Bridge.FreeSlots = 20;
        harness.Controller.Resume();
        Assert.Equal(FishingRunState.Traveling, harness.Controller.State);

        harness.Bridge.RodCondition = 0f;
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Paused));
        Assert.Equal("the fishing rod is broken", harness.Controller.FailureReason);
    }

    [Fact]
    public void FruitlessCastsPauseTheRunSoAWrongBundledBaitCostsMinutes()
    {
        var harness = Build();
        harness.Bridge.CatchItemId = MoatCarp; // the hole gives the wrong fish
        Assert.True(harness.Controller.Start(BlackEel, 1));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));

        for (var i = 0; i < FishingController.MaxCastsWithoutTarget + 2; i++)
        {
            harness.Bridge.PoleReady();
            harness.PumpUntil(() => harness.Bridge.Phase == FishingPhase.LineInWater, maxTicks: 10);
            harness.Bridge.Bite();
            harness.PumpUntil(() => harness.Bridge.Phase == FishingPhase.Hooking, maxTicks: 10);
            if (harness.Controller.State == FishingRunState.Paused)
                break;
        }

        Assert.Equal(FishingRunState.Paused, harness.Controller.State);
        Assert.Contains("bundled bait table may be wrong", harness.Controller.FailureReason);
        Assert.Equal(0, harness.Controller.Caught);
    }

    [Fact]
    public void AResumedRunGetsAFreshBudgetOfCastsAndItsJobBack()
    {
        var harness = Build();
        harness.Bridge.CatchItemId = MoatCarp;
        Assert.True(harness.Controller.Start(BlackEel, 1));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));

        for (var i = 0; i < FishingController.MaxCastsWithoutTarget + 2; i++)
        {
            harness.Bridge.PoleReady();
            harness.PumpUntil(() => harness.Bridge.Phase == FishingPhase.LineInWater, maxTicks: 10);
            harness.Bridge.Bite();
            harness.PumpUntil(() => harness.Bridge.Phase == FishingPhase.Hooking, maxTicks: 10);
            if (harness.Controller.State == FishingRunState.Paused)
                break;
        }

        Assert.Equal(FishingRunState.Paused, harness.Controller.State);

        // Resuming has to actually resume: the fruitless-cast count starts over
        // instead of pausing again on the first tick.
        harness.Controller.Resume();
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing, maxTicks: 30));
        for (var i = 0; i < 5; i++)
        {
            harness.Controller.Tick();
            harness.Clock.Advance(1);
        }

        Assert.NotEqual(FishingRunState.Paused, harness.Controller.State);
    }

    [Fact]
    public void LeavingTheFisherJobPausesAndResumingPutsTheGearsetBackOn()
    {
        var harness = Build();
        Assert.True(harness.Controller.Start(BlackEel, 5));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));

        harness.Bridge.JobId = 8; // the user swapped to a crafter
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Paused));
        Assert.Equal("the character is no longer on FSH", harness.Controller.FailureReason);
        Assert.StartsWith("Paused:", harness.Controller.StatusText);

        harness.Bridge.Calls.Clear();
        harness.Controller.Resume();
        Assert.Equal(FishingRunState.PreparingJob, harness.Controller.State);
        Assert.True(harness.PumpUntil(() => harness.Bridge.JobId == GatheringActions.FisherJobId, maxTicks: 20));
        Assert.Contains($"Gearset({GatheringActions.FisherJobId})", harness.Bridge.Calls);
    }

    [Fact]
    public void ARefusedHooksetFallsStraightThroughToPlainHook()
    {
        var harness = Build();
        // Level 90, and a tug the bridge reports as strong: Powerful Hookset is
        // chosen, the game refuses it, and the fish must still be hooked.
        harness.Bridge.Tug = FishingTug.Strong;
        harness.Bridge.RefuseAction = 4103;

        Assert.True(harness.Controller.Start(BlackEel, 1));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));
        harness.Bridge.PoleReady();
        Assert.True(harness.PumpUntil(() => harness.Bridge.Phase == FishingPhase.LineInWater));

        harness.Bridge.Bite();
        harness.Controller.Tick();

        Assert.DoesNotContain("Action(4103)", harness.Bridge.Calls); // the game refused it outright
        Assert.Contains("Action(296)", harness.Bridge.Calls);        // plain Hook, same tick
        Assert.Equal(1, harness.Controller.Caught);

        // With the hookset affordable and allowed, it is the one that fires.
        var other = Build();
        other.Bridge.Tug = FishingTug.Strong;
        Assert.True(other.Controller.Start(BlackEel, 1));
        Assert.True(other.PumpUntil(() => other.Controller.State == FishingRunState.Fishing));
        other.Bridge.PoleReady();
        Assert.True(other.PumpUntil(() => other.Bridge.Phase == FishingPhase.LineInWater));
        other.Bridge.Bite();
        other.Controller.Tick();
        Assert.Contains("Action(4103)", other.Bridge.Calls);
        Assert.DoesNotContain("Action(296)", other.Bridge.Calls);
    }

    [Fact]
    public void AMissingSpotGearsetOrBaitRefusesToStartWithTheReason()
    {
        var harness = Build();

        harness.Bridge.ItemCounts[RatTail] = 0;
        Assert.False(harness.Controller.Start(BlackEel, 1));
        Assert.Contains("no Rat Tail in the bag", harness.Controller.StatusText);

        harness.Bridge.ItemCounts[RatTail] = 10;
        harness.Bridge.Gearset = false;
        Assert.False(harness.Controller.Start(BlackEel, 1));
        Assert.Contains("no FSH gearset", harness.Controller.StatusText);

        harness.Bridge.Gearset = true;
        Assert.False(harness.Controller.Start(Wentletrap, 1));
        Assert.Contains("spear fishing", harness.Controller.StatusText);
    }

    [Fact]
    public void StopPutsTheRodAwayAndLeavesTheRunIdle()
    {
        var harness = Build();
        Assert.True(harness.Controller.Start(BlackEel, 5));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));
        harness.Bridge.PoleReady();
        Assert.True(harness.PumpUntil(() => harness.Bridge.Fishing));

        harness.Controller.Stop();

        Assert.Equal(FishingRunState.Idle, harness.Controller.State);
        Assert.Contains("Action(299)", harness.Bridge.Calls);
        Assert.False(harness.Bridge.Fishing);
    }

    [Fact]
    public void ARunInAnotherZoneTeleportsFirst()
    {
        var sheets = Sheets();
        sheets.Spots.RemoveAll(s => s.TerritoryId != FarAwayZone);
        var harness = Build(sheets: sheets);
        harness.Bridge.Territory = UpperHathoeva;

        Assert.True(harness.Controller.Start(BlackEel, 1));
        Assert.True(harness.PumpUntil(() => harness.Bridge.Calls.Any(c => c.StartsWith("Teleport("))));
        Assert.Contains($"Teleport({FarAwayZone})", harness.Bridge.Calls);
    }

    [Fact]
    public void ATimeRestrictedFishIsFishedForAnywayButTheLogSaysTheSheetsDoNotKnowWhen()
    {
        var harness = Build();

        Assert.True(harness.Controller.Start(BlackEel, 1));
        Assert.Contains(harness.Log.Lines, l =>
            l.StartsWith("WRN [Fishing] Black Eel is time") && l.Contains("the game sheets do not say when"));
    }

    [Fact]
    public void BeforeTheFirstCastTheBaitCannotBeReadBackSoTheRunCastsAnyway()
    {
        var harness = Build();
        harness.Bridge.HandlerUp = false; // no fishing event handler yet

        Assert.True(harness.Controller.Start(BlackEel, 1));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));
        Assert.Contains($"Bait({RatTail})", harness.Bridge.Calls);
        Assert.Contains(harness.Log.Lines, l => l.Contains("could not be confirmed; casting anyway"));

        // Cast still goes out: the catch is what settles whether the bait was right.
        Assert.True(harness.PumpUntil(() => harness.Bridge.Calls.Contains("Action(289)")));

        // But the rod is out and the game still reports nothing: stop rather
        // than cast into the dark forever.
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Paused, maxTicks: 30));
        Assert.Contains("fishing event handler never came up", harness.Controller.FailureReason);
    }

    [Fact]
    public void AUserBaitPairingOverridesTheBundledTableAndCanAskForAMooch()
    {
        var overrides = new Dictionary<uint, FishingBaitChoice>
        {
            [BlackEel] = new() { BaitItemId = CrayfishBall, MoochFromItemId = MoatCarp },
        };
        var harness = Build(baitOverrides: overrides);

        var plan = harness.Database.FindSpot(BlackEel, UpperHathoeva);
        Assert.Equal(CrayfishBall, plan!.BaitItemId);
        Assert.True(plan.NeedsMooch);

        Assert.True(harness.Controller.Start(BlackEel, 1));
        Assert.True(harness.PumpUntil(() => harness.Controller.State == FishingRunState.Fishing));
        Assert.Contains($"Bait({CrayfishBall})", harness.Bridge.Calls);

        // Nothing on the line yet: a plain cast.
        harness.Bridge.PoleReady();
        Assert.True(harness.PumpUntil(() => harness.Bridge.Calls.Contains("Action(289)")));

        // The base fish is on the line and the game says it can be mooched:
        // mooch it instead of casting again.
        harness.Bridge.PoleReady();
        harness.Bridge.CanMooch = true;
        Assert.True(harness.PumpUntil(() => harness.Bridge.Calls.Contains("Action(297)")));
        Assert.Equal(1, harness.Bridge.Calls.Count(c => c == "Action(289)"));
    }

    // ------------------------------------------------------------------ source

    private sealed class FakeVendor : IMaterialSource
    {
        public readonly List<uint> Asked = [];
        public FakeVendorRun? LastRun;
        public bool Sells = true;

        public MaterialSourceKind Kind => MaterialSourceKind.Buy;
        public string Name => "vendor";

        public SourceOffer? Offer(uint itemId, int amount)
        {
            Asked.Add(itemId);
            return Sells ? new SourceOffer(itemId, amount, MaterialSourceKind.Buy, $"Buy {amount}× item {itemId}", 60, 100) : null;
        }

        public ISourceRun Start(SourceOffer offer) => LastRun = new FakeVendorRun(offer);
    }

    private sealed class FakeVendorRun : ISourceRun
    {
        private readonly SourceOffer offer;

        public FakeVendorRun(SourceOffer offer) => this.offer = offer;

        public Action? OnTick { get; set; }
        public SourceRunState State { get; set; } = SourceRunState.Running;
        public string StatusText => $"Buying item {offer.ItemId}.";
        public int Obtained { get; set; }
        public int Ticks { get; private set; }

        public void Tick()
        {
            Ticks++;
            OnTick?.Invoke();
        }

        public void Pause(string reason) => State = SourceRunState.Paused;
        public void Resume() => State = SourceRunState.Running;
        public void Stop() => State = SourceRunState.Idle;
        public IEnumerable<string> Describe() => ["fake vendor run"];
    }

    private static FishingSource Source(Harness harness, IMaterialSource? vendor = null) =>
        new(harness.Bridge, harness.Database, harness.Controller, harness.Log, harness.Clock, vendor);

    [Fact]
    public void TheSourceOffersOnlyWhatItCanActuallyFish()
    {
        var harness = Build();
        var source = Source(harness);

        var offer = source.Offer(BlackEel, 3);
        Assert.NotNull(offer);
        Assert.Equal(MaterialSourceKind.Fish, offer!.Kind);
        Assert.Equal(3, offer.Amount);
        Assert.Equal(0, offer.GilCost);
        Assert.Contains("Black Eel", offer.Description);
        Assert.Contains("Rat Tail", offer.Description);
        Assert.True(offer.EstimatedSeconds > 0);

        // Not a fish, a spearfishing catch, or no bait in the bag and no vendor.
        Assert.Null(source.Offer(Mythril, 1));
        Assert.Null(source.Offer(Wentletrap, 1));
        harness.Bridge.ItemCounts[RatTail] = 0;
        Assert.Null(source.Offer(BlackEel, 1));
        Assert.Contains(harness.Log.Lines, l => l.Contains("no Rat Tail in the bag and no vendor sells it"));
    }

    [Fact]
    public void WithoutBaitTheSourceChainsAVendorRunAndPaysForIt()
    {
        var harness = Build();
        harness.Bridge.ItemCounts[RatTail] = 0;
        var vendor = new FakeVendor();
        var source = Source(harness, vendor);

        var offer = source.Offer(BlackEel, 2);
        Assert.NotNull(offer);
        Assert.Contains(RatTail, vendor.Asked);
        Assert.Equal(100, offer!.GilCost);
        Assert.Contains("buying Rat Tail on the way", offer.Description);

        // The run buys the bait before it starts fishing.
        var run = source.Start(offer);
        run.Tick();
        Assert.NotNull(vendor.LastRun);
        Assert.Equal(FishingRunState.Idle, harness.Controller.State);

        vendor.LastRun!.OnTick = () => harness.Bridge.ItemCounts[RatTail] = 99;
        vendor.LastRun.State = SourceRunState.Completed;
        run.Tick();
        run.Tick();
        Assert.Equal(99, harness.Bridge.GetItemCount(RatTail));
        Assert.NotEqual(FishingRunState.Idle, harness.Controller.State);
        Assert.Equal(SourceRunState.Running, run.State);
    }

    [Fact]
    public void TheRunFinishesWhenTheBagHoldsTheFish()
    {
        var harness = Build();
        var source = Source(harness);
        var run = source.Start(source.Offer(BlackEel, 1)!);

        Assert.Equal(SourceRunState.Running, run.State);
        for (var i = 0; i < 40 && run.State == SourceRunState.Running; i++)
        {
            run.Tick();
            harness.Clock.Advance(1);
            if (harness.Controller.State == FishingRunState.Fishing)
            {
                if (harness.Bridge.Phase == FishingPhase.LineInWater)
                    harness.Bridge.Bite();
                else if (harness.Bridge.Phase == FishingPhase.None)
                    harness.Bridge.PoleReady();
            }
        }

        Assert.Equal(SourceRunState.Completed, run.State);
        Assert.Equal(1, run.Obtained);
        Assert.Contains(run.Describe(), l => l.Contains("Black Eel"));
    }

    [Fact]
    public void ABaitRunThatComesBackEmptyFailsInsteadOfShoppingForever()
    {
        var harness = Build();
        harness.Bridge.ItemCounts[RatTail] = 0;
        var vendor = new FakeVendor();
        var source = Source(harness, vendor);
        var run = source.Start(source.Offer(BlackEel, 1)!);

        run.Tick();
        Assert.NotNull(vendor.LastRun);

        // The vendor says it is done but nothing landed in the bag.
        vendor.LastRun!.State = SourceRunState.Completed;
        run.Tick();
        run.Tick();

        Assert.Equal(SourceRunState.Failed, run.State);
        Assert.Contains("no Rat Tail in the bag", run.StatusText);
    }

    [Fact]
    public void TwoRunsCannotFightOverTheOneRod()
    {
        var harness = Build();
        var source = Source(harness);
        var first = source.Start(source.Offer(BlackEel, 5)!);
        first.Tick();
        Assert.Equal(SourceRunState.Running, first.State);
        Assert.True(harness.Controller.IsBusy);

        var second = source.Start(source.Offer(BlackEel, 5)!);
        second.Tick();

        Assert.Equal(SourceRunState.Failed, second.State);
        Assert.Contains("already using the rod", second.StatusText);

        // The second run's failure must not have stowed the first run's rod.
        Assert.Equal(SourceRunState.Running, first.State);
        Assert.True(harness.Controller.IsBusy);
    }

    [Fact]
    public void AFailedRunReportsTheControllerReason()
    {
        var harness = Build();
        harness.Bridge.Gearset = false;
        var source = Source(harness);

        // The offer is refused outright without a gearset...
        Assert.Null(source.Offer(BlackEel, 1));

        // ...and a run started from a stale offer fails instead of hanging.
        harness.Bridge.Gearset = true;
        var offer = source.Offer(BlackEel, 1)!;
        harness.Bridge.Gearset = false;
        var run = source.Start(offer);
        run.Tick();
        Assert.Equal(SourceRunState.Failed, run.State);
        Assert.Contains("FSH gearset", run.StatusText);
    }
}
