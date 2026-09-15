using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Sourcing;

/// <summary>
/// One place an item can be bought for gil (roadmap 7.3b): the shop row, the
/// NPC that opens it, where that NPC stands and what the item costs. Price is
/// <c>Item.PriceMid</c> — the sheet's shop price; the live window may differ
/// (festival or Grand Company discounts), so the run buys against the window
/// and only the offer and the plan use this number.
/// </summary>
public sealed record ShopVendor(
    uint ItemId,
    int Price,
    int StackSize,
    uint ShopId,
    string ShopName,
    uint NpcId,
    string NpcName,
    uint TerritoryId,
    string ZoneName,
    bool OpensMenu)
{
    /// <summary>"Engerrand (Limsa Lominsa Lower Decks)" — what the log and the plan tree show.</summary>
    public string Where => $"{NpcName} ({ZoneName})";
}

/// <summary>
/// What the vendor source needs to know about gil shops. The sheet-backed
/// <c>Game.ShopDatabase</c> implements it; tests supply a fake, so the offer
/// maths never needs the game's data files.
/// </summary>
public interface IVendorDirectory
{
    /// <summary>
    /// Vendors of the item, best first (current zone, then a zone with an
    /// attuned aetheryte, then cheapest). Empty when nothing sells it or no
    /// seller could be placed on the map.
    /// </summary>
    IReadOnlyList<ShopVendor> FindVendors(uint itemId);
}

/// <summary>
/// The vendor ranking rule of roadmap 7.3b, kept pure so it can be tested
/// without the sheets and shared by the database and the source.
/// </summary>
public static class VendorOrdering
{
    /// <summary>
    /// The current zone first, then any zone the teleport list reaches, then
    /// whatever is cheapest; ties go to the lower NPC id so a plan and the run
    /// that follows it pick the same vendor. Vendors with no place on the map
    /// are dropped.
    /// </summary>
    public static IReadOnlyList<ShopVendor> Order(
        IEnumerable<ShopVendor> candidates, uint currentTerritoryId, Func<uint, bool> canTeleportTo) =>
        candidates
            .Where(v => v.TerritoryId != 0)
            .OrderBy(v => Rank(v, currentTerritoryId, canTeleportTo))
            .ThenBy(v => v.Price)
            .ThenBy(v => v.NpcId)
            .ToList();

    /// <summary>The character can get to the vendor: it is in this zone, or the zone has an attuned aetheryte.</summary>
    public static bool IsReachable(ShopVendor vendor, uint currentTerritoryId, Func<uint, bool> canTeleportTo) =>
        vendor.TerritoryId != 0 && (vendor.TerritoryId == currentTerritoryId || canTeleportTo(vendor.TerritoryId));

    private static int Rank(ShopVendor vendor, uint currentTerritoryId, Func<uint, bool> canTeleportTo) =>
        vendor.TerritoryId == currentTerritoryId ? 0 : canTeleportTo(vendor.TerritoryId) ? 1 : 2;
}

/// <summary>
/// Buys missing materials at a gil vendor (roadmap 7.3b). Registered with
/// the production runner as a <see cref="IMaterialSource"/>: it offers when a
/// reachable NPC sells the item and the gil rules allow it, and the run it
/// starts walks there through the NPC interactor, opens the Shop window,
/// buys in batches and verifies by inventory delta.
/// </summary>
public sealed class VendorSource : IMaterialSource, IRunBudget
{
    /// <summary>The Shop window's own quantity field tops out at 99 per purchase.</summary>
    public const int MaxPerPurchase = 99;

    private readonly IVendorDirectory shops;
    private readonly INpcLocator npcs;
    private readonly INpcInteractor interactor;
    private readonly IGameBridge gameBridge;
    private readonly AutomationSettings settings;
    private readonly ILog log;
    private readonly Action? persist; // saves the settings after a vendor is marked absent
    private readonly IClock clock;
    private readonly Func<uint, string> itemName;

    /// <summary>The vendor an offer named, so the run goes where the plan said.</summary>
    private readonly Dictionary<uint, ShopVendor> offered = new();

    public VendorSource(
        IVendorDirectory shops,
        INpcLocator npcs,
        INpcInteractor interactor,
        IGameBridge gameBridge,
        AutomationSettings settings,
        ILog log,
        IClock clock,
        Func<uint, string>? itemName = null,
        Action? persist = null)
    {
        this.persist = persist;
        this.shops = shops;
        this.npcs = npcs;
        this.interactor = interactor;
        this.gameBridge = gameBridge;
        this.settings = settings;
        this.log = log;
        this.clock = clock;
        this.itemName = itemName ?? (id => $"item {id}");
    }

    public MaterialSourceKind Kind => MaterialSourceKind.Buy;

    public string Name => "vendor";

    /// <summary>Gil this run has committed at vendors so far (roadmap 7.3b: the per-run spend cap).</summary>
    public long SpentThisRun { get; private set; }

    /// <summary>
    /// Clears the per-run spend counter. The coordinator calls this from
    /// <c>ProductionRunner.Start</c> so "gil a single run may spend" means
    /// one production run, not one session.
    /// </summary>
    public void ResetRunBudget()
    {
        if (SpentThisRun != 0)
            log.Information($"[Vendor] Run budget reset ({SpentThisRun} gil spent in the previous run).");
        SpentThisRun = 0;
    }

    /// <summary>What a single run may still spend: the gil floor and the per-run cap, whichever binds first.</summary>
    public long RemainingBudget =>
        Math.Max(0, Math.Min(gameBridge.Gil - settings.GilFloor, settings.GilSpendCapPerRun - SpentThisRun));

    public SourceOffer? Offer(uint itemId, int amount)
    {
        if (amount <= 0)
            return null;

        var vendor = BestVendor(itemId);
        if (vendor == null)
            return null;

        // The gil rule of roadmap 7.3b, checked without spending anything:
        // Offer is pure (the runner also calls it to preview a schedule).
        var cost = (long)vendor.Price * amount;
        if (cost > RemainingBudget)
            return null;

        offered[itemId] = vendor;
        var batches = Math.Max(1, (amount + MaxPerPurchase - 1) / MaxPerPurchase);
        return new SourceOffer(
            itemId,
            amount,
            MaterialSourceKind.Buy,
            $"Buy {amount}× {itemName(itemId)} from {vendor.Where}, {cost} gil",
            TravelSeconds + (BuySeconds * batches),
            cost);
    }

    /// <summary>"buy from Engerrand (Limsa Lominsa Lower Decks)" for the plan tree (roadmap 7.12); null when nothing sells it.</summary>
    public string? SourceLabel(uint itemId) =>
        BestVendor(itemId) is { } vendor ? $"buy from {vendor.Where}, {vendor.Price} gil each" : null;

    public ISourceRun Start(SourceOffer offer)
    {
        // The plan may be minutes old: ask again so a zone change is taken
        // into account, and fall back to what the offer named.
        var vendor = BestVendor(offer.ItemId) ?? offered.GetValueOrDefault(offer.ItemId);
        return new VendorRun(this, vendor, offer, npcs, interactor, gameBridge, log, clock, itemName);
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Vendor source: gil {gameBridge.Gil}, floor {settings.GilFloor}, cap {settings.GilSpendCapPerRun}, " +
                     $"spent this run {SpentThisRun}, remaining budget {RemainingBudget}.";
    }

    /// <summary>Rough trip cost for the schedule: teleport, ride and dialog.</summary>
    private const int TravelSeconds = 75;

    /// <summary>Rough cost of one ≤99 purchase, window settling included.</summary>
    private const int BuySeconds = 8;

    private ShopVendor? BestVendor(uint itemId)
    {
        foreach (var vendor in shops.FindVendors(itemId))
        {
            // A merchant that was not there last time (seasonal) is skipped for good.
            if (settings.AbsentVendorNpcs.Contains(vendor.NpcId))
                continue;

            // FindVendors already ranks by reachability; the first entry that
            // is actually reachable is the one to use.
            if (VendorOrdering.IsReachable(vendor, gameBridge.CurrentTerritoryId, gameBridge.CanTeleportTo))
                return vendor;
        }

        return null;
    }

    /// <summary>The trip reached the placement and found no such NPC: remember it so the next plan takes another vendor or another source.</summary>
    public void MarkAbsent(ShopVendor vendor)
    {
        if (settings.AbsentVendorNpcs.Contains(vendor.NpcId))
            return;

        settings.AbsentVendorNpcs.Add(vendor.NpcId);
        persist?.Invoke();
        log.Warning($"[Vendor] {vendor.NpcName} ({vendor.ZoneName}) is not in the world; not offered again (Settings › Sourcing lists absent vendors).");
    }

    /// <summary>Booked when a run starts and corrected to the real spend when it ends.</summary>
    internal void Commit(long gil) => SpentThisRun += gil;

    /// <summary>The balance no purchase may take the character below (roadmap 7.3b).</summary>
    internal long FloorGil => settings.GilFloor;
}

/// <summary>Where a <see cref="VendorRun"/> is; the phases of one shopping trip.</summary>
public enum VendorRunState
{
    Idle,

    /// <summary>The NPC interactor is teleporting, travelling and driving the shop dialog.</summary>
    GoingToVendor,

    /// <summary>The interaction is done; waiting for the Shop window and letting it settle.</summary>
    OpeningShop,

    /// <summary>A batch is being bought and verified by inventory delta.</summary>
    Buying,

    ClosingShop,
    Completed,
    Failed,
    Paused,
}

/// <summary>
/// One shopping trip (roadmap 7.3b): go to the vendor with the NPC
/// interactor, open the Shop window, buy the amount in batches of at most
/// <see cref="VendorSource.MaxPerPurchase"/> while the bag has room, and
/// close up. Every batch is confirmed by the bag count, never by the buy
/// call's return (spec §34).
/// </summary>
public sealed class VendorRun : AutomationMachine<VendorRunState>, ISourceRun
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>Settle after the Shop window opens before clicking in it (pacing, like AfterNodeOpen).</summary>
    private static readonly TimeSpan AfterShopOpen = TimeSpan.FromSeconds(1.5);

    private readonly VendorSource source;
    private readonly ShopVendor? vendor;
    private readonly SourceOffer offer;
    private readonly INpcLocator npcs;
    private readonly INpcInteractor interactor;
    private readonly IGameBridge gameBridge;
    private readonly Func<uint, string> itemName;
    private readonly Throttle attempts;

    private readonly int startCount;
    private long committed;
    private DateTime phaseStartedAt;
    private DateTime shopOpenedAt;
    private int batchTarget;          // what the batch in flight should add to the bag
    private int batchBaseline;        // the bag count when it was ordered
    private bool menuScript;          // the script in flight expects a "Purchase" menu
    private bool triedBothScripts;
    private VendorRunState resumeTo = VendorRunState.GoingToVendor;

    internal VendorRun(
        VendorSource source,
        ShopVendor? vendor,
        SourceOffer offer,
        INpcLocator npcs,
        INpcInteractor interactor,
        IGameBridge gameBridge,
        ILog log,
        IClock clock,
        Func<uint, string> itemName)
        : base(log, clock, "[Vendor]", VendorRunState.Idle, "Idle.")
    {
        this.source = source;
        this.vendor = vendor;
        this.offer = offer;
        this.npcs = npcs;
        this.interactor = interactor;
        this.gameBridge = gameBridge;
        this.itemName = itemName;
        attempts = new Throttle(clock, RetryInterval);
        startCount = gameBridge.GetItemCount(offer.ItemId);
        phaseStartedAt = clock.UtcNow;
        Begin();
    }

    /// <summary>The runner's view of this run; <see cref="AutomationMachine{T}.State"/> stays the phase.</summary>
    public SourceRunState RunState => State switch
    {
        VendorRunState.Completed => SourceRunState.Completed,
        VendorRunState.Failed => SourceRunState.Failed,
        VendorRunState.Paused => SourceRunState.Paused,
        VendorRunState.Idle => SourceRunState.Idle,
        _ => SourceRunState.Running,
    };

    SourceRunState ISourceRun.State => RunState;

    /// <summary>Bought so far, by the bag count (spec §34).</summary>
    public int Obtained => Math.Max(0, gameBridge.GetItemCount(offer.ItemId) - startCount);

    /// <summary>What the purchases have actually cost, at the price the offer was made with.</summary>
    public long GilSpent => vendor == null ? 0 : (long)vendor.Price * Obtained;

    public void Pause(string reason)
    {
        if (State is VendorRunState.Completed or VendorRunState.Failed or VendorRunState.Paused)
            return;

        resumeTo = State;
        interactor.Pause(reason);
        Transition(VendorRunState.Paused, $"Paused: {reason}");
    }

    public void Resume()
    {
        if (State != VendorRunState.Paused)
            return;

        interactor.Resume();
        phaseStartedAt = Clock.UtcNow;
        attempts.Reset();
        Transition(resumeTo, $"Resumed: {PhaseText(resumeTo)}");
    }

    public void Stop()
    {
        if (State is VendorRunState.Completed or VendorRunState.Failed)
            return;

        CloseUp();
        Transition(VendorRunState.Idle, $"Stopped after {Obtained}/{offer.Amount}.");
        Settle();
    }

    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return vendor == null
            ? $"Vendor: none known for {itemName(offer.ItemId)}"
            : $"Vendor: {vendor.Where}, shop {vendor.ShopId} \"{vendor.ShopName}\", {vendor.Price} gil each, " +
              $"stack {vendor.StackSize}, {(vendor.OpensMenu ? "menu" : "opens the shop directly")}";
        yield return $"Wanted {offer.Amount}, obtained {Obtained}, batch in flight {batchTarget}, committed {committed} gil";
        foreach (var line in interactor.Describe())
            yield return "  npc: " + line;
    }

    protected override void OnTick()
    {
        switch (State)
        {
            case VendorRunState.GoingToVendor:
                TickGoingToVendor();
                break;

            case VendorRunState.OpeningShop:
                TickOpeningShop();
                break;

            case VendorRunState.Buying:
                TickBuying();
                break;

            case VendorRunState.ClosingShop:
                TickClosingShop();
                break;
        }
    }

    private void Begin()
    {
        if (vendor == null)
        {
            Fail($"no reachable vendor sells {itemName(offer.ItemId)}");
            return;
        }

        var target = npcs.Locate(vendor.NpcId);
        if (target == null)
        {
            Fail($"{vendor.NpcName} could not be placed on the map");
            return;
        }

        // Book the offer's cost against the run budget straight away, so two
        // sourced materials in one plan cannot each spend the whole cap; the
        // number is corrected to what was really spent when the run ends.
        committed = offer.GilCost;
        source.Commit(committed);

        menuScript = vendor.OpensMenu;
        StartInteraction(target);
    }

    private void StartInteraction(NpcTarget target)
    {
        phaseStartedAt = Clock.UtcNow;
        var script = menuScript
            ? (IReadOnlyList<DialogStep>)[new SelectOption("Purchase"), new WaitForAddon("Shop")]
            : [new WaitForAddon("Shop")];
        if (!interactor.Start(target, script, $"buy {offer.Amount}× {itemName(offer.ItemId)} from {vendor!.NpcName}"))
        {
            Fail("another NPC interaction is already in flight");
            return;
        }

        Transition(VendorRunState.GoingToVendor, $"Going to {vendor.Where} for {offer.Amount}× {itemName(offer.ItemId)}.");
    }

    private void TickGoingToVendor()
    {
        // Whoever starts an interaction ticks it (m3.md); the driver does not.
        interactor.Tick();
        switch (interactor.State)
        {
            case NpcInteractionState.Completed:
                shopOpenedAt = DateTime.MinValue;
                phaseStartedAt = Clock.UtcNow;
                Transition(VendorRunState.OpeningShop, $"At {vendor!.NpcName}; opening the shop.");
                return;

            case NpcInteractionState.Failed:
                // The menu heuristic (does interacting raise a "Purchase"
                // option or the Shop window itself?) is read off the sheets;
                // when it was wrong, the other script is tried once.
                if (!triedBothScripts && npcs.Locate(vendor!.NpcId) is { } target)
                {
                    triedBothScripts = true;
                    menuScript = !menuScript;
                    Log.Warning($"[Vendor] {vendor.NpcName}: {interactor.FailureReason}; retrying " +
                                (menuScript ? "with the \"Purchase\" menu option." : "as a shop that opens directly."));
                    interactor.Stop();
                    StartInteraction(target);
                    return;
                }

                // The placement is right but nobody stands there: a seasonal
                // merchant. Remember it and let the next plan pick another
                // vendor or another source.
                if (interactor.FailureReason.Contains("is not in the object table", StringComparison.Ordinal))
                {
                    source.MarkAbsent(vendor!);
                    Fail($"{vendor!.NpcName} is not in the world (a seasonal vendor?); it will not be offered again — run again for another source");
                    return;
                }

                Fail($"could not reach {vendor!.NpcName} ({interactor.FailureReason})");
                return;

            case NpcInteractionState.Paused:
                Pause(interactor.StatusText);
                return;

            default:
                StatusText = interactor.StatusText;
                return;
        }
    }

    private void TickOpeningShop()
    {
        if (!gameBridge.IsAddonVisible("Shop"))
        {
            if (TimedOut("the shop window did not open"))
                return;

            StatusText = $"Waiting for {vendor!.NpcName}'s shop window...";
            return;
        }

        // Human pacing: let the freshly opened window settle before buying.
        if (shopOpenedAt == DateTime.MinValue)
        {
            shopOpenedAt = Clock.UtcNow;
            StatusText = "Shop open; settling.";
            return;
        }

        if (Clock.UtcNow - shopOpenedAt < AfterShopOpen)
            return;

        StartBatch();
    }

    private void StartBatch()
    {
        var remaining = offer.Amount - Obtained;
        if (remaining <= 0)
        {
            BeginClosing();
            return;
        }

        // A purchase lands as one stack move: without a free slot (and no
        // partial stack to top up) the bag cannot take it.
        if (gameBridge.GetFreeInventorySlots() < 1 && gameBridge.GetItemCount(offer.ItemId) % Math.Max(1, vendor!.StackSize) == 0)
        {
            FailPartial("the bag is full");
            return;
        }

        var affordable = vendor!.Price > 0 ? (int)Math.Min(int.MaxValue, (gameBridge.Gil - source.FloorGil) / vendor.Price) : remaining;
        var batch = Math.Min(Math.Min(remaining, VendorSource.MaxPerPurchase), Math.Max(1, vendor.StackSize));
        batch = Math.Min(batch, Math.Max(0, affordable));
        if (batch <= 0)
        {
            FailPartial("not enough gil above the floor");
            return;
        }

        batchTarget = batch;
        batchBaseline = gameBridge.GetItemCount(offer.ItemId);
        phaseStartedAt = Clock.UtcNow;
        attempts.Reset();
        Transition(VendorRunState.Buying, $"Buying {batch}× {itemName(offer.ItemId)} ({Obtained}/{offer.Amount} so far).");
    }

    private void TickBuying()
    {
        // Some purchases raise a confirmation; answer it before anything else.
        if (gameBridge.IsAddonVisible("SelectYesno"))
        {
            attempts.Try(() => gameBridge.FireAddonCallbackInt("SelectYesno", 0));
            return;
        }

        if (gameBridge.GetItemCount(offer.ItemId) - batchBaseline >= batchTarget)
        {
            Log.Information($"[Vendor] Bought {batchTarget}× {itemName(offer.ItemId)} ({Obtained}/{offer.Amount}).");
            batchTarget = 0;
            StartBatch();
            return;
        }

        if (!gameBridge.IsAddonVisible("Shop"))
        {
            FailPartial("the shop window closed mid-purchase");
            return;
        }

        if (TimedOut("the purchase did not arrive in the bag"))
            return;

        attempts.Try(() => gameBridge.BuyFromShop(offer.ItemId, batchTarget));
        StatusText = $"Buying {batchTarget}× {itemName(offer.ItemId)} ({Obtained}/{offer.Amount})...";
    }

    private void BeginClosing()
    {
        phaseStartedAt = Clock.UtcNow;
        attempts.Reset();
        Transition(VendorRunState.ClosingShop, $"Bought {Obtained}× {itemName(offer.ItemId)}; closing the shop.");
    }

    private void TickClosingShop()
    {
        if (!gameBridge.IsAddonVisible("Shop"))
        {
            interactor.Stop();
            Settle();
            Transition(VendorRunState.Completed, $"Bought {Obtained}× {itemName(offer.ItemId)} at {vendor!.Where} for {GilSpent} gil.");
            return;
        }

        // A window that refuses to close is not worth failing the run over:
        // the goods are in the bag.
        if (Clock.UtcNow - phaseStartedAt > PhaseTimeout)
        {
            Log.Warning("[Vendor] The shop window did not close; carrying on.");
            interactor.Stop();
            Settle();
            Transition(VendorRunState.Completed, $"Bought {Obtained}× {itemName(offer.ItemId)} at {vendor!.Where} for {GilSpent} gil.");
            return;
        }

        attempts.Try(gameBridge.CloseShop);
    }

    /// <summary>A phase that ran out of time; true when it did (the caller returns).</summary>
    private bool TimedOut(string reason)
    {
        if (Clock.UtcNow - phaseStartedAt <= PhaseTimeout)
            return false;

        FailPartial(reason);
        return true;
    }

    /// <summary>Gave up with some of the amount already bought: close up, then fail.</summary>
    private void FailPartial(string reason)
    {
        CloseUp();
        Fail(Obtained > 0 ? $"{reason} after {Obtained}/{offer.Amount}" : reason);
    }

    private void Fail(string reason)
    {
        Settle();
        Transition(VendorRunState.Failed, reason);
    }

    private void CloseUp()
    {
        if (gameBridge.IsAddonVisible("Shop"))
            gameBridge.CloseShop();
        interactor.Stop();
    }

    /// <summary>Books what the trip really cost in place of the amount reserved at the start.</summary>
    private void Settle()
    {
        source.Commit(GilSpent - committed);
        committed = GilSpent;
    }

    private string PhaseText(VendorRunState state) => state switch
    {
        VendorRunState.GoingToVendor => $"going to {vendor?.Where ?? "the vendor"}",
        VendorRunState.OpeningShop => "opening the shop",
        VendorRunState.Buying => $"buying {itemName(offer.ItemId)}",
        VendorRunState.ClosingShop => "closing the shop",
        _ => state.ToString(),
    };

}
