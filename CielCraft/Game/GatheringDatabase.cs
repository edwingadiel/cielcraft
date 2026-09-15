using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CielCraft.Core;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Which gathering job collects an item, from game data (spec §33): the
/// GatheringPointBase sheet links gathering types to GatheringItem entries.
/// Fishing is out of scope (spec §32).
/// </summary>
public sealed class GatheringDatabase
{
    public const uint MinerJobId = CielCraft.Core.GatheringActions.MinerJobId;
    public const uint BotanistJobId = CielCraft.Core.GatheringActions.BotanistJobId;

    private readonly Func<CharacterCapabilities> capabilities;
    private Dictionary<uint, uint>? itemToJob;
    private Dictionary<uint, List<GatheringLocation>>? itemToLocations;
    private Dictionary<uint, List<ReductionSource>>? crystalToReductions;

    /// <summary>Elemental crystal item ids (Fire..Water, 8–13); the matching cluster is the crystal id + 6 (14–19).</summary>
    public const uint FirstCrystalItemId = 8;
    public const uint LastCrystalItemId = 13;
    public const uint ClusterOffset = 6;

    /// <summary>
    /// An ephemeral collectable that aetherial reduction turns into crystals
    /// and clusters of one element (roadmap 7.15): the collectable to gather,
    /// where, and the crystal it yields. Read from the sheets: an ephemeral
    /// node lists the element's crystal among its own items and the
    /// reducible ones carry Item.AetherialReduce; the result table itself is
    /// server-side, so the crystal/cluster counts per reduction are not known
    /// here (a High-tier reduction of a level-cap collectable gives a few
    /// clusters plus crystals).
    /// </summary>
    public sealed record ReductionSource(uint CollectableItemId, string CollectableName, uint CrystalItemId, GatheringLocation Location);

    public GatheringDatabase(Func<CharacterCapabilities>? capabilities = null)
    {
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
    }

    /// <summary>ClassJob row id (16 = MIN, 17 = BTN) that gathers the item; null when not gatherable.</summary>
    public uint? GetGatheringJob(uint itemId)
    {
        EnsureIndex();
        return itemToJob!.TryGetValue(itemId, out var job) ? job : null;
    }

    /// <summary>
    /// Best known node area for the item; null when unknown. Picked at query
    /// time so the flight preference follows the current capabilities
    /// (roadmap 7.16): untimed first, then a zone with flight, then the
    /// lowest gathering level.
    /// </summary>
    public GatheringLocation? FindLocation(uint itemId)
    {
        EnsureIndex();
        return itemToLocations!.TryGetValue(itemId, out var candidates)
            ? CapabilityRules.ChooseSource(candidates, capabilities())
            : null;
    }

    /// <summary>Whether the item is an elemental crystal or cluster (the reduction path's targets).</summary>
    public static bool IsCrystalOrCluster(uint itemId) =>
        itemId >= FirstCrystalItemId && itemId <= LastCrystalItemId + ClusterOffset;

    /// <summary>The crystal of a cluster (or the crystal itself); 0 for anything else.</summary>
    public static uint CrystalOf(uint itemId) =>
        itemId >= FirstCrystalItemId && itemId <= LastCrystalItemId ? itemId
        : itemId > LastCrystalItemId && itemId <= LastCrystalItemId + ClusterOffset ? itemId - ClusterOffset
        : 0;

    /// <summary>
    /// The ephemeral collectable to reduce for a crystal or cluster (roadmap
    /// 7.15), preferring the given territory (the per-element crystal spot)
    /// and otherwise the highest-level node, whose collectables reduce to the
    /// most clusters. Within a node the lowest-level reducible item is chosen
    /// so no perception requirement gets in the way. Null when the element
    /// has no ephemeral source.
    /// </summary>
    public ReductionSource? FindReductionSource(uint crystalOrClusterItemId, uint preferredTerritory = 0)
    {
        EnsureIndex();
        var crystal = CrystalOf(crystalOrClusterItemId);
        if (crystal == 0 || !crystalToReductions!.TryGetValue(crystal, out var sources))
            return null;

        ReductionSource? best = null;
        foreach (var source in sources)
        {
            if (best == null
                || (source.Location.TerritoryId == preferredTerritory && best.Location.TerritoryId != preferredTerritory)
                || (best.Location.TerritoryId != preferredTerritory && source.Location.GatheringLevel > best.Location.GatheringLevel))
                best = source;
        }

        return best;
    }

    /// <summary>Every ephemeral reduction source known, for the schedule page and the report.</summary>
    public IReadOnlyList<ReductionSource> AllReductionSources()
    {
        EnsureIndex();
        var all = new List<ReductionSource>();
        foreach (var list in crystalToReductions!.Values)
            all.AddRange(list);
        return all;
    }

    /// <summary>Place name of a territory ("The Dravanian Forelands"); "zone N" when unknown.</summary>
    public static string GetTerritoryName(uint territoryId) =>
        territoryId != 0 && Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory)
            ? territory.PlaceName.Value.Name.ExtractText()
            : $"zone {territoryId}";

    private List<(uint ItemId, string Name)>? gatherableNames;

    /// <summary>
    /// Case-insensitive substring search over the names of items the node
    /// data lists as MIN/BTN gatherable (roadmap 7.1); names that start with
    /// the query first, then shorter names, like the craftable search.
    /// </summary>
    public IReadOnlyList<(uint ItemId, string Name)> SearchGatherable(string query, int maxResults = 10)
    {
        var needle = query.Trim();
        if (needle.Length < 2)
            return [];

        if (gatherableNames == null)
        {
            EnsureIndex();
            gatherableNames = [];
            var items = Plugin.DataManager.GetExcelSheet<Item>();
            foreach (var itemId in itemToJob!.Keys)
            {
                if (items.TryGetRow(itemId, out var item))
                {
                    var name = item.Name.ExtractText();
                    if (name.Length > 0)
                        gatherableNames.Add((itemId, name));
                }
            }
        }

        var results = new List<(uint ItemId, string Name)>();
        foreach (var entry in gatherableNames)
        {
            if (!entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(entry);
            if (results.Count >= maxResults * 4)
                break;
        }

        results.Sort((a, b) =>
        {
            var aStarts = a.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
            var bStarts = b.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
            if (aStarts != bStarts)
                return aStarts ? -1 : 1;

            return a.Name.Length.CompareTo(b.Name.Length);
        });

        return results.Count > maxResults ? results.GetRange(0, maxResults) : results;
    }

    private void EnsureIndex()
    {
        if (itemToJob != null)
            return;

        // GatheringItem row id -> real item id.
        var gatheringItemToItem = new Dictionary<uint, uint>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<GatheringItem>())
        {
            var realItem = row.Item.RowId;
            if (realItem != 0)
                gatheringItemToItem[row.RowId] = realItem;
        }

        // GatheringPointBase row -> (job, level, item ids).
        itemToJob = new Dictionary<uint, uint>();
        var baseInfo = new Dictionary<uint, (uint Job, byte Level, List<uint> Items)>();
        foreach (var point in Plugin.DataManager.GetExcelSheet<GatheringPointBase>())
        {
            // GatheringType: 0 Mining / 1 Quarrying (MIN), 2 Logging / 3 Harvesting (BTN).
            var job = point.GatheringType.RowId switch
            {
                0 or 1 => MinerJobId,
                2 or 3 => BotanistJobId,
                _ => 0u,
            };
            if (job == 0)
                continue;

            var items = new List<uint>();
            foreach (var entry in point.Item)
            {
                if (entry.RowId != 0 && gatheringItemToItem.TryGetValue(entry.RowId, out var itemId))
                {
                    items.Add(itemId);
                    itemToJob.TryAdd(itemId, job);
                }
            }

            if (items.Count > 0)
                baseInfo[point.RowId] = (job, point.GatheringLevel, items);
        }

        // GatheringPoint gives the territory; ExportedGatheringPoint (keyed by
        // the base row) gives approximate world X/Z and radius. Every distinct
        // (base, territory) area is kept per item so the best one can be
        // chosen against the character's capabilities later.
        itemToLocations = new Dictionary<uint, List<GatheringLocation>>();
        crystalToReductions = new Dictionary<uint, List<ReductionSource>>();
        var seen = new HashSet<(uint Item, uint Base, uint Territory)>();
        var seenReductions = new HashSet<(uint Item, uint Base, uint Territory)>();
        var exported = Plugin.DataManager.GetExcelSheet<ExportedGatheringPoint>();
        var transients = Plugin.DataManager.GetExcelSheet<GatheringPointTransient>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var point in Plugin.DataManager.GetExcelSheet<GatheringPoint>())
        {
            var baseId = point.GatheringPointBase.RowId;
            var territory = point.TerritoryType.RowId;
            if (territory <= 1 || !baseInfo.TryGetValue(baseId, out var info))
                continue;

            if (!exported.TryGetRow(baseId, out var coords) || (coords.X == 0 && coords.Y == 0))
                continue;

            var (windows, kind) = ReadTimeWindows(transients, point.RowId, IsFolkloreNode(point));
            var location = new GatheringLocation(
                0, info.Job, info.Level, territory, new Vector2(coords.X, coords.Y), coords.Radius,
                windows, kind);

            foreach (var itemId in info.Items)
            {
                if (!seen.Add((itemId, baseId, territory)))
                    continue;

                if (!itemToLocations.TryGetValue(itemId, out var list))
                    itemToLocations[itemId] = list = [];

                list.Add(location with { ItemId = itemId });
            }

            // Ephemeral node (7.15): the element crystal it lists names the
            // element; every item with AetherialReduce reduces to it. The
            // lowest-level reducible item is the safe pick (no perception gate).
            if (kind != NodeKind.Ephemeral)
                continue;

            var crystal = info.Items.FirstOrDefault(id => id >= FirstCrystalItemId && id <= LastCrystalItemId);
            if (crystal == 0)
                continue;

            uint reducible = 0;
            uint reducibleLevel = uint.MaxValue;
            var reducibleName = "";
            foreach (var itemId in info.Items)
            {
                if (!itemSheet.TryGetRow(itemId, out var item) || item.AetherialReduce == 0)
                    continue;

                if (item.LevelItem.RowId < reducibleLevel)
                {
                    reducible = itemId;
                    reducibleLevel = item.LevelItem.RowId;
                    reducibleName = item.Name.ExtractText();
                }
            }

            if (reducible == 0 || !seenReductions.Add((reducible, baseId, territory)))
                continue;

            if (!crystalToReductions.TryGetValue(crystal, out var reductions))
                crystalToReductions[crystal] = reductions = [];

            reductions.Add(new ReductionSource(reducible, reducibleName, crystal, location with { ItemId = reducible }));
        }
    }

    /// <summary>A legendary (folklore) node: its sub-category names a folklore book (roadmap 7.15).</summary>
    private static bool IsFolkloreNode(GatheringPoint point)
    {
        var sub = point.GatheringSubCategory;
        return sub.RowId != 0 && sub.IsValid
            && sub.Value.FolkloreBook.ExtractText().Contains("Folklore", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ET windows for a gathering point and the node kind they imply; empty =
    /// always up (spec §38). Rare-pop windows make an unspoiled node — a
    /// legendary one when the point's sub-category is a folklore book
    /// (roadmap 7.15) — and ephemeral times an ephemeral one.
    /// </summary>
    private static (IReadOnlyList<CielCraft.Core.EtWindow> Windows, NodeKind Kind) ReadTimeWindows(
        Lumina.Excel.ExcelSheet<GatheringPointTransient> transients, uint gatheringPointId, bool folklore)
    {
        if (!transients.TryGetRow(gatheringPointId, out var transient))
            return ([], NodeKind.Normal);

        var windows = new List<CielCraft.Core.EtWindow>();
        var kind = NodeKind.Normal;

        // Unspoiled/legendary nodes: up to three windows in the pop table.
        var table = transient.GatheringRarePopTimeTable;
        if (table.RowId != 0 && table.IsValid)
        {
            var row = table.Value;
            for (var i = 0; i < row.StartTime.Count; i++)
            {
                int start = row.StartTime[i];
                int duration = row.Duration[i];
                if (start >= 2400 || duration == 0 || duration >= 2400)
                    continue;

                windows.Add(new CielCraft.Core.EtWindow(
                    CielCraft.Core.EorzeaClock.FromHhmm(start),
                    CielCraft.Core.EorzeaClock.FromHhmm(duration)));
                kind = folklore ? NodeKind.Legendary : NodeKind.Unspoiled;
            }
        }

        // Ephemeral nodes: a single start/end pair.
        if (transient.EphemeralStartTime < 2400 && transient.EphemeralEndTime < 2400
            && transient.EphemeralStartTime != transient.EphemeralEndTime)
        {
            var start = CielCraft.Core.EorzeaClock.FromHhmm(transient.EphemeralStartTime);
            var end = CielCraft.Core.EorzeaClock.FromHhmm(transient.EphemeralEndTime);
            windows.Add(new CielCraft.Core.EtWindow(start, ((end - start) % 1440 + 1440) % 1440));
            kind = NodeKind.Ephemeral;
        }

        return (windows, kind);
    }
}
