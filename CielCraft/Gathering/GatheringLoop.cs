using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Gathering;

public enum GatheringLoopState
{
    Idle,
    Running,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// "Gather item X ×N" (spec §66): runs the single-node controller node after
/// node until the inventory holds the requested amount. Progress is measured
/// against live inventory, never assumed from swing counts. Nodes that fail
/// (wrong contents, unreachable, despawned) are blacklisted for this run; too
/// many consecutive failures stop the loop.
/// </summary>
public sealed class GatheringLoop : AutomationMachine<GatheringLoopState>
{
    private const int MaxConsecutiveFailures = 5;

    private readonly IGameBridge gameBridge;
    private readonly GatheringController controller;
    private readonly Core.INavigationProvider navigation;
    private readonly AutomationSettings configuration;
    private readonly Game.MaintenanceService maintenance;
    private readonly HashSet<ulong> blacklistedNodes = [];

    private static readonly TimeSpan NoNodeTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NavmeshTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartRetryInterval = TimeSpan.FromSeconds(2);

    private uint itemId;
    private int targetQuantity;
    private int baselineCount;
    private int consecutiveFailures;
    private bool controllerActive;
    private CollectableTier? collectableTier;
    private int collectablesTaken;   // summed over finished node runs (7.1)
    private DateTime noNodeSince = DateTime.MaxValue;
    private DateTime navmeshWaitSince = DateTime.MaxValue;
    private DateTime lastStartAttempt = DateTime.MinValue;
    private System.Numerics.Vector3? areaCenter;
    private DateTime lastCordialAt = DateTime.MinValue;
    private DateTime lastHousekeepingAt = DateTime.MinValue;

    /// <summary>
    /// Progress toward the amount: the inventory gain, and for a collectable
    /// order (7.1) at least the collectables the controller took — the
    /// appraisal window is the observed event there, the bag count the check.
    /// </summary>
    public int Gathered => itemId == 0 ? 0
        : Math.Max(
            Math.Max(0, gameBridge.GetItemCount(itemId) - baselineCount),
            collectableTier != null ? collectablesTaken + (controllerActive ? controller.CollectablesTaken : 0) : 0);

    public int TargetQuantity => targetQuantity;

    public GatheringLoop(
        IGameBridge gameBridge,
        GatheringController controller,
        Core.INavigationProvider navigation,
        AutomationSettings configuration,
        Game.MaintenanceService maintenance,
        ILog log,
        IClock clock)
        : base(log, clock, "[Gather]", GatheringLoopState.Idle, "Idle.")
    {
        this.maintenance = maintenance;
        this.gameBridge = gameBridge;
        this.controller = controller;
        this.navigation = navigation;
        this.configuration = configuration;
    }

    /// <summary>Starts the loop; a tier means the item is gathered as a collectable at that tier's collectability (7.1).</summary>
    public bool Start(
        uint gatherItemId,
        int quantity,
        System.Numerics.Vector3? nodeAreaCenter = null,
        CollectableTier? tier = null)
    {
        if (State is GatheringLoopState.Running or GatheringLoopState.Paused)
            return false;

        if (gatherItemId == 0 || quantity < 1)
        {
            Transition(GatheringLoopState.Idle, "A specific item id and quantity are required.");
            return false;
        }

        itemId = gatherItemId;
        targetQuantity = quantity;
        areaCenter = nodeAreaCenter;
        baselineCount = gameBridge.GetItemCount(itemId);
        consecutiveFailures = 0;
        controllerActive = false;
        collectableTier = tier;
        collectablesTaken = 0;
        noNodeSince = DateTime.MaxValue;
        navmeshWaitSince = DateTime.MaxValue;
        blacklistedNodes.Clear();

        Transition(GatheringLoopState.Running, $"Gathering item {itemId} ×{quantity}{TierText()}.");
        return true;
    }

    private string TierText() => collectableTier is { } tier ? $" as {tier} collectables" : "";

    public void Pause(string reason)
    {
        if (State != GatheringLoopState.Running)
            return;

        controller.Pause("loop paused");
        // The between-node area drift runs while the controller is idle, so
        // its Pause cannot stop that movement — stop it here.
        navigation.Stop();
        Transition(GatheringLoopState.Paused, $"Paused: {reason}.");
    }

    public void Resume()
    {
        if (State != GatheringLoopState.Paused)
            return;

        if (controller.State == GatheringState.Paused)
            controller.Resume();

        Transition(GatheringLoopState.Running, ProgressText());
    }

    public void Stop()
    {
        controller.Stop();
        maintenance.Abort();
        navigation.Stop();
        if (State is GatheringLoopState.Running or GatheringLoopState.Paused)
            Transition(GatheringLoopState.Idle, $"Stopped by user at {Gathered}/{targetQuantity}.");
    }

    protected override void OnTick()
    {
        if (State != GatheringLoopState.Running)
            return;

        // Completion, inventory-space and cordial checks all poll native
        // inventory sweeps; once a second is plenty (node runs start at most
        // every two seconds anyway).
        if (Clock.UtcNow - lastHousekeepingAt > TimeSpan.FromSeconds(1))
        {
            lastHousekeepingAt = Clock.UtcNow;
            var gathered = Gathered;
            if (gathered >= targetQuantity)
            {
                controller.Stop();

                // The target is usually reached mid-node. The close request can
                // be ignored while a swing animates, so keep asking (once a
                // second) and only report completion once the character is free.
                if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
                {
                    gameBridge.CloseGatheringWindow();
                    StatusText = $"Gathered {gathered}/{targetQuantity}; leaving the node...";
                    return;
                }

                Transition(GatheringLoopState.Completed, $"Completed: {gathered}/{targetQuantity} gathered.");
                return;
            }

            if (gameBridge.GetFreeInventorySlots() < 1)
            {
                Pause("inventory is full");
                return;
            }

            // Cordials only between nodes: item use while the node window is
            // open is refused and would just burn the retry back-off.
            if (controller.State is not (GatheringState.MovingToNode or GatheringState.Interacting
                or GatheringState.GatheringNode or GatheringState.CollectableNode))
                TryCordial();
        }

        // Between-node maintenance (repair/food) — never while a node run is live.
        var nodeRunActive = controller.State is GatheringState.MovingToNode
            or GatheringState.Interacting or GatheringState.GatheringNode or GatheringState.CollectableNode;
        if (!nodeRunActive)
        {
            if (maintenance.Tick())
            {
                StatusText = maintenance.StatusText;
                return;
            }

            if (maintenance.BlockedReason != null)
            {
                Pause(maintenance.BlockedReason);
                return;
            }
        }

        switch (controller.State)
        {
            case GatheringState.MovingToNode:
            case GatheringState.Interacting:
            case GatheringState.GatheringNode:
            case GatheringState.CollectableNode:
                return; // a node run is in progress

            case GatheringState.Paused:
                Transition(GatheringLoopState.Paused, $"Paused: {controller.StatusText}");
                return;

            case GatheringState.Completed when controllerActive:
                consecutiveFailures = 0;
                controllerActive = false;
                collectablesTaken += controller.CollectablesTaken;
                StatusText = ProgressText();
                break;

            case GatheringState.Failed when controllerActive:
                controllerActive = false;
                collectablesTaken += controller.CollectablesTaken; // a node can fail after handing over collectables
                consecutiveFailures++;
                if (controller.LastNodeId != 0)
                    blacklistedNodes.Add(controller.LastNodeId);

                Log.Warning(
                    $"[Gather] Node run failed ({controller.StatusText}); " +
                    $"blacklisting node, failure {consecutiveFailures}/{MaxConsecutiveFailures}.");

                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    Transition(GatheringLoopState.Failed, $"Failed: {consecutiveFailures} node runs in a row failed.");
                    return;
                }

                break;
        }

        // Start the next node run. Node groups respawn on a delay after being
        // exhausted, so an empty object table is retried before giving up.
        if (Clock.UtcNow - lastStartAttempt < StartRetryInterval)
            return;

        lastStartAttempt = Clock.UtcNow;

        // Still at the previous node (window up, or the gathering condition
        // not yet cleared after closing it): the character is pinned until it
        // is gone, so nothing can start.
        if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
        {
            gameBridge.CloseGatheringWindow();
            StatusText = $"{ProgressText()} Leaving the previous node...";
            return;
        }

        // vnavmesh rebuilds its mesh after every zone change (the first visit
        // to a zone can take a minute or more). The no-node clock must not run
        // while nodes are merely out of walking reach.
        if (!navigation.IsReady && !NodeInReach())
        {
            if (navmeshWaitSince == DateTime.MaxValue)
            {
                navmeshWaitSince = Clock.UtcNow;
                Log.Information("[Gather] Waiting for the navmesh to build before approaching a node.");
            }

            noNodeSince = DateTime.MaxValue;
            if (Clock.UtcNow - navmeshWaitSince > NavmeshTimeout)
                Transition(
                    GatheringLoopState.Failed,
                    $"Failed: the navmesh did not become ready within {NavmeshTimeout.TotalMinutes:F0} minutes.");
            else
                StatusText = $"{ProgressText()} Waiting for the navmesh to build...";
            return;
        }

        navmeshWaitSince = DateTime.MaxValue;

        if (controller.Start(itemId, blacklistedNodes, targetQuantity - Gathered, areaCenter, collectableTier))
        {
            controllerActive = true;
            noNodeSince = DateTime.MaxValue;
        }
        else
        {
            if (noNodeSince == DateTime.MaxValue)
                noNodeSince = Clock.UtcNow;

            // Drift back toward the node-area center while waiting: fresh
            // spawns may be outside object-table range (roadmap 2.4).
            var player = gameBridge.GetPlayerState();
            if (areaCenter is { } center && player != null && navigation.IsReady
                && System.Numerics.Vector3.Distance(player.Position, center) > 60f
                && !navigation.IsMoving)
            {
                navigation.MoveCloseTo(center, 15f, fly: false);
            }

            if (Clock.UtcNow - noNodeSince > NoNodeTimeout)
                Transition(
                    GatheringLoopState.Failed,
                    $"Failed: no usable gathering node appeared within {NoNodeTimeout.TotalSeconds:F0}s ({controller.StatusText}).");
            else
                StatusText = $"{ProgressText()} Waiting for a node to appear...";
        }
    }

    /// <summary>Drinks a cordial between nodes when a full one fits into the GP pool (roadmap 2.2).</summary>
    private void TryCordial()
    {
        if (!configuration.UseCordials || Clock.UtcNow - lastCordialAt < TimeSpan.FromSeconds(5))
            return;

        var player = gameBridge.GetPlayerState();
        if (player == null || player.MaxGp == 0 || player.CurrentGp > player.MaxGp - 350)
            return;

        foreach (var cordial in Core.GatheringActions.Cordials)
        {
            // HQ consumables are addressed as item id + 1,000,000; the count
            // covers both qualities, so try the HQ form first.
            if (gameBridge.GetItemCount(cordial) > 0
                && (gameBridge.UseItem(cordial + 1_000_000) || gameBridge.UseItem(cordial)))
            {
                lastCordialAt = Clock.UtcNow;
                Log.Information($"[Gather] Drinking cordial (item {cordial}); GP {player.CurrentGp}/{player.MaxGp}.");
                return;
            }
        }

        // None usable (cooldown or none held): back off before checking again.
        lastCordialAt = Clock.UtcNow;
    }

    private string ProgressText() => $"Gathered {Gathered}/{targetQuantity} of item {itemId}{TierText()}.";

    /// <summary>A usable node is close enough to interact with without navigation.</summary>
    private bool NodeInReach() =>
        gameBridge.FindNearestGatheringNode(blacklistedNodes, areaCenter) is { } nearest
        && nearest.Distance <= GatheringController.InteractRange;

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"Item {itemId} ×{targetQuantity}: gathered {Gathered} (baseline {baselineCount}; tier {collectableTier?.ToString() ?? "-"}, collectables taken {collectablesTaken}); consecutive failures {consecutiveFailures}; controllerActive {controllerActive}; blacklisted nodes {blacklistedNodes.Count}";
        yield return $"noNodeSince {(noNodeSince == DateTime.MaxValue ? "-" : noNodeSince.ToString("HH:mm:ss") + "Z")}; navmeshWaitSince {(navmeshWaitSince == DateTime.MaxValue ? "-" : navmeshWaitSince.ToString("HH:mm:ss") + "Z")}; last start attempt {lastStartAttempt:HH:mm:ss}Z; area center {areaCenter?.ToString() ?? "-"}; last cordial {lastCordialAt:HH:mm:ss}Z";
    }
}
