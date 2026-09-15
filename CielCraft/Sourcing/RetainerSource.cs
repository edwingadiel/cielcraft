using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Sourcing;

/// <summary>
/// A material source that takes stock off a retainer (roadmap 7.17): the
/// production runner asks it for any raw material no MIN/BTN node yields, and
/// when a retainer holds the whole amount the run goes to a summoning bell,
/// summons that retainer, withdraws, verifies by the bag count and sends it
/// away again.
///
/// With <see cref="AutomationSettings.RetainerVentures"/> on it also offers a
/// venture that is <em>already returning</em> — never one that would still
/// take an hour, which would park the whole production. Sending retainers out
/// ahead of a run so their ventures are back when it needs them ("start
/// ventures early") is the follow-up design noted in the P3b report.
/// </summary>
public sealed class RetainerSource : IMaterialSource
{
    private readonly IGameBridge gameBridge;
    private readonly RetainerDatabase retainers;
    private readonly AutomationSettings configuration;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly INpcInteractor? npc;
    private readonly Func<uint, string> itemName;

    public RetainerSource(
        IGameBridge gameBridge,
        RetainerDatabase retainers,
        AutomationSettings configuration,
        ILog log,
        IClock clock,
        INpcInteractor? npc = null,
        Func<uint, string>? itemName = null)
    {
        this.gameBridge = gameBridge;
        this.retainers = retainers;
        this.configuration = configuration;
        this.log = log;
        this.clock = clock;
        this.npc = npc;
        this.itemName = itemName ?? (id => $"item {id}");
    }

    public MaterialSourceKind Kind => MaterialSourceKind.Retainer;

    public string Name => "retainer";

    public SourceOffer? Offer(uint itemId, int amount)
    {
        var supply = Choose(itemId, amount);
        if (supply == null)
            return null;

        var description = supply.Kind == RetainerSupplyKind.Withdraw
            ? $"Withdraw {itemName(itemId)} ×{amount} from {supply.Retainer.Name}"
            : $"Collect {supply.Venture?.ItemName ?? itemName(itemId)} ×{amount} from {supply.Retainer.Name}'s venture";

        // Retainer stock costs nothing; a venture was paid for when it was sent.
        return new SourceOffer(itemId, amount, Kind, description, supply.EstimatedSeconds);
    }

    public ISourceRun Start(SourceOffer offer)
    {
        // Re-decide at start: the offer may be a few minutes old, and the
        // retainer that held the stock is the one the run has to summon.
        var supply = Choose(offer.ItemId, offer.Amount);
        return new RetainerRun(
            gameBridge, retainers, supply, offer, npc, log, clock, itemName);
    }

    private RetainerSupply? Choose(uint itemId, int amount) =>
        RetainerOffers.Choose(
            retainers.Retainers(),
            itemId,
            amount,
            retainer => retainers.HeldBy(retainer.Index, itemId),
            retainers.VentureInFlight,
            configuration.RetainerVentures,
            clock.UtcNow);

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"Retainer source — ventures {configuration.RetainerVentures}, interactor {(npc == null ? "not wired" : "wired")}";
        foreach (var line in retainers.Describe())
            yield return line;
    }
}

public enum RetainerRunPhase
{
    Idle,
    AtBell,
    OpeningInventory,
    Withdrawing,
    OpeningVentureReport,
    CollectingVenture,
    ClosingRetainerWindow,
    LeavingBell,
    Completed,
    Failed,
    Paused,
}

/// <summary>
/// One withdrawal or venture collection in flight (roadmap 7.17), ticked by
/// the production runner. The bag count is the truth: the run is only complete
/// once the inventory actually holds what the offer promised.
/// </summary>
public sealed class RetainerRun : AutomationMachine<RetainerRunPhase>, ISourceRun
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>The retainer menu entry that opens its bag ("Entrust or withdraw items").</summary>
    private const string ItemsMenuEntry = "item";

    /// <summary>The retainer menu entry that reports a venture ("View venture report").</summary>
    private const string VentureMenuEntry = "venture";

    private readonly IGameBridge gameBridge;
    private readonly RetainerSupply? supply;
    private readonly SourceOffer offer;
    private readonly BellSession bell;
    private readonly Throttle attempts;
    private readonly Func<uint, string> itemName;

    private readonly int baseline;
    private DateTime phaseStartedAt;
    private DateTime settleUntil;
    private RetainerRunPhase resumeTo = RetainerRunPhase.Idle;
    private bool withdrawRequested;
    private string failureReason = "";

    public RetainerRun(
        IGameBridge gameBridge,
        RetainerDatabase retainers,
        RetainerSupply? supply,
        SourceOffer offer,
        INpcInteractor? npc,
        ILog log,
        IClock clock,
        Func<uint, string> itemName)
        : base(log, clock, "[Retainer]", RetainerRunPhase.Idle, "Idle.")
    {
        this.gameBridge = gameBridge;
        this.supply = supply;
        this.offer = offer;
        this.itemName = itemName;
        bell = new BellSession(gameBridge, retainers, npc, log, clock, "[Retainer]");
        attempts = new Throttle(clock, RetryInterval);
        baseline = gameBridge.GetItemCount(offer.ItemId);

        if (supply == null)
        {
            failureReason = "no retainer holds the material any more";
            Transition(RetainerRunPhase.Failed, $"{offer.Description}: {failureReason}.");
            return;
        }

        bell.Start(supply.Retainer.Index, offer.Description);
        Transition(RetainerRunPhase.AtBell, $"{offer.Description}: going to a summoning bell.");
    }

    /// <summary>What the bag has gained since the run began; never negative.</summary>
    public int Obtained => Math.Max(0, gameBridge.GetItemCount(offer.ItemId) - baseline);

    SourceRunState ISourceRun.State => State switch
    {
        RetainerRunPhase.Completed => SourceRunState.Completed,
        RetainerRunPhase.Failed => SourceRunState.Failed,
        RetainerRunPhase.Paused => SourceRunState.Paused,
        RetainerRunPhase.Idle => SourceRunState.Idle,
        _ => SourceRunState.Running,
    };

    public void Pause(string reason)
    {
        if (State is RetainerRunPhase.Paused or RetainerRunPhase.Completed or RetainerRunPhase.Failed)
            return;

        resumeTo = State;
        bell.Pause(reason);
        Transition(RetainerRunPhase.Paused, reason);
    }

    public void Resume()
    {
        if (State != RetainerRunPhase.Paused)
            return;

        bell.Resume();
        Enter(resumeTo, $"{offer.Description}: resumed.");
    }

    public void Stop()
    {
        if (State is RetainerRunPhase.Completed or RetainerRunPhase.Failed)
            return;

        bell.Abort();
        SetState(RetainerRunPhase.Idle, "Stopped.");
    }

    protected override void OnTick()
    {
        if (State is RetainerRunPhase.Completed or RetainerRunPhase.Failed
            or RetainerRunPhase.Paused or RetainerRunPhase.Idle)
            return;

        if (Clock.UtcNow < settleUntil)
            return;

        // The bell session drives travel, the list and the dismissal; the run
        // only acts while the retainer is actually out.
        bell.Tick();
        switch (bell.State)
        {
            case BellSessionState.Failed:
                Fail(bell.FailureReason);
                return;

            case BellSessionState.Paused:
                Pause(bell.StatusText);
                return;

            case BellSessionState.Done:
                Complete();
                return;
        }

        if (bell.State != BellSessionState.Ready)
        {
            StatusText = bell.StatusText;
            return;
        }

        if (State == RetainerRunPhase.AtBell)
        {
            Enter(
                supply!.Kind == RetainerSupplyKind.Withdraw ? RetainerRunPhase.OpeningInventory : RetainerRunPhase.OpeningVentureReport,
                supply.Kind == RetainerSupplyKind.Withdraw
                    ? $"{offer.Description}: opening the retainer's bag."
                    : $"{offer.Description}: opening the venture report.");
            return;
        }

        switch (State)
        {
            case RetainerRunPhase.OpeningInventory:
                if (gameBridge.IsRetainerInventoryOpen)
                {
                    Settle(Pacing.AfterNodeOpen);
                    withdrawRequested = false;
                    Enter(RetainerRunPhase.Withdrawing, $"{offer.Description}: withdrawing.");
                    break;
                }

                if (TimedOut("the retainer's bag did not open"))
                    break;

                attempts.Try(() => gameBridge.SelectRetainerMenuOption(ItemsMenuEntry));
                break;

            case RetainerRunPhase.Withdrawing:
                if (Obtained >= offer.Amount)
                {
                    Log.Information($"[Retainer] {itemName(offer.ItemId)} ×{Obtained} withdrawn from {supply!.Retainer.Name}.");
                    Enter(RetainerRunPhase.ClosingRetainerWindow, $"{offer.Description}: closing the retainer's bag.");
                    break;
                }

                if (TimedOut($"only {Obtained} of {offer.Amount} {itemName(offer.ItemId)} came out of the retainer"))
                    break;

                // One request, then watch the bag: the move is a server round
                // trip, so hammering it would withdraw the same stack twice.
                if (!withdrawRequested)
                {
                    attempts.Try(() =>
                    {
                        gameBridge.WithdrawFromRetainer(offer.ItemId, offer.Amount - Obtained);
                        withdrawRequested = true;
                    });
                }
                else if (attempts.IsReady && Obtained <= 0)
                {
                    withdrawRequested = false;
                }

                break;

            case RetainerRunPhase.OpeningVentureReport:
                if (gameBridge.IsAddonVisible("RetainerTaskResult"))
                {
                    Settle(Pacing.AfterNodeOpen);
                    Enter(RetainerRunPhase.CollectingVenture, $"{offer.Description}: collecting the venture.");
                    break;
                }

                if (TimedOut("the venture report did not open"))
                    break;

                attempts.Try(() => gameBridge.SelectRetainerMenuOption(VentureMenuEntry));
                break;

            case RetainerRunPhase.CollectingVenture:
                if (Obtained >= offer.Amount)
                {
                    Log.Information(
                        $"[Retainer] {supply!.Retainer.Name}'s venture brought {itemName(offer.ItemId)} ×{Obtained}.");
                    Enter(RetainerRunPhase.ClosingRetainerWindow, $"{offer.Description}: closing the venture report.");
                    break;
                }

                if (TimedOut($"the venture brought {Obtained} of {offer.Amount} {itemName(offer.ItemId)}"))
                    break;

                attempts.Try(() => gameBridge.CollectVenture());
                break;

            case RetainerRunPhase.ClosingRetainerWindow:
                if (!gameBridge.IsRetainerInventoryOpen && !gameBridge.IsAddonVisible("RetainerTaskResult"))
                {
                    Enter(RetainerRunPhase.LeavingBell, $"{offer.Description}: leaving the bell.");
                    bell.Finish();
                    break;
                }

                if (TimedOut("the retainer window would not close"))
                    break;

                attempts.Try(gameBridge.DismissRetainer);
                break;

            case RetainerRunPhase.LeavingBell:
                // The bell session is dismissing and closing; its Done case
                // above completes the run.
                StatusText = bell.StatusText;
                break;
        }
    }

    private void Complete()
    {
        if (State == RetainerRunPhase.Completed)
            return;

        if (Obtained < offer.Amount)
        {
            Fail($"only {Obtained} of {offer.Amount} {itemName(offer.ItemId)} reached the bag");
            return;
        }

        Transition(RetainerRunPhase.Completed, $"{offer.Description}: {Obtained} in the bag.");
    }

    private void Enter(RetainerRunPhase next, string statusText)
    {
        phaseStartedAt = Clock.UtcNow;
        attempts.Reset();
        Transition(next, statusText);
    }

    private void Settle(TimeSpan delay) => settleUntil = Clock.UtcNow + delay;

    private bool TimedOut(string reason)
    {
        if (Clock.UtcNow - phaseStartedAt <= StepTimeout)
            return false;

        Fail(reason);
        return true;
    }

    private void Fail(string reason)
    {
        failureReason = reason;
        bell.Abort();
        // The status text is what the runner reports as the failure reason.
        Transition(RetainerRunPhase.Failed, reason);
    }

    public override IEnumerable<string> Describe()
    {
        yield return $"Retainer run {State} — {StatusText}";
        yield return $"{offer.Description}; obtained {Obtained}/{offer.Amount} (baseline {baseline}); " +
                     $"phase since {phaseStartedAt:HH:mm:ss}Z; failure: {(failureReason.Length == 0 ? "-" : failureReason)}";
        foreach (var line in bell.Describe())
            yield return "  " + line;
    }
}
