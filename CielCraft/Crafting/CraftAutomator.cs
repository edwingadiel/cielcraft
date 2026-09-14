using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly Configuration configuration;

    private IReadOnlyList<uint> rotation = [];
    private uint classJobId;
    private int nextIndex;
    private bool waitingForReady;
    private DateTime waitingSince;
    private bool adaptive;
    private int baseProgress;
    private byte crafterLevel;
    private int pendingConsume;
    private uint[] remainingCache = [];
    private DateTime? exhaustedAt;

    public AutomationState State { get; private set; } = AutomationState.Idle;
    public string StatusText { get; private set; } = "Idle.";
    public int TotalActions => rotation.Count;
    public int CompletedActions => Math.Min(nextIndex, rotation.Count);
    public uint? NextRaphaelAction => nextIndex < rotation.Count ? rotation[nextIndex] : null;

    public CraftAutomator(
        IGameBridge gameBridge,
        CraftStateMonitor craftMonitor,
        ActionExecutor executor,
        Configuration configuration)
    {
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;
        this.executor = executor;
        this.configuration = configuration;

        executor.ActionResolved += OnActionResolved;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        executor.ActionResolved -= OnActionResolved;
    }

    /// <summary>Specialist actions do not advance the step counter.</summary>
    private static bool AdvancesStep(uint raphaelActionId) => !CraftActionData.IsSpecialist(raphaelActionId);

    private int targetQuality;

    /// <param name="craftBaseProgress">Progress per 100% efficiency, from the solve; 0 disables the adaptive rules that need it.</param>
    /// <param name="qualityTarget">Absolute quality goal; 0 = the recipe maximum.</param>
    public bool Start(IReadOnlyList<uint> actions, uint jobId, int craftBaseProgress = 0, int qualityTarget = 0)
    {
        if (State == AutomationState.Running)
            return false;

        if (actions.Count == 0 || !gameBridge.IsCrafting || executor.State != ExecutorState.Idle)
            return false;

        rotation = actions;
        classJobId = jobId;
        nextIndex = 0;
        waitingForReady = false;
        adaptive = configuration.AdaptiveCrafting;
        baseProgress = craftBaseProgress;
        targetQuality = qualityTarget;
        crafterLevel = (byte)(gameBridge.GetPlayerState()?.Level ?? 0);
        pendingConsume = 1;
        exhaustedAt = null;
        RebuildRemaining();

        Transition(
            AutomationState.Running,
            $"Running: 0/{rotation.Count} actions{(adaptive ? " (adaptive)" : "")}.");
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
        // Bookkeeping must survive a pause: an in-flight action still resolves,
        // and dropping it would desync nextIndex and repeat the action on
        // Resume. Only fully idle/terminal states ignore resolutions.
        if (State is not (AutomationState.Running or AutomationState.Paused))
            return;

        switch (outcome)
        {
            case ActionOutcome.StepAdvanced:
                nextIndex += pendingConsume;
                pendingConsume = 1;
                RebuildRemaining();
                if (State == AutomationState.Paused)
                {
                    StatusText = $"Paused after action resolved ({nextIndex}/{rotation.Count}).";
                    break;
                }

                if (nextIndex >= rotation.Count)
                {
                    // With adaptive crafting the engine keeps synthesizing past
                    // the plan while the quality target is met; otherwise give
                    // the craft a grace period first — the final action's step
                    // advance is often observed a few frames before the
                    // crafting flag clears, and pausing there would flag a
                    // successful craft as incomplete.
                    var craft = craftMonitor.Current;
                    var goal = targetQuality > 0 ? Math.Min(targetQuality, craft?.MaxQuality ?? 0) : craft?.MaxQuality ?? 0;
                    if (!(adaptive && baseProgress > 0 && craft != null && craft.Quality >= goal))
                    {
                        exhaustedAt = DateTime.UtcNow;
                        StatusText = $"Running: {nextIndex}/{rotation.Count} actions (awaiting craft end).";
                    }
                }
                else
                {
                    StatusText = $"Running: {nextIndex}/{rotation.Count} actions.";
                }

                break;

            case ActionOutcome.CraftEnded:
                // The executor reports CraftEnded for the final action because
                // the synthesis window closes as it resolves. If the in-flight
                // action was expected to retire the rest of the plan (the last
                // planned action, or an adaptive finisher), the craft ran to
                // completion; anything earlier is an abnormal end.
                if (exhaustedAt != null || nextIndex + pendingConsume >= rotation.Count)
                {
                    exhaustedAt = null;
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
                if (State == AutomationState.Running)
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
            if (exhaustedAt != null)
            {
                // The craft ended right after the final planned action: success.
                exhaustedAt = null;
                nextIndex = rotation.Count;
                Transition(AutomationState.Completed, $"Completed: all {rotation.Count} actions executed.");
                return;
            }

            Transition(AutomationState.Failed, "Craft ended unexpectedly between actions.");
            return;
        }

        if (exhaustedAt is { } exhausted)
        {
            if (DateTime.UtcNow - exhausted > TimeSpan.FromSeconds(4))
            {
                exhaustedAt = null;
                Pause("rotation exhausted but the craft is still in progress");
            }

            return;
        }

        var decision = NextDecision();
        if (decision == null)
            return;

        var resolved = CraftActionResolver.ResolveForJob(decision.ActionId, classJobId);
        if (resolved == null)
        {
            Pause($"could not resolve action {decision.ActionId} for job {classJobId}");
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

        if (decision.DeviationReason != null)
            Plugin.Log.Information($"[Adaptive] {decision.DeviationReason}.");

        pendingConsume = decision.ConsumeFromPlan;

        if (!executor.TryExecute(resolved.Value, AdvancesStep(decision.ActionId)))
            Pause($"executor refused the action ({executor.LastResult})");
    }

    private void RebuildRemaining() =>
        remainingCache = nextIndex >= rotation.Count ? [] : rotation.Skip(nextIndex).ToArray();

    private AdaptiveDecision? NextDecision()
    {
        if (adaptive && baseProgress > 0 && craftMonitor.Current is { } craft)
            return AdaptiveEngine.Decide(craft, remainingCache, baseProgress, crafterLevel, targetQuality);

        return remainingCache.Length > 0 ? new AdaptiveDecision(remainingCache[0], 1, null) : null;
    }

    private void Transition(AutomationState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Plugin.Log.Information($"[Craft] {statusText}");
    }
}
