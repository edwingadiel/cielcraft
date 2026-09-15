using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// <see cref="IZoneDirectory"/> over the game sheets (roadmap 7.5). Kept out
/// of CombatDatabase.cs so the database itself stays Dalamud-free and compiles
/// straight into the tests, the way ShopDatabase and VendorSource are split.
///
/// A place name can belong to several territories (the open-world zone, its
/// instanced copies, a PvP variant); the open world wins, then a town, then
/// the lowest row - which is the only kind of zone a hunt can walk into
/// anyway. Verified 2026-09-15 against the sheets: TerritoryIntendedUse 1 is
/// the open world ("South Shroud" -> 153, "Western Thanalan" -> 140), 0 a
/// town, 3 a dungeon.
/// </summary>
public sealed class SheetZoneDirectory : IZoneDirectory
{
    /// <summary>TerritoryIntendedUse rows: 0 = town, 1 = open world (read from the sheet, 2026-09-15).</summary>
    private const uint OpenWorldUse = 1;
    private const uint TownUse = 0;

    private Dictionary<string, uint>? byName;

    public uint TerritoryOf(string placeName)
    {
        if (string.IsNullOrWhiteSpace(placeName))
            return 0;

        EnsureIndex();
        return byName!.GetValueOrDefault(Normalize(placeName));
    }

    public string NameOf(uint territoryId) => GatheringDatabase.GetTerritoryName(territoryId);

    public MapBounds? BoundsOf(uint territoryId)
    {
        if (territoryId == 0 || !Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory))
            return null;

        var map = territory.Map.ValueNullable;
        if (map == null || map.Value.SizeFactor == 0)
            return null;

        // The standard map transform: value -> (value + offset) * SizeFactor/100,
        // where the image covers [-1024, 1024] of the scaled axis. That box is
        // the whole map image, not the walkable area; the sweep snaps every
        // point it picks to the navmesh and skips the ones with no floor.
        var half = 1024f * 100f / map.Value.SizeFactor;
        float centerX = -map.Value.OffsetX;
        float centerZ = -map.Value.OffsetY;
        return new MapBounds(centerX - half, centerZ - half, centerX + half, centerZ + half);
    }

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    private void EnsureIndex()
    {
        if (byName != null)
            return;

        byName = new Dictionary<string, uint>();
        var ranks = new Dictionary<string, int>();
        foreach (var territory in Plugin.DataManager.GetExcelSheet<TerritoryType>())
        {
            var place = territory.PlaceName.ValueNullable;
            if (place == null)
                continue;

            var name = place.Value.Name.ExtractText();
            if (name.Length == 0)
                continue;

            var use = territory.TerritoryIntendedUse.RowId;
            var rank = use == OpenWorldUse ? 0 : use == TownUse ? 1 : 2;
            var key = Normalize(name);
            if (byName.TryGetValue(key, out _) && ranks[key] <= rank)
                continue;

            byName[key] = territory.RowId;
            ranks[key] = rank;
        }
    }
}
