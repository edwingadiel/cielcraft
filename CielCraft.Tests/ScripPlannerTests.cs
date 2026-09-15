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
/// The collectables-for-scrips planner and the exchange source (roadmap
/// 7.17): which turn-in the two preferences choose and at which tier, the
/// offer maths against held currency, and the run flow — travel, buy, verify
/// by bag delta, close — against a scripted bridge and NPC interactor.
/// </summary>
public class ScripPlannerTests
{
    private const uint PurpleCrafters = 33913;
    private const uint OrangeCrafters = 41784;
    private const uint Carpenter = 8;
    private const uint Miner = 16;

    private static readonly Dictionary<uint, int> MaxedJobs = new() { [Carpenter] = 100, [Miner] = 100 };

    /// <summary>A cheap craft: one material, 10 scrips at Low, 20 at High.</summary>
    private static ScripTurnIn CheapCraft => new(
        100, "Rarefied Plank", PurpleCrafters, Carpenter, 90, IsGathered: false,
        LowReward: 10, MidReward: 14, HighReward: 20,
        LowThreshold: 400, MidThreshold: 700, HighThreshold: 1000,
        MaterialCost: 1, SecondsPerUnit: 120, TurnInNpcId: 7, TurnInNpcName: "collectable appraiser");

    /// <summary>A rich craft: six materials, but 60 scrips at High and quick per scrip.</summary>
    private static ScripTurnIn RichCraft => new(
        101, "Rarefied Cabinet", PurpleCrafters, Carpenter, 90, IsGathered: false,
        LowReward: 30, MidReward: 45, HighReward: 60,
        LowThreshold: 600, MidThreshold: 800, HighThreshold: 1000,
        MaterialCost: 6, SecondsPerUnit: 150, TurnInNpcId: 7, TurnInNpcName: "collectable appraiser");

    /// <summary>A gathered turn-in gated behind a level the test character may not have.</summary>
    private static ScripTurnIn HighLevelGather => new(
        102, "Rarefied Ore", PurpleCrafters, Miner, 100, IsGathered: true,
        LowReward: 15, MidReward: 22, HighReward: 36,
        LowThreshold: 600, MidThreshold: 800, HighThreshold: 1000,
        MaterialCost: 1, SecondsPerUnit: 45, TurnInNpcId: 7, TurnInNpcName: "collectable appraiser");

    [Fact]
    public void CheapestTakesTheFewestMaterialsPerScripAtTheLowestPayingTier()
    {
        var need = new ScripNeed(PurpleCrafters, "Purple Crafters' Scrip", 100);

        var plan = ScripPlanner.Plan(need, [CheapCraft, RichCraft], MaxedJobs, ScripSourcePreference.Cheapest);

        Assert.NotNull(plan);
        var step = Assert.Single(plan!.Steps);
        // 1 material / 10 scrips beats 6 / 30; Low is the lowest tier that pays.
        Assert.Equal(CheapCraft.ItemId, step.TurnIn.ItemId);
        Assert.Equal(CollectableTier.Low, step.Tier);
        Assert.Equal(10, step.Count);
        Assert.Equal(100, plan.Scrips);
        Assert.Equal(7u, plan.TurnInNpcId);
    }

    [Fact]
    public void FastestTakesTheFewestSecondsPerScripAtTheHighestPayingTier()
    {
        var need = new ScripNeed(PurpleCrafters, "Purple Crafters' Scrip", 100);

        var plan = ScripPlanner.Plan(need, [CheapCraft, RichCraft], MaxedJobs, ScripSourcePreference.Fastest);

        Assert.NotNull(plan);
        var step = Assert.Single(plan!.Steps);
        // 150 s / 60 scrips beats 120 s / 20; High is the tier that pays most.
        Assert.Equal(RichCraft.ItemId, step.TurnIn.ItemId);
        Assert.Equal(CollectableTier.High, step.Tier);
        Assert.Equal(2, step.Count);
        Assert.Equal(120, plan.Scrips);
    }

    [Fact]
    public void TheTargetsAreCollectableOrdersOfTheRightKindAndTier()
    {
        var need = new ScripNeed(PurpleCrafters, "Purple Crafters' Scrip", 30);

        var plan = ScripPlanner.Plan(need, [HighLevelGather], MaxedJobs, ScripSourcePreference.Fastest);

        var target = Assert.Single(plan!.Targets);
        Assert.Equal(HighLevelGather.ItemId, target.ItemId);
        Assert.Equal(OrderKind.Gather, target.Kind);
        Assert.Equal(ProductionMode.Collectable, target.Mode);
        Assert.Equal(CollectableTier.High, target.CollectableTier);
        Assert.Equal(1, target.Quantity);
    }

    [Fact]
    public void ATurnInAboveTheCharactersLevelIsNotPlanned()
    {
        var need = new ScripNeed(PurpleCrafters, "Purple Crafters' Scrip", 100);
        var levels = new Dictionary<uint, int> { [Miner] = 99 };

        Assert.Null(ScripPlanner.Plan(need, [HighLevelGather], levels, ScripSourcePreference.Cheapest));

        levels[Miner] = 100;
        Assert.NotNull(ScripPlanner.Plan(need, [HighLevelGather], levels, ScripSourcePreference.Cheapest));
    }

    [Fact]
    public void ATurnInForAnotherCurrencyIsIgnored()
    {
        var need = new ScripNeed(OrangeCrafters, "Orange Crafters' Scrip", 50);

        Assert.Null(ScripPlanner.Plan(need, [CheapCraft, RichCraft], MaxedJobs, ScripSourcePreference.Cheapest));
    }

    [Fact]
    public void ATierWorthNothingIsNeverChosen()
    {
        // Pre-endwalker turn-ins pay only at the top tier.
        var topOnly = CheapCraft with { LowReward = 0, MidReward = 0, HighReward = 12 };
        var need = new ScripNeed(PurpleCrafters, "Purple Crafters' Scrip", 24);

        var plan = ScripPlanner.Plan(need, [topOnly], MaxedJobs, ScripSourcePreference.Cheapest);

        var step = Assert.Single(plan!.Steps);
        Assert.Equal(CollectableTier.High, step.Tier);
        Assert.Equal(2, step.Count);
    }

    [Fact]
    public void APlanThatWouldNeedMoreThanTheCapIsRefused()
    {
        var need = new ScripNeed(PurpleCrafters, "Purple Crafters' Scrip", 100);

        Assert.Null(ScripPlanner.Plan(need, [CheapCraft], MaxedJobs, ScripSourcePreference.Cheapest, maxUnits: 5));
    }

    // -------------------------------------------------------- the source

    [Fact]
    public void NoOfferWhenTheCurrencyIsShortAndTheShortfallIsNamed()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 100;

        Assert.Null(source.Offer(Material, 4));

        var need = source.MissingCurrency(Material, 4);
        Assert.NotNull(need);
        // 4 items = 2 purchases of 2 × 250 scrips = 500, of which 100 are held.
        Assert.Equal(PurpleCrafters, need!.CurrencyItemId);
        Assert.Equal(400, need.Amount);
    }

    [Fact]
    public void OfferWhenTheCurrencyCoversThePurchasesAndTheNpcIsKnown()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 500;

        var offer = source.Offer(Material, 4);

        Assert.NotNull(offer);
        Assert.Equal(MaterialSourceKind.Exchange, offer!.Kind);
        Assert.Equal(0, offer.GilCost);
        Assert.Contains("Purple Crafters' Scrip", offer.Description);
        Assert.Contains("Rowena", offer.Description);
        Assert.Null(source.MissingCurrency(Material, 4));
    }

    [Fact]
    public void NoOfferWhenTheShopsNpcCannotBeLocated()
    {
        var (source, bridge, _, npcs) = BuildSource();
        bridge.Currency[PurpleCrafters] = 5000;
        npcs.Targets.Clear();

        Assert.Null(source.Offer(Material, 1));
        Assert.Null(source.MissingCurrency(Material, 1));
    }

    [Fact]
    public void AGrandCompanyLineIsSkippedBelowTheRequiredRank()
    {
        var (source, bridge, catalog, _) = BuildSource();
        catalog.Offers[Material] = [SealOffer with { RequiredGrandCompanyRank = 6 }];
        bridge.Currency[StormSeal] = 100_000;
        bridge.Company = 1;
        bridge.Rank = 5;

        Assert.Null(source.Offer(Material, 1));

        bridge.Rank = 6;
        Assert.NotNull(source.Offer(Material, 1));
    }

    // ------------------------------------------------------------ the run

    [Fact]
    public void TheRunTravelsBuysVerifiesAndCloses()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 500;
        var clock = Clock;
        var run = (ISourceRun)source.Start(source.Offer(Material, 4)!);

        run.Tick();                                       // Idle → Approaching
        Assert.Equal(SourceRunState.Running, run.State);
        Assert.Equal(Rowena.NpcId, Interactor.Started!.NpcId);
        Assert.Equal(new WaitForAddon("ShopExchangeCurrency"), Assert.Single(Interactor.Script!));

        Interactor.State = NpcInteractionState.Completed;
        run.Tick();                                       // the shop window is up
        Assert.Empty(bridge.Buys);

        run.Tick();                                       // pacing: the window has not settled yet
        Assert.Empty(bridge.Buys);

        clock.Advance(1.5);
        run.Tick();                                       // buys two lines of two
        Assert.Equal([(Shop, Material, 2)], bridge.Buys);

        run.Tick();                                       // the bag rose: enough, close
        run.Tick();
        Assert.Equal(SourceRunState.Completed, run.State);
        Assert.Equal(4, run.Obtained);
        Assert.Equal(1, bridge.ExchangeClosed);
        Assert.Equal(1, Interactor.Stops);
        Assert.Equal(0, bridge.Currency[PurpleCrafters]);
    }

    [Fact]
    public void ABuyThatChangesNothingFailsTheRunInsteadOfLooping()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 500;
        bridge.BuysDeliver = false;
        var run = (ISourceRun)source.Start(source.Offer(Material, 4)!);

        run.Tick();
        Interactor.State = NpcInteractionState.Completed;
        run.Tick();
        Clock.Advance(1.5);
        run.Tick();                                       // the buy goes out
        Assert.NotEmpty(bridge.Buys);

        Clock.Advance(20);
        run.Tick();                                       // nothing arrived within the verify timeout
        Assert.Equal(SourceRunState.Failed, run.State);
        Assert.Contains("did not arrive", run.StatusText);
        Assert.Equal(1, bridge.ExchangeClosed);
    }

    [Fact]
    public void AConfirmationDialogIsAnsweredBeforeTheBagIsChecked()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 500;
        var run = (ISourceRun)source.Start(source.Offer(Material, 2)!);

        run.Tick();
        Interactor.State = NpcInteractionState.Completed;
        run.Tick();
        Clock.Advance(1.5);
        bridge.Visible.Add("SelectYesno");
        run.Tick();                                       // the buy goes out, the shop asks

        run.Tick();                                       // yes
        Assert.Contains("SelectYesno:0", bridge.Callbacks);
        run.Tick();                                       // the bag rose → close
        run.Tick();
        Assert.Equal(SourceRunState.Completed, run.State);
    }

    [Fact]
    public void AnUnreachableNpcFailsTheRunWithTheInteractorsReason()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 500;
        var run = (ISourceRun)source.Start(source.Offer(Material, 2)!);

        run.Tick();
        Interactor.State = NpcInteractionState.Failed;
        Interactor.FailureReason = "no attuned aetheryte in Ul'dah";
        run.Tick();

        Assert.Equal(SourceRunState.Failed, run.State);
        Assert.Equal("no attuned aetheryte in Ul'dah", run.StatusText);
    }

    [Fact]
    public void PauseAndResumeTravelWithTheInteractor()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 500;
        var run = (ISourceRun)source.Start(source.Offer(Material, 2)!);

        run.Tick();
        run.Pause("production paused");
        Assert.Equal(SourceRunState.Paused, run.State);
        Assert.Equal(NpcInteractionState.Paused, Interactor.State);

        run.Resume();
        Assert.Equal(SourceRunState.Running, run.State);
        Assert.NotEqual(NpcInteractionState.Paused, Interactor.State);
    }

    // ------------------------------------------------------- the turn-in

    [Fact]
    public void TheTurnInRunHandsCollectablesOverUntilTheScripsAreThere()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 0;
        bridge.Collectables[(CheapCraft.ItemId, CheapCraft.HighThreshold)] = 2;

        var run = source.StartTurnIn(new ScripNeed(PurpleCrafters, "Purple Crafters' Scrip", 30))!;
        Assert.NotNull(run);

        run.Tick();                                       // → Approaching
        Interactor.State = NpcInteractionState.Completed;
        run.Tick();                                       // the appraiser's window is up
        Clock.Advance(1.5);
        run.Tick();                                       // selects the collectable
        Assert.Equal([CheapCraft.ItemId], bridge.TurnIns);

        run.Tick();                                       // hands it over
        Assert.Equal(1, bridge.HandIns);
        run.Tick();                                       // 20 scrips in: not enough yet
        Assert.Equal(SourceRunState.Running, run.State);
        Assert.Equal(20, run.Obtained);

        Clock.Advance(1.5);
        run.Tick();
        run.Tick();
        run.Tick();                                       // the second one takes it past 30
        Assert.Equal(40, run.Obtained);

        run.Tick();                                       // → Closing
        run.Tick();
        Assert.Equal(SourceRunState.Completed, run.State);
        Assert.Equal(1, bridge.CollectablesClosed);
    }

    [Fact]
    public void NoTurnInRunWhenNothingInTheBagReachesAPayingTier()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Collectables.Clear();

        Assert.Null(source.StartTurnIn(new ScripNeed(PurpleCrafters, "Purple Crafters' Scrip", 30)));
    }

    [Fact]
    public void AShortPurchaseChainsTheTurnInBeforeBuying()
    {
        var (source, bridge, _, _) = BuildSource();
        bridge.Currency[PurpleCrafters] = 500;
        var offer = source.Offer(Material, 2)!;

        // The scrips go away between the offer and the start (a long gather
        // phase in between); collectables in the bag cover the difference.
        bridge.Currency[PurpleCrafters] = 0;
        bridge.Collectables[(CheapCraft.ItemId, CheapCraft.HighThreshold)] = 20;
        var run = (ISourceRun)source.Start(offer);

        run.Tick();                                       // → TurningIn, not straight to the shop
        Assert.Contains("Purple Crafters' Scrip", run.StatusText);
        Assert.Null(Interactor.Started);
        run.Tick();
        Assert.Equal(NpcTargetKindAppraiser, Interactor.Started!.NpcId);
    }

    // ------------------------------------------------------------- fixture

    private const uint Material = 500;
    private const uint Shop = 1770873;
    private const uint StormSeal = 20;
    private const uint NpcTargetKindAppraiser = 9;

    private static readonly ExchangeCurrency PurpleScrip =
        new(PurpleCrafters, "Purple Crafters' Scrip", ExchangeCurrencyKind.Scrip);

    private static readonly ExchangeCurrency Seal =
        new(StormSeal, "Storm Seal", ExchangeCurrencyKind.GrandCompanySeal);

    /// <summary>Two of the material for 250 scrips, from Rowena's scrip exchange.</summary>
    private static readonly ExchangeOffer ScripOffer = new(
        Material, "Integral Lumber", 2, PurpleScrip, 250, Shop, ExchangeShopKind.SpecialShop, 6, "Rowena");

    private static readonly ExchangeOffer SealOffer = new(
        Material, "Integral Lumber", 1, Seal, 200, 1441793, ExchangeShopKind.GrandCompanyShop, 8, "storm quartermaster");

    private static readonly NpcTarget Rowena = new(6, "Rowena", 130, new Vector3(1, 2, 3), 1000);

    private FakeInteractor Interactor = null!;
    private FakeClock Clock = null!;

    private (ExchangeSource Source, FakeBridge Bridge, FakeCatalog Catalog, FakeLocator Npcs) BuildSource()
    {
        Clock = new FakeClock();
        Interactor = new FakeInteractor();
        var bridge = new FakeBridge();
        var catalog = new FakeCatalog();
        catalog.Offers[Material] = [ScripOffer];
        catalog.TurnIns[PurpleCrafters] = [CheapCraft];
        var npcs = new FakeLocator();
        npcs.Targets[Rowena.NpcId] = Rowena;
        npcs.Targets[NpcTargetKindAppraiser] = new NpcTarget(
            NpcTargetKindAppraiser, "collectable appraiser", 130, new Vector3(4, 5, 6), 1001);
        npcs.Targets[8] = new NpcTarget(8, "storm quartermaster", 128, new Vector3(7, 8, 9), 1002);

        var source = new ExchangeSource(
            catalog, bridge, Interactor, npcs,
            () => new AutomationSettings(),
            () => CharacterCapabilities.Unknown with { IsKnown = true, JobLevels = MaxedJobs },
            new ListLog(), Clock);
        return (source, bridge, catalog, npcs);
    }

    private sealed class FakeCatalog : IExchangeCatalog
    {
        public readonly Dictionary<uint, List<ExchangeOffer>> Offers = new();
        public readonly Dictionary<uint, List<ScripTurnIn>> TurnIns = new();

        public IReadOnlyList<ExchangeOffer> FindExchanges(uint itemId) =>
            Offers.TryGetValue(itemId, out var list) ? list : [];

        public IReadOnlyList<ScripTurnIn> FindTurnIns(uint currencyItemId) =>
            TurnIns.TryGetValue(currencyItemId, out var list) ? list : [];

        public IReadOnlyList<uint> TurnInNpcIds => [NpcTargetKindAppraiser];

        public string GetItemName(uint itemId) => $"Item{itemId}";

        public string GetZoneName(uint territoryId) => $"Zone{territoryId}";
    }

    private sealed class FakeLocator : INpcLocator
    {
        public readonly Dictionary<uint, NpcTarget> Targets = new();

        public NpcTarget? Locate(uint npcId) => Targets.GetValueOrDefault(npcId);
    }

    private sealed class FakeInteractor : INpcInteractor
    {
        public NpcInteractionState State { get; set; } = NpcInteractionState.Idle;

        public string StatusText { get; private set; } = "";

        public string FailureReason { get; set; } = "";

        public NpcTarget? Started { get; private set; }

        public IReadOnlyList<DialogStep>? Script { get; private set; }

        public int Stops { get; private set; }

        public bool Start(NpcTarget target, IReadOnlyList<DialogStep> script, string description)
        {
            Started = target;
            Script = script;
            StatusText = description;
            State = NpcInteractionState.Traveling;
            return true;
        }

        public void Tick()
        {
        }

        public void Pause(string reason)
        {
            State = NpcInteractionState.Paused;
            StatusText = reason;
        }

        public void Resume() => State = NpcInteractionState.Traveling;

        public void Stop()
        {
            Stops++;
            State = NpcInteractionState.Idle;
        }

        public IEnumerable<string> Describe()
        {
            yield return "fake interactor";
        }
    }

    /// <summary>
    /// The slice of the bridge an exchange touches; everything else throws so
    /// the run cannot wander into game state a test did not script.
    /// </summary>
    private sealed class FakeBridge : IGameBridge
    {
        public readonly Dictionary<uint, long> Currency = new();
        public readonly Dictionary<uint, int> Items = new();
        public readonly Dictionary<(uint ItemId, int MinCollectability), int> Collectables = new();
        public readonly List<(uint Shop, uint Item, int Count)> Buys = [];
        public readonly List<uint> TurnIns = [];

        public bool BuysDeliver = true;
        public int HandIns;
        public int ExchangeClosed;
        public int CollectablesClosed;
        public uint Company;
        public int Rank;

        private uint pendingTurnIn;

        public long GetCurrencyCount(uint currencyItemId) => Currency.GetValueOrDefault(currencyItemId);

        public uint GrandCompanyId => Company;

        public int GrandCompanyRank => Rank;

        public bool ExchangeBuy(uint shopId, uint itemId, int count)
        {
            Buys.Add((shopId, itemId, count));
            if (!BuysDeliver)
                return true;

            // The scrip exchange hands over ReceiveCount per purchase.
            Items[itemId] = Items.GetValueOrDefault(itemId) + (count * 2);
            Currency[PurpleCrafters] = Currency.GetValueOrDefault(PurpleCrafters) - (count * 250);
            return true;
        }

        public void CloseExchangeShop() => ExchangeClosed++;

        public int GetCollectableCount(uint itemId, int minCollectability) =>
            Collectables.GetValueOrDefault((itemId, minCollectability));

        public bool TurnInCollectable(uint itemId)
        {
            TurnIns.Add(itemId);
            pendingTurnIn = itemId;
            return true;
        }

        public bool HandInCollectable()
        {
            HandIns++;
            if (pendingTurnIn == 0)
                return false;

            var key = Collectables.Keys.First(k => k.ItemId == pendingTurnIn);
            Collectables[key] = Math.Max(0, Collectables[key] - 1);
            Currency[PurpleCrafters] = Currency.GetValueOrDefault(PurpleCrafters) + 20;
            pendingTurnIn = 0;
            return true;
        }

        public void CloseCollectablesShop() => CollectablesClosed++;

        public int GetItemCount(uint itemId) => Items.GetValueOrDefault(itemId);

        public int GetFreeInventorySlots() => 10;

        /// <summary>The shop windows are up once the interactor says so; SelectYesno only when a test raises it.</summary>
        public readonly HashSet<string> Visible =
            ["ShopExchangeCurrency", "InclusionShop", "GrandCompanyExchange", "CollectablesShop"];

        public readonly List<string> Callbacks = [];

        public bool IsAddonVisible(string addonName) => Visible.Contains(addonName);

        public bool FireAddonCallbackInt(string addonName, int value)
        {
            Callbacks.Add($"{addonName}:{value}");
            if (addonName == "SelectYesno")
                Visible.Remove("SelectYesno");

            return true;
        }

        // ---- not reached by an exchange ----

        private static NotImplementedException Unexpected() => new("the exchange must not touch this");

        // ---- stubs for members other packages added (merge) ----
        public FishingSnapshot? GetFishingState() => throw Unexpected();
        public float GetMainHandConditionPercent() => throw Unexpected();
        public bool IsFishing => throw Unexpected();
        public bool SelectBait(uint baitItemId) => throw Unexpected();

        // ---- stubs for members other packages added (merge) ----
        public bool AdvanceTalk() => throw Unexpected();
        public bool AssignVenture(uint ventureTaskId) => throw Unexpected();
        public bool BuyFromShop(uint itemId, int count) => throw Unexpected();
        public bool CanTeleportTo(uint territoryId) => throw Unexpected();
        public void CloseRetainerList() => throw Unexpected();
        public void CloseShop() => throw Unexpected();
        public bool CollectVenture() => throw Unexpected();
        public bool ConfirmDesynthesis() => throw Unexpected();
        public int DepositToRetainer(uint itemId, int count) => throw Unexpected();
        public bool Desynthesize(uint itemId) => throw Unexpected();
        public bool DiscardItem(uint itemId) => throw Unexpected();
        public void DismissRetainer() => throw Unexpected();
        public (ulong ObjectId, System.Numerics.Vector3 Position)? FindNpcObject(uint dataId) => throw Unexpected();
        public SummoningBellSnapshot? FindSummoningBell() => throw Unexpected();
        public int GetRetainerItemCount(int retainerIndex, uint itemId) => throw Unexpected();
        public IReadOnlyList<RetainerSnapshot> GetRetainers() => throw Unexpected();
        public long Gil => throw Unexpected();
        public bool IsNearSummoningBell => throw Unexpected();
        public bool IsRetainerInventoryOpen => throw Unexpected();
        public bool IsRetainerSummoned => throw Unexpected();
        public bool OpenRetainerList() => throw Unexpected();
        public IReadOnlyList<string> ReadDialogOptions() => throw Unexpected();
        public bool SelectDialogOption(string textContains) => throw Unexpected();
        public bool SelectRetainer(int retainerIndex) => throw Unexpected();
        public bool SelectRetainerMenuOption(string textContains) => throw Unexpected();
        public int WithdrawFromRetainer(uint itemId, int count) => throw Unexpected();

        public bool IsLoggedIn => throw Unexpected();
        public bool IsCrafting => throw Unexpected();
        public bool IsPreparingToCraft => throw Unexpected();
        public bool IsGathering => throw Unexpected();
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
        public int GetHqItemCount(uint itemId) => throw Unexpected();
        public IReadOnlyList<IngredientRequirement> GetRecipeRequirements(ushort recipeId) => throw Unexpected();
        public uint CurrentClassJobId => throw Unexpected();
        public void OpenRecipe(uint recipeId) => throw Unexpected();
        public void CloseRecipeNote() => throw Unexpected();
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
        public Vector3? PlayerPosition => throw Unexpected();
        public bool IsMounted => throw Unexpected();
        public void TryMount() => throw Unexpected();
        public void TryDismount() => throw Unexpected();
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
}
