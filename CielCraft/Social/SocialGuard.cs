using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Crafting;
using CielCraft.Game;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace CielCraft.Social;

/// <summary>
/// Game-side half of roadmap 7.10: watches for trade requests, party/FC
/// invites and tells while automation runs, and carries out what
/// <see cref="SocialGuardCore"/> decides.
///
/// Detection: the Trade window ("Trade" addon) becoming visible is the
/// signal — the client opens it for the receiver the moment a request
/// arrives, and a visible window is unambiguous where chat could be
/// filtered or delayed. The sender's name comes from the LogMessage the
/// game prints at the same time (row 34, "&lt;player&gt; wishes to trade
/// with you."), read by row id so it is language-independent; the addon's
/// own string values are the fallback.
/// </summary>
public sealed class SocialGuard : IDisposable, ISocialActions
{
    private const string TradeAddon = "Trade";

    // LogMessage sheet rows (read from the game data; ids, not English text).
    private const uint LogTradeRequest = 34;      // "<player> wishes to trade with you."
    private const uint LogPartyInvite = 3;        // "<player> invites you to a party."
    private const uint LogFreeCompanyInvite = 1885; // "You have received a free company invite from <player>."

    /// <summary>A person notices the window, then reacts; also lets the chat line land first.</summary>
    private static readonly TimeSpan NoticeDelay = TimeSpan.FromMilliseconds(750);

    /// <summary>How long a "wishes to trade" line stays usable for naming the window that follows.</summary>
    private static readonly TimeSpan NoticeMatchWindow = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan DeclineRetryInterval = TimeSpan.FromSeconds(2);

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly IGameBridge gameBridge;

    private bool tradeWasVisible;
    private DateTime tradeSeenAt = DateTime.MaxValue;
    private (string Name, string? World, DateTime At)? lastTradeNotice;
    private DateTime declineRequestedAt = DateTime.MaxValue;
    private int declineAttempts;

    public SocialGuardCore Core { get; }

    public SocialGuard(Plugin plugin, IGameBridge gameBridge, Configuration configuration)
    {
        this.plugin = plugin;
        this.gameBridge = gameBridge;
        this.configuration = configuration;
        Core = new SocialGuardCore(Settings, this, IsAutomationActive, () => DateTime.UtcNow);

        Plugin.ChatGui.LogMessage += OnLogMessage;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.ChatGui.LogMessage -= OnLogMessage;
    }

    private SocialSettings Settings() => new(
        configuration.SocialDeclineTrades,
        configuration.SocialBlacklistTraders,
        configuration.SocialPauseSeconds,
        configuration.SocialLogTells,
        configuration.SocialLogInvites);

    private bool IsAutomationActive() =>
        plugin.ProductionRunner.State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed or ProductionState.Paused)
        || plugin.BatchCrafter.State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting or BatchState.QuickStarting or BatchState.QuickRunning
        || plugin.GatheringLoop.State == Gathering.GatheringLoopState.Running;

    private void OnUpdate(IFramework framework)
    {
        try
        {
            var now = DateTime.UtcNow;
            var visible = gameBridge.IsAddonVisible(TradeAddon);
            if (visible && !tradeWasVisible)
                tradeSeenAt = now;
            tradeWasVisible = visible;

            if (tradeSeenAt != DateTime.MaxValue && now - tradeSeenAt >= NoticeDelay)
            {
                tradeSeenAt = DateTime.MaxValue;
                var (name, world) = ResolveTradePartner(now);
                Core.OnEvent(new SocialEvent(SocialEventKind.TradeRequest, name, world, false));
            }

            // A cancel that did not take (window re-shown, callback ignored) gets one retry.
            if (declineRequestedAt != DateTime.MaxValue && now - declineRequestedAt >= DeclineRetryInterval)
            {
                if (!gameBridge.IsAddonVisible(TradeAddon))
                    declineRequestedAt = DateTime.MaxValue;
                else if (declineAttempts < 2)
                {
                    declineAttempts++;
                    declineRequestedAt = now;
                    Plugin.Log.Warning("[Social] Trade window still open; sending cancel again.");
                    gameBridge.FireAddonCallbackInt(TradeAddon, -1);
                }
                else
                {
                    declineRequestedAt = DateTime.MaxValue;
                    Plugin.Log.Warning("[Social] Trade window would not close; leaving it to the user.");
                }
            }

            Core.Tick();
        }
        catch (Exception e)
        {
            Plugin.Log.TickError("Social", e);
        }
    }

    private (string Name, string? World) ResolveTradePartner(DateTime now)
    {
        if (lastTradeNotice is { } notice && now - notice.At <= NoticeMatchWindow)
        {
            lastTradeNotice = null;
            return (notice.Name, notice.World);
        }

        // No chat line (filtered, or it came late): the window carries the
        // partner's name among its string values. Names are "First Last".
        var strings = gameBridge.ReadAddonStrings(TradeAddon);
        Plugin.Log.Debug($"[Social] Trade window strings: {string.Join(" | ", strings)}");
        var candidate = strings.FirstOrDefault(s => s.Contains(' ') && s.Length <= 21 && !s.EndsWith('.'));
        return (candidate ?? "unknown", null);
    }

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            switch (message.LogMessageId)
            {
                case LogTradeRequest:
                {
                    var (name, world) = SourceOf(message);
                    lastTradeNotice = (name, world, DateTime.UtcNow);
                    break;
                }
                case LogPartyInvite:
                {
                    var (name, world) = SourceOf(message);
                    Core.OnEvent(new SocialEvent(SocialEventKind.PartyInvite, name, world, gameBridge.IsPartyOrFreeCompanyMember(name)));
                    break;
                }
                case LogFreeCompanyInvite:
                {
                    var (name, world) = SourceOf(message);
                    Core.OnEvent(new SocialEvent(SocialEventKind.FreeCompanyInvite, name, world, false));
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "[Social] Failed to read a log message.");
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (message.LogKind != XivChatType.TellIncoming)
                return;

            var payload = message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
            var name = payload?.PlayerName ?? message.Sender.TextValue;
            var world = payload?.World.ValueNullable?.Name.ExtractText();
            var friendly = message.SourceKind is XivChatRelationKind.PartyMember or XivChatRelationKind.AllianceMember
                || gameBridge.IsPartyOrFreeCompanyMember(name);
            Core.OnEvent(new SocialEvent(SocialEventKind.Tell, name, world, friendly));
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "[Social] Failed to read a chat message.");
        }
    }

    /// <summary>Sender name and home world of a log message; parameters are only valid inside the event.</summary>
    private static (string Name, string? World) SourceOf(ILogMessage message)
    {
        var source = message.SourceEntity;
        if (source != null && source.IsPlayer)
        {
            var name = source.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                return (name, source.HomeWorld.ValueNullable?.Name.ExtractText());
        }

        for (var i = 0; i < message.ParameterCount; i++)
        {
            if (message.TryGetStringParameter(i, out var text))
            {
                var name = text.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    return (name, null);
            }
        }

        return ("unknown", null);
    }

    // ---- ISocialActions ----------------------------------------------------

    public bool DeclineTrade()
    {
        // Callback -1 is the window's own cancel, the same close path the
        // gathering and quick-synthesis windows use (DalamudGameBridge).
        if (!gameBridge.FireAddonCallbackInt(TradeAddon, -1))
            return false;

        declineRequestedAt = DateTime.UtcNow;
        declineAttempts = 0;
        return true;
    }

    public bool IsBlacklisted(string sender, string? world) => FindEntry(sender, world) != null;

    public bool AddToBlacklist(string sender, string? world)
    {
        if (sender == "unknown")
            return false;

        if (FindEntry(sender, world) != null)
            return true;

        configuration.SocialBlacklist.Add(new Configuration.BlacklistEntry
        {
            Name = sender,
            World = world,
            AddedAtUtc = DateTime.UtcNow,
            Reason = "trade request during automation",
        });
        configuration.Save();
        return true;
    }

    public bool PauseAutomation(string reason) => plugin.PauseTopLayer(reason);

    public void ResumeAutomation() => plugin.ResumeTopLayer(SocialGuardCore.PauseReason);

    public void Log(string message) => Plugin.Log.Information($"[Social] {message}");

    private Configuration.BlacklistEntry? FindEntry(string sender, string? world) =>
        configuration.SocialBlacklist.FirstOrDefault(entry =>
            string.Equals(entry.Name, sender, StringComparison.OrdinalIgnoreCase)
            && (entry.World == null || world == null || string.Equals(entry.World, world, StringComparison.OrdinalIgnoreCase)));

    public IEnumerable<string> Describe()
    {
        yield return $"Settling {Core.IsSettling} (remaining {Core.SettleRemaining.TotalSeconds:F0}s); trade window visible {tradeWasVisible}; pending trade notice {(lastTradeNotice is { } n ? $"{n.Name} at {n.At:HH:mm:ss}Z" : "-")}";
        yield return $"Blacklist entries {configuration.SocialBlacklist.Count}; decline {configuration.SocialDeclineTrades}, blacklist {configuration.SocialBlacklistTraders}, pause {configuration.SocialPauseSeconds}s, log tells {configuration.SocialLogTells}, log invites {configuration.SocialLogInvites}";
        if (Core.History.Count == 0)
            yield return "No social events.";
        foreach (var record in Core.History)
            yield return record.ToString();
    }
}
