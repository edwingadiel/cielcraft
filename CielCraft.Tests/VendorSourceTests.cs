using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Game;
using CielCraft.Sourcing;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// The vendor source (roadmap 7.3b) against fakes: the offer maths (gil
/// floor, per-run cap, price × amount), the vendor ordering rule, and one
/// shopping trip driven step by step — interactor, Shop window, batched
/// purchases verified by inventory delta, close.
/// </summary>
public class VendorSourceTests
{
    private const uint RockSalt = 5518;
    private const uint Unsold = 9999;
    private const uint Engerrand = 1001204;
    private const uint Fridurih = 1001966;
    private const uint LimsaLowerDecks = 129;
    private const uint UldahStepsOfThal = 131;
    private const uint OldGridania = 133;

    /// <summary>The slice of the bridge a vendor run touches; everything else throws.</summary>
    private sealed class FakeBridge : IGameBridge
    {
        public readonly HashSet<string> Visible = [];
        public readonly List<string> Calls = [];
        public readonly Dictionary<uint, int> ItemCounts = new();
        public readonly HashSet<uint> Attuned = [];

        public long GilBalance = 100_000;
        public int FreeSlots = 10;
        public uint Territory = OldGridania;

        /// <summary>The shop hands the goods over the moment it is asked, unless a test says otherwise.</summary>
        public bool ShopDelivers = true;
        public bool ShopSellsItem = true;

        public long Gil => GilBalance;

        public uint CurrentTerritoryId => Territory;

        public bool CanTeleportTo(uint territoryId) => Attuned.Contains(territoryId);

        public int GetItemCount(uint itemId) => ItemCounts.GetValueOrDefault(itemId);

        public int GetFreeInventorySlots() => FreeSlots;

        public bool IsAddonVisible(string addonName) => Visible.Contains(addonName);

        public bool BuyFromShop(uint itemId, int count)
        {
            Calls.Add($"Buy({itemId},{count})");
            if (!Visible.Contains("Shop") || !ShopSellsItem)
                return false;

            if (ShopDelivers)
            {
                ItemCounts[itemId] = ItemCounts.GetValueOrDefault(itemId) + count;
                GilBalance -= count;
            }

            return true;
        }

        public void CloseShop()
        {
            Calls.Add("CloseShop");
            Visible.Remove("Shop");
        }

        public bool FireAddonCallbackInt(string addonName, int value)
        {
            Calls.Add($"Callback({addonName},{value})");
            if (addonName == "SelectYesno")
                Visible.Remove("SelectYesno");
            return true;
        }

        // ---- not reached by a vendor run ----

        private static NotImplementedException Unexpected() => new("the vendor run must not touch this");

        // ---- stubs for members other packages added (merge) ----
        public FishingSnapshot? GetFishingState() => throw Unexpected();
        public float GetMainHandConditionPercent() => throw Unexpected();
        public bool IsFishing => throw Unexpected();
        public bool SelectBait(uint baitItemId) => throw Unexpected();

        // ---- stubs for members other packages added (merge) ----
        public void CloseCollectablesShop() => throw Unexpected();
        public void CloseExchangeShop() => throw Unexpected();
        public bool ExchangeBuy(uint shopId, uint itemId, int count) => throw Unexpected();
        public int GetCollectableCount(uint itemId, int minCollectability) => throw Unexpected();
        public long GetCurrencyCount(uint currencyItemId) => throw Unexpected();
        public uint GrandCompanyId => throw Unexpected();
        public int GrandCompanyRank => throw Unexpected();
        public bool HandInCollectable() => throw Unexpected();
        public bool TurnInCollectable(uint itemId) => throw Unexpected();

        // ---- stubs for members other packages added (merge) ----
        public bool AssignVenture(uint ventureTaskId) => throw Unexpected();
        public void CloseRetainerList() => throw Unexpected();
        public bool CollectVenture() => throw Unexpected();
        public bool ConfirmDesynthesis() => throw Unexpected();
        public int DepositToRetainer(uint itemId, int count) => throw Unexpected();
        public bool Desynthesize(uint itemId) => throw Unexpected();
        public bool DiscardItem(uint itemId) => throw Unexpected();
        public void DismissRetainer() => throw Unexpected();
        public SummoningBellSnapshot? FindSummoningBell() => throw Unexpected();
        public int GetRetainerItemCount(int retainerIndex, uint itemId) => throw Unexpected();
        public IReadOnlyList<RetainerSnapshot> GetRetainers() => throw Unexpected();
        public bool IsNearSummoningBell => throw Unexpected();
        public bool IsRetainerInventoryOpen => throw Unexpected();
        public bool IsRetainerSummoned => throw Unexpected();
        public bool OpenRetainerList() => throw Unexpected();
        public bool SelectRetainer(int retainerIndex) => throw Unexpected();
        public bool SelectRetainerMenuOption(string textContains) => throw Unexpected();
        public int WithdrawFromRetainer(uint itemId, int count) => throw Unexpected();

        // ---- NPC (7.3) ----
        public (ulong ObjectId, System.Numerics.Vector3 Position)? FindNpcObject(uint dataId) => throw Unexpected();
        public IReadOnlyList<string> ReadDialogOptions() => throw Unexpected();
        public bool SelectDialogOption(string textContains) => throw Unexpected();
        public bool AdvanceTalk() => throw Unexpected();

        public bool IsLoggedIn => throw Unexpected();
        public bool IsCrafting => throw Unexpected();
        public bool IsPreparingToCraft => throw Unexpected();
        public bool IsGathering => throw Unexpected();
        public Vector3? PlayerPosition => throw Unexpected();
        public bool IsMounted => throw Unexpected();
        public void TryMount() => throw Unexpected();
        public void TryDismount() => throw Unexpected();
        public PlayerSnapshot? GetPlayerState() => throw Unexpected();
        public CraftSnapshot? GetCraftState() => throw Unexpected();
        public GatheringSnapshot? GetGatheringState() => throw Unexpected();
        public bool IsGatheringActionInProgress => throw Unexpected();
        public GatheringNodeSnapshot? FindNearestGatheringNode(IReadOnlyCollection<ulong>? excludedObjectIds = null, Vector3? origin = null) => throw Unexpected();
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
        public void CloseRecipeNote() => throw Unexpected();
        public bool EquipGearsetForJob(uint classJobId) => throw Unexpected();
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
        public int GetHqItemCount(uint itemId) => throw Unexpected();
        public float GetLowestEquipmentConditionPercent() => throw Unexpected();
        public void OpenRepairWindow() => throw Unexpected();
        public bool PlaySoundEffect(int soundEffectNumber) => throw Unexpected();
        public void ExecuteChatCommand(string command) => throw Unexpected();
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
        public bool IsItemOnCooldown(uint itemId) => throw Unexpected();
        // ---- Combat (7.5) ----
        public IReadOnlyList<HuntTargetSnapshot> FindHuntTargets(uint bnpcNameId, IReadOnlyCollection<ulong>? excludedObjectIds = null, Vector3? origin = null) => throw Unexpected();
        public bool TargetObject(ulong objectId) => throw Unexpected();
        public ulong CurrentTargetId => throw Unexpected();
        public (uint BNpcNameId, string Name)? CurrentTargetMob => throw Unexpected();
        public float PlayerHpPercent => throw Unexpected();
        public bool IsInCombat => throw Unexpected();
        public bool IsDead => throw Unexpected();
        public bool AnswerReturnPrompt() => throw Unexpected();
        public int EnemiesTargetingMe() => throw Unexpected();
    }

    /// <summary>A scripted stand-in for P1's interactor: the test says when it arrives or gives up.</summary>
    private sealed class FakeInteractor : INpcInteractor
    {
        public readonly List<string> Calls = [];
        public readonly List<IReadOnlyList<DialogStep>> Scripts = [];

        public NpcInteractionState State { get; set; } = NpcInteractionState.Idle;

        public string StatusText { get; set; } = "Idle.";

        public string FailureReason { get; set; } = "";

        public bool Accepts = true;

        public bool Start(NpcTarget target, IReadOnlyList<DialogStep> script, string description)
        {
            Calls.Add($"Start({target.NpcId})");
            Scripts.Add(script);
            if (!Accepts)
                return false;

            State = NpcInteractionState.Traveling;
            StatusText = description;
            return true;
        }

        public void Tick()
        {
        }

        public void Pause(string reason)
        {
            Calls.Add("Pause");
            State = NpcInteractionState.Paused;
        }

        public void Resume()
        {
            Calls.Add("Resume");
            State = NpcInteractionState.Traveling;
        }

        public void Stop()
        {
            Calls.Add("Stop");
            State = NpcInteractionState.Idle;
        }

        public IEnumerable<string> Describe()
        {
            yield return $"fake interactor {State}";
        }
    }

    private sealed class FakeLocator : INpcLocator
    {
        public readonly Dictionary<uint, NpcTarget> Targets = new();

        public NpcTarget? Locate(uint npcId) => Targets.GetValueOrDefault(npcId);
    }

    private sealed class FakeDirectory : IVendorDirectory
    {
        public readonly Dictionary<uint, List<ShopVendor>> ByItem = new();

        public IReadOnlyList<ShopVendor> FindVendors(uint itemId) => ByItem.GetValueOrDefault(itemId) ?? [];
    }

    private static ShopVendor Vendor(
        uint npcId, string name, uint territory, string zone, int price, bool menu = false, int stack = 999) =>
        new(RockSalt, price, stack, 0x40030, "", npcId, name, territory, zone, menu);

    private sealed record Harness(
        VendorSource Source,
        FakeBridge Bridge,
        FakeInteractor Interactor,
        FakeLocator Locator,
        FakeDirectory Shops,
        FakeClock Clock,
        ListLog Log,
        AutomationSettings Settings);

    private static Harness Build(long gil = 100_000, long floor = 50_000, long cap = 200_000, bool menu = false)
    {
        var clock = new FakeClock();
        var log = new ListLog();
        var bridge = new FakeBridge { GilBalance = gil, Territory = OldGridania };
        bridge.Attuned.Add(LimsaLowerDecks);
        bridge.Attuned.Add(UldahStepsOfThal);

        var locator = new FakeLocator();
        locator.Targets[Engerrand] = new NpcTarget(Engerrand, "Engerrand", LimsaLowerDecks, new Vector3(-129.5f, 18.2f, 28.6f), 1001204);
        locator.Targets[Fridurih] = new NpcTarget(Fridurih, "Fridurih", UldahStepsOfThal, new Vector3(126.8f, 4f, -83.8f), 1001966);

        var shops = new FakeDirectory();
        shops.ByItem[RockSalt] = [Vendor(Engerrand, "Engerrand", LimsaLowerDecks, "Limsa Lominsa Lower Decks", 15, menu)];

        var settings = new AutomationSettings { GilFloor = floor, GilSpendCapPerRun = cap };
        var interactor = new FakeInteractor();
        var source = new VendorSource(shops, locator, interactor, bridge, settings, log, clock, id => $"item {id}");
        return new Harness(source, bridge, interactor, locator, shops, clock, log, settings);
    }

    // ------------------------------------------------------------- the offer

    [Fact]
    public void OffersAReachableVendorWithThePriceAndTheTrip()
    {
        var h = Build();

        var offer = h.Source.Offer(RockSalt, 5);

        Assert.NotNull(offer);
        Assert.Equal(MaterialSourceKind.Buy, offer.Kind);
        Assert.Equal(RockSalt, offer.ItemId);
        Assert.Equal(5, offer.Amount);
        Assert.Equal(75, offer.GilCost);
        Assert.Equal("Buy 5× item 5518 from Engerrand (Limsa Lominsa Lower Decks), 75 gil", offer.Description);
        Assert.True(offer.EstimatedSeconds > 0);

        // Offer is asked again for every schedule preview: it must not spend.
        Assert.Equal(0, h.Source.SpentThisRun);
    }

    [Fact]
    public void NoOfferWhenNothingSellsTheItem()
    {
        var h = Build();

        Assert.Null(h.Source.Offer(Unsold, 5));
        Assert.Null(h.Source.Offer(RockSalt, 0));
    }

    [Fact]
    public void NoOfferWhenTheVendorsZoneIsNotAttuned()
    {
        var h = Build();
        h.Bridge.Attuned.Remove(LimsaLowerDecks);

        Assert.Null(h.Source.Offer(RockSalt, 5));

        // Standing in the zone needs no aetheryte.
        h.Bridge.Territory = LimsaLowerDecks;
        Assert.NotNull(h.Source.Offer(RockSalt, 5));
    }

    [Fact]
    public void TheGilFloorStopsTheOffer()
    {
        var h = Build(gil: 50_600, floor: 50_000);

        // 40 × 15 = 600, exactly what is above the floor.
        Assert.NotNull(h.Source.Offer(RockSalt, 40));
        Assert.Null(h.Source.Offer(RockSalt, 41));
    }

    [Fact]
    public void ThePerRunCapStopsTheOfferUntilTheBudgetIsReset()
    {
        var h = Build(gil: 1_000_000, floor: 0, cap: 300);

        Assert.NotNull(h.Source.Offer(RockSalt, 20));
        Assert.Null(h.Source.Offer(RockSalt, 21));

        // A run that spent the cap blocks the next offer; a fresh run clears it.
        h.Source.Commit(300);
        Assert.Equal(300, h.Source.SpentThisRun);
        Assert.Null(h.Source.Offer(RockSalt, 1));

        h.Source.ResetRunBudget();
        Assert.Equal(0, h.Source.SpentThisRun);
        Assert.NotNull(h.Source.Offer(RockSalt, 20));
        Assert.Contains(h.Log.Lines, l => l.Contains("[Vendor] Run budget reset (300 gil spent"));
    }

    // -------------------------------------------------------- vendor ranking

    [Fact]
    public void VendorsAreRankedCurrentZoneThenAttunedThenCheapest()
    {
        var here = Vendor(1, "Here", OldGridania, "Old Gridania", 40);
        var attunedCheap = Vendor(2, "Cheap", LimsaLowerDecks, "Limsa", 10);
        var attunedDear = Vendor(3, "Dear", UldahStepsOfThal, "Ul'dah", 30);
        var unreachable = Vendor(4, "Far", 999, "Far away", 1);
        var placeless = Vendor(5, "Nowhere", 0, "", 1);

        var ordered = VendorOrdering.Order(
            [placeless, unreachable, attunedDear, attunedCheap, here],
            OldGridania,
            t => t is LimsaLowerDecks or UldahStepsOfThal);

        Assert.Equal(["Here", "Cheap", "Dear", "Far"], ordered.Select(v => v.NpcName).ToArray());
        Assert.DoesNotContain(ordered, v => v.NpcName == "Nowhere");
        Assert.True(VendorOrdering.IsReachable(attunedCheap, OldGridania, t => t == LimsaLowerDecks));
        Assert.False(VendorOrdering.IsReachable(unreachable, OldGridania, _ => false));
    }

    [Fact]
    public void TiesGoToTheCheaperVendorThenTheLowerNpcId()
    {
        var ordered = VendorOrdering.Order(
            [Vendor(9, "Nine", LimsaLowerDecks, "Limsa", 10), Vendor(2, "Two", LimsaLowerDecks, "Limsa", 10), Vendor(5, "Five", LimsaLowerDecks, "Limsa", 5)],
            OldGridania,
            _ => true);

        Assert.Equal(["Five", "Two", "Nine"], ordered.Select(v => v.NpcName).ToArray());
    }

    // ----------------------------------------------------------- the run

    /// <summary>Drives the run to the point where the Shop window is open and settled.</summary>
    private static VendorRun Shopping(Harness h, int amount)
    {
        var offer = h.Source.Offer(RockSalt, amount)!;
        var run = (VendorRun)h.Source.Start(offer);
        Assert.Equal(VendorRunState.GoingToVendor, run.State);

        // The interactor's last script step is WaitForAddon("Shop"), so the
        // window is up by the time it reports Completed.
        h.Bridge.Visible.Add("Shop");
        h.Interactor.State = NpcInteractionState.Completed;
        run.Tick();
        Assert.Equal(VendorRunState.OpeningShop, run.State);

        run.Tick();                       // sees the window, starts settling
        h.Clock.Advance(2);
        run.Tick();                       // settled → first batch
        Assert.Equal(VendorRunState.Buying, run.State);
        return run;
    }

    [Fact]
    public void BuysTheAmountVerifiesByBagCountAndClosesTheShop()
    {
        var h = Build();
        var run = Shopping(h, 5);
        var asRun = (ISourceRun)run;

        Assert.Equal(new WaitForAddon("Shop"), Assert.Single(h.Interactor.Scripts[0]));

        run.Tick();                       // orders the batch
        Assert.Contains("Buy(5518,5)", h.Bridge.Calls);
        run.Tick();                       // the bag grew: batch done, nothing left
        Assert.Equal(VendorRunState.ClosingShop, run.State);
        Assert.Contains(h.Log.Lines, l => l.Contains("[Vendor] Bought 5× item 5518 (5/5)."));

        run.Tick();                       // closes the window
        Assert.Contains("CloseShop", h.Bridge.Calls);
        run.Tick();                       // gone → completed
        Assert.Equal(SourceRunState.Completed, asRun.State);
        Assert.Equal(5, asRun.Obtained);
        Assert.Equal(75, run.GilSpent);
        Assert.Contains("Stop", h.Interactor.Calls);

        // The run budget is corrected from the booked offer to the real spend.
        Assert.Equal(75, h.Source.SpentThisRun);
    }

    [Fact]
    public void LargeAmountsAreBoughtInBatchesOfNinetyNine()
    {
        var h = Build(gil: 1_000_000, floor: 0);
        var run = Shopping(h, 150);

        for (var i = 0; i < 8 && run.State != VendorRunState.ClosingShop; i++)
            run.Tick();

        Assert.Equal(["Buy(5518,99)", "Buy(5518,51)"], h.Bridge.Calls.Where(c => c.StartsWith("Buy(")).ToArray());
        Assert.Equal(150, ((ISourceRun)run).Obtained);
    }

    [Fact]
    public void APurchaseConfirmationIsAnsweredBeforeTheNextAttempt()
    {
        var h = Build();
        var run = Shopping(h, 5);
        h.Bridge.Visible.Add("SelectYesno");

        run.Tick();
        Assert.Equal(["Callback(SelectYesno,0)"], h.Bridge.Calls.Where(c => c.StartsWith("Callback")).ToArray());
        Assert.DoesNotContain(h.Bridge.Calls, c => c.StartsWith("Buy("));
    }

    [Fact]
    public void AMenuVendorGetsThePurchaseOptionInItsScript()
    {
        var h = Build(menu: true);
        var offer = h.Source.Offer(RockSalt, 5)!;
        h.Source.Start(offer);

        Assert.Equal(
            [new SelectOption("Purchase"), new WaitForAddon("Shop")],
            h.Interactor.Scripts[0].ToArray());
    }

    [Fact]
    public void AFailedInteractionRetriesWithTheOtherScriptOnce()
    {
        var h = Build();
        var offer = h.Source.Offer(RockSalt, 5)!;
        var run = (VendorRun)h.Source.Start(offer);

        h.Interactor.State = NpcInteractionState.Failed;
        h.Interactor.FailureReason = "the shop window never opened";
        run.Tick();

        // The sheet heuristic said "opens directly"; the menu script is next.
        Assert.Equal(VendorRunState.GoingToVendor, run.State);
        Assert.Equal(2, h.Interactor.Scripts.Count);
        Assert.Equal(new SelectOption("Purchase"), h.Interactor.Scripts[1][0]);
        Assert.Contains(h.Log.Lines, l => l.Contains("retrying with the \"Purchase\" menu option"));

        h.Interactor.State = NpcInteractionState.Failed;
        run.Tick();
        Assert.Equal(SourceRunState.Failed, ((ISourceRun)run).State);
        Assert.Contains("could not reach Engerrand", run.StatusText);
        Assert.Equal(0, h.Source.SpentThisRun);   // the booked gil is given back
    }

    [Fact]
    public void AFullBagStopsTheRunBeforeItBuys()
    {
        var h = Build();
        h.Bridge.FreeSlots = 0;
        var offer = h.Source.Offer(RockSalt, 5)!;
        var run = (VendorRun)h.Source.Start(offer);

        h.Bridge.Visible.Add("Shop");
        h.Interactor.State = NpcInteractionState.Completed;
        run.Tick();
        run.Tick();
        h.Clock.Advance(2);
        run.Tick();

        Assert.Equal(SourceRunState.Failed, ((ISourceRun)run).State);
        Assert.Contains("the bag is full", run.StatusText);
        Assert.Contains("CloseShop", h.Bridge.Calls);
    }

    [Fact]
    public void APurchaseThatNeverArrivesTimesOutAndClosesUp()
    {
        var h = Build();
        h.Bridge.ShopDelivers = false;
        var run = Shopping(h, 5);

        run.Tick();
        Assert.Contains("Buy(5518,5)", h.Bridge.Calls);

        h.Clock.Advance(20);
        run.Tick();
        Assert.Equal(SourceRunState.Failed, ((ISourceRun)run).State);
        Assert.Contains("the purchase did not arrive in the bag", run.StatusText);
        Assert.Contains("CloseShop", h.Bridge.Calls);
        Assert.Contains("Stop", h.Interactor.Calls);
    }

    [Fact]
    public void PauseAndResumeTravelWithTheInteractor()
    {
        var h = Build();
        var offer = h.Source.Offer(RockSalt, 5)!;
        var run = (VendorRun)h.Source.Start(offer);
        var asRun = (ISourceRun)run;

        run.Pause("the user moved");
        Assert.Equal(SourceRunState.Paused, asRun.State);
        Assert.Contains("Pause", h.Interactor.Calls);

        run.Resume();
        Assert.Equal(SourceRunState.Running, asRun.State);
        Assert.Equal(VendorRunState.GoingToVendor, run.State);
        Assert.Contains("Resume", h.Interactor.Calls);
    }

    [Fact]
    public void StopClosesTheShopAndLeavesTheRunIdle()
    {
        var h = Build();
        var run = Shopping(h, 5);

        run.Stop();
        Assert.Equal(SourceRunState.Idle, ((ISourceRun)run).State);
        Assert.Contains("CloseShop", h.Bridge.Calls);
        Assert.Contains("Stop", h.Interactor.Calls);
    }

    [Fact]
    public void AVendorThatCannotBePlacedFailsTheRunImmediately()
    {
        var h = Build();
        var offer = h.Source.Offer(RockSalt, 5)!;

        // The shop is still known (the offer cached it), but the NPC has no
        // row on the map any more.
        h.Locator.Targets.Clear();
        var run = (VendorRun)h.Source.Start(offer);
        Assert.Equal(SourceRunState.Failed, ((ISourceRun)run).State);
        Assert.Contains("could not be placed on the map", run.StatusText);

        // Nothing sells it at all: the run never leaves the ground.
        var fresh = Build();
        fresh.Shops.ByItem.Clear();
        var blind = (VendorRun)fresh.Source.Start(offer);
        Assert.Equal(SourceRunState.Failed, ((ISourceRun)blind).State);
        Assert.Contains("no reachable vendor", blind.StatusText);
    }

    [Fact]
    public void DescribeReportsTheVendorAndTheBudget()
    {
        var h = Build();
        var run = (VendorRun)h.Source.Start(h.Source.Offer(RockSalt, 5)!);

        Assert.Contains(run.Describe(), l => l.Contains("Engerrand (Limsa Lominsa Lower Decks)") && l.Contains("15 gil each"));
        Assert.Contains(h.Source.Describe(), l => l.Contains("floor 50000") && l.Contains("cap 200000"));
    }

    // --------------------------------------------------- the plan tree label

    [Fact]
    public void RawMaterialNodesShowTheVendorInsteadOfTheZone()
    {
        var h = Build();
        var label = h.Source.SourceLabel(RockSalt);

        Assert.Equal("buy from Engerrand (Limsa Lominsa Lower Decks), 15 gil each", label);
        Assert.Null(h.Source.SourceLabel(Unsold));

        var node = new CielCraft.Core.Planning.PlanNode { ItemId = RockSalt, Need = 5, Missing = 5, SourceLabel = label };
        var detail = CielCraft.Core.Planning.PlanTree.Describe(node, id => $"job {id}", id => $"zone {id}");
        Assert.Equal("need 5, owned 0, missing 5 [buy from Engerrand (Limsa Lominsa Lower Decks), 15 gil each]", detail);
    }
}
