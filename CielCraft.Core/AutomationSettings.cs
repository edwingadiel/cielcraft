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

    /// <summary>
    /// Crafter stats last seen per job (ClassJob row id), so the HQ-intermediates
    /// check (roadmap 7.22) can run at plan time right after a reload, before
    /// the character has been on the job this session.
    /// </summary>
    public System.Collections.Generic.Dictionary<uint, CrafterStatsEntry> KnownCrafterStats { get; set; } = new();

    // ---- M3: sourcing beyond gather / craft ----

    /// <summary>Never spend gil below this balance (roadmap 7.3b).</summary>
    public long GilFloor { get; set; } = 50_000;

    /// <summary>Gil a single run may spend at vendors and exchanges (roadmap 7.3b / 7.17).</summary>
    public long GilSpendCapPerRun { get; set; } = 200_000;

    /// <summary>Buy a gatherable material from a vendor instead of gathering it when a shop sells it (roadmap 7.3b).</summary>
    public bool BuyWhenGatherable { get; set; }

    /// <summary>Walk to a mender when self-repair finds no Dark Matter (roadmap 7.3a).</summary>
    public bool MenderRepair { get; set; } = true;

    /// <summary>
    /// Vendor NPCs (ENpcResident ids) that were not in the world when a trip
    /// reached their placement — seasonal and event merchants the sheets list
    /// all year (the "festive fisher", 2026-09-15). Never offered again; clear
    /// the list to retry them.
    /// </summary>
    public System.Collections.Generic.List<uint> AbsentVendorNpcs { get; set; } = [];

    /// <summary>How the collectables-for-scrips planner picks turn-ins (roadmap 7.17).</summary>
    public ScripSourcePreference ScripSourcePreference { get; set; } = ScripSourcePreference.Cheapest;

    /// <summary>Desynthesize byproducts no order needs (roadmap 7.17); off by default.</summary>
    public bool DesynthUnusedByproducts { get; set; }

    /// <summary>Discard byproducts no order needs and nothing desynthesizes (roadmap 7.17); off by default.</summary>
    public bool TrashCleanup { get; set; }

    /// <summary>Send retainers on ventures for missing materials when they can bring them (roadmap 7.17).</summary>
    public bool RetainerVentures { get; set; }

    /// <summary>Let the AutoHook plugin handle bite timing over IPC when it is installed (roadmap 7.4).</summary>
    public bool FishingPreferAutoHook { get; set; } = true;

    // ---- M4: combat drops (7.5) — opt-in, a combat plugin drives the fight ----

    /// <summary>Hunt monsters for drops no other source supplies; off by default (roadmap 7.5).</summary>
    public bool HuntingEnabled { get; set; }

    /// <summary>ClassJob row id to fight on; 0 = the highest-level combat job with a gearset.</summary>
    public uint CombatJobId { get; set; }

    /// <summary>Below this HP percentage the hunt disengages and retreats (roadmap 7.5).</summary>
    public int HuntRetreatHpPercent { get; set; } = 30;

    /// <summary>Never fight a mob more than this many levels above the job (0 = same level or below).</summary>
    public int HuntMaxLevelAbove { get; set; }

    /// <summary>Leave mobs another player is fighting alone (roadmap 7.5 etiquette).</summary>
    public bool HuntSkipMobsTargetedByOthers { get; set; } = true;

    // ---- P3b: retainers, desynthesis, trash cleanup (roadmap 7.17) ----

    /// <summary>
    /// The storage policy the inventory keeper applies after a run (roadmap
    /// 7.17). Empty by default: with no rule the keeper does nothing, so
    /// <see cref="DesynthUnusedByproducts"/> and <see cref="TrashCleanup"/>
    /// can never act on an item the user has not named.
    /// </summary>
    public System.Collections.Generic.List<StorageRule> StorageRules { get; set; } = [];
    // ---- P4 (fishing, roadmap 7.4) additions ----

    /// <summary>
    /// Bait per fish the user set by hand, keyed by the fish's item id
    /// (roadmap 7.4). The game sheets carry no bait-per-fish data at all, so
    /// CielCraft ships a small bundled table; an entry here overrides it — the
    /// place to correct a wrong pairing or add a fish the table does not know.
    /// </summary>
    public System.Collections.Generic.Dictionary<uint, FishingBaitChoice> FishingBait { get; set; } = new();

    // ---- M4 package A: remembered hunt spots (roadmap 7.5) ----

    /// <summary>
    /// Where a monster was actually found and killed (roadmap 7.5). The
    /// bundled drop table only knows the zone; a spot remembered in game —
    /// by the hunt run when a fight succeeds, or by the Hunting panel's
    /// "remember this spot" button — is the first place the next hunt goes,
    /// which turns a zone sweep into a teleport and a short ride.
    /// </summary>
    public System.Collections.Generic.List<HuntSpot> HuntSpots { get; set; } = [];
}

/// <summary>
/// How to catch one fish (roadmap 7.4): the bait to apply, and the fish it has
/// to be mooched from when it does not bite on bait directly (0 = cast for it).
/// </summary>
public sealed class FishingBaitChoice
{
    public uint BaitItemId { get; set; }

    public uint MoochFromItemId { get; set; }
}

/// <summary>
/// One remembered monster location (roadmap 7.5). Plain mutable data with
/// float components rather than a Vector3 so the configuration serializer
/// round-trips it like every other settings type.
/// </summary>
[System.Serializable]
public sealed class HuntSpot
{
    /// <summary>BNpcName row of the monster, which is how the object table is searched.</summary>
    public uint BNpcNameId { get; set; }

    public uint TerritoryId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    /// <summary>When the spot was last confirmed; the most recent one wins when several are remembered.</summary>
    public DateTime RememberedAtUtc { get; set; }

    /// <summary>How often a hunt has found the monster here; ties between spots go to the better-used one.</summary>
    public int Hits { get; set; } = 1;

    public System.Numerics.Vector3 Position => new(X, Y, Z);
}

/// <summary>Which turn-ins the collectables planner prefers when several earn the scrips an order needs (roadmap 7.17).</summary>
public enum ScripSourcePreference
{
    /// <summary>Fewest materials / crafts per scrip.</summary>
    Cheapest,

    /// <summary>Fewest trips and windows, even at a higher material cost.</summary>
    Fastest,
}

/// <summary>Craftsmanship / control / CP / level of a crafter job as last seen (roadmap 7.22).</summary>
public sealed class CrafterStatsEntry
{
    public int Craftsmanship { get; set; }

    public int Control { get; set; }

    public int Cp { get; set; }

    public int Level { get; set; }
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
