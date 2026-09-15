using System;
using System.Linq;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class InfrastructureTests
{
    private enum Phase { Idle, Working, Done }

    private sealed class CountingMachine : AutomationMachine<Phase>
    {
        public int Ticks;
        public bool Throw;

        public CountingMachine(ILog log, IClock clock) : base(log, clock, "[Test]", Phase.Idle, "Idle.")
        {
        }

        public void Start() => Transition(Phase.Working, "Working.");

        protected override void OnTick()
        {
            Ticks++;
            if (Throw)
                throw new InvalidOperationException("boom");
            if (State == Phase.Working && Ticks >= 3)
                Transition(Phase.Done, "Done.");
        }
    }

    [Fact]
    public void ThrottleRunsOncePerIntervalAndResets()
    {
        var clock = new FakeClock();
        var throttle = new Throttle(clock, TimeSpan.FromSeconds(2));
        var runs = 0;

        Assert.True(throttle.Try(() => runs++));
        Assert.False(throttle.Try(() => runs++));
        clock.Advance(1.9);
        Assert.False(throttle.Try(() => runs++));
        clock.Advance(0.2);
        Assert.True(throttle.Try(() => runs++));
        Assert.Equal(2, runs);

        throttle.Reset();
        Assert.True(throttle.Try(() => runs++));
        Assert.Equal(3, runs);
    }

    [Fact]
    public void MachineLogsTransitionsAndSurvivesTickExceptions()
    {
        var log = new ListLog();
        var machine = new CountingMachine(log, new FakeClock());

        machine.Start();
        machine.Tick();
        machine.Tick();
        machine.Tick();
        Assert.Equal(Phase.Done, machine.State);
        Assert.Contains("INF [Test] Working.", log.Lines);
        Assert.Contains("INF [Test] Done.", log.Lines);

        machine.Throw = true;
        machine.Tick();
        Assert.Equal(4, machine.Ticks);
        Assert.Contains(log.Lines, l => l.StartsWith("ERR [CountingMachine] tick: boom"));
        Assert.Equal("State Done — Done.", machine.Describe().First());
    }

    [Fact]
    public void FakeClockAdvancesDeterministically()
    {
        var clock = new FakeClock();
        var start = clock.UtcNow;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(start.AddMinutes(5), clock.UtcNow);
    }
}
