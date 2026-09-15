using System.Numerics;
using CielCraft.Core;
using CielCraft.Core.Scheduling;
using Xunit;

namespace CielCraft.Tests;

public class NodeSchedulerTests
{
    private const double SecondsPerEtMinute = EorzeaClock.RealSecondsPerEorzeaMinute;
    private static readonly TimeSpan Lead = TimeSpan.FromMinutes(2);

    private readonly FakeClock clock = new();

    private int EtNow => EorzeaClock.MinuteOfDay(new DateTimeOffset(clock.UtcNow));

    /// <summary>A window that opens in the given number of ET minutes (negative = opened that long ago).</summary>
    private EtWindow Window(int opensInEtMinutes, int durationEtMinutes = 120) =>
        new(((EtNow + opensInEtMinutes) % 1440 + 1440) % 1440, durationEtMinutes);

    private static ScheduleTask Timed(int id, int amount, EtWindow window, NodeKind kind = NodeKind.Unspoiled, bool collectable = false) =>
        new(id, (uint)(100 + id), $"Timed{id}", amount, [window], kind, 400, Vector2.Zero, collectable);

    private static ScheduleTask Untimed(int id, int amount) =>
        new(id, (uint)(200 + id), $"Untimed{id}", amount, [], NodeKind.Normal, 140, Vector2.Zero, false);

    private static GatherGpState FullGp(int cordials = 0) => new(800, 800, 6, cordials);

    private static SchedulerOptions Options(bool waitAtHome = false, bool hasHome = false, int waitAtHomeMinutes = 8, bool atHome = false) =>
        new(Lead, TimeSpan.FromHours(3), waitAtHome, waitAtHomeMinutes, hasHome, atHome);

    private Schedule Build(IReadOnlyList<ScheduleTask> tasks, IReadOnlyList<ScheduleCraftStep>? steps = null, GatherGpState? gp = null, SchedulerOptions? options = null) =>
        NodeScheduler.Build(tasks, steps ?? [], clock.UtcNow, gp ?? FullGp(), options ?? Options());

    private static T Required<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    private static void AssertClose(DateTime expected, DateTime actual, double toleranceSeconds = 3.5) =>
        Assert.True(Math.Abs((expected - actual).TotalSeconds) <= toleranceSeconds, $"expected {expected:HH:mm:ss.f}, got {actual:HH:mm:ss.f}");

    [Fact]
    public void NextWindowIsPlacedInRealTimeWithTheTravelLead()
    {
        var schedule = Build([Timed(0, 10, Window(120))]);

        var plan = Assert.Single(schedule.Timed);
        var visit = Required(plan.Next);
        var expectedStart = clock.UtcNow + TimeSpan.FromSeconds(120 * SecondsPerEtMinute);
        AssertClose(expectedStart, visit.Start);
        AssertClose(expectedStart + TimeSpan.FromSeconds(120 * SecondsPerEtMinute), visit.End);
        Assert.Equal(visit.Start, visit.Arrive);
        Assert.Equal(visit.Start - Lead, visit.Depart);
        Assert.Equal("08:00–10:00 ET".Length, visit.EtLabel.Length);
    }

    [Fact]
    public void AnOpenWindowStartsInThePastAndArrivalIncludesTheLead()
    {
        // Opened 30 ET minutes ago (87.5 s); 120 ET minutes long (350 s).
        var schedule = Build([Timed(0, 10, Window(-30))]);

        var visit = Required(schedule.Timed[0].Next);
        AssertClose(clock.UtcNow - TimeSpan.FromSeconds(30 * SecondsPerEtMinute), visit.Start);
        Assert.Equal(clock.UtcNow + Lead, visit.Arrive);
        Assert.True(visit.IsOpenAt(clock.UtcNow));
        // 350 − 87.5 − 120 = 142.5 s usable → two nodes of 60 s.
        Assert.Equal(2, visit.NodesByTime);
        var first = Required(schedule.First);
        Assert.Equal(ScheduleEntryKind.TimedVisit, first.Kind);
    }

    [Fact]
    public void NodesPerVisitFollowTimeAndGpAndYieldIsConservative()
    {
        var schedule = Build([Timed(0, 100, Window(120))]);

        var visit = Required(schedule.Timed[0].Next);
        Assert.Equal(5, visit.NodesByTime);                           // 350 s / 60 s
        Assert.Equal(800, visit.GpWanted);                             // unspoiled, per the cost stub
        Assert.Equal(800, visit.GpAtArrival);
        Assert.Equal(1, visit.NodesByGp);                              // (800 + 116 ticks × 6) / 800
        Assert.Equal(5, visit.NodesPlanned);
        Assert.Equal(12 + 4 * 4, visit.ExpectedYield);                 // one buffed node (4 × 3), four plain ones
        Assert.Equal(100, visit.RemainingBefore);
        Assert.Equal(72, visit.RemainingAfter);
    }

    [Fact]
    public void WindowsNeededExtrapolatesBeyondTheHorizon()
    {
        var schedule = Build([Timed(0, 100, Window(120))]);

        var plan = schedule.Timed[0];
        Assert.Equal(3, plan.Visits.Count);                            // +5.8 min, +75.8 min, +145.8 min within 3 h
        Assert.False(plan.CoveredWithinHorizon);
        Assert.Equal(4, plan.WindowsNeeded);                           // 28 per visit: 3 listed + 1 more
        AssertClose(plan.Visits[0].Start + TimeSpan.FromMinutes(70), plan.Visits[1].Start);
        Assert.Equal(2, plan.Visits[1].Ordinal);
        Assert.Equal(5, plan.NodesPerVisit);
        Assert.Equal(28, plan.YieldPerVisit);
    }

    [Fact]
    public void PlanningStopsOnceTheAmountIsCovered()
    {
        var schedule = Build([Timed(0, 20, Window(120))]);

        var plan = schedule.Timed[0];
        var visit = Assert.Single(plan.Visits);
        Assert.True(plan.CoveredWithinHorizon);
        Assert.Equal(1, plan.WindowsNeeded);
        Assert.Equal(3, visit.NodesPlanned);                           // 12 + 4 + 4 ≥ 20
        Assert.Equal(20, visit.ExpectedYield);
    }

    [Fact]
    public void CordialsRaiseTheGpBudget()
    {
        // Empty pool; a window in 30 ET minutes (87.5 s) that lasts 60 (175 s).
        // Arrival is now + the 2 min lead (40 ticks → 240 GP), leaving 142.5 s
        // of window (47 ticks → 282 GP more): 522 GP, short of an 800 GP run.
        var empty = new GatherGpState(0, 800, 6, CordialsOwned: 0);
        var withCordials = new GatherGpState(0, 800, 6, CordialsOwned: 2);

        var without = Required(Build([Timed(0, 100, Window(30, 60))], gp: empty).Timed[0].Next);
        var with = Required(Build([Timed(0, 100, Window(30, 60))], gp: withCordials).Timed[0].Next);

        Assert.Equal(240, without.GpAtArrival);
        Assert.Equal(2, without.NodesByTime);
        Assert.Equal(0, without.NodesByGp);
        Assert.Equal(0, without.CordialsPlanned);
        Assert.Equal(2 * 4, without.ExpectedYield);
        Assert.Equal(1, with.NodesByGp);                               // 522 + one cordial (the cooldown allows one) ≥ 800
        Assert.Equal(1, with.CordialsPlanned);
        Assert.Equal(12 + 4, with.ExpectedYield);
    }

    [Fact]
    public void CollectableNodesHandOverIntegrityMinusOne()
    {
        var schedule = Build([Timed(0, 6, Window(120), NodeKind.Ephemeral, collectable: true)]);

        var visit = Required(schedule.Timed[0].Next);
        Assert.Equal(600, visit.GpWanted);
        Assert.Equal(2, visit.NodesPlanned);
        Assert.Equal(6, visit.ExpectedYield);
    }

    [Fact]
    public void UntimedWorkFillsTheGapWhenItFits()
    {
        // Window in ~20 real minutes (411 ET minutes); 8 items ≈ 2 nodes + lead = 4 min.
        var schedule = Build([Timed(0, 10, Window(411)), Untimed(1, 8)]);

        Assert.Collection(
            schedule.Entries,
            e => { Assert.Equal(ScheduleEntryKind.UntimedGather, e.Kind); Assert.Equal(1, e.Task!.Id); },
            e => Assert.Equal(ScheduleEntryKind.Idle, e.Kind),
            e => { Assert.Equal(ScheduleEntryKind.TimedVisit, e.Kind); Assert.Equal(0, e.Task!.Id); });
        Assert.Equal(schedule.Entries[0].Until, schedule.Entries[1].From);
        Assert.Equal(schedule.Entries[2].Visit!.Depart, schedule.Entries[1].Until);
    }

    [Fact]
    public void UntimedWorkThatDoesNotFitRunsAfterTheWindow()
    {
        // 200 items ≈ 50 nodes = 52 min, far more than the 20 min gap.
        var schedule = Build([Timed(0, 10, Window(411)), Untimed(1, 200)]);

        Assert.Collection(
            schedule.Entries,
            e => Assert.Equal(ScheduleEntryKind.Idle, e.Kind),
            e => Assert.Equal(ScheduleEntryKind.TimedVisit, e.Kind),
            e => Assert.Equal(ScheduleEntryKind.UntimedGather, e.Kind));
    }

    [Fact]
    public void ReadyCraftStepsAreSlottedIntoTheGapInPlanOrder()
    {
        var steps = new List<ScheduleCraftStep>
        {
            new(0, "Ingot", 5, Ready: true),
            new(1, "Sword", 1, Ready: false),
        };

        var schedule = Build([Timed(0, 10, Window(411))], steps);

        Assert.Collection(
            schedule.Entries,
            e => { Assert.Equal(ScheduleEntryKind.CraftStep, e.Kind); Assert.Equal(0, e.Step!.StepIndex); },
            e => Assert.Equal(ScheduleEntryKind.Idle, e.Kind),
            e => Assert.Equal(ScheduleEntryKind.TimedVisit, e.Kind),
            e => { Assert.Equal(ScheduleEntryKind.CraftStep, e.Kind); Assert.Equal(1, e.Step!.StepIndex); });
    }

    [Fact]
    public void ALongGapGoesHomeBeforeCraftingAndWaiting()
    {
        var steps = new List<ScheduleCraftStep> { new(0, "Ingot", 2, Ready: true) };
        var schedule = Build([Timed(0, 10, Window(411))], steps, options: Options(waitAtHome: true, hasHome: true));

        Assert.Collection(
            schedule.Entries,
            e => Assert.Equal(ScheduleEntryKind.GoHome, e.Kind),
            e => Assert.Equal(ScheduleEntryKind.CraftStep, e.Kind),
            e => { Assert.Equal(ScheduleEntryKind.Idle, e.Kind); Assert.Contains("at home", e.Text); },
            e => Assert.Equal(ScheduleEntryKind.TimedVisit, e.Kind));
    }

    [Fact]
    public void NoHomeTripWhenDisabledUnsetOrAlreadyHome()
    {
        var task = Timed(0, 10, Window(411));

        Assert.DoesNotContain(Build([task], options: Options(waitAtHome: false, hasHome: true)).Entries, e => e.Kind == ScheduleEntryKind.GoHome);
        Assert.DoesNotContain(Build([task], options: Options(waitAtHome: true, hasHome: false)).Entries, e => e.Kind == ScheduleEntryKind.GoHome);

        var atHome = Build([task], options: Options(waitAtHome: true, hasHome: true, atHome: true));
        Assert.DoesNotContain(atHome.Entries, e => e.Kind == ScheduleEntryKind.GoHome);
        Assert.Contains("at home", atHome.Entries[0].Text);
    }

    [Fact]
    public void AShortGapIdlesInPlace()
    {
        // Window in ~5 real minutes (103 ET minutes): shorter than the 8-minute home threshold.
        var schedule = Build([Timed(0, 10, Window(103))], options: Options(waitAtHome: true, hasHome: true));

        Assert.Collection(
            schedule.Entries,
            e => { Assert.Equal(ScheduleEntryKind.Idle, e.Kind); Assert.Contains("here", e.Text); },
            e => Assert.Equal(ScheduleEntryKind.TimedVisit, e.Kind));
    }

    [Fact]
    public void AWindowOpeningWithinTheLeadGoesFirst()
    {
        // Opens in 20 ET minutes (58 s): departure is already due.
        var schedule = Build([Untimed(1, 8), Timed(0, 10, Window(20))]);

        Assert.Equal(ScheduleEntryKind.TimedVisit, schedule.Entries[0].Kind);
        Assert.Equal(ScheduleEntryKind.UntimedGather, schedule.Entries[1].Kind);
        Assert.True(schedule.Entries[0].Visit!.Depart <= clock.UtcNow);
    }

    [Fact]
    public void TwoTimedTasksAreVisitedInTimeOrder()
    {
        var schedule = Build([Timed(0, 10, Window(300)), Timed(1, 10, Window(120))]);

        var visits = schedule.Entries.Where(e => e.Kind == ScheduleEntryKind.TimedVisit).ToList();
        Assert.Equal(2, visits.Count);
        Assert.Equal(1, visits[0].Task!.Id);
        Assert.Equal(0, visits[1].Task!.Id);
        Assert.Equal(2, schedule.Timed.Count);
        Assert.True(schedule.Visits[0].Start < schedule.Visits[1].Start);
    }

    [Fact]
    public void AWindowWithNoUsableTimeLeftRollsOverToTheNextDay()
    {
        // Opened 110 ET minutes ago, 120 long: 10 ET minutes (29 s) left, less than a node.
        var schedule = Build([Timed(0, 10, Window(-110))]);

        var visit = Required(schedule.Timed[0].Next);
        var expected = clock.UtcNow - TimeSpan.FromSeconds(110 * SecondsPerEtMinute) + TimeSpan.FromMinutes(70);
        AssertClose(expected, visit.Start);
        Assert.False(visit.IsOpenAt(clock.UtcNow));
    }

    [Fact]
    public void ThePlanOrderAdvancesWithTheClock()
    {
        var task = Timed(0, 10, Window(120));
        var before = Build([task]);
        clock.Advance(TimeSpan.FromSeconds(120 * SecondsPerEtMinute + 30));
        var after = Build([task]);

        Assert.Equal(ScheduleEntryKind.Idle, before.Entries[0].Kind);
        Assert.Equal(ScheduleEntryKind.TimedVisit, after.Entries[0].Kind);
        Assert.True(after.Timed[0].Next!.IsOpenAt(clock.UtcNow));
    }

    [Fact]
    public void NoTimedTasksMeansPlainPlanOrder()
    {
        var steps = new List<ScheduleCraftStep> { new(0, "Ingot", 2, Ready: false) };
        var schedule = Build([Untimed(0, 8), Untimed(1, 4)], steps);

        Assert.Empty(schedule.Timed);
        Assert.False(schedule.HasTimedWork);
        Assert.Collection(
            schedule.Entries,
            e => Assert.Equal(0, e.Task!.Id),
            e => Assert.Equal(1, e.Task!.Id),
            e => Assert.Equal(ScheduleEntryKind.CraftStep, e.Kind));
    }

    [Fact]
    public void RegenIsPerServerTickAndCapped()
    {
        Assert.Equal(160, NodeScheduler.Regen(100, 800, 6, TimeSpan.FromSeconds(30)));
        Assert.Equal(800, NodeScheduler.Regen(700, 800, 6, TimeSpan.FromMinutes(5)));
        Assert.Equal(100, NodeScheduler.Regen(100, 800, 6, TimeSpan.Zero));
    }

    [Fact]
    public void YieldPerNodeFollowsTheBuffTheGpAffords()
    {
        var options = Options();
        Assert.Equal(12, NodeScheduler.YieldPerNode(NodeKind.Unspoiled, false, 800, buffed: true, options));
        Assert.Equal(8, NodeScheduler.YieldPerNode(NodeKind.Normal, false, 400, buffed: true, options));
        Assert.Equal(4, NodeScheduler.YieldPerNode(NodeKind.Normal, false, 800, buffed: false, options));
        Assert.Equal(3, NodeScheduler.YieldPerNode(NodeKind.Ephemeral, true, 600, buffed: true, options));
    }
}
