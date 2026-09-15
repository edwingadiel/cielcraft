using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Fishing;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Reads the fishing sheets through Dalamud's data manager (roadmap 7.4). The
/// only Dalamud-bound half of the fishing database; everything that decides
/// anything lives in <see cref="FishingDatabase"/> and is tested with fake rows.
/// Sheets read on 2026-09-15: <c>FishingSpot</c>, <c>FishParameter</c>,
/// <c>FishingNoteInfo</c>, <c>SpearfishingItem</c>, <c>FishingBaitParameter</c>.
/// </summary>
public sealed class FishingSheetReader : IFishingSheetReader
{
    /// <summary>Fishing tackle; a <c>FishingBaitParameter</c> row of any other category is a fish used as mooch bait.</summary>
    private const uint FishingTackleCategory = 33;

    public IEnumerable<FishingSpotRow> ReadSpots()
    {
        var territories = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<FishingSpot>())
        {
            var territory = row.TerritoryType.RowId;
            if (territory <= 1)
                continue;

            var items = new List<uint>();
            foreach (var item in row.Item)
            {
                if (item.RowId != 0)
                    items.Add(item.RowId);
            }

            if (items.Count == 0)
                continue;

            var name = row.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            ushort scale = 100;
            short offsetX = 0;
            short offsetY = 0;
            if (territories.TryGetRow(territory, out var territoryRow) && territoryRow.Map.IsValid)
            {
                var map = territoryRow.Map.Value;
                scale = map.SizeFactor;
                offsetX = map.OffsetX;
                offsetY = map.OffsetY;
            }

            yield return new FishingSpotRow(
                row.RowId,
                name,
                territory,
                // FishingSpot stores map-marker coordinates, not world ones:
                // marker = (world + offset) * sizeFactor/100 + 1024 (the usual
                // map packing). Checked against ExportedGatheringPoint's world
                // X/Y in the same zones on 2026-09-15 — a zone's fishing holes
                // only spread out over the map once the 1024 is taken off.
                new Vector3(ToWorld(row.X, scale, offsetX), 0f, ToWorld(row.Z, scale, offsetY)),
                row.Radius * 100f / (scale == 0 ? 100 : scale),
                row.GatheringLevel,
                row.FishingSpotCategory,
                row.Rare,
                items);
        }
    }

    private static float ToWorld(int marker, ushort sizeFactor, short offset)
    {
        var scale = (sizeFactor == 0 ? 100 : sizeFactor) / 100f;
        return (marker - 1024f) / scale - offset;
    }

    public IEnumerable<FishRow> ReadFish()
    {
        // FishingNoteInfo carries the restriction flags per fish; FishParameter
        // the log text. Neither carries an ET window or a weather list — the
        // sheets only say *that* a fish is restricted, not when.
        var notes = new Dictionary<uint, (bool Time, bool Weather, bool Special, bool Collectable)>();
        foreach (var note in Plugin.DataManager.GetExcelSheet<FishingNoteInfo>())
        {
            if (note.Item.RowId != 0)
            {
                notes.TryAdd(
                    note.Item.RowId,
                    (note.TimeRestriction != 0, note.WeatherRestriction != 0, note.SpecialConditions != 0, note.IsCollectable != 0));
            }
        }

        var items = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<FishParameter>())
        {
            var itemId = row.Item.RowId;
            if (itemId == 0)
                continue;

            var flags = notes.GetValueOrDefault(itemId);
            yield return new FishRow(
                itemId,
                items.TryGetRow(itemId, out var item) ? item.Name.ExtractText() : $"item {itemId}",
                row.Text.ExtractText(),
                flags.Time,
                flags.Weather,
                flags.Special,
                flags.Collectable);
        }
    }

    public IEnumerable<SpearfishRow> ReadSpearfish()
    {
        foreach (var row in Plugin.DataManager.GetExcelSheet<SpearfishingItem>())
        {
            if (row.Item.RowId == 0)
                continue;

            yield return new SpearfishRow(
                row.Item.RowId,
                row.Item.ValueNullable?.Name.ExtractText() ?? $"item {row.Item.RowId}",
                row.TerritoryType.RowId);
        }
    }

    /// <summary>
    /// The fishing action catalogue resolved from the Action sheet by English
    /// name (the gathering catalogue's rule, roadmap 7.14): a patch that
    /// renumbers an action is picked up, and a name that is gone keeps the
    /// bundled id and is logged once.
    /// </summary>
    public static FishingActionCatalog CreateActionCatalog(ILog log)
    {
        var byName = new Dictionary<string, (uint Id, int Level, int Gp)>();
        try
        {
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>())
            {
                if (!row.IsPlayerAction || row.ClassJob.RowId != GatheringActions.FisherJobId)
                    continue;

                var name = row.Name.ExtractText().Trim().ToLowerInvariant();
                if (name.Length == 0)
                    continue;

                // PrimaryCostType 7 is GP on the DoL actions.
                byName.TryAdd(name, (row.RowId, row.ClassJobLevel, row.PrimaryCostType == 7 ? row.PrimaryCostValue : 0));
            }
        }
        catch (System.Exception e)
        {
            log.Error($"[Fishing] The Action sheet could not be read ({e.Message}); using the bundled fishing action ids.");
        }

        var catalog = new FishingActionCatalog(name =>
            byName.TryGetValue(name.ToLowerInvariant(), out var entry) ? entry : null);

        if (catalog.Unresolved.Count > 0)
        {
            log.Warning(
                $"[Fishing] {catalog.Unresolved.Count} fishing action name(s) not found in the Action sheet; " +
                $"keeping the bundled ids for: {string.Join(", ", catalog.Unresolved)}.");
        }

        return catalog;
    }

    public IEnumerable<BaitRow> ReadBaits()
    {
        foreach (var row in Plugin.DataManager.GetExcelSheet<FishingBaitParameter>())
        {
            if (row.Item.RowId == 0 || row.Item.ValueNullable is not { } item)
                continue;

            yield return new BaitRow(
                row.Item.RowId,
                item.Name.ExtractText(),
                (int)item.LevelItem.RowId,
                item.ItemUICategory.RowId == FishingTackleCategory);
        }
    }
}
