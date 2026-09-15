using System;
using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>Player-to-player pokes the guard knows about (roadmap 7.10).</summary>
public enum SocialEventKind
{
    TradeRequest,
    PartyInvite,
    FreeCompanyInvite,
    Tell,
}

/// <summary>One incoming social interaction, as observed by the game layer.</summary>
/// <param name="FromPartyOrFreeCompany">Sender is in the party/alliance or shares the free company: not a stranger.</param>
public sealed record SocialEvent(
    SocialEventKind Kind,
    string Sender,
    string? World,
    bool FromPartyOrFreeCompany);

/// <summary>Settings snapshot handed to the decision logic; mirrors the "Social" config section.</summary>
public sealed record SocialSettings(
    bool DeclineTrades = true,
    bool BlacklistTraders = true,
    int PauseSeconds = 20,
    bool LogTells = true,
    bool LogInvites = true)
{
    public static readonly SocialSettings Default = new();
}

/// <summary>What happened to an event; kept for the diagnostic report.</summary>
public sealed record SocialEventRecord(DateTime At, SocialEventKind Kind, string Sender, string? World, string Action)
{
    public override string ToString() =>
        $"{At:HH:mm:ss}Z {Kind} from {Sender}{(World != null ? "@" + World : "")}: {Action}";
}

/// <summary>Side effects the decision logic can request; the plugin implements them against the game.</summary>
public interface ISocialActions
{
    /// <summary>Closes the Trade window; false when nothing was open to close.</summary>
    bool DeclineTrade();

    bool IsBlacklisted(string sender, string? world);

    /// <summary>Adds the sender to the blacklist; false when it could not be added.</summary>
    bool AddToBlacklist(string sender, string? world);

    /// <summary>Pauses the driving automation layer; false when nothing was running.</summary>
    bool PauseAutomation(string reason);

    /// <summary>Resumes the layer the guard paused, if it is still paused by the guard.</summary>
    void ResumeAutomation();

    /// <summary>Plugin log line (the caller prefixes "[Social]").</summary>
    void Log(string message);
}

/// <summary>
/// Pure decision logic for roadmap 7.10: a trade request during automation is
/// the usual "is that a bot?" poke, so behave like a person would — decline
/// it, keep the sender away, and hesitate before carrying on. Party invites
/// and tells from strangers are only logged. No Dalamud types here so the
/// rules are unit-testable with fakes.
/// </summary>
public sealed class SocialGuardCore
{
    public const int HistoryCapacity = 20;
    public const string PauseReason = "trade request";

    private readonly Func<SocialSettings> settings;
    private readonly ISocialActions actions;
    private readonly Func<bool> automationActive;
    private readonly Func<DateTime> clock;
    private readonly List<SocialEventRecord> history = new(HistoryCapacity);

    private DateTime resumeAt = DateTime.MaxValue;

    public SocialGuardCore(
        Func<SocialSettings> settings, ISocialActions actions, Func<bool> automationActive, Func<DateTime> clock)
    {
        this.settings = settings;
        this.actions = actions;
        this.automationActive = automationActive;
        this.clock = clock;
    }

    /// <summary>True while a run is paused by the guard and waiting out the settle time.</summary>
    public bool IsSettling => resumeAt != DateTime.MaxValue;

    public TimeSpan SettleRemaining => IsSettling ? Max(resumeAt - clock(), TimeSpan.Zero) : TimeSpan.Zero;

    public IReadOnlyList<SocialEventRecord> History => history;

    public void OnEvent(SocialEvent e)
    {
        var s = settings();
        switch (e.Kind)
        {
            case SocialEventKind.TradeRequest:
                HandleTrade(e, s);
                break;
            case SocialEventKind.Tell:
                // Party/FC mates chatting is normal; only strangers are worth a note.
                if (s.LogTells && !e.FromPartyOrFreeCompany)
                    Record(e, "logged");
                break;
            case SocialEventKind.PartyInvite:
            case SocialEventKind.FreeCompanyInvite:
                if (s.LogInvites)
                    Record(e, e.FromPartyOrFreeCompany ? "logged (free company)" : "logged");
                break;
        }
    }

    /// <summary>Per-frame: resumes the run once the settle time has elapsed.</summary>
    public void Tick()
    {
        if (!IsSettling || clock() < resumeAt)
            return;

        resumeAt = DateTime.MaxValue;
        actions.ResumeAutomation();
        actions.Log("Settle time over; resuming.");
    }

    /// <summary>The user stopped or resumed things themselves: forget the pending auto-resume.</summary>
    public void CancelSettle() => resumeAt = DateTime.MaxValue;

    private void HandleTrade(SocialEvent e, SocialSettings s)
    {
        // While settling the layer reads as paused, but the run is still ours.
        if (!IsSettling && !automationActive())
        {
            // Nobody is being impersonated while the user is at the keyboard.
            Record(e, "ignored (no automation running)");
            return;
        }

        var steps = new List<string>(4);
        var known = actions.IsBlacklisted(e.Sender, e.World);

        if (s.DeclineTrades)
            steps.Add(actions.DeclineTrade() ? "declined" : "decline failed (no Trade window)");

        if (s.BlacklistTraders)
        {
            if (known)
                steps.Add("already blacklisted");
            else
                steps.Add(actions.AddToBlacklist(e.Sender, e.World) ? "blacklisted" : "blacklist failed");
        }

        // A known pest gets no second hesitation; a new one does. Repeated
        // pokes during the settle simply restart the clock.
        if (s.PauseSeconds > 0 && !known)
        {
            var wasSettling = IsSettling;
            if (wasSettling || actions.PauseAutomation(PauseReason))
            {
                resumeAt = clock() + TimeSpan.FromSeconds(s.PauseSeconds);
                steps.Add(wasSettling ? $"settle restarted ({s.PauseSeconds}s)" : $"paused {s.PauseSeconds}s");
            }
            else
            {
                steps.Add("pause skipped (nothing to pause)");
            }
        }

        Record(e, steps.Count == 0 ? "no action (all disabled)" : string.Join(", ", steps));
    }

    private void Record(SocialEvent e, string action)
    {
        if (history.Count >= HistoryCapacity)
            history.RemoveAt(0);

        var record = new SocialEventRecord(clock(), e.Kind, e.Sender, e.World, action);
        history.Add(record);
        actions.Log(record.ToString());
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
