using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Game;

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
public sealed class CraftAutomator : AutomationMachine<AutomationState>, IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);

    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;
    private readonly ActionExecutor executor;
    private readonly AutomationSettings configuration;
    private readonly IActionResolver actionResolver;

    private IReadOnlyList<uint> rotation = [];
    private uint classJobId;
    private int nextIndex;
    private bool waitingForReady;
    private DateTime waitingSince;
    private bool adaptive;
    private int baseProgress;
    private byte crafterLevel;
    private int pendingConsume;
    private int expectedStep; // craft step the rotation position corresponds to; 0 = unknown
    private uint[] remainingCache = [];
    private DateTime? exhaustedAt;

    public int TotalActions => rotation.Count;
    public int CompletedActions => Math.Min(nextIndex, rotation.Count);
    public uint? NextRaphaelAction => nextIndex < rotation.Count ? rotation[nextIndex] : null;

    public CraftAutomator(
        IGameBridge gameBridge,
        CraftStateMonitor craftMonitor,
        ActionExecutor executor,
        AutomationSettings configuration,
        IActionResolver actionResolver,
        ILog log,
        IClock clock)
        : base(log, clock, "[Craft]", AutomationState.Idle, "Idle.")
    {
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;
        this.executor = executor;
        this.configuration = configuration;
        this.actionResolver = actionResolver;

        executor.ActionResolved += OnActionResolved;
    }

    public void Dispose()
    {
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
        expectedStep = craftMonitor.Current?.Step ?? 0;
        RebuildRemaining();

        Transition(
            AutomationState.Running,
            $"Running: 0/{rotation.Count} actions{(adaptive ? " (adaptive)" : "")}.");
        return true;
    }

    public void Pause(string reason) => Transition(AutomationState.Paused, $"Paused: {reason}.");

    /// <summary>
    /// Resumes a paused run. Returns false when there is nothing left to run
    /// (rotation exhausted with the craft unfinished) — the owner must then
    /// recover another way (e.g. re-solve from the live state).
    /// </summary>
    public bool Resume()
    {
        if (State != AutomationState.Paused)
            return false;

        if (!gameBridge.IsCrafting)
        {
            Transition(AutomationState.Failed, "Cannot resume: no craft is active.");
            return false;
        }

        // A step taken by hand while paused (test B2) shifts every buff window;
        // continuing the old plan walked straight into a failed synthesis.
        // Refuse, so the owner re-solves from the live state instead.
        if (craftMonitor.Current is { } live && expectedStep > 0 && live.Step != expectedStep)
        {
            Transition(
                AutomationState.Failed,
                $"Cannot resume: the craft moved from step {expectedStep} to {live.Step} while paused; the plan no longer applies.");
            return false;
        }

        if (nextIndex >= rotation.Count && exhaustedAt == null && !AdaptiveCanContinue())
        {
            StatusText = "Paused: rotation exhausted; nothing to resume.";
            return false;
        }

        // A grace window frozen by the pause must restart, not expire instantly.
        if (exhaustedAt != null)
            exhaustedAt = Clock.UtcNow;

        waitingForReady = false;
        Transition(AutomationState.Running, $"Running: {CompletedActions}/{rotation.Count} actions.");
        return true;
    }

    /// <summary>Adaptive mode keeps synthesizing past the plan once the quality goal is met.</summary>
    private bool AdaptiveCanContinue()
    {
        var craft = craftMonitor.Current;
        if (!adaptive || baseProgress <= 0 || craft == null)
            return false;

        var goal = targetQuality > 0 ? Math.Min(targetQuality, craft.MaxQuality) : craft.MaxQuality;
        return craft.Quality >= goal;
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
                expectedStep = craftMonitor.Current?.Step ?? expectedStep + 1;
                RebuildRemaining();

                if (nextIndex < rotation.Count)
                {
                    StatusText = State == AutomationState.Paused
                        ? $"Paused after action resolved ({nextIndex}/{rotation.Count})."
                        : $"Running: {nextIndex}/{rotation.Count} actions.";
                    break;
                }

                // Rotation exhausted. Progress is the real signal: a complete
                // progress bar means the craft is finishing (the step advance
                // is observed a few frames before the crafting flag clears),
                // so wait for the end; anything less is a genuine shortfall.
                if (AdaptiveCanContinue())
                    break;

                var current = craftMonitor.Current;
                if (current == null || current.Progress >= current.MaxProgress)
                {
                    exhaustedAt = Clock.UtcNow;
                    StatusText = $"{(State == AutomationState.Paused ? "Paused" : "Running")}: rotation done, awaiting craft end.";
                }
                else if (State == AutomationState.Running)
                {
                    Pause($"rotation exhausted with progress {current.Progress}/{current.MaxProgress}");
                }
                else
                {
                    StatusText = "Paused: rotation exhausted; nothing to resume.";
                }

                break;

            case ActionOutcome.CraftEnded:
                // The executor reports CraftEnded for the final action because
                // the synthesis window closes as it resolves. If the in-flight
                // action was expected to retire the rest of the plan (the last
                // planned action, or an adaptive finisher), the craft ran to
                // completion; anything earlier is an abnormal end. (The grace
                // window, when armed, already implies nextIndex == Count.)
                if (nextIndex + pendingConsume >= rotation.Count)
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

    protected override void OnTick()
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
            // Progress was complete when the plan ran out; the craft should end
            // on its own within moments. The ceiling only guards a stalled client.
            if (Clock.UtcNow - exhausted > TimeSpan.FromSeconds(10))
            {
                exhaustedAt = null;
                Pause("the craft did not end after the rotation completed");
            }

            return;
        }

        // A step advance with nothing pending means a request that timed out
        // landed late (the game executed it after the executor gave up): count
        // it as the plan action it was, or the stale plan gets judged against a
        // state one action ahead of it.
        if (expectedStep > 0 && craftMonitor.Current is { } advanced && advanced.Step > expectedStep
            && nextIndex < rotation.Count)
        {
            Log.Information(
                $"[Craft] Step {expectedStep} -> {advanced.Step} without a pending request; " +
                $"assuming {RaphaelActionNames.NameOf(rotation[nextIndex])} landed late.");
            nextIndex++;
            expectedStep = advanced.Step;
            RebuildRemaining();
        }

        var decision = NextDecision();
        if (decision == null)
            return;

        // The engine judged the remaining plan unable to finish the craft;
        // pausing hands the batch its one mid-craft re-solve from the live state.
        if (decision.RequestsResolve)
        {
            Pause(decision.DeviationReason ?? "the remaining plan can no longer finish the craft");
            return;
        }

        var resolved = actionResolver.ResolveForJob(decision.ActionId, classJobId);
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
                waitingSince = Clock.UtcNow;
            }
            else if (Clock.UtcNow - waitingSince > ReadyTimeout)
            {
                Pause($"action {resolved.Value} did not become usable within {ReadyTimeout.TotalSeconds:F0}s");
            }

            return;
        }

        waitingForReady = false;

        if (decision.DeviationReason != null)
            Log.Information($"[Adaptive] {decision.DeviationReason}.");

        pendingConsume = decision.ConsumeFromPlan;

        Log.Information(
            $"[Craft] Executing {RaphaelActionNames.NameOf(decision.ActionId)} " +
            $"(plan index {nextIndex}/{rotation.Count}, consumes {decision.ConsumeFromPlan}) as action {resolved.Value}.");

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

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"Plan index {nextIndex}/{rotation.Count}; job {classJobId}; adaptive {adaptive}; base progress {baseProgress}; level {crafterLevel}; target quality {targetQuality}; pendingConsume {pendingConsume}";
        yield return $"waitingForReady {waitingForReady} (since {waitingSince:HH:mm:ss}Z); exhaustedAt {(exhaustedAt is { } at ? at.ToString("HH:mm:ss") + "Z" : "-")}";
        if (rotation.Count > 0)
            yield return "Rotation: " + string.Join(", ", rotation.Select((action, i) =>
                (i == nextIndex ? ">" : "") + RaphaelActionNames.NameOf(action)));
    }
}
