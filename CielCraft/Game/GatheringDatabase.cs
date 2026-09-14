using System.Collections.Generic;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>Where an item can be gathered: territory plus approximate node-area center (spec §33/§35).</summary>
public sealed record GatheringLocation(
    uint ItemId,
    uint JobId,
    byte GatheringLevel,
    uint TerritoryId,
    Vector2 Position,
    float Radius,
    IReadOnlyList<CielCraft.Core.EtWindow> Windows)
{
    public bool IsTimed => Windows.Count > 0;
}

/// <summary>
/// Which gathering job collects an item, from game data (spec §33): the
/// GatheringPointBase sheet links gathering types to GatheringItem entries.
/// Fishing is out of scope (spec §32).
/// </summary>
public sealed class GatheringDatabase
{
    public const uint MinerJobId = 16;
    public const uint BotanistJobId = 17;

    private Dictionary<uint, uint>? itemToJob;
    private Dictionary<uint, GatheringLocation>? itemToLocation;

    /// <summary>ClassJob row id (16 = MIN, 17 = BTN) that gathers the item; null when not gatherable.</summary>
    public uint? GetGatheringJob(uint itemId)
    {
        EnsureIndex();
        return itemToJob!.TryGetValue(itemId, out var job) ? job : null;
    }

    /// <summary>Lowest-level known node area for the item; null when unknown.</summary>
    public GatheringLocation? FindLocation(uint itemId)
    {
        EnsureIndex();
        return itemToLocation!.GetValueOrDefault(itemId);
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
        // the base row) gives approximate world X/Z and radius.
        itemToLocation = new Dictionary<uint, GatheringLocation>();
        var exported = Plugin.DataManager.GetExcelSheet<ExportedGatheringPoint>();
        var transients = Plugin.DataManager.GetExcelSheet<GatheringPointTransient>();
        foreach (var point in Plugin.DataManager.GetExcelSheet<GatheringPoint>())
        {
            var baseId = point.GatheringPointBase.RowId;
            var territory = point.TerritoryType.RowId;
            if (territory <= 1 || !baseInfo.TryGetValue(baseId, out var info))
                continue;

            if (!exported.TryGetRow(baseId, out var coords) || (coords.X == 0 && coords.Y == 0))
                continue;

            var location = new GatheringLocation(
                0, info.Job, info.Level, territory, new Vector2(coords.X, coords.Y), coords.Radius,
                ReadTimeWindows(transients, point.RowId));

            foreach (var itemId in info.Items)
            {
                // Untimed sources beat timed ones; within the same kind, the
                // lowest gathering level wins.
                if (!itemToLocation.TryGetValue(itemId, out var existing)
                    || (existing.IsTimed && !location.IsTimed)
                    || (existing.IsTimed == location.IsTimed && info.Level < existing.GatheringLevel))
                    itemToLocation[itemId] = location with { ItemId = itemId };
            }
        }
    }

    /// <summary>ET windows for a gathering point; empty = always up (spec §38).</summary>
    private static IReadOnlyList<CielCraft.Core.EtWindow> ReadTimeWindows(
        Lumina.Excel.ExcelSheet<GatheringPointTransient> transients, uint gatheringPointId)
    {
        if (!transients.TryGetRow(gatheringPointId, out var transient))
            return [];

        var windows = new List<CielCraft.Core.EtWindow>();

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
            }
        }

        // Ephemeral nodes: a single start/end pair.
        if (transient.EphemeralStartTime < 2400 && transient.EphemeralEndTime < 2400
            && transient.EphemeralStartTime != transient.EphemeralEndTime)
        {
            var start = CielCraft.Core.EorzeaClock.FromHhmm(transient.EphemeralStartTime);
            var end = CielCraft.Core.EorzeaClock.FromHhmm(transient.EphemeralEndTime);
            windows.Add(new CielCraft.Core.EtWindow(start, ((end - start) % 1440 + 1440) % 1440));
        }

        return windows;
    }
}
