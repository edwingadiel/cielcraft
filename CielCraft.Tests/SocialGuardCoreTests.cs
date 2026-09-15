using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

/// <summary>Roadmap 7.10: decision logic for trade requests, invites and tells during a run.</summary>
public class SocialGuardCoreTests
{
    private sealed class FakeActions : ISocialActions
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> Blacklist { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool TradeWindowOpen { get; set; } = true;
        public bool SomethingRunning { get; set; } = true;

        public bool DeclineTrade()
        {
            Calls.Add("decline");
            return TradeWindowOpen;
        }

        public bool IsBlacklisted(string sender, string? world) => Blacklist.Contains(sender);

        public bool AddToBlacklist(string sender, string? world)
        {
            Calls.Add($"blacklist {sender}");
            return Blacklist.Add(sender);
        }

        public bool PauseAutomation(string reason)
        {
            Calls.Add($"pause ({reason})");
            return SomethingRunning;
        }

        public void ResumeAutomation() => Calls.Add("resume");

        public void Log(string message) => Calls.Add("log: " + message);
    }

    private sealed class Harness
    {
        public FakeActions Actions { get; } = new();
        public SocialSettings Settings { get; set; } = SocialSettings.Default;
        public bool AutomationActive { get; set; } = true;
        public DateTime Now { get; set; } = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        public SocialGuardCore Core { get; }

        public Harness()
        {
            Core = new SocialGuardCore(() => Settings, Actions, () => AutomationActive, () => Now);
        }

        public IEnumerable<string> ActionCalls => Actions.Calls.Where(c => !c.StartsWith("log:"));
    }

    private static SocialEvent Trade(string sender = "Poke Tester", string? world = "Zalera") =>
        new(SocialEventKind.TradeRequest, sender, world, false);

    [Fact]
    public void TradeDuringRunIsDeclinedBlacklistedAndPaused()
    {
        var h = new Harness();

        h.Core.OnEvent(Trade());

        Assert.Equal(["decline", "blacklist Poke Tester", "pause (trade request)"], h.ActionCalls);
        Assert.True(h.Core.IsSettling);
        Assert.Equal(TimeSpan.FromSeconds(20), h.Core.SettleRemaining);
        var record = Assert.Single(h.Core.History);
        Assert.Equal(SocialEventKind.TradeRequest, record.Kind);
        Assert.Equal("Poke Tester", record.Sender);
        Assert.Equal("Zalera", record.World);
        Assert.Equal("declined, blacklisted, paused 20s", record.Action);
    }

    [Fact]
    public void ResumesOnlyAfterTheSettleTime()
    {
        var h = new Harness();
        h.Core.OnEvent(Trade());
        h.Actions.Calls.Clear();

        h.Now += TimeSpan.FromSeconds(19);
        h.Core.Tick();
        Assert.Empty(h.ActionCalls);
        Assert.True(h.Core.IsSettling);

        h.Now += TimeSpan.FromSeconds(1);
        h.Core.Tick();
        Assert.Equal(["resume"], h.ActionCalls);
        Assert.False(h.Core.IsSettling);

        // No second resume on later ticks.
        h.Now += TimeSpan.FromSeconds(5);
        h.Core.Tick();
        Assert.Equal(["resume"], h.ActionCalls);
    }

    [Fact]
    public void TradeWhileIdleIsOnlyNoted()
    {
        var h = new Harness { AutomationActive = false };

        h.Core.OnEvent(Trade());

        Assert.Empty(h.ActionCalls);
        Assert.False(h.Core.IsSettling);
        Assert.Equal("ignored (no automation running)", Assert.Single(h.Core.History).Action);
    }

    [Fact]
    public void KnownSenderIsDeclinedWithoutAnotherPause()
    {
        var h = new Harness();
        h.Actions.Blacklist.Add("Poke Tester");

        h.Core.OnEvent(Trade());

        Assert.Equal(["decline"], h.ActionCalls);
        Assert.False(h.Core.IsSettling);
        Assert.Equal("declined, already blacklisted", Assert.Single(h.Core.History).Action);
    }

    [Fact]
    public void SecondTradeDuringSettleRestartsTheClockWithoutPausingAgain()
    {
        var h = new Harness();
        h.Core.OnEvent(Trade());
        h.Actions.Calls.Clear();
        // The layer now reads as paused, so the plugin reports no automation.
        h.AutomationActive = false;

        h.Now += TimeSpan.FromSeconds(15);
        h.Core.OnEvent(Trade("Other Poker", "Zalera"));

        Assert.Equal(["decline", "blacklist Other Poker"], h.ActionCalls);
        Assert.Equal(TimeSpan.FromSeconds(20), h.Core.SettleRemaining);
        Assert.Equal("declined, blacklisted, settle restarted (20s)", h.Core.History[^1].Action);
    }

    [Fact]
    public void EverythingOffTakesNoAction()
    {
        var h = new Harness { Settings = new SocialSettings(DeclineTrades: false, BlacklistTraders: false, PauseSeconds: 0) };

        h.Core.OnEvent(Trade());

        Assert.Empty(h.ActionCalls);
        Assert.Equal("no action (all disabled)", Assert.Single(h.Core.History).Action);
    }

    [Fact]
    public void ZeroPauseSecondsSkipsThePause()
    {
        var h = new Harness { Settings = SocialSettings.Default with { PauseSeconds = 0 } };

        h.Core.OnEvent(Trade());

        Assert.Equal(["decline", "blacklist Poke Tester"], h.ActionCalls);
        Assert.False(h.Core.IsSettling);
    }

    [Fact]
    public void FailedDeclineAndNothingToPauseAreReported()
    {
        var h = new Harness();
        h.Actions.TradeWindowOpen = false;
        h.Actions.SomethingRunning = false;

        h.Core.OnEvent(Trade());

        Assert.False(h.Core.IsSettling);
        Assert.Equal("decline failed (no Trade window), blacklisted, pause skipped (nothing to pause)", Assert.Single(h.Core.History).Action);
    }

    [Fact]
    public void CancelSettleDropsThePendingResume()
    {
        var h = new Harness();
        h.Core.OnEvent(Trade());
        h.Actions.Calls.Clear();

        h.Core.CancelSettle();
        h.Now += TimeSpan.FromMinutes(1);
        h.Core.Tick();

        Assert.Empty(h.ActionCalls);
        Assert.False(h.Core.IsSettling);
    }

    [Fact]
    public void TellsFromStrangersAreLoggedPartyAndFcAreNot()
    {
        var h = new Harness();

        h.Core.OnEvent(new SocialEvent(SocialEventKind.Tell, "Random Passerby", "Zalera", false));
        h.Core.OnEvent(new SocialEvent(SocialEventKind.Tell, "Party Friend", null, true));

        Assert.Empty(h.ActionCalls);
        var record = Assert.Single(h.Core.History);
        Assert.Equal("Random Passerby", record.Sender);
        Assert.Equal("logged", record.Action);
        Assert.Contains(h.Actions.Calls, c => c.StartsWith("log:") && c.Contains("Random Passerby"));
    }

    [Fact]
    public void TellLoggingCanBeTurnedOff()
    {
        var h = new Harness { Settings = SocialSettings.Default with { LogTells = false } };

        h.Core.OnEvent(new SocialEvent(SocialEventKind.Tell, "Random Passerby", null, false));

        Assert.Empty(h.Core.History);
    }

    [Fact]
    public void InvitesAreLoggedNeverAnswered()
    {
        var h = new Harness();

        h.Core.OnEvent(new SocialEvent(SocialEventKind.PartyInvite, "Party Leader", "Zalera", false));
        h.Core.OnEvent(new SocialEvent(SocialEventKind.PartyInvite, "Fc Mate", "Zalera", true));
        h.Core.OnEvent(new SocialEvent(SocialEventKind.FreeCompanyInvite, "Recruiter Guy", null, false));

        Assert.Empty(h.ActionCalls);
        Assert.Equal(3, h.Core.History.Count);
        Assert.Equal("logged", h.Core.History[0].Action);
        Assert.Equal("logged (free company)", h.Core.History[1].Action);
        Assert.Equal(SocialEventKind.FreeCompanyInvite, h.Core.History[2].Kind);
    }

    [Fact]
    public void InviteLoggingCanBeTurnedOff()
    {
        var h = new Harness { Settings = SocialSettings.Default with { LogInvites = false } };

        h.Core.OnEvent(new SocialEvent(SocialEventKind.PartyInvite, "Party Leader", null, false));

        Assert.Empty(h.Core.History);
    }

    [Fact]
    public void HistoryKeepsTheLastTwentyEvents()
    {
        var h = new Harness();
        for (var i = 0; i < 25; i++)
            h.Core.OnEvent(new SocialEvent(SocialEventKind.Tell, $"Stranger {i}", null, false));

        Assert.Equal(SocialGuardCore.HistoryCapacity, h.Core.History.Count);
        Assert.Equal("Stranger 5", h.Core.History[0].Sender);
        Assert.Equal("Stranger 24", h.Core.History[^1].Sender);
    }

    [Fact]
    public void RecordTextCarriesKindSenderWorldAndAction()
    {
        var record = new SocialEventRecord(new DateTime(2026, 9, 15, 12, 34, 56, DateTimeKind.Utc), SocialEventKind.TradeRequest, "Poke Tester", "Zalera", "declined");

        Assert.Equal("12:34:56Z TradeRequest from Poke Tester@Zalera: declined", record.ToString());
    }
}
