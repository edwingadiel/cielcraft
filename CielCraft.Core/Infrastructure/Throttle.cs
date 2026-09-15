using System;

namespace CielCraft.Core;

/// <summary>
/// "At most once per interval" gate for retried game actions (open a window,
/// press a button, request a mount). Replaces the five per-class
/// Throttled/RetryInterval copies (review follow-up under roadmap 5.1).
/// </summary>
public sealed class Throttle
{
    private readonly IClock clock;

    public Throttle(IClock clock, TimeSpan interval)
    {
        this.clock = clock;
        Interval = interval;
    }

    public TimeSpan Interval { get; }

    /// <summary>When the gate last let an attempt through; MinValue before the first.</summary>
    public DateTime LastAttempt { get; private set; } = DateTime.MinValue;

    public bool IsReady => clock.UtcNow - LastAttempt >= Interval;

    /// <summary>Runs the action if the interval has elapsed since the last attempt; returns whether it ran.</summary>
    public bool Try(Action action)
    {
        if (!IsReady)
            return false;

        LastAttempt = clock.UtcNow;
        action();
        return true;
    }

    /// <summary>Forget the last attempt so the next Try runs immediately (a new phase starts).</summary>
    public void Reset() => LastAttempt = DateTime.MinValue;

    /// <summary>Mark an attempt without running anything (the caller acted itself).</summary>
    public void Touch() => LastAttempt = clock.UtcNow;
}
