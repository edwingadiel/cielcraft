using System;
using System.Collections.Generic;
using System.Numerics;

namespace CielCraft.Core.Scheduling;

/// <summary>
/// A gather task as the scheduler sees it (roadmap 7.15). Id is the caller's
/// handle (the runner's queue index); Amount is what is still missing.
/// </summary>
public sealed record ScheduleTask(
    int Id,
    uint ItemId,
    string Name,
    int Amount,
    IReadOnlyList<EtWindow> Windows,
    NodeKind Kind,
    uint TerritoryId,
    Vector2 AreaPosition,
    bool Collectable,
    uint JobId = 0)
{
    public bool IsTimed => Windows.Count > 0;
}

/// <summary>A craft step the schedule may slot between windows; Ready = its ingredients are already in the bag.</summary>
public sealed record ScheduleCraftStep(int StepIndex, string Name, int Crafts, bool Ready);

/// <summary>
/// What the character brings to the schedule: the GP pool now, the regen per
/// server tick (3 s; 5 base + 1 per Enhanced GP Regeneration trait), and the
/// cordials in the bag with the GP one restores (300 = a plain Cordial; the
/// loop drinks the strongest that fits, so this is the conservative middle).
/// </summary>
public sealed record GatherGpState(int CurrentGp, int MaxGp, int RegenPerTick, int CordialsOwned, int CordialGp = 300)
{
    public const double TickSeconds = 3.0;
}

/// <summary>
/// The scheduler's knobs. TravelLead is teleport + travel before a window (2
/// real minutes today, passed by the runner); Horizon is how far ahead
/// windows are listed for the panel (the first window of every task is
/// always planned, even beyond it). The per-node timings are conservative
/// estimates used for slot counts and for fitting work into gaps; a run
/// that is faster simply finishes early.
/// </summary>
public sealed record SchedulerOptions(
    TimeSpan TravelLead,
    TimeSpan Horizon,
    bool WaitAtHome,
    int WaitAtHomeMinutes,
    bool HasHome,
    bool AtHome = false)
{
    /// <summary>Swings, buffs and the node window: ≈ 40 s per node.</summary>
    public int NodeSeconds { get; init; } = 40;

    /// <summary>Walking to the next node of the group.</summary>
    public int WalkSeconds { get; init; } = 20;

    /// <summary>Gathering attempts a node offers; timed nodes have 4 (Solid Reason adds one, not counted).</summary>
    public int IntegrityPerNode { get; init; } = 4;

    /// <summary>Over-estimate per craft so a step slotted into a gap does not overrun the window.</summary>
    public int CraftSecondsPerCraft { get; init; } = 40;

    /// <summary>Job change + crafting log per step.</summary>
    public int CraftSetupSeconds { get; init; } = 30;

    /// <summary>Teleport home and the loading screen.</summary>
    public int HomeTeleportSeconds { get; init; } = 60;

    /// <summary>Cordials share one recast group.</summary>
    public TimeSpan CordialCooldown { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan TimePerNode => TimeSpan.FromSeconds(NodeSeconds + WalkSeconds);
}

/// <summary>
/// One planned trip to a timed node: the window's real start/end, when to
/// leave (Start − lead) and arrive, how many nodes the time and the GP allow,
/// how many are planned (stops once the amount is covered), and the
/// conservative yield expected.
/// </summary>
public sealed record ScheduledVisit(
    ScheduleTask Task,
    EtWindow Window,
    DateTime Start,
    DateTime End,
    DateTime Depart,
    DateTime Arrive,
    int NodesByTime,
    int NodesByGp,
    int NodesPlanned,
    int GpWanted,
    int GpAtArrival,
    int CordialsPlanned,
    int ExpectedYield,
    int Ordinal,
    int RemainingBefore)
{
    /// <summary>"08:00–10:00 ET".</summary>
    public string EtLabel => $"{Hhmm(Window.StartMinute)}–{Hhmm((Window.StartMinute + Window.DurationMinutes) % 1440)} ET";

    public int RemainingAfter => Math.Max(0, RemainingBefore - ExpectedYield);

    public bool IsOpenAt(DateTime time) => time >= Start && time < End;

    public static string Hhmm(int minuteOfDay) => $"{minuteOfDay / 60:D2}:{minuteOfDay % 60:D2}";
}

/// <summary>A timed item of the plan with its visits within the horizon and the windows it needs in total.</summary>
public sealed record TimedItemPlan(
    ScheduleTask Task,
    IReadOnlyList<ScheduledVisit> Visits,
    int WindowsNeeded,
    bool CoveredWithinHorizon,
    int NodesPerVisit,
    int YieldPerVisit,
    int GpWanted)
{
    public ScheduledVisit? Next => Visits.Count > 0 ? Visits[0] : null;
}

public enum ScheduleEntryKind
{
    /// <summary>Leave at From (lead before the window), gather until Until.</summary>
    TimedVisit,

    /// <summary>An untimed gather task done while waiting (or after the windows).</summary>
    UntimedGather,

    /// <summary>A craft step; between windows only when its ingredients are in the bag.</summary>
    CraftStep,

    /// <summary>Wait in place until the next departure.</summary>
    Idle,

    /// <summary>Teleport home and wait there (WaitAtHomeForWindows, roadmap 7.6 / 7.15).</summary>
    GoHome,
}

/// <summary>One line of the plan order, in time order.</summary>
public sealed record ScheduleEntry(
    ScheduleEntryKind Kind,
    DateTime From,
    DateTime Until,
    string Text,
    ScheduledVisit? Visit = null,
    ScheduleTask? Task = null,
    ScheduleCraftStep? Step = null)
{
    public TimeSpan Duration => Until - From;
}

/// <summary>The schedule of a plan: the timed items, every visit in time order and the plan order the runner follows.</summary>
public sealed record Schedule(
    DateTime BuiltAt,
    IReadOnlyList<TimedItemPlan> Timed,
    IReadOnlyList<ScheduledVisit> Visits,
    IReadOnlyList<ScheduleEntry> Entries)
{
    public static readonly Schedule Empty = new(DateTime.MinValue, [], [], []);

    public ScheduleEntry? First => Entries.Count > 0 ? Entries[0] : null;

    public bool HasTimedWork => Timed.Count > 0;
}
