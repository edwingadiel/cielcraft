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
/// GP spending and the collectable appraisal loop are decided by the node
/// class's rotation table (roadmap 7.14), one action per decision, each
/// confirmed by an observed change. Dalamud-free apart from the catalogue
/// (roadmap 5.1): the plugin ticks it from the framework driver.
/// </summary>
public sealed class GatheringController : AutomationMachine<GatheringState>
{
    private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InteractTimeout = TimeSpan.FromSeconds(20); // dismount + landing + interact
    private static readonly TimeSpan SwingTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BuffTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlotPopulateTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    // The table is consulted a few times a second at most: node facts are
    // re-read for every decision (statuses, boon chance after a Gift).
    private static readonly TimeSpan DecisionInterval = TimeSpan.FromMilliseconds(250);
    // The game refuses to open a node from ~2.8y (observed in Western Thanalan);
    // walk right up to it. The travel driver walks the last stretch on foot.
    internal const float InteractRange = 2.0f;

    private readonly IGameBridge gameBridge;
    private readonly INavigationProvider navigation;
    private readonly AutomationSettings configuration;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly GatheringActionCatalog catalog;
    private readonly GatheringRotationSet rotations;
    private readonly Throttle retry;
    private readonly Throttle decisionGate;
    private readonly TravelDriver travel;
    private readonly HashSet<GatherAction> used = [];
    private readonly HashSet<GatherAction> unusable = [];

    private uint requestedItemId;
    private GatheringNodeSnapshot? node;
    private NodeKind nodeKind;
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
    private bool buffsBroken;
    private GatheringNodeFacts facts = GatheringNodeFacts.Unknown;
    private bool factsLogged;
    private bool fallbackLogged;
    private string lastDecision = "-";
    private (GatherAction Action, uint ActionId, uint GpBefore, int IntegrityBefore, DateTime At)? pendingBuff;
    private (int Collectability, int Integrity, DateTime At)? pendingCollectAction;
    private int collectablesTaken;
    private CollectableTier? collectableTier;

    public GatheringController(
        IGameBridge gameBridge,
        INavigationProvider navigation,
        AutomationSettings configuration,
        ILog log,
        IClock clock,
        Func<CharacterCapabilities>? capabilities = null,
        GatheringActionCatalog? catalog = null)
        : base(log, clock, "[Gather]", GatheringState.Idle, "Idle.")
    {
        this.gameBridge = gameBridge;
        this.navigation = navigation;
        this.configuration = configuration;
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
        this.catalog = catalog ?? new GatheringActionCatalog(log);
        rotations = new GatheringRotationSet(configuration, log);
        retry = new Throttle(clock, RetryInterval);
        decisionGate = new Throttle(clock, DecisionInterval);
        travel = new TravelDriver(navigation, gameBridge, clock, log, "[Gather]");
    }

    /// <summary>Object id of the node this run targeted; 0 before the first run.</summary>
    public ulong LastNodeId { get; private set; }

    /// <summary>The last failure was the approach (path, mount, timeout), not the node itself; the loop retries such a node once.</summary>
    public bool LastFailureWasTravel { get; private set; }

    /// <summary>Collectables taken from the node of this run (roadmap 7.1); the loop sums them across nodes.</summary>
    public int CollectablesTaken => collectablesTaken;

    /// <summary>The action catalogue in use (resolved from the Action sheet); the loop shares its cordial list.</summary>
    public GatheringActionCatalog Catalog => catalog;

    /// <summary>
    /// Gathers the nearest node. itemId 0 = first gatherable slot; needed caps
    /// GP spending decisions; preferNear ranks candidate nodes by distance
    /// from that point (the recorded node area) instead of from the player.
    /// A tier makes that tier's collectability the appraisal goal (7.1);
    /// without one the highest defined threshold is. kind picks the rotation
    /// table (7.14); a point the game data marks as timed counts as
    /// unspoiled even when the caller says Normal.
    /// </summary>
    public bool Start(
        uint itemId,
        IReadOnlyCollection<ulong>? excludedNodes = null,
        int needed = int.MaxValue,
        System.Numerics.Vector3? preferNear = null,
        CollectableTier? tier = null,
        NodeKind kind = NodeKind.Normal)
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
        LastFailureWasTravel = false;
        requestedItemId = itemId;
        nodeKind = kind;
        neededCount = needed;
        gainedAtSwing = -1;
        gainedCached = 0;
        collectablesTaken = 0;
        collectableTier = tier;
        pendingCollectAction = null;
        buffsBroken = false;
        pendingBuff = null;
        used.Clear();
        unusable.Clear();
        facts = GatheringNodeFacts.Unknown;
        factsLogged = false;
        fallbackLogged = false;
        lastDecision = "-";
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
            // pause froze. Collectable appraisals and buffs likewise.
            if (awaitingSwing)
                swingStartedAt = Clock.UtcNow;
            if (pendingCollectAction is { } pending)
                pendingCollectAction = pending with { At = Clock.UtcNow };
            if (pendingBuff is { } buff)
                pendingBuff = buff with { At = Clock.UtcNow };

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
    /// Collectable node (roadmap 4.3 / 7.14): the Collectable table decides
    /// each step — by default Scrutiny then Meticulous, Collect at the goal
    /// or on the last attempt at the minimum that counts. The goal is the
    /// ordered tier (7.1) or, without one, the highest defined threshold.
    /// Observed transitions: collectability change for appraisals, integrity
    /// drop for Collect, GP drop for the GP buffs.
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

        var playerGp = gameBridge.GetPlayerState()?.CurrentGp ?? 0;
        if (ResolvePendingBuff(playerGp, snap.IntegrityRemaining))
            return;

        if (gameBridge.IsGatheringActionInProgress)
            return;

        var jobId = gameBridge.GetPlayerState()?.ClassJobId ?? 0;
        if (jobId is not (GatheringActions.MinerJobId or GatheringActions.BotanistJobId))
        {
            Fail("not on a gathering job at a collectable node");
            return;
        }

        // Without a tier the highest defined threshold is the goal and the
        // final attempt settles for any reached threshold rather than wasting
        // it. With a tier (7.1) only that tier counts toward the order, so
        // the final attempt collects only when the tier is reached.
        var highest = snap.HighThreshold > 0 ? snap.HighThreshold
            : snap.MidThreshold > 0 ? snap.MidThreshold
            : snap.LowThreshold;
        var wanted = collectableTier switch
        {
            CollectableTier.Low => snap.LowThreshold,
            CollectableTier.Mid => snap.MidThreshold,
            CollectableTier.High => snap.HighThreshold,
            _ => highest,
        };
        var goal = wanted > 0 ? wanted : highest;
        var minimum = collectableTier != null ? goal
            : snap.LowThreshold > 0 ? snap.LowThreshold : goal;

        if (!decisionGate.IsReady)
            return;

        decisionGate.Touch();
        RefreshFacts();
        var maxGp = gameBridge.GetPlayerState()?.MaxGp ?? 0;
        var context = new GatheringRotationContext
        {
            Class = NodeClass.Collectable,
            Gp = SpendableGp(playerGp),
            MaxGp = (int)maxGp,
            Integrity = snap.IntegrityRemaining,
            IntegrityMax = snap.IntegrityTotal,
            Collectability = snap.Collectability,
            CollectabilityMax = snap.CollectabilityMax,
            CollectabilityGoal = goal,
            CollectabilityMinimum = minimum,
            BoonChance = facts.BoonChance,
            Bonuses = facts.Bonuses,
            Statuses = facts.Statuses,
            Used = used,
            Unusable = unusable,
            GpCost = catalog.GpCost,
        };

        var table = rotations.For(NodeClass.Collectable);
        var chosen = table.Next(context, out var rule);
        if (chosen == null)
        {
            if (!fallbackLogged)
            {
                fallbackLogged = true;
                Log.Information("[Gather] The collectable table chose nothing; appraising with Meticulous.");
            }

            chosen = GatherAction.Meticulous;
        }

        var action = chosen.Value;
        if (!GatheringActions.ConsumesAttempt(action))
        {
            TryUseBuff(action, rule, jobId, playerGp, snap.IntegrityRemaining);
            return;
        }

        retry.Try(() =>
        {
            foreach (var actionId in catalog.ActionIds(action, jobId))
            {
                if (gameBridge.IsCraftActionReady(actionId) && gameBridge.ExecuteCraftAction(actionId))
                {
                    used.Add(action);
                    lastDecision = Describe(action, rule);
                    pendingCollectAction = (snap.Collectability, snap.IntegrityRemaining, Clock.UtcNow);
                    return;
                }
            }

            unusable.Add(action);
            if (action == GatherAction.Meticulous)
                Pause("the appraisal action is not usable (GP or level)");
            else
                Log.Information($"[Gather] {GatheringActions.DisplayName(action)} is not usable here; skipping it for this node.");
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
                LastFailureWasTravel = true;
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

        if (TickRotation(gathering))
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
    /// GP spending at an item node (spec §37, roadmap 7.14): the node class's
    /// table picks one action per decision; usability (GP, level, unlock) is
    /// the game's own action status; effects are confirmed by observing GP
    /// or integrity change. Returns true while a buff is in flight.
    /// </summary>
    private bool TickRotation(GatheringSnapshot gathering)
    {
        if (awaitingSwing)
            return false;

        if (ResolvePendingBuff(gathering.CurrentGp, gathering.IntegrityRemaining))
            return true;

        if (buffsBroken || gameBridge.IsGatheringActionInProgress)
            return false;

        var jobId = gameBridge.GetPlayerState()?.ClassJobId ?? 0;
        if (jobId is not (GatheringActions.MinerJobId or GatheringActions.BotanistJobId))
            return false;

        if (!decisionGate.IsReady)
            return false;

        decisionGate.Touch();

        if (gainedAtSwing != gatherSwings)
        {
            gainedAtSwing = gatherSwings;
            gainedCached = Math.Max(0, gameBridge.GetItemCount(chosenItemId) - baselineCount);
        }

        var gained = gainedCached;
        var remaining = neededCount == int.MaxValue ? int.MaxValue : Math.Max(0, neededCount - gained);
        var yieldPerSwing = gatherSwings > 0 ? Math.Max(1, gained / gatherSwings) : 1;

        RefreshFacts();
        var nodeClass = ItemNodeClass();
        var context = new GatheringRotationContext
        {
            Class = nodeClass,
            Gp = SpendableGp(gathering.CurrentGp),
            MaxGp = (int)gathering.MaxGp,
            Integrity = gathering.IntegrityRemaining,
            IntegrityMax = gathering.IntegrityTotal,
            Remaining = remaining,
            YieldPerSwing = yieldPerSwing,
            BoonChance = facts.BoonChance,
            Bonuses = facts.Bonuses,
            Statuses = facts.Statuses,
            Used = used,
            Unusable = unusable,
            GpCost = catalog.GpCost,
        };

        var action = rotations.For(nodeClass).Next(context, out var rule);
        return action != null && TryUseBuff(action.Value, rule, jobId, gathering.CurrentGp, gathering.IntegrityRemaining);
    }

    /// <summary>
    /// The buff in flight resolved (GP dropped, or integrity rose for a
    /// restore) or timed out; a timeout stops all GP spending on this node —
    /// the action did not land and firing more would just burn GP.
    /// Returns true while one is still pending.
    /// </summary>
    private bool ResolvePendingBuff(uint currentGp, int integrity)
    {
        if (pendingBuff is not { } pending)
            return false;

        if (currentGp < pending.GpBefore || integrity > pending.IntegrityBefore)
        {
            pendingBuff = null;
            return false;
        }

        if (Clock.UtcNow - pending.At > BuffTimeout)
        {
            Log.Warning($"[Gather] {GatheringActions.DisplayName(pending.Action)} (action {pending.ActionId}) did not resolve; skipping buffs for this node.");
            buffsBroken = true;
            pendingBuff = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Fires a buff-type action (anything that does not consume an attempt),
    /// trying the catalogue's ids in order (an upgraded action first). An
    /// action the game refuses is marked unusable for this node so the table
    /// falls through to its next rule at the next decision.
    /// </summary>
    private bool TryUseBuff(GatherAction action, GatheringRotationRule? rule, uint jobId, uint gpBefore, int integrityBefore)
    {
        foreach (var actionId in catalog.ActionIds(action, jobId))
        {
            if (gameBridge.IsCraftActionReady(actionId) && gameBridge.ExecuteCraftAction(actionId))
            {
                used.Add(action);
                lastDecision = Describe(action, rule);
                pendingBuff = (action, actionId, gpBefore, integrityBefore, Clock.UtcNow);
                Log.Information($"[Gather] {lastDecision} — action {actionId}, GP {gpBefore}.");
                return true;
            }
        }

        unusable.Add(action);
        Log.Information($"[Gather] {GatheringActions.DisplayName(action)} is not usable here (level, unlock or GP); skipping it for this node.");
        return false;
    }

    private static string Describe(GatherAction action, GatheringRotationRule? rule) =>
        rule == null
            ? GatheringActions.DisplayName(action)
            : $"{GatheringActions.DisplayName(action)} (rule {rule.Line}: {rule.Text})";

    /// <summary>GP the table may spend: none when buffs are off or a buff failed to land on this node.</summary>
    private int SpendableGp(uint currentGp) =>
        configuration.UseGatheringBuffs && !buffsBroken ? (int)currentGp : 0;

    /// <summary>
    /// Re-reads the node facts (bonus conditions, boon chance, statuses) for
    /// a decision; the first read of a node is logged with the row texts so
    /// the boon reading can be checked against the window in game.
    /// </summary>
    private void RefreshFacts()
    {
        if (node == null)
            return;

        facts = gameBridge.GetGatheringNodeFacts(node.ObjectId, chosenSlot);
        if (!factsLogged)
        {
            factsLogged = true;
            Log.Information(
                $"[Gather] Node facts: {facts.Describe()}; class {ItemNodeClass()}; " +
                $"row texts [{string.Join(" | ", facts.SlotTexts)}].");
        }
    }

    /// <summary>The table an item (non-collectable) node uses: a timed point is unspoiled whatever the caller said.</summary>
    private NodeClass ItemNodeClass()
    {
        var kind = facts.IsTimed && nodeKind == NodeKind.Normal ? NodeKind.Unspoiled : nodeKind;
        return GatheringRotationTable.ClassFor(kind, collectable: false, GatheringActions.IsCrystal(chosenItemId));
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
        yield return $"Requested item {requestedItemId}; chosen item {chosenItemId} slot {chosenSlot}; needed {(neededCount == int.MaxValue ? "unlimited" : neededCount.ToString())}; last node {LastNodeId}; kind {nodeKind}";
        yield return node == null
            ? "Node: none"
            : $"Node: {node.Name} #{node.ObjectId} at {node.Position.X:F1}, {node.Position.Y:F1}, {node.Position.Z:F1} ({node.Distance:F1}y at selection)";
        yield return $"Swings {gatherSwings}; awaitingSwing {awaitingSwing} (since {swingStartedAt:HH:mm:ss}Z); lastIntegrity {lastIntegrity}; baseline count {baselineCount}; gained {gainedCached} (at swing {gainedAtSwing}); buffsBroken {buffsBroken}; pendingBuff {(pendingBuff is { } pending ? $"{pending.Action} #{pending.ActionId} (GP {pending.GpBefore}, integrity {pending.IntegrityBefore}, at {pending.At:HH:mm:ss}Z)" : "-")}";
        yield return $"Rotation: last decision {lastDecision}; used [{string.Join(", ", used)}]; unusable [{string.Join(", ", unusable)}]; facts {facts.Describe()}";
        yield return $"Phase since {phaseStartedAt:HH:mm:ss}Z; last attempt {retry.LastAttempt:HH:mm:ss}Z; collectables taken {collectablesTaken}; tier {collectableTier?.ToString() ?? "-"}";
        foreach (var line in travel.Describe())
            yield return line;
    }
}
