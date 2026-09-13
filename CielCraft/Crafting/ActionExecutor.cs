using System;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Plugin.Services;

namespace CielCraft.Crafting;

public enum ExecutorState
{
    Idle,
    AwaitingResolution,
}

/// <summary>
/// Executes a single craft action and observes its resolution through
/// game-state transitions — never sleeps (spec §11/§12). One action at a
/// time; the next request is refused until the previous one resolved.
/// </summary>
public sealed class ActionExecutor : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;

    private CraftSnapshot? baseline;
    private DateTime requestedAt;

    public ExecutorState State { get; private set; } = ExecutorState.Idle;

    /// <summary>Human-readable result of the last request, for the debug UI.</summary>
    public string LastResult { get; private set; } = "No action executed yet.";

    public ActionExecutor(IGameBridge gameBridge, CraftStateMonitor craftMonitor)
    {
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;

        // Subscribed after CraftStateMonitor so each tick sees the fresh snapshot.
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
    }

    /// <summary>Pre-execution guard list per spec §12. Returns false with a logged reason.</summary>
    public bool TryExecute(uint actionId)
    {
        if (State == ExecutorState.AwaitingResolution)
            return Reject("previous action is still resolving");

        if (!gameBridge.IsCrafting)
            return Reject("no craft is active");

        var current = craftMonitor.Current;
        if (current == null)
            return Reject("craft state is not readable");

        if (!gameBridge.IsCraftActionReady(actionId))
            return Reject($"action {actionId} is not currently usable");

        if (!gameBridge.ExecuteCraftAction(actionId))
            return Reject($"the game rejected action {actionId}");

        baseline = current;
        requestedAt = DateTime.UtcNow;
        State = ExecutorState.AwaitingResolution;
        LastResult = $"Action {actionId} requested at step {current.Step}...";
        Plugin.Log.Information($"[Craft] Requested action {actionId} at step {current.Step}.");
        return true;
    }

    private bool Reject(string reason)
    {
        LastResult = $"Rejected: {reason}.";
        Plugin.Log.Warning($"[Craft] Action rejected: {reason}.");
        return false;
    }

    private void OnUpdate(IFramework framework)
    {
        if (State != ExecutorState.AwaitingResolution || baseline == null)
            return;

        var outcome = ActionResolution.Evaluate(
            baseline,
            craftMonitor.Current,
            gameBridge.IsCrafting,
            DateTime.UtcNow - requestedAt,
            Timeout);

        if (outcome == ActionOutcome.Pending)
            return;

        var current = craftMonitor.Current;
        LastResult = outcome switch
        {
            ActionOutcome.StepAdvanced =>
                $"Resolved: step {baseline.Step} -> {current!.Step}, " +
                $"progress {current.Progress}/{current.MaxProgress}, " +
                $"durability {current.Durability}/{current.MaxDurability}.",
            ActionOutcome.CraftEnded => "Craft ended while waiting for resolution.",
            _ => "Timed out: no state transition observed.",
        };

        Plugin.Log.Information($"[Craft] {LastResult}");
        State = ExecutorState.Idle;
        baseline = null;
    }
}
