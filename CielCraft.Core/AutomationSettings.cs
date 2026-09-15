namespace CielCraft.Core;

/// <summary>
/// The settings the automation layers read. Plain data with no Dalamud
/// dependency so the layers can live in Core (roadmap 5.1); the plugin's
/// Configuration derives from it and adds persistence and UI-only fields.
/// </summary>
public class AutomationSettings
{
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
}
