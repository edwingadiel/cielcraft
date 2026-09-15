using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using CielCraft.Core;

namespace CielCraft.Game;

/// <summary>
/// One line of the bundled drop table (roadmap 7.5): a monster that drops an
/// item, as <c>tools/refresh-drops.ps1</c> wrote it. The zone is a place name
/// rather than a territory id so a patch cannot rot the bundle; the database
/// resolves it against <c>TerritoryType</c> on load.
/// </summary>
public sealed record DropTableMob(uint BNpcNameId, string Name, int Level, string Zone);

/// <summary>
/// The world XZ box a zone's map covers (roadmap 7.5), from the <c>Map</c>
/// sheet: a map image spans 2048 / (SizeFactor / 100) units in each axis,
/// centred on the negated offsets. It is the outer bound of a zone sweep, not
/// the walkable area — the sweep snaps every point to the navmesh anyway.
/// </summary>
public sealed record MapBounds(float MinX, float MinZ, float MaxX, float MaxZ)
{
    public Vector3 Center => new((MinX + MaxX) / 2f, 0f, (MinZ + MaxZ) / 2f);

    public float Width => MaxX - MinX;

    public float Depth => MaxZ - MinZ;

    public bool Contains(Vector3 point) =>
        point.X >= MinX && point.X <= MaxX && point.Z >= MinZ && point.Z <= MaxZ;
}

/// <summary>
/// The sheet lookups the combat database needs, behind an interface so the
/// ranking can be tested without the game's data files (the same trick the
/// vendor source plays with <c>IVendorDirectory</c>).
/// </summary>
public interface IZoneDirectory
{
    /// <summary>TerritoryType row for a place name ("South Shroud"); 0 when no zone carries it.</summary>
    uint TerritoryOf(string placeName);

    /// <summary>Place name of a territory; "zone N" when unknown.</summary>
    string NameOf(uint territoryId);

    /// <summary>The territory's map box, or null when it has no map.</summary>
    MapBounds? BoundsOf(uint territoryId);
}

/// <summary>
/// Where to hunt an item (roadmap 7.5): the bundled Garland Tools drop table
/// plus the spots the character actually found monsters at, remembered in the
/// configuration. Implements <see cref="IHuntSpots"/> for the combat source
/// and the Hunting panel.
///
/// Ranking, as the M4 contract asks: a remembered spot first (the hunt then
/// teleports straight to it), then the lowest-level monster in a zone with an
/// attuned aetheryte, then anything else. Monsters whose zone does not resolve
/// to a territory are dropped — a hunt could not travel there.
/// </summary>
public sealed class CombatDatabase : IHuntSpots
{
    /// <summary>Remembered spots are merged when they are this close; farther apart they are separate camps.</summary>
    public const float SameSpotRange = 40f;

    /// <summary>Remembered spots kept per monster and territory; the oldest is dropped beyond this.</summary>
    public const int MaxRememberedSpots = 400;

    private readonly IZoneDirectory zones;
    private readonly AutomationSettings settings;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly Func<uint, bool> canTeleportTo;
    private readonly Func<uint> currentTerritoryId;
    private readonly System.Action? saveSettings;
    private readonly IReadOnlyDictionary<uint, IReadOnlyList<DropTableMob>> table;

    private readonly string loadedFrom;
    private int unresolvedZones;

    public CombatDatabase(
        IReadOnlyDictionary<uint, IReadOnlyList<DropTableMob>> dropTable,
        IZoneDirectory zones,
        AutomationSettings settings,
        ILog log,
        IClock clock,
        Func<uint, bool> canTeleportTo,
        Func<uint> currentTerritoryId,
        System.Action? saveSettings = null,
        string loadedFrom = "(in memory)")
    {
        table = dropTable;
        this.zones = zones;
        this.settings = settings;
        this.log = log;
        this.clock = clock;
        this.canTeleportTo = canTeleportTo;
        this.currentTerritoryId = currentTerritoryId;
        this.saveSettings = saveSettings;
        this.loadedFrom = loadedFrom;
    }

    /// <summary>Items the bundled table knows a monster for.</summary>
    public int ItemCount => table.Count;

    /// <summary>
    /// Reads the bundled <c>Data/drops.json</c> next to the plugin assembly
    /// (<c>tools/refresh-drops.ps1</c> writes it). A missing or broken file is
    /// logged and leaves an empty database, which simply never offers a hunt.
    /// </summary>
    public static CombatDatabase FromBundle(
        string? assemblyDirectory,
        IZoneDirectory zones,
        AutomationSettings settings,
        ILog log,
        IClock clock,
        Func<uint, bool> canTeleportTo,
        Func<uint> currentTerritoryId,
        System.Action? saveSettings = null)
    {
        var path = Path.Combine(assemblyDirectory ?? ".", "Data", "drops.json");
        var (loaded, description) = LoadTable(path, log);
        return new CombatDatabase(loaded, zones, settings, log, clock, canTeleportTo, currentTerritoryId, saveSettings, description);
    }

    /// <summary>Parses a drop table file; an empty table and the reason when it cannot be read.</summary>
    public static (IReadOnlyDictionary<uint, IReadOnlyList<DropTableMob>> Table, string Source) LoadTable(string path, ILog log)
    {
        var empty = new Dictionary<uint, IReadOnlyList<DropTableMob>>();
        if (!File.Exists(path))
        {
            log.Warning($"[Hunt] No drop table at {path}; hunting will offer nothing. Run tools/refresh-drops.ps1.");
            return (empty, $"missing ({path})");
        }

        try
        {
            using var stream = File.OpenRead(path);
            var entries = JsonSerializer.Deserialize<List<DropFileEntry>>(stream, JsonOptions) ?? [];
            var byItem = new Dictionary<uint, IReadOnlyList<DropTableMob>>();
            foreach (var entry in entries)
            {
                if (entry.Item == 0 || entry.Mobs == null || entry.Mobs.Count == 0)
                    continue;

                var mobs = new List<DropTableMob>();
                foreach (var mob in entry.Mobs)
                {
                    if (mob.Bnpc == 0 || string.IsNullOrWhiteSpace(mob.Zone))
                        continue;

                    mobs.Add(new DropTableMob(mob.Bnpc, mob.Name ?? "", mob.Level, mob.Zone));
                }

                if (mobs.Count > 0)
                    byItem[entry.Item] = mobs;
            }

            log.Information($"[Hunt] Drop table: {byItem.Count} items from {Path.GetFileName(path)}.");
            return (byItem, $"{path} ({byItem.Count} items)");
        }
        catch (Exception e)
        {
            log.Error($"[Hunt] Could not read the drop table at {path}: {e.Message}");
            return (empty, $"unreadable ({e.Message})");
        }
    }

    /// <summary>
    /// Every known monster for the item, best first. Recomputed per call: the
    /// ranking depends on where the character stands and what is attuned.
    /// </summary>
    public IReadOnlyList<MobDrop> DropsOf(uint itemId)
    {
        if (!table.TryGetValue(itemId, out var rows))
            return [];

        var here = currentTerritoryId();
        var drops = new List<MobDrop>();
        foreach (var row in rows)
        {
            var territory = zones.TerritoryOf(row.Zone);
            if (territory == 0)
            {
                unresolvedZones++;
                continue;
            }

            var spot = BestSpotFor(row.BNpcNameId, territory);
            drops.Add(new MobDrop(
                itemId, row.BNpcNameId, row.Name, row.Level, territory, zones.NameOf(territory), spot?.Position));
        }

        // Rank: a remembered spot, then a zone the character can reach, then
        // the rest; within a rank the lowest-level monster (safest fight),
        // then the current zone, then a stable id order.
        return drops
            .OrderBy(Rank)
            .ThenBy(d => SortLevel(d.Level))
            .ThenByDescending(d => d.TerritoryId == here)
            .ThenBy(d => d.BNpcNameId)
            .ToList();
    }

    /// <summary>The remembered spot the database would send a hunt to, or null.</summary>
    public HuntSpot? BestSpotFor(uint bnpcNameId, uint territoryId)
    {
        HuntSpot? best = null;
        foreach (var spot in settings.HuntSpots)
        {
            if (spot.BNpcNameId != bnpcNameId || spot.TerritoryId != territoryId)
                continue;

            if (best == null || spot.Hits > best.Hits
                || (spot.Hits == best.Hits && spot.RememberedAtUtc > best.RememberedAtUtc))
                best = spot;
        }

        return best;
    }

    /// <summary>
    /// Remembers where a monster was actually found (roadmap 7.5). A spot
    /// within <see cref="SameSpotRange"/> of a known one is the same camp:
    /// its hit count goes up and its position is refreshed rather than the
    /// list growing a near-duplicate.
    /// </summary>
    public void RememberSpot(uint bnpcNameId, uint territoryId, Vector3 position)
    {
        if (bnpcNameId == 0 || territoryId == 0)
            return;

        foreach (var known in settings.HuntSpots)
        {
            if (known.BNpcNameId != bnpcNameId || known.TerritoryId != territoryId)
                continue;

            if (Vector3.Distance(known.Position, position) > SameSpotRange)
                continue;

            known.X = position.X;
            known.Y = position.Y;
            known.Z = position.Z;
            known.Hits++;
            known.RememberedAtUtc = clock.UtcNow;
            saveSettings?.Invoke();
            return;
        }

        settings.HuntSpots.Add(new HuntSpot
        {
            BNpcNameId = bnpcNameId,
            TerritoryId = territoryId,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            Hits = 1,
            RememberedAtUtc = clock.UtcNow,
        });

        // The list is a cache, not a record: drop the least useful entry
        // (fewest hits, then oldest) rather than letting it grow for ever.
        while (settings.HuntSpots.Count > MaxRememberedSpots)
        {
            var worst = settings.HuntSpots
                .OrderBy(s => s.Hits)
                .ThenBy(s => s.RememberedAtUtc)
                .First();
            settings.HuntSpots.Remove(worst);
        }

        log.Information($"[Hunt] Remembered a spot for monster {bnpcNameId} in {zones.NameOf(territoryId)}.");
        saveSettings?.Invoke();
    }

    /// <summary>The territory's map box for a zone sweep; null when the zone has no map.</summary>
    public MapBounds? BoundsOf(uint territoryId) => zones.BoundsOf(territoryId);

    public IEnumerable<string> Describe()
    {
        yield return $"Combat database: {table.Count} items with a known drop, source {loadedFrom}; " +
                     $"{settings.HuntSpots.Count} remembered spots; {unresolvedZones} zone lookups unresolved.";

        foreach (var spot in settings.HuntSpots.OrderByDescending(s => s.RememberedAtUtc).Take(10))
        {
            yield return $"  spot: monster {spot.BNpcNameId} in {zones.NameOf(spot.TerritoryId)} " +
                         $"at {spot.X:F0}, {spot.Y:F0}, {spot.Z:F0} ({spot.Hits} hits, {spot.RememberedAtUtc:yyyy-MM-dd HH:mm}Z)";
        }
    }

    /// <summary>
    /// Garland shows "??" for the level of instance and boss monsters, which
    /// the refresh script writes as 0. Unknown is not "lowest": such a
    /// monster sorts last, and the hunt source refuses it outright
    /// (<see cref="MobDrop.Level"/> 0 = do not pick a fight blind).
    /// </summary>
    public static int SortLevel(int level) => level <= 0 ? int.MaxValue : level;

    /// <summary>A remembered spot beats reachability, which beats anything else.</summary>
    private int Rank(MobDrop drop)
    {
        if (drop.Position != null)
            return 0;

        return drop.TerritoryId == currentTerritoryId() || canTeleportTo(drop.TerritoryId) ? 1 : 2;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The shape <c>tools/refresh-drops.ps1</c> writes.</summary>
    private sealed class DropFileEntry
    {
        public uint Item { get; set; }

        public List<DropFileMob>? Mobs { get; set; }
    }

    private sealed class DropFileMob
    {
        public uint Bnpc { get; set; }

        public string? Name { get; set; }

        public int Level { get; set; }

        public string? Zone { get; set; }
    }
}
