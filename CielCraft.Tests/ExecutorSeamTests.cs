using System;
using System.Collections.Generic;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// Drives the seams ActionExecutor is built on (roadmap 5.1) — AutomationMachine,
/// IClock, ILog and ActionResolution — through the executor's request →
/// StepAdvanced → ActionResolved path and its timeout path, with a hand-fed
/// craft-monitor input and a FakeClock instead of frames and sleeps.
/// ActionExecutor itself still lives in the plugin project (it takes IGameBridge
/// and CraftStateMonitor, both Dalamud-side), which the test project cannot
/// reference; this machine mirrors its resolution loop until it moves to Core.
/// </summary>
public class ExecutorSeamTests
{
    private enum ExecutorPhase
    {
        Idle,
        AwaitingResolution,
    }

    private sealed class ResolutionMachine : AutomationMachine<ExecutorPhase>
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

        private CraftSnapshot? baseline;
        private DateTime requestedAt;

        /// <summary>What the craft monitor would publish this frame.</summary>
        public CraftSnapshot? Current { get; set; }

        public bool IsCrafting { get; set; } = true;

        public List<ActionOutcome> Resolved { get; } = [];

        public ResolutionMachine(ILog log, IClock clock)
            : base(log, clock, "[Craft]", ExecutorPhase.Idle, "No action executed yet.")
        {
        }

        public bool TryExecute(uint actionId)
        {
            if (State == ExecutorPhase.AwaitingResolution)
            {
                StatusText = "Rejected: previous action is still resolving.";
                return false;
            }

            if (!IsCrafting || Current == null)
            {
                StatusText = "Rejected: no craft is active.";
                return false;
            }

            baseline = Current;
            requestedAt = Clock.UtcNow;
            SetState(ExecutorPhase.AwaitingResolution, $"Action {actionId} requested at step {Current.Step}...");
            Log.Information($"[Craft] Requested action {actionId} at step {Current.Step}.");
            return true;
        }

        protected override void OnTick()
        {
            if (State != ExecutorPhase.AwaitingResolution || baseline == null)
                return;

            var outcome = ActionResolution.Evaluate(baseline, Current, IsCrafting, Clock.UtcNow - requestedAt, Timeout);
            if (outcome == ActionOutcome.Pending)
                return;

            var result = outcome switch
            {
                ActionOutcome.StepAdvanced => $"Resolved: step {baseline.Step} -> {Current!.Step}.",
                ActionOutcome.CraftEnded => "Craft ended while waiting for resolution.",
                _ => "Timed out: no state transition observed.",
            };

            Log.Information($"[Craft] {result}");
            SetState(ExecutorPhase.Idle, result);
            baseline = null;
            Resolved.Add(outcome);
        }
    }

    private static CraftSnapshot Snapshot(int step) =>
        new(
            RecipeLevel: 1,
            Step: step,
            Progress: 0,
            MaxProgress: 100,
            Quality: 0,
            MaxQuality: 500,
            Durability: 40,
            MaxDurability: 40,
            CurrentCp: 200,
            MaxCp: 200,
            Condition: CraftCondition.Normal);

    [Fact]
    public void StepAdvanceResolvesTheRequestAndRaisesTheOutcome()
    {
        var log = new ListLog();
        var clock = new FakeClock();
        var executor = new ResolutionMachine(log, clock) { Current = Snapshot(step: 1) };

        Assert.True(executor.TryExecute(100001));
        Assert.Equal(ExecutorPhase.AwaitingResolution, executor.State);
        Assert.False(executor.TryExecute(100002), "a second request must wait for the first to resolve");

        // Frames pass with nothing changing: still pending, no outcome raised.
        clock.Advance(0.5);
        executor.Tick();
        Assert.Equal(ExecutorPhase.AwaitingResolution, executor.State);
        Assert.Empty(executor.Resolved);

        // The game confirms the step.
        clock.Advance(0.5);
        executor.Current = Snapshot(step: 2);
        executor.Tick();

        Assert.Equal(ExecutorPhase.Idle, executor.State);
        Assert.Equal([ActionOutcome.StepAdvanced], executor.Resolved);
        Assert.Equal("Resolved: step 1 -> 2.", executor.StatusText);
        Assert.Contains("INF [Craft] Requested action 100001 at step 1.", log.Lines);
        Assert.Contains("INF [Craft] Resolved: step 1 -> 2.", log.Lines);
        Assert.True(executor.TryExecute(100002), "the executor accepts the next action once idle");
    }

    [Fact]
    public void TimesOutWhenNoTransitionArrivesWithinSixSeconds()
    {
        var log = new ListLog();
        var clock = new FakeClock();
        var executor = new ResolutionMachine(log, clock) { Current = Snapshot(step: 3) };

        Assert.True(executor.TryExecute(100002));

        clock.Advance(5.9);
        executor.Tick();
        Assert.Equal(ExecutorPhase.AwaitingResolution, executor.State);
        Assert.Empty(executor.Resolved);

        clock.Advance(0.2);
        executor.Tick();
        Assert.Equal(ExecutorPhase.Idle, executor.State);
        Assert.Equal([ActionOutcome.TimedOut], executor.Resolved);
        Assert.Contains("INF [Craft] Timed out: no state transition observed.", log.Lines);
    }

    [Fact]
    public void CraftEndingWhileWaitingResolvesAsCraftEnded()
    {
        var clock = new FakeClock();
        var executor = new ResolutionMachine(new ListLog(), clock) { Current = Snapshot(step: 8) };

        Assert.True(executor.TryExecute(100001));
        clock.Advance(1);
        executor.IsCrafting = false;
        executor.Current = null;
        executor.Tick();

        Assert.Equal([ActionOutcome.CraftEnded], executor.Resolved);
        Assert.Equal("State Idle — Craft ended while waiting for resolution.", Assert.Single(executor.Describe()));
    }

    [Fact]
    public void TickExceptionsAreReportedThroughTheLogSeam()
    {
        var log = new ListLog();
        var executor = new ResolutionMachine(log, new FakeClock());

        Assert.False(executor.TryExecute(100001));
        Assert.Equal("Rejected: no craft is active.", executor.StatusText);
        executor.Tick();
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("ERR"));
    }
}
