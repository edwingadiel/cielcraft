using System;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Plugin.Services;

namespace CielCraft.Gathering;

public enum GatheringState
{
    Idle,
    MovingToNode,
    Interacting,
    GatheringNode,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// Automates one gathering node (spec §65): navigate to the nearest targetable
/// node, interact, pick the requested item slot, gather on observed integrity
/// transitions until the node is exhausted, then verify the inventory gain.
/// </summary>
public sealed class GatheringController : IDisposable
{
    private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InteractTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SwingTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private const float InteractRange = 3.0f;

    private readonly IGameBridge gameBridge;
    private readonly INavigationProvider navigation;

    private uint requestedItemId;
    private GatheringNodeSnapshot? node;
    private uint chosenItemId;
    private int chosenSlot = -1;
    private int baselineCount;
    private int lastIntegrity = -1;
    private int gatherSwings;
    private bool awaitingSwing;
    private DateTime phaseStartedAt;
    private DateTime lastAttemptAt;
    private int mountAttempts;
    private bool flyBlocked;
    private bool flyAttempted;

    public GatheringState State { get; private set; } = GatheringState.Idle;
    public string StatusText { get; private set; } = "Idle.";

    public GatheringController(IGameBridge gameBridge, INavigationProvider navigation)
    {
        this.gameBridge = gameBridge;
        this.navigation = navigation;

        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
    }

    /// <summary>Object id of the node this run targeted; 0 before the first run.</summary>
    public ulong LastNodeId { get; private set; }

    /// <summary>Gathers the nearest node. itemId 0 = first gatherable slot.</summary>
    public bool Start(uint itemId, System.Collections.Generic.IReadOnlyCollection<ulong>? excludedNodes = null)
    {
        if (State is GatheringState.MovingToNode or GatheringState.Interacting or GatheringState.GatheringNode)
            return false;

        if (gameBridge.IsCrafting)
        {
            Transition(GatheringState.Idle, "Cannot start while crafting.");
            return false;
        }

        node = gameBridge.FindNearestGatheringNode(excludedNodes);
        if (node == null)
        {
            Transition(GatheringState.Idle, "No targetable gathering node nearby.");
            return false;
        }

        if (!navigation.IsReady && node.Distance > InteractRange)
        {
            Transition(GatheringState.Idle, "Navigation is not ready and the node is out of reach.");
            return false;
        }

        LastNodeId = node.ObjectId;
        requestedItemId = itemId;
        chosenItemId = 0;
        chosenSlot = -1;
        baselineCount = 0;
        lastIntegrity = -1;
        gatherSwings = 0;
        awaitingSwing = false;

        mountAttempts = 0;
        flyBlocked = false;
        flyAttempted = false;
        EnterPhase(GatheringState.MovingToNode, $"Moving to {node.Name} ({node.Distance:F0}y away).");
        return true;
    }

    public void Pause(string reason)
    {
        if (State is GatheringState.MovingToNode or GatheringState.Interacting or GatheringState.GatheringNode)
        {
            navigation.Stop();
            Transition(GatheringState.Paused, $"Paused: {reason}.");
        }
    }

    public void Resume()
    {
        if (State != GatheringState.Paused)
            return;

        if (gameBridge.GetGatheringState() != null)
            EnterPhase(GatheringState.GatheringNode, "Resuming at the open node.");
        else if (node != null)
            EnterPhase(GatheringState.MovingToNode, "Resuming approach.");
        else
            Transition(GatheringState.Idle, "Nothing to resume.");
    }

    public void Stop()
    {
        navigation.Stop();
        if (State is not (GatheringState.Idle or GatheringState.Completed or GatheringState.Failed))
            Transition(GatheringState.Idle, "Stopped by user.");
    }

    private void OnUpdate(IFramework framework)
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
        }
    }

    private void TickMoving()
    {
        var player = gameBridge.GetPlayerState();
        if (player == null || node == null)
        {
            Fail("player or node vanished during approach");
            return;
        }

        var distance = System.Numerics.Vector3.Distance(player.Position, node.Position);
        if (distance <= InteractRange)
        {
            navigation.Stop();
            EnterPhase(GatheringState.Interacting, $"Arrived at {node.Name}; interacting.");
            return;
        }

        if (DateTime.UtcNow - phaseStartedAt > NavigateTimeout)
        {
            Fail($"could not reach the node within {NavigateTimeout.TotalSeconds:F0}s");
            return;
        }

        if (navigation.IsMoving)
        {
            flyAttempted = false;
            return;
        }

        Throttled(() =>
        {
            // Mount for long legs between nodes (roadmap 1.1).
            if (!gameBridge.IsMounted && mountAttempts < 3 && distance > 80f)
            {
                mountAttempts++;
                gameBridge.TryMount();
                return;
            }

            if (flyAttempted)
                flyBlocked = true;

            var fly = gameBridge.IsMounted && !flyBlocked;
            flyAttempted = fly;
            navigation.MoveCloseTo(node.Position, InteractRange - 0.5f, fly);
        });
    }

    private void TickInteracting()
    {
        // Gathering requires being dismounted.
        if (gameBridge.IsMounted)
        {
            Throttled(gameBridge.TryDismount);
            return;
        }

        if (gameBridge.GetGatheringState() != null)
        {
            EnterPhase(GatheringState.GatheringNode, "Node open; gathering.");
            return;
        }

        if (DateTime.UtcNow - phaseStartedAt > InteractTimeout)
        {
            Fail("the gathering window did not open");
            return;
        }

        Throttled(() =>
        {
            if (node != null && !gameBridge.InteractWithObject(node.ObjectId))
                Fail("the node despawned before it could be opened");
        });
    }

    private void TickGathering()
    {
        var gathering = gameBridge.GetGatheringState();

        if (gathering == null)
        {
            // Node closed: exhausted (normal) or despawned mid-way.
            FinishNode();
            return;
        }

        if (chosenSlot < 0)
        {
            if (!ChooseSlot(gathering))
                return;
        }

        if (awaitingSwing)
        {
            if (gathering.IntegrityRemaining < lastIntegrity)
            {
                awaitingSwing = false;
                lastIntegrity = gathering.IntegrityRemaining;
                gatherSwings++;
                StatusText = $"Gathering: {gatherSwings} swings, integrity {gathering.IntegrityRemaining}/{gathering.IntegrityTotal}.";
            }
            else if (DateTime.UtcNow - lastAttemptAt > SwingTimeout)
            {
                Pause("gather attempt did not resolve in time");
            }

            return;
        }

        if (gameBridge.IsGatheringActionInProgress)
            return;

        Throttled(() =>
        {
            if (gameBridge.GatherSlot(chosenSlot))
            {
                awaitingSwing = true;
            }
        });
    }

    private bool ChooseSlot(GatheringSnapshot gathering)
    {
        GatheringItemSlot? slot = null;
        foreach (var candidate in gathering.Items)
        {
            if (requestedItemId != 0)
            {
                if (candidate.ItemId == requestedItemId && candidate.Enabled)
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
            Fail(requestedItemId != 0
                ? $"item {requestedItemId} is not gatherable at this node"
                : "no gatherable item in this node");
            return false;
        }

        chosenSlot = slot.Index;
        chosenItemId = slot.ItemId;
        baselineCount = gameBridge.GetItemCount(chosenItemId);
        lastIntegrity = gathering.IntegrityRemaining;
        Plugin.Log.Information(
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
        else
            Fail($"node finished but inventory did not increase (swings: {gatherSwings})");
    }

    private void EnterPhase(GatheringState state, string statusText)
    {
        phaseStartedAt = DateTime.UtcNow;
        lastAttemptAt = DateTime.MinValue;
        Transition(state, statusText);
    }

    private void Throttled(Action action)
    {
        if (DateTime.UtcNow - lastAttemptAt < RetryInterval)
            return;

        lastAttemptAt = DateTime.UtcNow;
        action();
    }

    private void Fail(string reason) => Transition(GatheringState.Failed, $"Failed: {reason}.");

    private void Transition(GatheringState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Plugin.Log.Information($"[Gather] {statusText}");
    }
}
