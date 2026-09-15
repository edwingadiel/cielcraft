using System;

namespace CielCraft.Core;

/// <summary>
/// Time seam for the automation layers (roadmap 5.1): every timeout, throttle
/// and settle delay reads the clock here, so offline tests can advance time
/// deterministically instead of sleeping.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Manually advanced clock for tests.</summary>
public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan delta) => UtcNow += delta;

    public void Advance(double seconds) => UtcNow += TimeSpan.FromSeconds(seconds);
}
