using System;
using System.Collections.Generic;
using CielCraft.Game;
using Dalamud.Plugin.Services;

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
public sealed class GatheringLoop : IDisposable
{
    private const int MaxConsecutiveFailures = 5;

    private readonly IGameBridge gameBridge;
    private readonly GatheringController controller;
    private readonly Core.INavigationProvider navigation;
    private readonly Configuration configuration;
    private readonly Game.MaintenanceService maintenance;
    private readonly HashSet<ulong> blacklistedNodes = [];

    private static readonly TimeSpan NoNodeTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StartRetryInterval = TimeSpan.FromSeconds(2);

    private uint itemId;
    private int targetQuantity;
    private int baselineCount;
    private int consecutiveFailures;
    private bool controllerActive;
    private DateTime noNodeSince = DateTime.MaxValue;
    private DateTime lastStartAttempt = DateTime.MinValue;
    private System.Numerics.Vector3? areaCenter;
    private DateTime lastCordialAt = DateTime.MinValue;
    private DateTime lastHousekeepingAt = DateTime.MinValue;

    public GatheringLoopState State { get; private set; } = GatheringLoopState.Idle;
    public string StatusText { get; private set; } = "Idle.";
    public int Gathered => itemId == 0 ? 0 : Math.Max(0, gameBridge.GetItemCount(itemId) - baselineCount);
    public int TargetQuantity => targetQuantity;

    public GatheringLoop(
        IGameBridge gameBridge,
        GatheringController controller,
        Core.INavigationProvider navigation,
        Configuration configuration,
        Game.MaintenanceService maintenance)
    {
        this.maintenance = maintenance;
        this.gameBridge = gameBridge;
        this.controller = controller;
        this.navigation = navigation;
        this.configuration = configuration;

        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
    }

    public bool Start(uint gatherItemId, int quantity, System.Numerics.Vector3? nodeAreaCenter = null)
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
        blacklistedNodes.Clear();

        Transition(GatheringLoopState.Running, $"Gathering item {itemId} ×{quantity}.");
        return true;
    }

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

    private void OnUpdate(IFramework framework)
    {
        try
        {
            Tick(framework);
        }
        catch (Exception e)
        {
            Plugin.Log.TickError(nameof(GatheringLoop), e);
        }
    }

    private void Tick(IFramework framework)
    {
        if (State != GatheringLoopState.Running)
            return;

        // Completion, inventory-space and cordial checks all poll native
        // inventory sweeps; once a second is plenty (node runs start at most
        // every two seconds anyway).
        if (DateTime.UtcNow - lastHousekeepingAt > TimeSpan.FromSeconds(1))
        {
            lastHousekeepingAt = DateTime.UtcNow;
            var gathered = Gathered;
            if (gathered >= targetQuantity)
            {
                controller.Stop();
                Transition(GatheringLoopState.Completed, $"Completed: {gathered}/{targetQuantity} gathered.");
                return;
            }

            if (gameBridge.GetFreeInventorySlots() < 1)
            {
                Pause("inventory is full");
                return;
            }

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
                StatusText = ProgressText();
                break;

            case GatheringState.Failed when controllerActive:
                controllerActive = false;
                consecutiveFailures++;
                if (controller.LastNodeId != 0)
                    blacklistedNodes.Add(controller.LastNodeId);

                Plugin.Log.Warning(
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
        if (DateTime.UtcNow - lastStartAttempt < StartRetryInterval)
            return;

        lastStartAttempt = DateTime.UtcNow;

        if (controller.Start(itemId, blacklistedNodes, targetQuantity - Gathered))
        {
            controllerActive = true;
            noNodeSince = DateTime.MaxValue;
        }
        else
        {
            if (noNodeSince == DateTime.MaxValue)
                noNodeSince = DateTime.UtcNow;

            // Drift back toward the node-area center while waiting: fresh
            // spawns may be outside object-table range (roadmap 2.4).
            var player = gameBridge.GetPlayerState();
            if (areaCenter is { } center && player != null && navigation.IsReady
                && System.Numerics.Vector3.Distance(player.Position, center) > 60f
                && !navigation.IsMoving)
            {
                navigation.MoveCloseTo(center, 15f, fly: false);
            }

            if (DateTime.UtcNow - noNodeSince > NoNodeTimeout)
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
        if (!configuration.UseCordials || DateTime.UtcNow - lastCordialAt < TimeSpan.FromSeconds(5))
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
                lastCordialAt = DateTime.UtcNow;
                Plugin.Log.Information($"[Gather] Drinking cordial (item {cordial}); GP {player.CurrentGp}/{player.MaxGp}.");
                return;
            }
        }

        // None usable (cooldown or none held): back off before checking again.
        lastCordialAt = DateTime.UtcNow;
    }

    private string ProgressText() => $"Gathered {Gathered}/{targetQuantity} of item {itemId}.";

    private void Transition(GatheringLoopState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Plugin.Log.Information($"[Gather] {statusText}");
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"Item {itemId} ×{targetQuantity}: gathered {Gathered} (baseline {baselineCount}); consecutive failures {consecutiveFailures}; controllerActive {controllerActive}; blacklisted nodes {blacklistedNodes.Count}";
        yield return $"noNodeSince {(noNodeSince == DateTime.MaxValue ? "-" : noNodeSince.ToString("HH:mm:ss") + "Z")}; last start attempt {lastStartAttempt:HH:mm:ss}Z; area center {areaCenter?.ToString() ?? "-"}; last cordial {lastCordialAt:HH:mm:ss}Z";
    }
}
