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

    /// <summary>Food and potion for craft steps (roadmap 7.11); FoodItemId/FoodIsHq above are the pre-7.11 single food, migrated on load.</summary>
    public ConsumableSet CraftingConsumables { get; set; } = new();

    /// <summary>Food and potion for gather tasks (roadmap 7.11).</summary>
    public ConsumableSet GatheringConsumables { get; set; } = new();

    /// <summary>Where craft steps happen (roadmap 7.6): stay put, or teleport to a home point before the first step.</summary>
    public CraftingLocation CraftingLocation { get; set; } = CraftingLocation.Stay;

    /// <summary>Assist mode (roadmap 7.18): a synthesis the user starts by hand is run by the plugin.</summary>
    public bool AssistMode { get; set; }

    /// <summary>Lock-step (roadmap 7.18): pause before every craft action and wait for the user to step.</summary>
    public bool LockStep { get; set; }

    /// <summary>Manual rotations per recipe id (roadmap 7.8): action names or Teamcraft macro text; replaces the solver when set.</summary>
    public System.Collections.Generic.Dictionary<uint, string> ManualRotations { get; set; } = new();

    /// <summary>
    /// Gathering rotation overrides per node class (roadmap 7.14), keyed by
    /// the class name ("Normal", "Unspoiled", "Crystal", "Collectable"), in
    /// the same text format as the built-in tables; empty = built-in.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string> GatheringRotationOverrides { get; set; } = new();

    /// <summary>Extract materia from gear at 100% spiritbond between crafts and nodes (roadmap 7.2).</summary>
    public bool AutoExtractMateria { get; set; } = true;

    /// <summary>Craft intermediates HQ when the final recipe cannot reach its quality target from zero (roadmap 7.22).</summary>
    public bool HqIntermediates { get; set; } = true;

    /// <summary>Wait for a timed node's window at the home point (7.6) instead of beside the node (roadmap 7.15).</summary>
    public bool WaitAtHomeForWindows { get; set; } = true;

    /// <summary>Only waits longer than this go home; shorter ones idle in place (roadmap 7.15).</summary>
    public int WaitAtHomeMinutes { get; set; } = 8;
}

/// <summary>One consumable choice: an item and whether the HQ version is preferred; ItemId 0 = none.</summary>
public sealed class Consumable
{
    public uint ItemId { get; set; }

    public bool Hq { get; set; } = true;
}

/// <summary>The food and potion kept up during one kind of activity (roadmap 7.11).</summary>
public sealed class ConsumableSet
{
    public Consumable Food { get; set; } = new();

    public Consumable Potion { get; set; } = new();
}

/// <summary>Home points the runner can teleport to before crafting (roadmap 7.6).</summary>
public enum CraftingLocation
{
    /// <summary>Craft wherever the character is (after gathering: the zone aetheryte).</summary>
    Stay,

    /// <summary>The free company or private estate hall.</summary>
    EstateHall,

    Apartment,

    /// <summary>The inn room of the nearest city (or the last one visited).</summary>
    InnRoom,
}
