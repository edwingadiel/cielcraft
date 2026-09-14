using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class EorzeaTimeTests
{
    [Fact]
    public void WindowContainsItsRange()
    {
        var window = new EtWindow(EorzeaClock.FromHhmm(800), 120); // 08:00-10:00 ET

        Assert.False(window.Contains(EorzeaClock.FromHhmm(759)));
        Assert.True(window.Contains(EorzeaClock.FromHhmm(800)));
        Assert.True(window.Contains(EorzeaClock.FromHhmm(959)));
        Assert.False(window.Contains(EorzeaClock.FromHhmm(1000)));
    }

    [Fact]
    public void WindowWrapsAroundMidnight()
    {
        var window = new EtWindow(EorzeaClock.FromHhmm(2300), 120); // 23:00-01:00 ET

        Assert.True(window.Contains(EorzeaClock.FromHhmm(2330)));
        Assert.True(window.Contains(EorzeaClock.FromHhmm(30)));
        Assert.False(window.Contains(EorzeaClock.FromHhmm(130)));
    }

    [Fact]
    public void MinutesUntilOpenCountsForwardOnly()
    {
        var window = new EtWindow(EorzeaClock.FromHhmm(800), 120);

        Assert.Equal(0, window.MinutesUntilOpen(EorzeaClock.FromHhmm(900)));
        Assert.Equal(60, window.MinutesUntilOpen(EorzeaClock.FromHhmm(700)));
        // Just after closing: waits almost a full ET day.
        Assert.Equal(1320, window.MinutesUntilOpen(EorzeaClock.FromHhmm(1000)));
    }

    [Fact]
    public void NoWindowsMeansAlwaysOpen()
    {
        Assert.True(EorzeaClock.IsOpen([], 0));
        Assert.Equal(TimeSpan.Zero, EorzeaClock.RealTimeUntilOpen([], 0));
    }

    [Fact]
    public void AnEorzeaDayIsSeventyRealMinutes()
    {
        var fullDay = TimeSpan.FromSeconds(1440 * EorzeaClock.RealSecondsPerEorzeaMinute);
        Assert.Equal(70, fullDay.TotalMinutes, precision: 5);
    }

    [Fact]
    public void RealTimeUntilOpenPicksTheSoonestWindow()
    {
        var windows = new[]
        {
            new EtWindow(EorzeaClock.FromHhmm(1200), 120),
            new EtWindow(EorzeaClock.FromHhmm(400), 120),
        };

        // At 02:00 ET the 04:00 window is 120 ET minutes away.
        var wait = EorzeaClock.RealTimeUntilOpen(windows, EorzeaClock.FromHhmm(200));
        Assert.Equal(120 * EorzeaClock.RealSecondsPerEorzeaMinute, wait.TotalSeconds, precision: 3);
    }
}
