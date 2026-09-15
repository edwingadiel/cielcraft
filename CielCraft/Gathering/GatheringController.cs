using System;
using System.Linq;
using CielCraft.Core;
using CielCraft.Game;
using System.Collections.Generic;

namespace CielCraft.Gathering;

public enum GatheringState
{
    Idle,
    MovingToNode,
    Interacting,
    GatheringNode,
    CollectableNode,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// Automates one gathering node (spec §65): navigate to the nearest targetable
/// node, interact, pick the requested item slot, gather on observed integrity
/// transitions until the node is exhausted, then verify the inventory gain.
/// Dalamud-free (roadmap 5.1): the plugin ticks it from the framework driver.
/// </summary>
public sealed class GatheringController : AutomationMachine<GatheringState>
{
    private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InteractTimeout = TimeSpan.FromSeconds(20); // dismount + landing + interact
    private static readonly TimeSpan SwingTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SlotPopulateTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    // The game refuses to open a node from ~2.8y (observed in Western Thanalan);
    // walk right up to it. The travel driver walks the last stretch on foot.
    internal const float InteractRange = 2.0f;

    private readonly IGameBridge gameBridge;
    private readonly INavigationProvider navigation;
    private readonly Configuration configuration;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly Throttle retry;
    private readonly TravelDriver travel;

    private uint requestedItemId;
    private GatheringNodeSnapshot? node;
    private uint chosenItemId;
    private int chosenSlot = -1;
    private int baselineCount;
    private int lastIntegrity = -1;
    private int gatherSwings;
    private bool awaitingSwing;
    private DateTime swingStartedAt;
    private DateTime phaseStartedAt;
    private int neededCount = int.MaxValue;
    private int gainedAtSwing = -1;
    private int gainedCached;
    private bool yieldBuffUsed;
    private bool buffsBroken;
    private (uint ActionId, uint GpBefore, int IntegrityBefore, DateTime At)? pendingBuff;
    private (int Collectability, int Integrity, DateTime At)? pendingCollectAction;
    private int collectablesTaken;

    public GatheringController(
        IGameBridge gameBridge,
        INavigationProvider navigation,
        Configuration configuration,
        ILog log,
        IClock clock,
        Func<CharacterCapabilities>? capabilities = null)
        : base(log, clock, "[Gather]", GatheringState.Idle, "Idle.")
    {
        this.gameBridge = gameBridge;
        this.navigation = navigation;
        this.configuration = configuration;
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
        retry = new Throttle(clock, RetryInterval);
        travel = new TravelDriver(navigation, gameBridge, clock, log, "[Gather]");
    }

    /// <summary>Object id of the node this run targeted; 0 before the first run.</summary>
    public ulong LastNodeId { get; private set; }

    /// <summary>
    /// Gathers the nearest node. itemId 0 = first gatherable slot; needed caps
    /// GP spending decisions; preferNear ranks candidate nodes by distance
    /// from that point (the recorded node area) instead of from the player.
    /// </summary>
    public bool Start(
        uint itemId,
        IReadOnlyCollection<ulong>? excludedNodes = null,
        int needed = int.MaxValue,
        System.Numerics.Vector3? preferNear = null)
    {
        if (State is GatheringState.MovingToNode or GatheringState.Interacting
            or GatheringState.GatheringNode or GatheringState.CollectableNode)
            return false;

        if (gameBridge.IsCrafting)
        {
            Transition(GatheringState.Idle, "Cannot start while crafting.");
            return false;
        }

        // A window left open by a rejected node locks the character in place.
        if (gameBridge.GetGatheringState() != null)
        {
            gameBridge.CloseGatheringWindow();
            Transition(GatheringState.Idle, "Closing a stale gathering window.");
            return false;
        }

        node = gameBridge.FindNearestGatheringNode(excludedNodes, preferNear);
        if (node == null)
        {
            Transition(GatheringState.Idle, "No targetable gathering node nearby.");
            return false;
        }

        if (!navigation.IsReady && node.Distance > InteractRange)
        {
            Transition(GatheringState.Idle, "Waiting for the navmesh to build; the node is out of reach.");
            return false;
        }

        LastNodeId = node.ObjectId;
        requestedItemId = itemId;
        neededCount = needed;
        gainedAtSwing = -1;
        gainedCached = 0;
        collectablesTaken = 0;
        pendingCollectAction = null;
        yieldBuffUsed = false;
        buffsBroken = false;
        pendingBuff = null;
        chosenItemId = 0;
        chosenSlot = -1;
        baselineCount = 0;
        lastIntegrity = -1;
        gatherSwings = 0;
        awaitingSwing = false;

        StartApproach($"Moving to {node.Name} ({node.Distance:F0}y away).");
        return true;
    }

    public void Pause(string reason)
    {
        if (State is GatheringState.MovingToNode or GatheringState.Interacting
            or GatheringState.GatheringNode or GatheringState.CollectableNode)
        {
            travel.Stop();
            navigation.Stop();
            Transition(GatheringState.Paused, $"Paused: {reason}.");
        }
    }

    public void Resume()
    {
        if (State != GatheringState.Paused)
            return;

        if (gameBridge.GetGatheringState() != null)
        {
            // Keep an in-flight swing's observer intact (its integrity drop is
            // still the completion signal) but restart its deadline, which the
            // pause froze. Collectable appraisals likewise.
            if (awaitingSwing)
                swingStartedAt = Clock.UtcNow;
            if (pendingCollectAction is { } pending)
                pendingCollectAction = pending with { At = Clock.UtcNow };

            EnterPhase(GatheringState.GatheringNode, "Resuming at the open node.");
        }
        else if (node != null)
        {
            StartApproach("Resuming approach.");
        }
        else
        {
            Transition(GatheringState.Idle, "Nothing to resume.");
        }
    }

    public void Stop()
    {
        travel.Stop();
        navigation.Stop();
        CloseNodeWindow();
        if (State is not (GatheringState.Idle or GatheringState.Completed or GatheringState.Failed))
            Transition(GatheringState.Idle, "Stopped by user.");
    }

    /// <summary>
    /// The node window pins the character; never leave it up when this run is
    /// over. Also fires while the gathering condition lingers after the window
    /// was hidden, which is the stuck state a plain hide leaves behind.
    /// </summary>
    private void CloseNodeWindow()
    {
        if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
            gameBridge.CloseGatheringWindow();
    }

    protected override void OnTick()
    {
        switch (State)
        {
            case GatheringState.MovingToNode:
                TickMoving();
                break;
            case GatheringState.Interacting:
                TickInteracting();
                break;
            case GatheringState.GatheringNode:
                TickGathering();
                break;
            case GatheringState.CollectableNode:
                TickCollectable();
                break;
        }
    }

    /// <summary>
    /// Collectable node rotation (roadmap 4.3): Meticulous until the highest
    /// reachable threshold, Collect when reached — or on the last attempt at
    /// any threshold. Observed transitions: collectability change for
    /// appraisals, integrity drop for Collect.
    /// </summary>
    private void TickCollectable()
    {
        var snap = gameBridge.GetCollectableGatheringState();
        if (snap == null)
        {
            // Back to the item window (more attempts) or the node closed.
            if (gameBridge.GetGatheringState() != null)
            {
                pendingCollectAction = null;
                EnterPhase(GatheringState.GatheringNode, "Collectable window closed; node still open.");
            }
            else
            {
                FinishNode();
            }

            return;
        }

        if (pendingCollectAction is { } pending)
        {
            if (snap.Collectability != pending.Collectability || snap.IntegrityRemaining < pending.Integrity)
            {
                if (snap.IntegrityRemaining < pending.Integrity)
                    collectablesTaken++;

                pendingCollectAction = null;
                StatusText = $"Collectable: {snap.Collectability}/{snap.CollectabilityMax}, " +
                             $"integrity {snap.IntegrityRemaining}/{snap.IntegrityTotal}, taken {collectablesTaken}.";
            }
            else if (Clock.UtcNow - pending.At > SwingTimeout)
            {
                Pause("collectable action did not resolve in time");
            }

            return;
        }

        if (gameBridge.IsGatheringActionInProgress)
            return;

        var jobId = gameBridge.GetPlayerState()?.ClassJobId ?? 0;
        if (jobId is not (GatheringActions.MinerJobId or GatheringActions.BotanistJobId))
        {
            Fail("not on a gathering job at a collectable node");
            return;
        }

        // Highest defined threshold is the goal; settle for any reached
        // threshold on the final attempt rather than wasting it.
        var goal = snap.HighThreshold > 0 ? snap.HighThreshold
            : snap.MidThreshold > 0 ? snap.MidThreshold
            : snap.LowThreshold;
        var minimum = snap.LowThreshold > 0 ? snap.LowThreshold : goal;

        var shouldCollect = snap.Collectability >= goal
                            || (snap.IntegrityRemaining <= 1 && snap.Collectability >= minimum);

        var actionId = shouldCollect
            ? GatheringActions.Collect(jobId)
            : GatheringActions.Meticulous(jobId);

        retry.Try(() =>
        {
            if (gameBridge.IsCraftActionReady(actionId) && gameBridge.ExecuteCraftAction(actionId))
                pendingCollectAction = (snap.Collectability, snap.IntegrityRemaining, Clock.UtcNow);
            else if (!shouldCollect)
                Pause("the appraisal action is not usable (GP or level)");
        });
    }

    /// <summary>
    /// Hands the approach to the travel driver: precise arrival (fly to a
    /// landable spot, dismount, walk up) when flight is unlocked here (7.16),
    /// mounted or on foot otherwise.
    /// </summary>
    private void StartApproach(string statusText)
    {
        var fly = capabilities().CanFlyIn(gameBridge.CurrentTerritoryId);
        travel.Start(node!.Position, InteractRange, fly, preciseArrival: true, NavigateTimeout, node.Name);
        EnterPhase(GatheringState.MovingToNode, statusText);
    }

    private void TickMoving()
    {
        if (node == null)
        {
            Fail("node vanished during approach");
            return;
        }

        // Still in gathering mode from the previous node (the window may
        // already be hidden): the character cannot move until it clears.
        if (gameBridge.IsGathering)
        {
            retry.Try(CloseNodeWindow);
            StatusText = $"Leaving the previous node before moving to {node.Name}...";
            return;
        }

        travel.Tick();
        switch (travel.State)
        {
            case TravelState.Arrived:
                EnterPhase(GatheringState.Interacting, $"Arrived at {node.Name}; interacting.");
                break;
            case TravelState.Failed:
                Fail(travel.FailureReason);
                break;
            default:
                StatusText = travel.StatusText;
                break;
        }
    }

    private void TickInteracting()
    {
        // Gathering requires being dismounted.
        if (gameBridge.IsMounted)
        {
            retry.Try(gameBridge.TryDismount);
            return;
        }

        if (gameBridge.GetGatheringState() != null)
        {
            EnterPhase(GatheringState.GatheringNode, "Node open; gathering.");
            return;
        }

        if (Clock.UtcNow - phaseStartedAt > InteractTimeout)
        {
            Fail("the gathering window did not open");
            return;
        }

        // Pause a beat after arriving before touching the node (pacing).
        if (Clock.UtcNow - phaseStartedAt < Pacing.BeforeInteract)
            return;

        retry.Try(() =>
        {
            if (node != null && !gameBridge.InteractWithObject(node.ObjectId))
                Fail("the node despawned before it could be opened");
        });
    }

    private void TickGathering()
    {
        if (gameBridge.GetCollectableGatheringState() != null)
        {
            pendingCollectAction = null;
            EnterPhase(GatheringState.CollectableNode, "Collectable window open; appraising.");
            return;
        }

        var gathering = gameBridge.GetGatheringState();

        if (gathering == null)
        {
            // Node closed: exhausted (normal) or despawned mid-way.
            FinishNode();
            return;
        }

        // Quick gathering bypasses per-swing control; turn it off first (roadmap 2.3).
        if (gameBridge.IsQuickGatheringEnabled)
        {
            retry.Try(gameBridge.DisableQuickGathering);
            return;
        }

        if (chosenSlot < 0)
        {
            // Let the window settle before the first click (pacing; the slots
            // also fill in over these frames).
            if (Clock.UtcNow - phaseStartedAt < Pacing.AfterNodeOpen)
                return;

            if (!ChooseSlot(gathering))
                return;
        }

        if (TickBuffs(gathering))
            return;

        if (awaitingSwing)
        {
            if (gathering.IntegrityRemaining < lastIntegrity)
            {
                awaitingSwing = false;
                lastIntegrity = gathering.IntegrityRemaining;
                gatherSwings++;
                StatusText = $"Gathering: {gatherSwings} swings, integrity {gathering.IntegrityRemaining}/{gathering.IntegrityTotal}.";
            }
            else if (Clock.UtcNow - swingStartedAt > SwingTimeout)
            {
                Pause("gather attempt did not resolve in time");
            }

            return;
        }

        if (gameBridge.IsGatheringActionInProgress)
            return;

        retry.Try(() =>
        {
            if (gameBridge.GatherSlot(chosenSlot))
            {
                // Baseline for the completion check is the integrity right now,
                // not the value after the last swing: an integrity restore in
                // between (Solid Reason / Ageless Words) raises it, and the
                // next swing's drop would otherwise never register.
                lastIntegrity = gathering.IntegrityRemaining;
                awaitingSwing = true;
                swingStartedAt = Clock.UtcNow;
            }
        });
    }

    /// <summary>
    /// GP spending (spec §37): a yield buff once per node and integrity
    /// restores while they pay for themselves. Usability (GP, level, unlock)
    /// is the game's own action status; effects are confirmed by observing GP
    /// or integrity change. Returns true while a buff is in flight.
    /// </summary>
    private bool TickBuffs(GatheringSnapshot gathering)
    {
        if (!configuration.UseGatheringBuffs || buffsBroken || awaitingSwing)
            return false;

        if (pendingBuff is { } pending)
        {
            if (gathering.CurrentGp < pending.GpBefore || gathering.IntegrityRemaining > pending.IntegrityBefore)
            {
                pendingBuff = null;
                return false;
            }

            if (Clock.UtcNow - pending.At > TimeSpan.FromSeconds(5))
            {
                // The action did not land; stop spending GP this node.
                Log.Warning($"[Gather] Buff action {pending.ActionId} did not resolve; skipping buffs.");
                buffsBroken = true;
                pendingBuff = null;
            }

            return pendingBuff != null;
        }

        if (gameBridge.IsGatheringActionInProgress)
            return false;

        var jobId = gameBridge.GetPlayerState()?.ClassJobId ?? 0;
        if (jobId is not (GatheringActions.MinerJobId or GatheringActions.BotanistJobId))
            return false;

        if (gainedAtSwing != gatherSwings)
        {
            gainedAtSwing = gatherSwings;
            gainedCached = Math.Max(0, gameBridge.GetItemCount(chosenItemId) - baselineCount);
        }

        var gained = gainedCached;
        var remaining = neededCount == int.MaxValue ? int.MaxValue : Math.Max(0, neededCount - gained);
        var yieldPerSwing = gatherSwings > 0 ? Math.Max(1, gained / gatherSwings) : 1;

        // Yield buff: worth it when this node alone cannot cover the need.
        if (!yieldBuffUsed && remaining > gathering.IntegrityRemaining * yieldPerSwing)
        {
            foreach (var actionId in new[] { GatheringActions.YieldII(jobId), GatheringActions.YieldI(jobId) })
            {
                if (gameBridge.IsCraftActionReady(actionId) && gameBridge.ExecuteCraftAction(actionId))
                {
                    yieldBuffUsed = true;
                    pendingBuff = (actionId, gathering.CurrentGp, gathering.IntegrityRemaining, Clock.UtcNow);
                    Log.Information($"[Gather] Using yield buff (action {actionId}).");
                    return true;
                }
            }

            yieldBuffUsed = true; // not usable (GP/level); do not retry every tick
        }

        // Integrity restore: an extra swing is worth 300 GP while we still need more.
        if (gathering.IntegrityRemaining < gathering.IntegrityTotal
            && remaining > gathering.IntegrityRemaining * yieldPerSwing)
        {
            var actionId = GatheringActions.RestoreIntegrity(jobId);
            if (gameBridge.IsCraftActionReady(actionId) && gameBridge.ExecuteCraftAction(actionId))
            {
                pendingBuff = (actionId, gathering.CurrentGp, gathering.IntegrityRemaining, Clock.UtcNow);
                Log.Information($"[Gather] Restoring integrity (action {actionId}).");
                return true;
            }
        }

        return false;
    }

    private bool ChooseSlot(GatheringSnapshot gathering)
    {
        GatheringItemSlot? slot = null;
        var requestedPresent = false;
        foreach (var candidate in gathering.Items)
        {
            if (requestedItemId != 0)
            {
                if (candidate.ItemId != requestedItemId)
                    continue;

                requestedPresent = true;
                if (candidate.Enabled)
                {
                    slot = candidate;
                    break;
                }
            }
            else if (candidate.Enabled)
            {
                slot = candidate;
                break;
            }
        }

        if (slot == null)
        {
            // The window's item slots fill in over the first frames after it
            // opens: the list is empty, or the item is listed but its checkbox
            // is not clickable yet. Give them a moment before concluding the
            // node is the wrong one.
            var populating = gathering.Items.Count == 0 || requestedPresent || requestedItemId == 0;
            if (populating && Clock.UtcNow - phaseStartedAt < SlotPopulateTimeout)
            {
                StatusText = "Node open; waiting for the item list...";
                return false;
            }

            var contents = string.Join(", ", gathering.Items.Select(i => $"{i.ItemId}{(i.Enabled ? "" : " (disabled)")}"));
            Fail(requestedItemId != 0
                ? $"item {requestedItemId} is not gatherable at this node (node holds: {contents})"
                : $"no gatherable item in this node (node holds: {contents})");
            return false;
        }

        chosenSlot = slot.Index;
        chosenItemId = slot.ItemId;
        baselineCount = gameBridge.GetItemCount(chosenItemId);
        lastIntegrity = gathering.IntegrityRemaining;
        Log.Information(
            $"[Gather] Gathering item {chosenItemId} from slot {chosenSlot} " +
            $"(owned {baselineCount}, integrity {gathering.IntegrityRemaining}/{gathering.IntegrityTotal}).");
        return true;
    }

    private void FinishNode()
    {
        if (chosenItemId == 0)
        {
            Fail("the node closed before gathering started");
            return;
        }

        // Verify against inventory — never assume the swings yielded items (spec §34).
        var gained = gameBridge.GetItemCount(chosenItemId) - baselineCount;
        if (gained > 0)
            Transition(GatheringState.Completed, $"Completed: +{gained} of item {chosenItemId} in {gatherSwings} swings.");
        else if (collectablesTaken > 0)
            Transition(GatheringState.Completed, $"Completed: {collectablesTaken} collectable(s) taken.");
        else
            Fail($"node finished but inventory did not increase (swings: {gatherSwings})");
    }

    private void EnterPhase(GatheringState state, string statusText)
    {
        phaseStartedAt = Clock.UtcNow;
        retry.Reset();
        Transition(state, statusText);
    }

    private void Fail(string reason)
    {
        travel.Stop();
        navigation.Stop();
        CloseNodeWindow();
        Transition(GatheringState.Failed, $"Failed: {reason}.");
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        foreach (var line in base.Describe())
            yield return line;
        yield return $"Requested item {requestedItemId}; chosen item {chosenItemId} slot {chosenSlot}; needed {(neededCount == int.MaxValue ? "unlimited" : neededCount.ToString())}; last node {LastNodeId}";
        yield return node == null
            ? "Node: none"
            : $"Node: {node.Name} #{node.ObjectId} at {node.Position.X:F1}, {node.Position.Y:F1}, {node.Position.Z:F1} ({node.Distance:F1}y at selection)";
        yield return $"Swings {gatherSwings}; awaitingSwing {awaitingSwing} (since {swingStartedAt:HH:mm:ss}Z); lastIntegrity {lastIntegrity}; baseline count {baselineCount}; gained {gainedCached} (at swing {gainedAtSwing}); yieldBuffUsed {yieldBuffUsed}; buffsBroken {buffsBroken}; pendingBuff {(pendingBuff is { } pending ? $"{pending.ActionId} (GP {pending.GpBefore}, integrity {pending.IntegrityBefore}, at {pending.At:HH:mm:ss}Z)" : "-")}";
        yield return $"Phase since {phaseStartedAt:HH:mm:ss}Z; last attempt {retry.LastAttempt:HH:mm:ss}Z; collectables taken {collectablesTaken}";
        foreach (var line in travel.Describe())
            yield return line;
    }
}
