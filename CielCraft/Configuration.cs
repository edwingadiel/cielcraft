using System;
using System.Collections.Generic;
using CielCraft.Core;
using Dalamud.Configuration;

namespace CielCraft;

/// <summary>
/// Persisted plugin settings. The automation fields live in
/// <see cref="AutomationSettings"/> (Dalamud-free, read by the layers);
/// this adds persistence and the UI-only fields (roadmap 5.1).
/// </summary>
[Serializable]
public class Configuration : AutomationSettings, IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool OpenMainWindowOnLogin { get; set; } = false;

    /// <summary>In-game sound effect (&lt;se.1&gt;..&lt;se.16&gt;) on completion or a stop that needs the user; 0 = off (roadmap 7.20).</summary>
    public int AlertSoundEffect { get; set; } = 0;

    /// <summary>Read completion/attention notifications aloud through Windows speech (roadmap 7.20).</summary>
    public bool SpeakAlerts { get; set; } = false;

    /// <summary>Exit the game once the last production (and the queue) completes (roadmap 7.20).</summary>
    public bool ExitGameWhenDone { get; set; } = false;

    /// <summary>The first-run setup checklist was dismissed (roadmap 7.20).</summary>
    public bool SetupCompleted { get; set; } = false;

    /// <summary>UI only (roadmap 7.21): the main window's last page id ("Section/Page"), reopened next time.</summary>
    public string LastPage { get; set; } = "Orders";

    /// <summary>Interrupted production, offered for resume on load (roadmap 6.3); one entry per target since 7.13.</summary>
    public SavedProductionState SavedProduction { get; set; } = new();

    /// <summary>The order book (roadmap 7.13): groups of orders run in sequence by the OrderRunner.</summary>
    public OrderBook Orders { get; set; } = new();

    /// <summary>
    /// Obsolete (roadmap 6.8 queue, replaced by <see cref="Orders"/> in 7.13).
    /// Kept so older configs still deserialize; Plugin folds any entries into
    /// a "Queue" order group on load and clears the list.
    /// </summary>
    public List<QueuedTarget> QueueItems { get; set; } = [];

    /// <summary>Close the Trade window when a trade request arrives mid-run (roadmap 7.10).</summary>
    public bool SocialDeclineTrades { get; set; } = true;

    /// <summary>Remember who poked us so a repeat is declined without another pause (roadmap 7.10).</summary>
    public bool SocialBlacklistTraders { get; set; } = true;

    /// <summary>Settle time after a declined trade before the run continues; 0 = keep going.</summary>
    public int SocialPauseSeconds { get; set; } = 20;

    /// <summary>Note tells from strangers (not party/FC) in the log; never acted on.</summary>
    public bool SocialLogTells { get; set; } = true;

    /// <summary>Note party and free company invites in the log; never acted on.</summary>
    public bool SocialLogInvites { get; set; } = true;

    /// <summary>
    /// Plugin-side blacklist (roadmap 7.10). The client exposes the game
    /// blacklist read-only (InfoProxyBlacklist), so senders are remembered
    /// here instead; the guard declines them on sight.
    /// </summary>
    public List<BlacklistEntry> SocialBlacklist { get; set; } = [];

    [Serializable]
    public class BlacklistEntry
    {
        public string Name { get; set; } = "";
        public string? World { get; set; }
        public DateTime AddedAtUtc { get; set; }
        public string Reason { get; set; } = "";
    }

    [Serializable]
    public class SavedProductionState
    {
        public bool Active { get; set; }

        /// <summary>The plan's targets with their start-of-run bag counts (roadmap 7.13); resume re-plans what each still needs.</summary>
        public List<SavedTarget> Targets { get; set; } = [];

        // The first target, mirrored for the resume banner and for pre-7.13
        // configs (Plugin folds a legacy single target into Targets on load).
        public uint ItemId { get; set; }
        public int Quantity { get; set; }
        public int InitialCount { get; set; }
    }

    [Serializable]
    public class SavedTarget
    {
        public uint ItemId { get; set; }

        /// <summary>The ordered amount; what is still missing is measured against the bag on resume.</summary>
        public int Quantity { get; set; }

        /// <summary>NQ+HQ count when the run started.</summary>
        public int InitialCount { get; set; }

        /// <summary>HQ count when the run started; ForceHq progress is measured against this one.</summary>
        public int InitialHqCount { get; set; }

        public ProductionMode Mode { get; set; } = ProductionMode.Any;

        public bool MaterialsOnly { get; set; }

        /// <summary>Craft or gather order (roadmap 7.1); a resumed gather order must not re-plan as a craft.</summary>
        public OrderKind Kind { get; set; } = OrderKind.Craft;

        /// <summary>Tier of a collectable order (roadmap 7.23 / 7.1), kept across a resume.</summary>
        public CollectableTier CollectableTier { get; set; } = CollectableTier.High;
    }

    [Serializable]
    public class QueuedTarget
    {
        public uint ItemId { get; set; }
        public int Quantity { get; set; }
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
