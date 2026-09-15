using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;

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
/// Ticked after <see cref="CraftStateMonitor"/> so each frame sees the fresh snapshot.
/// </summary>
public sealed class ActionExecutor : AutomationMachine<ExecutorState>
{
    // Actions normally resolve in ~3 s, but the first action of a craft has
    // landed ~12 s after the request (Reflect, 2026-09-15); a short timeout
    // there paused the run and then double-counted the late step.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;

    private CraftSnapshot? baseline;
    private DateTime requestedAt;
    private bool expectStepAdvance = true;

    /// <summary>Human-readable result of the last request, for the debug UI.</summary>
    public string LastResult => StatusText;

    /// <summary>Raised on the framework thread when a requested action resolves.</summary>
    public event Action<ActionOutcome>? ActionResolved;

    public ActionExecutor(IGameBridge gameBridge, CraftStateMonitor craftMonitor, ILog log, IClock clock)
        : base(log, clock, "[Craft]", ExecutorState.Idle, "No action executed yet.")
    {
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;
    }

    /// <summary>Pre-execution guard list per spec §12. Returns false with a logged reason.</summary>
    public bool TryExecute(uint actionId, bool advancesStep = true)
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
        requestedAt = Clock.UtcNow;
        expectStepAdvance = advancesStep;
        SetState(ExecutorState.AwaitingResolution, $"Action {actionId} requested at step {current.Step}...");
        Log.Information($"[Craft] Requested action {actionId} at step {current.Step}.");
        return true;
    }

    private bool Reject(string reason)
    {
        StatusText = $"Rejected: {reason}.";
        Log.Warning($"[Craft] Action rejected: {reason}.");
        return false;
    }

    protected override void OnTick()
    {
        if (State != ExecutorState.AwaitingResolution || baseline == null)
            return;

        var outcome = ActionResolution.Evaluate(
            baseline,
            craftMonitor.Current,
            gameBridge.IsCrafting,
            Clock.UtcNow - requestedAt,
            Timeout,
            expectStepAdvance);

        if (outcome == ActionOutcome.Pending)
            return;

        var current = craftMonitor.Current;
        var result = outcome switch
        {
            ActionOutcome.StepAdvanced =>
                $"Resolved: step {baseline.Step} -> {current!.Step}, " +
                $"progress {current.Progress}/{current.MaxProgress}, " +
                $"durability {current.Durability}/{current.MaxDurability}.",
            ActionOutcome.CraftEnded => "Craft ended while waiting for resolution.",
            _ => "Timed out: no state transition observed.",
        };

        Log.Information($"[Craft] {result}");
        SetState(ExecutorState.Idle, result);
        baseline = null;

        ActionResolved?.Invoke(outcome);
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        yield return $"State {State}; last result: {LastResult}";
        if (baseline != null)
            yield return $"Awaiting resolution since {requestedAt:HH:mm:ss.fff}Z from step {baseline.Step} (expects step advance: {expectStepAdvance}; timeout {Timeout.TotalSeconds:F0}s)";
    }
}
