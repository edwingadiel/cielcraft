using System;
using System.Collections.Generic;
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
        var seen = new HashSet<(uint Item, uint Base, uint Territory)>();
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
                if (!seen.Add((itemId, baseId, territory)))
                    continue;

                if (!itemToLocations.TryGetValue(itemId, out var list))
                    itemToLocations[itemId] = list = [];

                list.Add(location with { ItemId = itemId });
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
