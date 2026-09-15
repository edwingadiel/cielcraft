using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Sourcing;

/// <summary>Which sheet an exchange lives in, and therefore which window the NPC opens (roadmap 7.17).</summary>
public enum ExchangeShopKind
{
    /// <summary>A plain SpecialShop row: the ShopExchangeCurrency window.</summary>
    SpecialShop,

    /// <summary>A SpecialShop reached through an InclusionShop (the scrip exchange's category list).</summary>
    InclusionShop,

    /// <summary>GCShop / GCScripShopItem: the Grand Company quartermaster's window.</summary>
    GrandCompanyShop,
}

/// <summary>What an exchange is paid with.</summary>
public enum ExchangeCurrencyKind
{
    /// <summary>An ordinary item handed over (crafted tokens, beast-tribe currency in the bag).</summary>
    Item,

    /// <summary>Crafters' / Gatherers' scrip — the scrip planner can earn these (roadmap 7.17).</summary>
    Scrip,

    Tomestone,

    GrandCompanySeal,
}

/// <summary>The currency of an exchange: always an item row, so the bag/currency count is one lookup.</summary>
public sealed record ExchangeCurrency(uint ItemId, string Name, ExchangeCurrencyKind Kind);

/// <summary>
/// One line of a currency shop: N of an item for M of a currency, at a shop
/// an NPC opens. <see cref="AddonName"/> is the window the run drives once
/// the interactor has finished the dialog.
/// </summary>
public sealed record ExchangeOffer(
    uint ItemId,
    string ItemName,
    int ReceiveCount,
    ExchangeCurrency Currency,
    int Cost,
    uint ShopId,
    ExchangeShopKind ShopKind,
    uint NpcId,
    string NpcName,
    int RequiredGrandCompanyRank = 0)
{
    /// <summary>The addon the NPC raises for this shop kind.</summary>
    public string AddonName => ShopKind switch
    {
        ExchangeShopKind.InclusionShop => "InclusionShop",
        ExchangeShopKind.GrandCompanyShop => "GrandCompanyExchange",
        _ => "ShopExchangeCurrency",
    };

    /// <summary>Currency for <paramref name="units"/> purchases of this line.</summary>
    public long CostFor(int units) => (long)Cost * units;

    /// <summary>Purchases needed to reach <paramref name="amount"/> items (a line may hand over a stack).</summary>
    public int UnitsFor(int amount) => (amount + Math.Max(1, ReceiveCount) - 1) / Math.Max(1, ReceiveCount);
}

/// <summary>What the exchange source needs to know; <see cref="ExchangeDatabase"/> implements it over the sheets.</summary>
public interface IExchangeCatalog
{
    /// <summary>Currency shops selling the item, best first (current zone, then reachable, then cheapest).</summary>
    IReadOnlyList<ExchangeOffer> FindExchanges(uint itemId);

    /// <summary>Collectables the appraiser takes for that scrip currency (roadmap 7.17).</summary>
    IReadOnlyList<ScripTurnIn> FindTurnIns(uint currencyItemId);

    /// <summary>Every collectable appraiser, so a turn-in can go to the first reachable one.</summary>
    IReadOnlyList<uint> TurnInNpcIds { get; }

    string GetItemName(uint itemId);

    /// <summary>Zone name for the log line; empty when the territory is unknown.</summary>
    string GetZoneName(uint territoryId);
}

/// <summary>
/// Supplies a material from a currency shop (roadmap 7.17): scrips,
/// tomestones, Grand Company seals or a plain token. Offers only when the
/// currency is already held and the shop's NPC can be found; when the
/// currency is scrips and the character is short, <see
/// cref="MissingCurrency"/> names the shortfall so the order runner can
/// prepend a collectable plan, and <see cref="StartTurnIn"/> hands in
/// collectables that are already in the bag.
/// </summary>
public sealed class ExchangeSource : IMaterialSource
{
    private readonly IExchangeCatalog catalog;
    private readonly IGameBridge bridge;
    private readonly INpcInteractor npc;
    private readonly INpcLocator npcs;
    private readonly Func<AutomationSettings> settings;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly bool tickNpc;

    /// <param name="tickNpc">
    /// False when the plugin's driver already ticks the shared
    /// <see cref="INpcInteractor"/> every frame, so a run never ticks it twice.
    /// </param>
    public ExchangeSource(
        IExchangeCatalog catalog,
        IGameBridge bridge,
        INpcInteractor npc,
        INpcLocator npcs,
        Func<AutomationSettings> settings,
        Func<CharacterCapabilities> capabilities,
        ILog log,
        IClock clock,
        bool tickNpc = true)
    {
        this.catalog = catalog;
        this.bridge = bridge;
        this.npc = npc;
        this.npcs = npcs;
        this.settings = settings;
        this.capabilities = capabilities;
        this.log = log;
        this.clock = clock;
        this.tickNpc = tickNpc;
    }

    public MaterialSourceKind Kind => MaterialSourceKind.Exchange;

    public string Name => "exchange";

    /// <summary>The offer a <see cref="Start"/> would run, so the run can re-read the shop it was built from.</summary>
    private readonly Dictionary<uint, ExchangeOffer> lastOffer = new();

    public SourceOffer? Offer(uint itemId, int amount)
    {
        if (amount <= 0)
            return null;

        var reachable = Reachable(itemId, amount);
        if (reachable == null)
            return null;

        var (offer, target, units) = reachable.Value;
        if (bridge.GetCurrencyCount(offer.Currency.ItemId) < offer.CostFor(units))
            return null;

        lastOffer[itemId] = offer;

        // Rough schedule cost: one trip plus a beat per purchase (7.15 only
        // needs an order of magnitude to slot the task between windows).
        var seconds = 150 + (units * 4);
        return new SourceOffer(
            itemId,
            amount,
            MaterialSourceKind.Exchange,
            $"Exchange {amount}× {offer.ItemName} for {offer.CostFor(units)} {offer.Currency.Name} " +
            $"at {target.Name} ({catalog.GetZoneName(target.TerritoryId)})",
            seconds);
    }

    /// <summary>
    /// The scrips the character is short of for this material, or null when no
    /// exchange is known, the shop is unreachable, the currency is not a scrip
    /// or enough is already held (roadmap 7.17; the order runner prepends the
    /// scrip plan's targets to the group that needs the item).
    /// </summary>
    public ScripNeed? MissingCurrency(uint itemId, int amount)
    {
        var reachable = Reachable(itemId, amount);
        if (reachable == null)
            return null;

        var (offer, _, units) = reachable.Value;
        if (offer.Currency.Kind != ExchangeCurrencyKind.Scrip)
            return null;

        var shortfall = offer.CostFor(units) - bridge.GetCurrencyCount(offer.Currency.ItemId);
        return shortfall <= 0 ? null : new ScripNeed(offer.Currency.ItemId, offer.Currency.Name, (int)shortfall);
    }

    /// <summary>
    /// What to make for a scrip shortfall, under the configured preference and
    /// the character's job levels; null when nothing the appraiser takes is
    /// within reach.
    /// </summary>
    public ScripPlan? PlanScrips(ScripNeed need) =>
        ScripPlanner.Plan(
            need,
            catalog.FindTurnIns(need.CurrencyItemId),
            capabilities().JobLevels,
            settings().ScripSourcePreference);

    public ISourceRun Start(SourceOffer offer)
    {
        if (!lastOffer.TryGetValue(offer.ItemId, out var exchange))
        {
            // Offer() is always called first by the runner, but a stale queue
            // (a replan across a reload) must not crash the tick.
            var reachable = Reachable(offer.ItemId, offer.Amount);
            if (reachable == null)
                return new FailedRun($"no exchange is known for {catalog.GetItemName(offer.ItemId)} any more");

            exchange = reachable.Value.Offer;
        }

        var target = npcs.Locate(exchange.NpcId);
        if (target == null)
            return new FailedRun($"{exchange.NpcName} cannot be located");

        var units = exchange.UnitsFor(offer.Amount);
        var shortfall = exchange.CostFor(units) - bridge.GetCurrencyCount(exchange.Currency.ItemId);
        ISourceRun? turnIn = null;
        if (shortfall > 0 && exchange.Currency.Kind == ExchangeCurrencyKind.Scrip)
        {
            // The currency went missing between the offer and the start (a
            // long gather phase in between): hand in collectables the bag
            // already holds before the purchase rather than failing the run.
            var need = new ScripNeed(exchange.Currency.ItemId, exchange.Currency.Name, (int)shortfall);
            turnIn = StartTurnIn(need);
        }

        return new ExchangeRun(exchange, target, offer.Amount, turnIn, bridge, npc, log, clock, tickNpc);
    }

    /// <summary>
    /// Hands collectables already in the bag to the appraiser for the scrips
    /// of <paramref name="need"/> (roadmap 7.17). Returns null when no known
    /// turn-in for that currency is in the bag at a paying collectability.
    /// </summary>
    public ISourceRun? StartTurnIn(ScripNeed need)
    {
        var candidates = new List<(ScripTurnIn TurnIn, CollectableTier Tier, int Count)>();
        foreach (var turnIn in catalog.FindTurnIns(need.CurrencyItemId))
        {
            foreach (var tier in turnIn.PayingTiers.Reverse())
            {
                // The bridge counts by collectability, which is what the
                // turn-in thresholds are in (quality / 10, roadmap 7.23).
                var count = bridge.GetCollectableCount(turnIn.ItemId, turnIn.CollectabilityAt(tier));
                if (count <= 0)
                    continue;

                candidates.Add((turnIn, tier, count));
                break;
            }
        }

        if (candidates.Count == 0)
            return null;

        // Every city has an appraiser; take the first the locator knows (it
        // prefers the current zone), not a fixed one across the world.
        var target = catalog.TurnInNpcIds.Select(npcs.Locate).FirstOrDefault(t => t != null);
        if (target == null)
        {
            log.Warning($"[Exchange] {need.Amount} {need.CurrencyName} short and collectables are in the bag, " +
                        "but no collectable appraiser could be located.");
            return null;
        }

        return new CollectableTurnInRun(need, candidates, target, bridge, npc, log, clock, tickNpc);
    }

    /// <summary>The first offer whose NPC the locator knows, with the purchases it needs.</summary>
    private (ExchangeOffer Offer, NpcTarget Target, int Units)? Reachable(uint itemId, int amount)
    {
        foreach (var offer in catalog.FindExchanges(itemId))
        {
            // Gil shops are P2's business (roadmap 7.3b): this source only
            // spends currencies the gil budget does not cover.
            if (offer.Currency.Kind == ExchangeCurrencyKind.Item && offer.Currency.ItemId == GilItemId)
                continue;

            if (offer.RequiredGrandCompanyRank > 0
                && (bridge.GrandCompanyId == 0 || bridge.GrandCompanyRank < offer.RequiredGrandCompanyRank))
                continue;

            var target = npcs.Locate(offer.NpcId);
            if (target == null)
                continue;

            return (offer, target, offer.UnitsFor(amount));
        }

        return null;
    }

    /// <summary>The Gil "item" row, as SpecialShop's gil costs name it.</summary>
    public const uint GilItemId = 1;

    /// <summary>A run that is already over: the queue task fails with a reason instead of the tick crashing.</summary>
    private sealed class FailedRun : ISourceRun
    {
        public FailedRun(string reason) => StatusText = reason;

        public SourceRunState State => SourceRunState.Failed;

        public string StatusText { get; }

        public int Obtained => 0;

        public void Tick()
        {
        }

        public void Pause(string reason)
        {
        }

        public void Resume()
        {
        }

        public void Stop()
        {
        }

        public IEnumerable<string> Describe()
        {
            yield return $"Exchange run failed before it started: {StatusText}";
        }
    }
}

public enum ExchangeRunState
{
    Idle,

    /// <summary>Handing collectables to the appraiser first because the scrips are short.</summary>
    TurningIn,

    /// <summary>The NPC interactor is teleporting / travelling / opening the shop.</summary>
    Approaching,

    Buying,

    /// <summary>Waiting for the bag count to rise (and answering a confirmation).</summary>
    Verifying,

    Closing,
    Completed,
    Failed,
    Paused,
}

/// <summary>
/// One currency purchase in flight (roadmap 7.17): optionally a collectable
/// turn-in first, then the NPC interactor to the shop window, then buy in
/// batches and verify by inventory delta, then close and let go of the NPC.
/// The bag count is the truth (spec §34) — a buy that changes nothing fails
/// the run rather than looping.
/// </summary>
public sealed class ExchangeRun : AutomationMachine<ExchangeRunState>, ISourceRun
{
    /// <summary>A window has to settle before it is clicked (Pacing); reuse the node-window beat.</summary>
    private static readonly TimeSpan AfterShopOpens = Pacing.AfterNodeOpen;

    /// <summary>One purchase per beat: the client rejects a second click in the same frame anyway.</summary>
    private static readonly TimeSpan BetweenPurchases = TimeSpan.FromSeconds(1.5);

    /// <summary>A buy that has produced nothing for this long is a broken flow, not a slow one.</summary>
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The shop window never appeared after the dialog said it would.</summary>
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(20);

    private readonly ExchangeOffer offer;
    private readonly NpcTarget target;
    private readonly int wanted;
    private readonly ISourceRun? turnIn;
    private readonly IGameBridge bridge;
    private readonly INpcInteractor npc;
    private readonly bool tickNpc;
    private readonly Throttle purchases;
    private readonly int startCount;

    private DateTime phaseStartedAt;
    private DateTime windowOpenedAt;
    private int countBeforeBuy;
    private int purchasesMade;
    private string pauseReason = "";

    public ExchangeRun(
        ExchangeOffer offer,
        NpcTarget target,
        int wanted,
        ISourceRun? turnIn,
        IGameBridge bridge,
        INpcInteractor npc,
        ILog log,
        IClock clock,
        bool tickNpc = true)
        : base(log, clock, "[Exchange]", ExchangeRunState.Idle, $"Exchanging for {offer.ItemName}...")
    {
        this.offer = offer;
        this.target = target;
        this.wanted = wanted;
        this.turnIn = turnIn;
        this.bridge = bridge;
        this.npc = npc;
        this.tickNpc = tickNpc;
        purchases = new Throttle(clock, BetweenPurchases);
        startCount = bridge.GetItemCount(offer.ItemId);
        phaseStartedAt = clock.UtcNow;
    }

    SourceRunState ISourceRun.State => RunState;

    /// <summary>The run's state in the production runner's vocabulary.</summary>
    public SourceRunState RunState => State switch
    {
        ExchangeRunState.Idle => SourceRunState.Idle,
        ExchangeRunState.Completed => SourceRunState.Completed,
        ExchangeRunState.Failed => SourceRunState.Failed,
        ExchangeRunState.Paused => SourceRunState.Paused,
        _ => SourceRunState.Running,
    };

    public int Obtained => Math.Max(0, bridge.GetItemCount(offer.ItemId) - startCount);

    protected override void OnTick()
    {
        switch (State)
        {
            case ExchangeRunState.Idle:
                if (turnIn != null && turnIn.State is SourceRunState.Idle or SourceRunState.Running)
                {
                    Transition(ExchangeRunState.TurningIn, $"Earning {offer.Currency.Name} for {offer.ItemName}...");
                    break;
                }

                Approach();
                break;

            case ExchangeRunState.TurningIn:
                TickTurnIn();
                break;

            case ExchangeRunState.Approaching:
                TickApproaching();
                break;

            case ExchangeRunState.Buying:
                TickBuying();
                break;

            case ExchangeRunState.Verifying:
                TickVerifying();
                break;

            case ExchangeRunState.Closing:
                TickClosing();
                break;
        }
    }

    private void TickTurnIn()
    {
        turnIn!.Tick();
        switch (turnIn.State)
        {
            case SourceRunState.Completed:
                Log.Information($"[Exchange] Turn-in done: {turnIn.StatusText}.");
                Approach();
                break;

            case SourceRunState.Failed:
                Fail($"the collectable turn-in failed ({turnIn.StatusText})");
                break;

            case SourceRunState.Paused:
                Transition(ExchangeRunState.Paused, $"Paused: {turnIn.StatusText}");
                break;
        }
    }

    private void Approach()
    {
        // The dialog ends on the shop window; the run drives it from there.
        var script = new List<DialogStep> { new WaitForAddon(offer.AddonName) };
        if (!npc.Start(target, script, $"{offer.NpcName} ({offer.Currency.Name} exchange)"))
        {
            Fail("another NPC interaction is already in flight");
            return;
        }

        EnterPhase(ExchangeRunState.Approaching, $"Going to {offer.NpcName} for {offer.ItemName}...");
    }

    private void TickApproaching()
    {
        if (tickNpc)
            npc.Tick();

        switch (npc.State)
        {
            case NpcInteractionState.Completed:
                windowOpenedAt = Clock.UtcNow;
                EnterPhase(ExchangeRunState.Buying, $"{offer.NpcName}: buying {offer.ItemName}...");
                break;

            case NpcInteractionState.Failed:
                Fail(npc.FailureReason.Length > 0 ? npc.FailureReason : "the NPC could not be reached");
                break;

            case NpcInteractionState.Paused:
                Transition(ExchangeRunState.Paused, $"Paused: {npc.StatusText}");
                break;

            default:
                StatusText = npc.StatusText;
                break;
        }
    }

    private void TickBuying()
    {
        var obtained = Obtained;
        if (obtained >= wanted)
        {
            EnterPhase(ExchangeRunState.Closing, $"{offer.ItemName} ×{obtained}: closing the shop.");
            return;
        }

        if (!bridge.IsAddonVisible(offer.AddonName))
        {
            if (Clock.UtcNow - windowOpenedAt > WindowTimeout)
                Fail($"the {offer.AddonName} window is not open");

            return;
        }

        // Pacing: let a freshly opened window settle before clicking it.
        if (Clock.UtcNow - windowOpenedAt < AfterShopOpens)
            return;

        var units = offer.UnitsFor(wanted - obtained);
        if (bridge.GetCurrencyCount(offer.Currency.ItemId) < offer.CostFor(1))
        {
            Fail($"{offer.Currency.Name} ran out after {purchasesMade} purchase(s)");
            return;
        }

        if (bridge.GetFreeInventorySlots() <= 0)
        {
            Fail("the bag is full");
            return;
        }

        // Stacks cap at 99 and so does every shop's count box.
        var batch = Math.Clamp(units, 1, 99);
        var affordable = (int)(bridge.GetCurrencyCount(offer.Currency.ItemId) / Math.Max(1, offer.Cost));
        batch = Math.Clamp(Math.Min(batch, affordable), 1, 99);

        countBeforeBuy = bridge.GetItemCount(offer.ItemId);
        if (!purchases.Try(() =>
            {
                if (bridge.ExchangeBuy(offer.ShopId, offer.ItemId, batch))
                {
                    purchasesMade++;
                    EnterPhase(ExchangeRunState.Verifying, $"Bought {batch}× {offer.ItemName}; checking the bag...");
                }
            }))
        {
            return;
        }

        if (State == ExchangeRunState.Buying && Clock.UtcNow - phaseStartedAt > WindowTimeout)
            Fail($"the shop would not sell {offer.ItemName}");
    }

    private void TickVerifying()
    {
        // Some shops ask before handing over a costly item; the same yes the
        // repair flow presses (MaintenanceService) answers it.
        if (bridge.IsAddonVisible("SelectYesno"))
        {
            bridge.FireAddonCallbackInt("SelectYesno", 0);
            phaseStartedAt = Clock.UtcNow;
            return;
        }

        if (bridge.GetItemCount(offer.ItemId) > countBeforeBuy)
        {
            var obtained = Obtained;
            StatusText = $"{offer.ItemName} {obtained}/{wanted}";
            EnterPhase(
                obtained >= wanted ? ExchangeRunState.Closing : ExchangeRunState.Buying,
                obtained >= wanted
                    ? $"{offer.ItemName} ×{obtained}: closing the shop."
                    : $"{offer.ItemName} {obtained}/{wanted}; buying more...");
            return;
        }

        if (Clock.UtcNow - phaseStartedAt > VerifyTimeout)
            Fail($"the purchase of {offer.ItemName} did not arrive in the bag");
    }

    private void TickClosing()
    {
        bridge.CloseExchangeShop();
        npc.Stop();
        Transition(
            ExchangeRunState.Completed,
            $"Exchanged {offer.CostFor(purchasesMade)} {offer.Currency.Name} for {Obtained}× {offer.ItemName} at {offer.NpcName}.");
    }

    public void Pause(string reason)
    {
        if (State is ExchangeRunState.Completed or ExchangeRunState.Failed)
            return;

        pauseReason = reason;
        if (State == ExchangeRunState.TurningIn)
            turnIn?.Pause(reason);
        else
            npc.Pause(reason);

        Transition(ExchangeRunState.Paused, $"Paused: {reason}");
    }

    public void Resume()
    {
        if (State != ExchangeRunState.Paused)
            return;

        if (turnIn is { State: SourceRunState.Paused })
        {
            turnIn.Resume();
            EnterPhase(ExchangeRunState.TurningIn, $"Earning {offer.Currency.Name} for {offer.ItemName}...");
            return;
        }

        npc.Resume();
        EnterPhase(
            npc.State == NpcInteractionState.Completed ? ExchangeRunState.Buying : ExchangeRunState.Approaching,
            $"Resumed: {offer.ItemName} {Obtained}/{wanted}.");
    }

    public void Stop()
    {
        if (State is ExchangeRunState.Completed or ExchangeRunState.Failed)
            return;

        turnIn?.Stop();
        bridge.CloseExchangeShop();
        npc.Stop();
        Transition(ExchangeRunState.Idle, $"Stopped after {Obtained}× {offer.ItemName}.");
    }

    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"Offer: {offer.ReceiveCount}× {offer.ItemName} for {offer.Cost} {offer.Currency.Name} " +
                     $"(shop {offer.ShopId}, {offer.ShopKind}, {offer.AddonName}) at {offer.NpcName}";
        yield return $"Wanted {wanted}, obtained {Obtained}, purchases {purchasesMade}, " +
                     $"currency held {bridge.GetCurrencyCount(offer.Currency.ItemId)}";
        if (pauseReason.Length > 0)
            yield return $"Last pause: {pauseReason}";

        if (turnIn != null)
            foreach (var line in turnIn.Describe())
                yield return "  turn-in: " + line;
    }

    private void EnterPhase(ExchangeRunState state, string status)
    {
        phaseStartedAt = Clock.UtcNow;
        purchases.Reset();
        Transition(state, status);
    }

    private void Fail(string reason)
    {
        bridge.CloseExchangeShop();
        npc.Stop();
        Transition(ExchangeRunState.Failed, reason);
    }
}

public enum TurnInRunState
{
    Idle,
    Approaching,

    /// <summary>Selecting the collectable's row in the CollectablesShop window.</summary>
    Selecting,

    /// <summary>Confirming the hand-over and waiting for the scrips.</summary>
    HandingIn,

    Closing,
    Completed,
    Failed,
    Paused,
}

/// <summary>
/// Hands collectables already in the bag to the collectable appraiser for
/// scrips (roadmap 7.17), one at a time, each confirmed by the currency
/// count rising. Chained by <see cref="ExchangeSource"/> before a purchase
/// whose scrips are short; <see cref="Obtained"/> counts scrips, not items.
/// </summary>
public sealed class CollectableTurnInRun : AutomationMachine<TurnInRunState>, ISourceRun
{
    private static readonly TimeSpan AfterWindowOpens = Pacing.AfterNodeOpen;
    private static readonly TimeSpan BetweenHandIns = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan HandInTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The appraiser's window; the NPC's dialog script ends on it.</summary>
    public const string AddonName = "CollectablesShop";

    private readonly ScripNeed need;
    private readonly List<(ScripTurnIn TurnIn, CollectableTier Tier, int Count)> candidates;
    private readonly NpcTarget target;
    private readonly IGameBridge bridge;
    private readonly INpcInteractor npc;
    private readonly bool tickNpc;
    private readonly Throttle handIns;
    private readonly long startCurrency;

    private int candidateIndex;
    private int handedIn;
    private long currencyBeforeHandIn;
    private DateTime phaseStartedAt;
    private DateTime windowOpenedAt;
    private string pauseReason = "";

    public CollectableTurnInRun(
        ScripNeed need,
        IReadOnlyList<(ScripTurnIn TurnIn, CollectableTier Tier, int Count)> candidates,
        NpcTarget target,
        IGameBridge bridge,
        INpcInteractor npc,
        ILog log,
        IClock clock,
        bool tickNpc = true)
        : base(log, clock, "[Exchange]", TurnInRunState.Idle, $"Turning collectables in for {need.CurrencyName}...")
    {
        this.need = need;
        this.candidates = [.. candidates];
        this.target = target;
        this.bridge = bridge;
        this.npc = npc;
        this.tickNpc = tickNpc;
        handIns = new Throttle(clock, BetweenHandIns);
        startCurrency = bridge.GetCurrencyCount(need.CurrencyItemId);
        phaseStartedAt = clock.UtcNow;
    }

    SourceRunState ISourceRun.State => RunState;

    public SourceRunState RunState => State switch
    {
        TurnInRunState.Idle => SourceRunState.Idle,
        TurnInRunState.Completed => SourceRunState.Completed,
        TurnInRunState.Failed => SourceRunState.Failed,
        TurnInRunState.Paused => SourceRunState.Paused,
        _ => SourceRunState.Running,
    };

    /// <summary>Scrips earned so far (the run's "items" are the currency it produces).</summary>
    public int Obtained => (int)Math.Max(0, bridge.GetCurrencyCount(need.CurrencyItemId) - startCurrency);

    /// <summary>Collectables handed over so far.</summary>
    public int HandedIn => handedIn;

    protected override void OnTick()
    {
        switch (State)
        {
            case TurnInRunState.Idle:
                Approach();
                break;

            case TurnInRunState.Approaching:
                TickApproaching();
                break;

            case TurnInRunState.Selecting:
                TickSelecting();
                break;

            case TurnInRunState.HandingIn:
                TickHandingIn();
                break;

            case TurnInRunState.Closing:
                TickClosing();
                break;
        }
    }

    private void Approach()
    {
        var script = new List<DialogStep> { new WaitForAddon(AddonName) };
        if (!npc.Start(target, script, $"{target.Name} (collectable turn-in)"))
        {
            Fail("another NPC interaction is already in flight");
            return;
        }

        EnterPhase(TurnInRunState.Approaching, $"Going to {target.Name} to hand in collectables...");
    }

    private void TickApproaching()
    {
        if (tickNpc)
            npc.Tick();

        switch (npc.State)
        {
            case NpcInteractionState.Completed:
                windowOpenedAt = Clock.UtcNow;
                EnterPhase(TurnInRunState.Selecting, $"{target.Name}: handing in collectables...");
                break;

            case NpcInteractionState.Failed:
                Fail(npc.FailureReason.Length > 0 ? npc.FailureReason : "the appraiser could not be reached");
                break;

            case NpcInteractionState.Paused:
                Transition(TurnInRunState.Paused, $"Paused: {npc.StatusText}");
                break;

            default:
                StatusText = npc.StatusText;
                break;
        }
    }

    private void TickSelecting()
    {
        if (Obtained >= need.Amount || candidateIndex >= candidates.Count)
        {
            EnterPhase(TurnInRunState.Closing, $"{Obtained} {need.CurrencyName} earned; closing the window.");
            return;
        }

        if (!bridge.IsAddonVisible(AddonName))
        {
            if (Clock.UtcNow - windowOpenedAt > WindowTimeout)
                Fail($"the {AddonName} window is not open");

            return;
        }

        // Pacing: the window has just opened, let it settle.
        if (Clock.UtcNow - windowOpenedAt < AfterWindowOpens)
            return;

        var (turnIn, tier, _) = candidates[candidateIndex];
        if (bridge.GetCollectableCount(turnIn.ItemId, turnIn.CollectabilityAt(tier)) <= 0)
        {
            candidateIndex++;
            return;
        }

        currencyBeforeHandIn = bridge.GetCurrencyCount(need.CurrencyItemId);
        if (!handIns.Try(() =>
            {
                if (bridge.TurnInCollectable(turnIn.ItemId))
                    EnterPhase(TurnInRunState.HandingIn, $"Handing over {turnIn.Name} ({tier})...");
            }))
        {
            return;
        }

        if (State == TurnInRunState.Selecting && Clock.UtcNow - phaseStartedAt > HandInTimeout)
        {
            // The window does not list it (level range, quest, wrong shop tab):
            // move on rather than stalling the whole production run.
            Log.Warning($"[Exchange] {turnIn.Name} is not listed in the appraiser's window; skipping it.");
            candidateIndex++;
            phaseStartedAt = Clock.UtcNow;
        }
    }

    private void TickHandingIn()
    {
        if (bridge.GetCurrencyCount(need.CurrencyItemId) > currencyBeforeHandIn)
        {
            handedIn++;
            EnterPhase(TurnInRunState.Selecting, $"{Obtained}/{need.Amount} {need.CurrencyName} after {handedIn} turn-in(s).");
            return;
        }

        if (bridge.IsAddonVisible("SelectYesno"))
        {
            bridge.FireAddonCallbackInt("SelectYesno", 0);
            phaseStartedAt = Clock.UtcNow;
            return;
        }

        handIns.Try(() => bridge.HandInCollectable());

        if (Clock.UtcNow - phaseStartedAt > HandInTimeout)
            Fail($"the hand-over produced no {need.CurrencyName}");
    }

    private void TickClosing()
    {
        bridge.CloseCollectablesShop();
        npc.Stop();
        Transition(
            TurnInRunState.Completed,
            $"Handed in {handedIn} collectable(s) for {Obtained} {need.CurrencyName} at {target.Name}.");
    }

    public void Pause(string reason)
    {
        if (State is TurnInRunState.Completed or TurnInRunState.Failed)
            return;

        pauseReason = reason;
        npc.Pause(reason);
        Transition(TurnInRunState.Paused, $"Paused: {reason}");
    }

    public void Resume()
    {
        if (State != TurnInRunState.Paused)
            return;

        npc.Resume();
        EnterPhase(
            npc.State == NpcInteractionState.Completed ? TurnInRunState.Selecting : TurnInRunState.Approaching,
            $"Resumed: {Obtained}/{need.Amount} {need.CurrencyName}.");
    }

    public void Stop()
    {
        if (State is TurnInRunState.Completed or TurnInRunState.Failed)
            return;

        bridge.CloseCollectablesShop();
        npc.Stop();
        Transition(TurnInRunState.Idle, $"Stopped after {handedIn} turn-in(s).");
    }

    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"Need {need.Amount} {need.CurrencyName}; earned {Obtained} from {handedIn} turn-in(s) at {target.Name}";
        foreach (var (turnIn, tier, count) in candidates)
            yield return $"  {turnIn.Name} ×{count} at {tier} ({turnIn.RewardAt(tier)} each)";

        if (pauseReason.Length > 0)
            yield return $"Last pause: {pauseReason}";
    }

    private void EnterPhase(TurnInRunState state, string status)
    {
        phaseStartedAt = Clock.UtcNow;
        handIns.Reset();
        Transition(state, status);
    }

    private void Fail(string reason)
    {
        bridge.CloseCollectablesShop();
        npc.Stop();
        Transition(TurnInRunState.Failed, reason);
    }
}
