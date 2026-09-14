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

    public GatheringLoopState State { get; private set; } = GatheringLoopState.Idle;
    public string StatusText { get; private set; } = "Idle.";
    public int Gathered => itemId == 0 ? 0 : Math.Max(0, gameBridge.GetItemCount(itemId) - baselineCount);
    public int TargetQuantity => targetQuantity;

    public GatheringLoop(IGameBridge gameBridge, GatheringController controller)
    {
        this.gameBridge = gameBridge;
        this.controller = controller;

        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
    }

    public bool Start(uint gatherItemId, int quantity)
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
        if (State is GatheringLoopState.Running or GatheringLoopState.Paused)
            Transition(GatheringLoopState.Idle, $"Stopped by user at {Gathered}/{targetQuantity}.");
    }

    private void OnUpdate(IFramework framework)
    {
        if (State != GatheringLoopState.Running)
            return;

        if (Gathered >= targetQuantity)
        {
            controller.Stop();
            Transition(GatheringLoopState.Completed, $"Completed: {Gathered}/{targetQuantity} gathered.");
            return;
        }

        if (gameBridge.GetFreeInventorySlots() < 1)
        {
            Pause("inventory is full");
            return;
        }

        switch (controller.State)
        {
            case GatheringState.MovingToNode:
            case GatheringState.Interacting:
            case GatheringState.GatheringNode:
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

        if (controller.Start(itemId, blacklistedNodes))
        {
            controllerActive = true;
            noNodeSince = DateTime.MaxValue;
        }
        else
        {
            if (noNodeSince == DateTime.MaxValue)
                noNodeSince = DateTime.UtcNow;

            if (DateTime.UtcNow - noNodeSince > NoNodeTimeout)
                Transition(
                    GatheringLoopState.Failed,
                    $"Failed: no usable gathering node appeared within {NoNodeTimeout.TotalSeconds:F0}s ({controller.StatusText}).");
            else
                StatusText = $"{ProgressText()} Waiting for a node to appear...";
        }
    }

    private string ProgressText() => $"Gathered {Gathered}/{targetQuantity} of item {itemId}.";

    private void Transition(GatheringLoopState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Plugin.Log.Information($"[Gather] {statusText}");
    }
}
