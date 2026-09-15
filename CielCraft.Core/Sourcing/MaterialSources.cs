using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>Where a missing material can come from besides a MIN/BTN node (roadmap 7.3b, 7.17, 7.4).</summary>
public enum MaterialSourceKind
{
    Gather,

    /// <summary>A gil vendor (GilShop).</summary>
    Buy,

    /// <summary>A scrip / tomestone / Grand Company exchange (SpecialShop, GC seals).</summary>
    Exchange,

    Fish,

    /// <summary>A retainer's inventory or venture.</summary>
    Retainer,
}

/// <summary>
/// A source's answer to "can you supply N of this item?": how, in words for
/// the log and the plan, a rough duration for the schedule and the gil it
/// would cost (0 when none).
/// </summary>
public sealed record SourceOffer(
    uint ItemId,
    int Amount,
    MaterialSourceKind Kind,
    string Description,
    int EstimatedSeconds,
    long GilCost = 0);

public enum SourceRunState
{
    Idle,
    Running,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// One supply job in flight (buy N at a vendor, fish N, exchange N …), ticked
/// by the production runner while it is the runner's current task. The
/// runner forwards its own pause / resume / stop; the run owns its travel,
/// dialogs and verification (the bag count is the truth, spec §34).
/// </summary>
public interface ISourceRun
{
    SourceRunState State { get; }

    string StatusText { get; }

    /// <summary>Items obtained so far, by inventory delta.</summary>
    int Obtained { get; }

    void Tick();

    void Pause(string reason);

    void Resume();

    void Stop();

    IEnumerable<string> Describe();
}

/// <summary>
/// A material source the runner can ask (roadmap 7.3b / 7.17 / 7.4). Sources
/// are consulted in the order the plugin registers them, only for materials
/// no MIN/BTN node yields (gathering stays the default source).
/// </summary>
public interface IMaterialSource
{
    MaterialSourceKind Kind { get; }

    /// <summary>Short name for the log and the plan ("vendor", "scrip exchange", "fishing").</summary>
    string Name { get; }

    /// <summary>An offer when this source can supply the amount now (data known, gil / scrips / job available); null otherwise.</summary>
    SourceOffer? Offer(uint itemId, int amount);

    /// <summary>Starts supplying; the returned run is ticked by the caller.</summary>
    ISourceRun Start(SourceOffer offer);
}

/// <summary>A source with a per-run spend budget (gil, scrips); the production runner resets it when a run starts.</summary>
public interface IRunBudget
{
    void ResetRunBudget();
}
