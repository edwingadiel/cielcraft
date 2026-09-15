using System;
using System.Collections.Generic;
using System.Linq;

namespace CielCraft.Core.Scheduling;

/// <summary>
/// Timed-node scheduler (roadmap 7.15). Given the gather tasks of a plan, the
/// craft steps still to do and the character's GP, it lists the next windows
/// of every timed task, sizes each visit (nodes the window length allows,
/// nodes the GP allows, the conservative yield) and lays out the plan order:
/// the earliest reachable window first, untimed gathering and ready craft
/// steps in the gaps, a wait (in place, or at home when the gap is long
/// enough and a home is set) for the rest. Pure and clock-free so it is
/// testable with a FakeClock; the runner rebuilds it at every decision
/// point, so estimates only need to be conservative, never exact.
///
/// Assumptions, all on the conservative side: since Shadowbringers a timed
/// node stays up for its whole window and respawns when exhausted, so a
/// visit can run several nodes; each node offers IntegrityPerNode swings; a
/// swing yields 1 (+2 with a Yield II class buff, +1 with Yield I) when the
/// GP for the full rotation is there and 1 otherwise; a collectable node
/// hands over integrity − 1 items.
/// </summary>
public static class NodeScheduler
{
    private const double RealSecondsPerEtMinute = EorzeaClock.RealSecondsPerEorzeaMinute;
    private static readonly TimeSpan EorzeaDay = TimeSpan.FromSeconds(1440 * RealSecondsPerEtMinute);
    private const int MaxVisitsPerTask = 50;

    public static Schedule Build(
        IReadOnlyList<ScheduleTask> tasks,
        IReadOnlyList<ScheduleCraftStep> steps,
        DateTime now,
        GatherGpState gp,
        SchedulerOptions options)
    {
        var entries = new List<ScheduleEntry>();
        var allVisits = new List<ScheduledVisit>();
        var untimed = tasks.Where(t => !t.IsTimed && t.Amount > 0).ToList();
        var pendingSteps = steps.ToList();
        var timed = tasks.Where(t => t.IsTimed && t.Amount > 0).Select(t => new TimedState(t)).ToList();

        var cursor = now;
        var maxGp = Math.Max(0, gp.MaxGp);
        var gpNow = Math.Clamp(gp.CurrentGp, 0, Math.Max(maxGp, gp.CurrentGp));
        var gpAt = now;         // when gpNow was last known; regen applies from here to the next arrival
        var cordials = Math.Max(0, gp.CordialsOwned);
        var atHome = options.AtHome;
        var horizonEnd = now + options.Horizon;

        while (true)
        {
            // The next reachable window across every timed task still short:
            // earliest arrival wins (a tie goes to the window that closes first).
            TimedState? bestTask = null;
            Occurrence? best = null;
            foreach (var state in timed)
            {
                if (state.Remaining <= 0 || state.BeyondHorizon)
                    continue;

                var occurrence = NextOccurrence(state.Task, cursor, options);
                if (best == null || occurrence.Arrive < best.Arrive
                    || (occurrence.Arrive == best.Arrive && occurrence.End < best.End))
                {
                    best = occurrence;
                    bestTask = state;
                }
            }

            if (best == null || bestTask == null)
                break;

            // Beyond the horizon: keep the first window of a task (the runner
            // needs something to wait for) but stop listing further ones.
            if (best.Start > horizonEnd && bestTask.Visits.Count > 0)
            {
                bestTask.BeyondHorizon = true;
                continue;
            }

            // Fill the gap before departure: untimed gathering (in the field
            // anyway), then home when the rest of the gap is long enough,
            // then the craft steps whose ingredients are in the bag, then idle.
            var depart = best.Arrive - options.TravelLead;
            if (depart > cursor)
            {
                for (var i = 0; i < untimed.Count;)
                {
                    var duration = EstimateGather(untimed[i], options);
                    if (cursor + duration <= depart)
                    {
                        entries.Add(new ScheduleEntry(
                            ScheduleEntryKind.UntimedGather, cursor, cursor + duration,
                            $"Gather {untimed[i].Name} ×{untimed[i].Amount} while waiting", Task: untimed[i]));
                        cursor += duration;
                        gpNow = 0;      // a node run spends the pool; only the regen from here on is left for the window
                        gpAt = cursor;
                        atHome = false;
                        untimed.RemoveAt(i);
                    }
                    else
                    {
                        i++;
                    }
                }

                var gap = depart - cursor;
                if (!atHome && options.WaitAtHome && options.HasHome
                    && gap >= TimeSpan.FromMinutes(Math.Max(0, options.WaitAtHomeMinutes)))
                {
                    var teleport = TimeSpan.FromSeconds(options.HomeTeleportSeconds);
                    entries.Add(new ScheduleEntry(
                        ScheduleEntryKind.GoHome, cursor, cursor + teleport,
                        $"Go home for the {Describe(gap)} wait before {bestTask.Task.Name}"));
                    cursor += teleport;
                    atHome = true;
                }

                while (pendingSteps.Count > 0 && pendingSteps[0].Ready)
                {
                    var step = pendingSteps[0];
                    var duration = EstimateCraft(step, options);
                    if (cursor + duration > depart)
                        break;

                    entries.Add(new ScheduleEntry(
                        ScheduleEntryKind.CraftStep, cursor, cursor + duration,
                        $"Craft {step.Name} ({step.Crafts} crafts) while waiting", Step: step));
                    cursor += duration;
                    pendingSteps.RemoveAt(0);
                }

                if (cursor < depart)
                {
                    entries.Add(new ScheduleEntry(
                        ScheduleEntryKind.Idle, cursor, depart,
                        $"Wait {(atHome ? "at home" : "here")} {Describe(depart - cursor)} for {bestTask.Task.Name}'s {ScheduledVisit.Hhmm(best.Window.StartMinute)} ET window"));
                    cursor = depart;
                }
            }

            // The visit itself.
            var arrive = best.Arrive;
            var gpAtArrival = Regen(gpNow, maxGp, gp.RegenPerTick, arrive - gpAt);
            var visit = PlanVisit(bestTask, best, gpAtArrival, cordials, gp, maxGp, options);
            bestTask.Visits.Add(visit);
            allVisits.Add(visit);
            bestTask.Remaining = visit.RemainingAfter;
            var visitEnd = arrive + TimeSpan.FromSeconds(visit.NodesPlanned * (options.NodeSeconds + options.WalkSeconds));
            if (visitEnd > best.End)
                visitEnd = best.End;

            entries.Add(new ScheduleEntry(
                ScheduleEntryKind.TimedVisit, depart, visitEnd,
                $"{bestTask.Task.Name}: {visit.EtLabel} window, {visit.NodesPlanned} node(s), ≈{visit.ExpectedYield} item(s)",
                Visit: visit, Task: bestTask.Task));

            var buffedNodes = Math.Min(visit.NodesPlanned, visit.NodesByGp);
            gpNow = Math.Clamp(
                Regen(gpAtArrival, maxGp, gp.RegenPerTick, visitEnd - arrive) + visit.CordialsPlanned * gp.CordialGp - buffedNodes * visit.GpWanted,
                0, Math.Max(maxGp, 0));
            cordials -= visit.CordialsPlanned;
            cursor = visitEnd;
            gpAt = visitEnd;
            atHome = false;

            if (bestTask.Visits.Count >= MaxVisitsPerTask)
                bestTask.BeyondHorizon = true;
        }

        // Whatever did not fit into a gap: the remaining untimed gathering,
        // then every craft step in plan order (ready or not — the runner
        // re-verifies ingredients before each step anyway).
        foreach (var task in untimed)
        {
            var duration = EstimateGather(task, options);
            entries.Add(new ScheduleEntry(
                ScheduleEntryKind.UntimedGather, cursor, cursor + duration, $"Gather {task.Name} ×{task.Amount}", Task: task));
            cursor += duration;
        }

        foreach (var step in pendingSteps)
        {
            var duration = EstimateCraft(step, options);
            entries.Add(new ScheduleEntry(
                ScheduleEntryKind.CraftStep, cursor, cursor + duration, $"Craft {step.Name} ({step.Crafts} crafts)", Step: step));
            cursor += duration;
        }

        var plans = timed.Select(state => state.ToPlan()).ToList();
        return new Schedule(now, plans, allVisits, entries);
    }

    /// <summary>GP after regenerating for a while, capped at the pool.</summary>
    public static int Regen(int gpNow, int maxGp, int regenPerTick, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            return Math.Min(gpNow, maxGp);

        var ticks = (int)(elapsed.TotalSeconds / GatherGpState.TickSeconds);
        return Math.Min(maxGp, gpNow + ticks * Math.Max(0, regenPerTick));
    }

    /// <summary>
    /// Items one node run is expected to hand over. Buffed = the GP for the
    /// class rotation is there (Yield II at 500 GP adds 2 per swing, Yield I
    /// at 400 adds 1); collectables hand over one per Collect and the last
    /// attempt may fall short of the tier.
    /// </summary>
    public static int YieldPerNode(NodeKind kind, bool collectable, int gpWanted, bool buffed, SchedulerOptions options)
    {
        var integrity = Math.Max(1, options.IntegrityPerNode);
        if (collectable || kind == NodeKind.Ephemeral)
            return Math.Max(1, integrity - 1);

        var perSwing = 1 + (buffed ? (gpWanted >= 500 ? 2 : gpWanted >= 400 ? 1 : 0) : 0);
        return integrity * perSwing;
    }

    /// <summary>Real time an untimed task is expected to take: the trip plus unbuffed node runs (over-estimated so it never crowds a window).</summary>
    public static TimeSpan EstimateGather(ScheduleTask task, SchedulerOptions options)
    {
        var perNode = YieldPerNode(task.Kind, task.Collectable, 0, buffed: false, options);
        var nodes = Math.Max(1, (task.Amount + perNode - 1) / perNode);
        return options.TravelLead + TimeSpan.FromSeconds(nodes * (options.NodeSeconds + options.WalkSeconds));
    }

    public static TimeSpan EstimateCraft(ScheduleCraftStep step, SchedulerOptions options) =>
        TimeSpan.FromSeconds(options.CraftSetupSeconds + Math.Max(1, step.Crafts) * options.CraftSecondsPerCraft);

    /// <summary>
    /// The task's earliest usable window occurrence from <paramref name="from"/>:
    /// arrival is the window's start or from + lead, whichever is later, and
    /// a window with less than one node's worth of time left rolls over to
    /// its next Eorzea day.
    /// </summary>
    public static Occurrence NextOccurrence(ScheduleTask task, DateTime from, SchedulerOptions options)
    {
        Occurrence? best = null;
        var etFrom = EorzeaClock.MinuteOfDay(new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)));
        foreach (var window in task.Windows)
        {
            var offset = ((etFrom - window.StartMinute) % 1440 + 1440) % 1440;
            var start = offset < window.DurationMinutes
                ? from - TimeSpan.FromSeconds(offset * RealSecondsPerEtMinute)
                : from + TimeSpan.FromSeconds((1440 - offset) * RealSecondsPerEtMinute);
            var end = start + TimeSpan.FromSeconds(window.DurationMinutes * RealSecondsPerEtMinute);
            var arrive = start > from + options.TravelLead ? start : from + options.TravelLead;

            if (end - arrive < options.TimePerNode)
            {
                start += EorzeaDay;
                end += EorzeaDay;
                arrive = start > from + options.TravelLead ? start : from + options.TravelLead;
            }

            var candidate = new Occurrence(window, start, end, arrive);
            if (best == null || candidate.Arrive < best.Arrive || (candidate.Arrive == best.Arrive && candidate.End < best.End))
                best = candidate;
        }

        return best!;
    }

    private static ScheduledVisit PlanVisit(
        TimedState state, Occurrence occurrence, int gpAtArrival, int cordials, GatherGpState gp, int maxGp, SchedulerOptions options)
    {
        var task = state.Task;
        var usable = occurrence.End - occurrence.Arrive;
        var gpWanted = GatheringRotationCost.GpPerNode(task.Kind, task.Collectable, maxGp);
        var nodesByTime = Math.Max(1, (int)(usable.TotalSeconds / (options.NodeSeconds + options.WalkSeconds)));
        var cordialsUsable = Math.Min(cordials, 1 + (int)(usable.TotalSeconds / Math.Max(1.0, options.CordialCooldown.TotalSeconds)));
        var regenDuringVisit = (int)(usable.TotalSeconds / GatherGpState.TickSeconds) * Math.Max(0, gp.RegenPerTick);
        var budget = gpAtArrival + regenDuringVisit + cordialsUsable * gp.CordialGp;
        var nodesByGp = gpWanted > 0 ? budget / gpWanted : nodesByTime;

        var buffedYield = YieldPerNode(task.Kind, task.Collectable, gpWanted, buffed: true, options);
        var baseYield = YieldPerNode(task.Kind, task.Collectable, gpWanted, buffed: false, options);
        var planned = 0;
        var buffed = 0;
        var yield = 0;
        while (planned < nodesByTime && yield < state.Remaining)
        {
            if (buffed < nodesByGp)
            {
                yield += buffedYield;
                buffed++;
            }
            else
            {
                yield += baseYield;
            }

            planned++;
        }

        var gpShort = buffed * gpWanted - gpAtArrival - regenDuringVisit;
        var cordialsPlanned = gpShort > 0 && gp.CordialGp > 0
            ? Math.Min(cordialsUsable, (gpShort + gp.CordialGp - 1) / gp.CordialGp)
            : 0;

        return new ScheduledVisit(
            task, occurrence.Window, occurrence.Start, occurrence.End,
            occurrence.Arrive - options.TravelLead, occurrence.Arrive,
            nodesByTime, nodesByGp, planned, gpWanted, gpAtArrival, cordialsPlanned,
            yield, state.Visits.Count + 1, state.Remaining);
    }

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:D2}s" : $"{span.Seconds}s";

    /// <summary>A window occurrence in real time.</summary>
    public sealed record Occurrence(EtWindow Window, DateTime Start, DateTime End, DateTime Arrive);

    private sealed class TimedState(ScheduleTask task)
    {
        public ScheduleTask Task { get; } = task;

        public int Remaining { get; set; } = task.Amount;

        public bool BeyondHorizon { get; set; }

        public List<ScheduledVisit> Visits { get; } = [];

        /// <summary>Windows needed: the visits planned, extrapolated at the last visit's yield when the horizon cut the list short.</summary>
        public TimedItemPlan ToPlan()
        {
            var windows = Visits.Count;
            var covered = Remaining <= 0;
            if (!covered && Visits.Count > 0)
            {
                var perVisit = Math.Max(1, Visits[^1].ExpectedYield);
                windows += (Remaining + perVisit - 1) / perVisit;
            }

            var first = Visits.Count > 0 ? Visits[0] : null;
            return new TimedItemPlan(
                Task, Visits, windows, covered,
                first?.NodesPlanned ?? 0, first?.ExpectedYield ?? 0,
                first?.GpWanted ?? 0);
        }
    }
}
