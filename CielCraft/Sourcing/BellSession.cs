using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Sourcing;

public enum BellSessionState
{
    Idle,

    /// <summary>No bell in this zone: teleporting to a city that has one.</summary>
    Teleporting,

    /// <summary>A bell is in the object table; walking up to it.</summary>
    Approaching,

    /// <summary>Ringing the bell and waiting for the retainer list.</summary>
    OpeningList,

    /// <summary>The retainer is being summoned.</summary>
    Summoning,

    /// <summary>The retainer is out; the caller drives its windows until it calls <see cref="BellSession.Finish"/>.</summary>
    Ready,

    Dismissing,
    ClosingList,
    Done,
    Failed,
    Paused,
}

/// <summary>
/// One visit to a summoning bell (roadmap 7.17), shared by the retainer
/// source and the inventory keeper: get to a bell, ring it, summon a
/// retainer, hand control to the caller, then send the retainer away and
/// close the list behind it.
///
/// The bells are not in the Level sheet (see
/// <see cref="RetainerDatabase.NearestBell"/>), so there are no bundled
/// coordinates: a bell already in the object table supplies its own position
/// for the walk, and a zone without one is left by teleporting to a city that
/// has one. Without an NPC interactor (roadmap 7.3, P1) the session can only
/// use a bell the character is already standing at.
/// </summary>
public sealed class BellSession : AutomationMachine<BellSessionState>
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1.5);

    private readonly IGameBridge gameBridge;
    private readonly RetainerDatabase retainers;
    private readonly INpcInteractor? npc;
    private readonly Throttle attempts;

    private int retainerIndex;
    private string description = "";
    private DateTime phaseStartedAt;
    private DateTime settleUntil;
    private BellSessionState resumeTo = BellSessionState.Idle;
    private bool interactionStarted;
    private bool teleportIssued;
    private bool sawLoadingScreen;
    private int teleportCandidate;

    public BellSession(
        IGameBridge gameBridge, RetainerDatabase retainers, INpcInteractor? npc, ILog log, IClock clock, string logPrefix)
        : base(log, clock, logPrefix, BellSessionState.Idle, "Idle.")
    {
        this.gameBridge = gameBridge;
        this.retainers = retainers;
        this.npc = npc;
        attempts = new Throttle(clock, RetryInterval);
    }

    /// <summary>Why the visit failed; empty otherwise.</summary>
    public string FailureReason { get; private set; } = "";

    /// <summary>The retainer this visit summoned (or is summoning).</summary>
    public int RetainerIndex => retainerIndex;

    /// <summary>Begins a visit; false when one is already in flight.</summary>
    public bool Start(int index, string what)
    {
        if (State is not (BellSessionState.Idle or BellSessionState.Done or BellSessionState.Failed))
            return false;

        retainerIndex = index;
        description = what;
        FailureReason = "";
        interactionStarted = false;
        teleportIssued = false;
        sawLoadingScreen = false;
        teleportCandidate = 0;
        Enter(BellSessionState.Approaching, $"{what}: heading to a summoning bell.");
        return true;
    }

    /// <summary>The caller is done with the summoned retainer: send it away and close up.</summary>
    public void Finish()
    {
        if (State != BellSessionState.Ready)
            return;

        Enter(BellSessionState.Dismissing, $"{description}: dismissing the retainer.");
    }

    /// <summary>Abandons the visit, closing whatever it left open.</summary>
    public void Abort()
    {
        if (State is BellSessionState.Idle or BellSessionState.Done)
            return;

        npc?.Stop();
        gameBridge.DismissRetainer();
        gameBridge.CloseRetainerList();
        SetState(BellSessionState.Idle, "Idle.");
    }

    public void Pause(string reason)
    {
        if (State is BellSessionState.Paused or BellSessionState.Idle or BellSessionState.Done or BellSessionState.Failed)
            return;

        resumeTo = State;
        npc?.Pause(reason);
        Transition(BellSessionState.Paused, $"Paused: {reason}");
    }

    public void Resume()
    {
        if (State != BellSessionState.Paused)
            return;

        npc?.Resume();
        Enter(resumeTo, $"{description}: resumed.");
    }

    protected override void OnTick()
    {
        if (Settling())
            return;

        switch (State)
        {
            case BellSessionState.Approaching:
                TickApproaching();
                break;

            case BellSessionState.Teleporting:
                TickTeleporting();
                break;

            case BellSessionState.OpeningList:
                if (gameBridge.IsAddonVisible("RetainerList"))
                {
                    // Give the list a beat before clicking a row (pacing).
                    Settle(Pacing.AfterNodeOpen);
                    Enter(BellSessionState.Summoning, $"{description}: summoning retainer {retainerIndex + 1}.");
                    break;
                }

                if (TimedOut("the retainer list did not open"))
                    break;

                attempts.Try(() => gameBridge.OpenRetainerList());
                break;

            case BellSessionState.Summoning:
                if (gameBridge.IsRetainerSummoned)
                {
                    Settle(Pacing.AfterNodeOpen);
                    Enter(BellSessionState.Ready, $"{description}: retainer summoned.");
                    break;
                }

                if (TimedOut("the retainer did not answer the bell"))
                    break;

                attempts.Try(() => gameBridge.SelectRetainer(retainerIndex));
                break;

            case BellSessionState.Ready:
                // The caller owns this phase; nothing to drive here.
                break;

            case BellSessionState.Dismissing:
                if (!gameBridge.IsRetainerSummoned)
                {
                    Settle(Pacing.BeforeInteract);
                    Enter(BellSessionState.ClosingList, $"{description}: closing the retainer list.");
                    break;
                }

                if (TimedOut("the retainer would not leave"))
                    break;

                attempts.Try(gameBridge.DismissRetainer);
                break;

            case BellSessionState.ClosingList:
                if (!gameBridge.IsAddonVisible("RetainerList"))
                {
                    Transition(BellSessionState.Done, $"{description}: done at the bell.");
                    break;
                }

                if (TimedOut("the retainer list would not close"))
                    break;

                attempts.Try(gameBridge.CloseRetainerList);
                break;
        }
    }

    private void TickApproaching()
    {
        if (gameBridge.IsNearSummoningBell)
        {
            npc?.Stop();
            Enter(BellSessionState.OpeningList, $"{description}: ringing the bell.");
            return;
        }

        var bell = gameBridge.FindSummoningBell();
        if (bell == null)
        {
            Enter(BellSessionState.Teleporting, $"{description}: no bell in this zone; teleporting to one.");
            return;
        }

        if (npc == null)
        {
            Fail("a summoning bell is in this zone but nothing can walk to it (the NPC interactor is not wired)");
            return;
        }

        if (!interactionStarted)
        {
            // The bell is an EventObj, not an ENpc: it is addressed by the
            // live object-table position and its DataId, and the script ends
            // the moment the retainer list is up — this session drives it.
            var target = new NpcTarget(0, "Summoning Bell", gameBridge.CurrentTerritoryId, bell.Position, 0);
            interactionStarted = npc.Start(target, [new WaitForAddon("RetainerList")], "a summoning bell");
            if (!interactionStarted)
            {
                Fail("another NPC interaction is already in flight");
                return;
            }
        }

        npc.Tick();
        switch (npc.State)
        {
            case NpcInteractionState.Completed:
                interactionStarted = false;
                Enter(BellSessionState.OpeningList, $"{description}: ringing the bell.");
                break;

            case NpcInteractionState.Failed:
                interactionStarted = false;
                Fail(npc.FailureReason.Length > 0 ? npc.FailureReason : "could not reach the summoning bell");
                break;

            case NpcInteractionState.Paused:
                Pause(npc.StatusText);
                break;

            default:
                StatusText = npc.StatusText;
                break;
        }
    }

    private void TickTeleporting()
    {
        if (gameBridge.IsBetweenAreas)
        {
            sawLoadingScreen = true;
            StatusText = $"{description}: loading...";
            return;
        }

        if (sawLoadingScreen)
        {
            sawLoadingScreen = false;
            teleportIssued = false;
            // Never act on the first frame of a new zone (pacing).
            Settle(Pacing.AfterZoneChange);
            Enter(BellSessionState.Approaching, $"{description}: looking for the bell.");
            return;
        }

        if (teleportIssued)
        {
            if (TimedOut("the teleport to a summoning bell did not happen"))
                return;

            return;
        }

        var candidates = retainers.BellCandidates();
        while (teleportCandidate < candidates.Count)
        {
            var site = candidates[teleportCandidate++];
            if (site.TerritoryId == gameBridge.CurrentTerritoryId)
                continue;

            // TeleportToTerritory answers false when nothing there is attuned,
            // which is also the attunement check — no separate query needed.
            if (!gameBridge.TeleportToTerritory(site.TerritoryId))
                continue;

            teleportIssued = true;
            phaseStartedAt = Clock.UtcNow;
            Log.Information($"{LogPrefix} {description}: teleporting to {site.PlaceName} for a summoning bell.");
            return;
        }

        Fail("no summoning bell is reachable (no attuned city aetheryte)");
    }

    /// <summary>A settle delay is running; nothing may be clicked until it expires (pacing).</summary>
    private bool Settling() => Clock.UtcNow < settleUntil;

    private void Settle(TimeSpan delay) => settleUntil = Clock.UtcNow + delay;

    private void Enter(BellSessionState next, string statusText)
    {
        phaseStartedAt = Clock.UtcNow;
        attempts.Reset();
        Transition(next, statusText);
    }

    private bool TimedOut(string reason)
    {
        var limit = State == BellSessionState.Teleporting ? TeleportTimeout : StepTimeout;
        if (Clock.UtcNow - phaseStartedAt <= limit)
            return false;

        Fail(reason);
        return true;
    }

    private void Fail(string reason)
    {
        FailureReason = reason;
        npc?.Stop();
        gameBridge.DismissRetainer();
        gameBridge.CloseRetainerList();
        Transition(BellSessionState.Failed, $"{description}: {reason}.");
    }

    public override IEnumerable<string> Describe()
    {
        yield return $"Bell visit {State} — {StatusText}";
        yield return $"retainer {retainerIndex}; phase since {phaseStartedAt:HH:mm:ss}Z; settle until {settleUntil:HH:mm:ss}Z; " +
                     $"near a bell {SafeNearBell()}; summoned {gameBridge.IsRetainerSummoned}; failure: {(FailureReason.Length == 0 ? "-" : FailureReason)}";
    }

    private string SafeNearBell()
    {
        try
        {
            return gameBridge.IsNearSummoningBell.ToString();
        }
        catch (Exception e)
        {
            return $"<{e.GetType().Name}>";
        }
    }
}
