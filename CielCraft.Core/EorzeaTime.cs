namespace CielCraft.Core;

/// <summary>An Eorzea Time availability window, in ET minutes of day (spec §38).</summary>
public sealed record EtWindow(int StartMinute, int DurationMinutes)
{
    public bool Contains(int minuteOfDay)
    {
        var offset = ((minuteOfDay - StartMinute) % 1440 + 1440) % 1440;
        return offset < DurationMinutes;
    }

    /// <summary>ET minutes until this window opens; 0 while it is open.</summary>
    public int MinutesUntilOpen(int minuteOfDay) =>
        Contains(minuteOfDay) ? 0 : ((StartMinute - minuteOfDay) % 1440 + 1440) % 1440;
}

/// <summary>Eorzea Time: 1 ET day = 70 real minutes (x20.5714 real time).</summary>
public static class EorzeaClock
{
    private const double EorzeaSecondsPerRealSecond = 3600.0 / 175.0;
    public const double RealSecondsPerEorzeaMinute = 175.0 / 60.0;

    public static int MinuteOfDay(DateTimeOffset realTime) =>
        (int)(realTime.ToUnixTimeSeconds() * EorzeaSecondsPerRealSecond / 60.0 % 1440.0);

    /// <summary>Empty windows mean always available.</summary>
    public static bool IsOpen(IReadOnlyList<EtWindow> windows, int minuteOfDay)
    {
        if (windows.Count == 0)
            return true;

        foreach (var window in windows)
        {
            if (window.Contains(minuteOfDay))
                return true;
        }

        return false;
    }

    /// <summary>Real time until the soonest window opens; zero when open or unrestricted.</summary>
    public static TimeSpan RealTimeUntilOpen(IReadOnlyList<EtWindow> windows, int minuteOfDay)
    {
        if (windows.Count == 0)
            return TimeSpan.Zero;

        var soonest = int.MaxValue;
        foreach (var window in windows)
            soonest = Math.Min(soonest, window.MinutesUntilOpen(minuteOfDay));

        return TimeSpan.FromSeconds(soonest * RealSecondsPerEorzeaMinute);
    }

    /// <summary>Converts the sheets' HHMM encoding (e.g. 800 = 08:00) to minutes of day.</summary>
    public static int FromHhmm(int hhmm) => hhmm / 100 * 60 + hhmm % 100;
}
