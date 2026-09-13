using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

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

    /// <summary>ClassJob row id (16 = MIN, 17 = BTN) that gathers the item; null when not gatherable.</summary>
    public uint? GetGatheringJob(uint itemId)
    {
        EnsureIndex();
        return itemToJob!.TryGetValue(itemId, out var job) ? job : null;
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

        itemToJob = new Dictionary<uint, uint>();
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

            foreach (var entry in point.Item)
            {
                if (entry.RowId != 0 && gatheringItemToItem.TryGetValue(entry.RowId, out var itemId))
                    itemToJob.TryAdd(itemId, job);
            }
        }
    }
}
