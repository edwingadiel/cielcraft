using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Plugin.Services;

namespace CielCraft.Crafting;

public enum AutomationState
{
    Idle,
    Running,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// Drives a solved rotation through the craft, one observed transition at a
/// time (spec §57): execute, wait for the game to confirm the step, execute
/// the next. Paces off the game's own action-readiness — no sleeps. Anything
/// unexpected pauses with a reason instead of blindly continuing (spec §49).
/// </summary>
public sealed class CraftAutomator : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);

    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;
    private readonly ActionExecutor executor;

    private IReadOnlyList<uint> rotation = [];
    private uint classJobId;
    private int nextIndex;
    private bool waitingForReady;
    private DateTime waitingSince;

    public AutomationState State { get; private set; } = AutomationState.Idle;
    public string StatusText { get; private set; } = "Idle.";
    public int TotalActions => rotation.Count;
    public int CompletedActions => Math.Min(nextIndex, rotation.Count);
    public uint? NextRaphaelAction => nextIndex < rotation.Count ? rotation[nextIndex] : null;

    public CraftAutomator(IGameBridge gameBridge, CraftStateMonitor craftMonitor, ActionExecutor executor)
    {
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;
        this.executor = executor;

        executor.ActionResolved += OnActionResolved;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        executor.ActionResolved -= OnActionResolved;
    }

    public bool Start(IReadOnlyList<uint> actions, uint jobId)
    {
        if (State == AutomationState.Running)
            return false;

        if (actions.Count == 0 || !gameBridge.IsCrafting || executor.State != ExecutorState.Idle)
            return false;

        rotation = actions;
        classJobId = jobId;
        nextIndex = 0;
        waitingForReady = false;

        Transition(AutomationState.Running, $"Running: 0/{rotation.Count} actions.");
        return true;
    }

    public void Pause(string reason) => Transition(AutomationState.Paused, $"Paused: {reason}.");

    public void Resume()
    {
        if (State != AutomationState.Paused)
            return;

        if (!gameBridge.IsCrafting)
        {
            Transition(AutomationState.Failed, "Cannot resume: no craft is active.");
            return;
        }

        waitingForReady = false;
        Transition(AutomationState.Running, $"Running: {CompletedActions}/{rotation.Count} actions.");
    }

    public void Stop()
    {
        if (State is AutomationState.Running or AutomationState.Paused)
            Transition(AutomationState.Idle, "Stopped by user.");
    }

    private void OnActionResolved(ActionOutcome outcome)
    {
        if (State != AutomationState.Running)
            return;

        switch (outcome)
        {
            case ActionOutcome.StepAdvanced:
                nextIndex++;
                if (nextIndex >= rotation.Count)
                    Pause("rotation exhausted but the craft is still in progress");
                else
                    StatusText = $"Running: {nextIndex}/{rotation.Count} actions.";
                break;

            case ActionOutcome.CraftEnded:
                // The executor reports CraftEnded for the final action because
                // the synthesis window closes as it resolves. If that action
                // was the last (or second-to-last) planned one, the craft ran
                // to completion; anything earlier is an abnormal end.
                if (nextIndex >= rotation.Count - 1)
                {
                    nextIndex = rotation.Count;
                    Transition(AutomationState.Completed, $"Completed: all {rotation.Count} actions executed.");
                }
                else
                {
                    Transition(
                        AutomationState.Failed,
                        $"Craft ended after {nextIndex}/{rotation.Count} actions (failed or cancelled).");
                }

                break;

            case ActionOutcome.TimedOut:
                Pause("action did not resolve within the timeout");
                break;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (State != AutomationState.Running)
            return;

        if (executor.State != ExecutorState.Idle)
            return;

        if (!gameBridge.IsCrafting)
        {
            Transition(AutomationState.Failed, "Craft ended unexpectedly between actions.");
            return;
        }

        var action = NextRaphaelAction;
        if (action == null)
            return;

        var resolved = CraftActionResolver.ResolveForJob(action.Value, classJobId);
        if (resolved == null)
        {
            Pause($"could not resolve action {action.Value} for job {classJobId}");
            return;
        }

        if (!gameBridge.IsCraftActionReady(resolved.Value))
        {
            // Typically the animation lock after the previous action; give the
            // game time, but never wait forever.
            if (!waitingForReady)
            {
                waitingForReady = true;
                waitingSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - waitingSince > ReadyTimeout)
            {
                Pause($"action {resolved.Value} did not become usable within {ReadyTimeout.TotalSeconds:F0}s");
            }

            return;
        }

        waitingForReady = false;

        if (!executor.TryExecute(resolved.Value))
            Pause($"executor refused the action ({executor.LastResult})");
    }

    private void Transition(AutomationState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Plugin.Log.Information($"[Production] {statusText}");
    }
}
