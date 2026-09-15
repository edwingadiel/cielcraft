using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Combat;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Sourcing;

/// <summary>
/// Kills monsters for a material nothing else supplies (roadmap 7.5).
/// Registered last in the production runner's source list: hunting is opt-in
/// (<see cref="AutomationSettings.HuntingEnabled"/>, off by default) and the
/// source offers nothing at all when no combat plugin is loaded, so a
/// character without one never notices it exists.
/// </summary>
public sealed class CombatSource : IMaterialSource
{
    /// <summary>Rough trip cost for the schedule: gearset, teleport and the ride out.</summary>
    private const int TravelSeconds = 90;

    /// <summary>
    /// Rough cost of one drop. Drop rates are not in any sheet and Garland
    /// does not publish them, so the schedule assumes a couple of kills per
    /// item at about half a minute each; it only orders the plan's steps.
    /// </summary>
    private const int SecondsPerDrop = 55;

    private readonly CombatDatabase database;
    private readonly ICombatDriver driver;
    private readonly IHuntBridge bridge;
    private readonly INavigationProvider navigation;
    private readonly AutomationSettings settings;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly Func<uint, string> itemName;

    /// <summary>The monster an offer named, so the run hunts what the plan promised.</summary>
    private readonly Dictionary<uint, MobDrop> offered = new();

    public CombatSource(
        CombatDatabase database,
        ICombatDriver driver,
        IHuntBridge bridge,
        INavigationProvider navigation,
        AutomationSettings settings,
        ILog log,
        IClock clock,
        Func<CharacterCapabilities>? capabilities = null,
        Func<uint, string>? itemName = null)
    {
        this.database = database;
        this.driver = driver;
        this.bridge = bridge;
        this.navigation = navigation;
        this.settings = settings;
        this.log = log;
        this.clock = clock;
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
        this.itemName = itemName ?? (id => $"item {id}");
    }

    public MaterialSourceKind Kind => MaterialSourceKind.Hunt;

    public string Name => "hunt";

    public SourceOffer? Offer(uint itemId, int amount)
    {
        if (amount <= 0 || !settings.HuntingEnabled || !driver.IsAvailable)
            return null;

        var job = ChooseCombatJob();
        if (job == 0)
            return null;

        var drop = BestDrop(itemId, job);
        if (drop == null)
            return null;

        offered[itemId] = drop;
        return new SourceOffer(
            itemId,
            amount,
            MaterialSourceKind.Hunt,
            $"Hunt {itemName(itemId)} ×{amount} from {drop.MobName} ({drop.ZoneName}, Lv {drop.Level})",
            TravelSeconds + (amount * SecondsPerDrop));
    }

    /// <summary>"hunt Raptors (Coerthas Central Highlands)" for the plan tree; null when nothing drops it.</summary>
    public string? SourceLabel(uint itemId)
    {
        if (!settings.HuntingEnabled || !driver.IsAvailable)
            return null;

        var job = ChooseCombatJob();
        if (job == 0)
            return null;

        var drop = BestDrop(itemId, job);
        return drop == null ? null : $"hunt {drop.MobName} ({drop.ZoneName}, Lv {drop.Level})";
    }

    public ISourceRun Start(SourceOffer offer)
    {
        // The plan may be minutes old: ask again so a zone change or a spot
        // remembered in the meantime is taken into account.
        var job = ChooseCombatJob();
        var drop = BestDrop(offer.ItemId, job) ?? offered.GetValueOrDefault(offer.ItemId);
        return new HuntRun(
            bridge, driver, navigation, settings, offer, drop, job, log, clock, database, capabilities, itemName);
    }

    /// <summary>
    /// The job the hunt fights on: the configured one when it has a gearset,
    /// otherwise the highest-level combat job that does (the M4 contract's
    /// "0 = the highest-level combat job with a gearset"). Package B's
    /// capability reader fills in the combat job levels; until then every
    /// level reads 0 and the first job with a gearset is taken.
    /// </summary>
    public uint ChooseCombatJob()
    {
        var known = capabilities();
        if (settings.CombatJobId != 0)
            return bridge.HasGearsetForJob(settings.CombatJobId) ? settings.CombatJobId : 0;

        uint best = 0;
        var bestLevel = -1;
        foreach (var job in CombatJobs.All)
        {
            if (!bridge.HasGearsetForJob(job))
                continue;

            var level = known.LevelOf(job);
            if (level > bestLevel)
            {
                best = job;
                bestLevel = level;
            }
        }

        return best;
    }

    public IEnumerable<string> Describe()
    {
        var job = ChooseCombatJob();
        yield return $"Hunt source: hunting {(settings.HuntingEnabled ? "on" : "off")}, driver {driver.Name} " +
                     $"(available {driver.IsAvailable}), job {job} " +
                     $"(level {capabilities().LevelOf(job)}, +{settings.HuntMaxLevelAbove} allowance), " +
                     $"retreat below {settings.HuntRetreatHpPercent}% HP, " +
                     $"{(settings.HuntSkipMobsTargetedByOthers ? "leaves" : "takes")} monsters other players are fighting.";
        foreach (var line in database.Describe())
            yield return "  " + line;
    }

    /// <summary>
    /// The best monster for the item the character may actually fight: the
    /// database's ranking (remembered spot, then a reachable zone, then the
    /// rest), filtered to the zones a teleport reaches and the levels the job
    /// can take. A job level of 0 means "not read yet" and allows everything,
    /// so a fresh session never plans a hunt away.
    /// </summary>
    private MobDrop? BestDrop(uint itemId, uint job)
    {
        var level = capabilities().LevelOf(job);
        var allowance = level <= 0 ? int.MaxValue : level + Math.Max(0, settings.HuntMaxLevelAbove);
        var here = bridge.CurrentTerritoryId;
        return database.DropsOf(itemId)
            .FirstOrDefault(drop =>
                // Level 0 is Garland's "??" — a boss or an instance monster.
                // Never plan a fight against something whose level is unknown.
                drop.Level > 0
                && drop.Level <= allowance
                && (drop.TerritoryId == here || bridge.CanTeleportTo(drop.TerritoryId)));
    }
}
