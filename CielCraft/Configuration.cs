using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace CielCraft;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool OpenMainWindowOnLogin { get; set; } = false;

    /// <summary>Allow intentional deviations from the Raphael plan based on live craft state (spec §15/§16).</summary>
    public bool AdaptiveCrafting { get; set; } = true;

    /// <summary>Use quick synthesis for intermediate production steps (NQ output).</summary>
    public bool QuickSynthIntermediates { get; set; } = true;

    /// <summary>Spend GP on yield and integrity actions while gathering (spec §37).</summary>
    public bool UseGatheringBuffs { get; set; } = true;

    /// <summary>Drink cordials between nodes when GP is low.</summary>
    public bool UseCordials { get; set; } = true;

    /// <summary>Quality goal for solves as a percentage of the recipe maximum (roadmap 3.4).</summary>
    public int TargetQualityPercent { get; set; } = 100;

    /// <summary>Self-repair with Dark Matter when any equipped piece drops below the threshold.</summary>
    public bool AutoRepair { get; set; } = true;

    public int RepairThresholdPercent { get; set; } = 30;

    /// <summary>Food to keep active during automation; 0 = off.</summary>
    public uint FoodItemId { get; set; }

    public bool FoodIsHq { get; set; } = true;

    /// <summary>Print production milestones (completed/paused/failed) to the game chat.</summary>
    public bool ChatNotifications { get; set; } = true;

    /// <summary>In-game sound effect (&lt;se.1&gt;..&lt;se.16&gt;) on completion or a stop that needs the user; 0 = off (roadmap 7.20).</summary>
    public int AlertSoundEffect { get; set; } = 0;

    /// <summary>Read completion/attention notifications aloud through Windows speech (roadmap 7.20).</summary>
    public bool SpeakAlerts { get; set; } = false;

    /// <summary>Exit the game once the last production (and the queue) completes (roadmap 7.20).</summary>
    public bool ExitGameWhenDone { get; set; } = false;

    /// <summary>The first-run setup checklist was dismissed (roadmap 7.20).</summary>
    public bool SetupCompleted { get; set; } = false;

    /// <summary>Fill HQ materials into the synthesis automatically before each craft.</summary>
    public bool PreferHqMaterials { get; set; } = true;

    /// <summary>Interrupted production, offered for resume on load (roadmap 6.3).</summary>
    public SavedProductionState SavedProduction { get; set; } = new();

    /// <summary>Pending production queue targets (roadmap 6.8).</summary>
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
        public uint ItemId { get; set; }
        public int Quantity { get; set; }
        public int InitialCount { get; set; }
    }

    [Serializable]
    public class QueuedTarget
    {
        public uint ItemId { get; set; }
        public int Quantity { get; set; }
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
