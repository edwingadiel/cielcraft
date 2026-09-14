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

    /// <summary>Fill HQ materials into the synthesis automatically before each craft.</summary>
    public bool PreferHqMaterials { get; set; } = true;

    /// <summary>Interrupted production, offered for resume on load (roadmap 6.3).</summary>
    public SavedProductionState SavedProduction { get; set; } = new();

    /// <summary>Pending production queue targets (roadmap 6.8).</summary>
    public List<QueuedTarget> QueueItems { get; set; } = [];

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
