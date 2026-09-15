using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Where a summoning bell can be rung (roadmap 7.17). Position is null unless
/// a bell is in the object table right now — the bells are not in the Level
/// sheet (see <see cref="RetainerDatabase.NearestBell"/>), so an out-of-zone
/// site only knows the territory to teleport to.
/// </summary>
public sealed record SummoningBellSite(uint TerritoryId, string PlaceName, System.Numerics.Vector3? Position, ulong ObjectId = 0);

/// <summary>
/// Retainer facts from the sheets and the client (roadmap 7.17): who the
/// character's retainers are and what they hold, which venture brings an item
/// and how much, and where the nearest summoning bell is. Cached per session
/// like <see cref="GatheringDatabase"/>.
/// </summary>
public sealed class RetainerDatabase
{
    /// <summary>Retainer pages, in the order <c>InventoryType.RetainerPage1…7</c> is laid out.</summary>
    public const int RetainerPageCount = 7;

    private readonly IGameBridge gameBridge;

    private Dictionary<uint, List<VentureOption>>? ventureByItem;
    private List<(uint TerritoryId, string PlaceName)>? bellTerritories;

    /// <summary>Session cache; static because the bridge's object-table scan needs it without holding a database.</summary>
    private static HashSet<uint>? bellDataIds;

    public RetainerDatabase(IGameBridge gameBridge)
    {
        this.gameBridge = gameBridge;
    }

    /// <summary>The character's retainers as the client last loaded them; empty when the list is not ready.</summary>
    public IReadOnlyList<RetainerSnapshot> Retainers() => gameBridge.GetRetainers();

    /// <summary>
    /// How many of the item the retainers hold, from the cached retainer
    /// pages. A retainer the character has not visited this session reports
    /// zero — the count is awareness, never a promise.
    /// </summary>
    public int HeldBy(int retainerIndex, uint itemId) => gameBridge.GetRetainerItemCount(retainerIndex, itemId);

    /// <summary>The retainer holding the most of the item, with the count; null when none holds any.</summary>
    public (RetainerSnapshot Retainer, int Count)? BestHolder(uint itemId)
    {
        (RetainerSnapshot Retainer, int Count)? best = null;
        foreach (var retainer in Retainers())
        {
            var count = HeldBy(retainer.Index, itemId);
            if (count > 0 && (best == null || count > best.Value.Count))
                best = (retainer, count);
        }

        return best;
    }

    // ------------------------------------------------------------ ventures

    /// <summary>Every venture that brings the item, cheapest retainer level first; empty when no venture does.</summary>
    public IReadOnlyList<VentureOption> VenturesFor(uint itemId)
    {
        EnsureVentures();
        return ventureByItem!.TryGetValue(itemId, out var options) ? options : [];
    }

    /// <summary>The venture a specific retainer could be sent on for the item; null when it cannot run any.</summary>
    public VentureOption? VentureFor(RetainerSnapshot retainer, uint itemId)
    {
        foreach (var option in VenturesFor(itemId))
        {
            if (CanRun(retainer, option))
                return option;
        }

        return null;
    }

    /// <summary>The item the venture in flight will bring back; null when the retainer is idle or the venture is unknown.</summary>
    public VentureOption? VentureInFlight(RetainerSnapshot retainer)
    {
        if (!retainer.OnVenture)
            return null;

        EnsureVentures();
        foreach (var options in ventureByItem!.Values)
        {
            foreach (var option in options)
            {
                if (option.TaskId == retainer.VentureId)
                    return option;
            }
        }

        return null;
    }

    /// <summary>
    /// The retainer meets the venture's level and class. The gathering-stat
    /// requirement cannot be checked offline — <c>RetainerManager</c> carries
    /// no perception/gathering field — so a venture above the retainer's stats
    /// is only ruled out by the game when it is assigned.
    /// </summary>
    public static bool CanRun(RetainerSnapshot retainer, VentureOption option) =>
        retainer.Available
        && retainer.Level >= option.RetainerLevel
        && AllowsJob(option.ClassJobCategoryId, retainer.ClassJobId);

    private void EnsureVentures()
    {
        if (ventureByItem != null)
            return;

        ventureByItem = new Dictionary<uint, List<VentureOption>>();

        // RetainerTask.Task points at RetainerTaskNormal (a fixed item) when
        // IsRandom is false and at RetainerTaskRandom (a grab bag) when it is;
        // only the fixed ones can be planned for a material.
        var normals = Plugin.DataManager.GetExcelSheet<RetainerTaskNormal>();
        foreach (var task in Plugin.DataManager.GetExcelSheet<RetainerTask>())
        {
            if (task.IsRandom || task.MaxTimemin == 0)
                continue;

            if (!normals.TryGetRow(task.Task.RowId, out var normal))
                continue;

            var item = normal.Item.ValueNullable;
            if (item == null || item.Value.RowId == 0)
                continue;

            var option = new VentureOption(
                TaskId: task.RowId,
                ItemId: item.Value.RowId,
                ItemName: item.Value.Name.ExtractText(),
                Quantities: normal.Quantity.Select(q => (int)q).ToArray(),
                RetainerLevel: task.RetainerLevel,
                RequiredGathering: task.RequiredGathering,
                RequiredItemLevel: task.RequiredItemLevel,
                Minutes: task.MaxTimemin,
                ClassJobCategoryId: task.ClassJobCategory.RowId);

            if (!ventureByItem.TryGetValue(option.ItemId, out var list))
                ventureByItem[option.ItemId] = list = [];
            list.Add(option);
        }

        foreach (var list in ventureByItem.Values)
            list.Sort((a, b) => a.RetainerLevel.CompareTo(b.RetainerLevel));
    }

    /// <summary>
    /// The ClassJobCategory row admits the retainer's class. Retainers only
    /// ever hold a base combat class or MIN/BTN/FSH, so the switch covers
    /// every id that can turn up here.
    /// </summary>
    private static bool AllowsJob(uint classJobCategoryId, uint classJobId)
    {
        if (!Plugin.DataManager.GetExcelSheet<ClassJobCategory>().TryGetRow(classJobCategoryId, out var category))
            return false;

        return classJobId switch
        {
            1 => category.GLA,
            2 => category.PGL,
            3 => category.MRD,
            4 => category.LNC,
            5 => category.ARC,
            6 => category.CNJ,
            7 => category.THM,
            16 => category.MIN,
            17 => category.BTN,
            18 => category.FSH,
            26 => category.ACN,
            29 => category.ROG,
            _ => category.ADV,
        };
    }

    // --------------------------------------------------------------- bells

    /// <summary>
    /// EObj row ids whose EObjName is "summoning bell", read from the sheets
    /// so a patch that adds a bell needs no code change. The bridge matches
    /// the object table's DataId against this set.
    /// </summary>
    public static IReadOnlyCollection<uint> BellDataIds()
    {
        if (bellDataIds != null)
            return bellDataIds;

        bellDataIds = [];
        foreach (var name in Plugin.DataManager.GetExcelSheet<EObjName>())
        {
            if (name.Singular.ExtractText().Contains("summoning bell", StringComparison.OrdinalIgnoreCase))
                bellDataIds.Add(name.RowId);
        }

        return bellDataIds;
    }

    /// <summary>
    /// Where to ring a bell from where the character stands. The summoning
    /// bells are *not* in the Level sheet — every one of them is placed by the
    /// zone's own layout data, and all twelve EObj rows share the same event
    /// handler — so there are no bundled coordinates: a bell in the object
    /// table gives its live position, and otherwise only the territory of a
    /// city that has one is known and the object table takes over after the
    /// teleport. Null when no bell is reachable at all.
    /// </summary>
    public SummoningBellSite? NearestBell(uint territoryId)
    {
        // Already in a zone with a bell in range of the object table: use it.
        if (gameBridge.FindSummoningBell() is { } bell)
            return new SummoningBellSite(territoryId, GatheringDatabase.GetTerritoryName(territoryId), bell.Position, bell.ObjectId);

        foreach (var candidate in BellCandidates())
        {
            if (candidate.TerritoryId != territoryId)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// City zones with a bell, in teleport-preference order. Attunement is
    /// not checked here: <see cref="IGameBridge.TeleportToTerritory"/> already
    /// answers false for a zone the character cannot reach, so the run walks
    /// this list until one is accepted.
    /// </summary>
    public IReadOnlyList<SummoningBellSite> BellCandidates() =>
        BellTerritories().Select(b => new SummoningBellSite(b.TerritoryId, b.PlaceName, null)).ToList();

    /// <summary>
    /// City zones that have a summoning bell by the aetheryte, resolved from
    /// the TerritoryType sheet by place name so no row ids are hard-coded.
    /// Ordered by expansion, which also puts the three starting cities — the
    /// ones every character can teleport to — first. Several of these names
    /// also belong to instanced or cutscene copies of the zone; the lowest
    /// row id is the live one, which is the one the sheet scan keeps.
    /// </summary>
    private static readonly string[] BellCityPlaceNames =
    [
        "Limsa Lominsa Lower Decks",
        "New Gridania",
        "Ul'dah - Steps of Nald",
        "Foundation",
        "Idyllshire",
        "Rhalgr's Reach",
        "Kugane",
        "The Crystarium",
        "Eulmore",
        "Old Sharlayan",
        "Radz-at-Han",
        "Tuliyollal",
        "Solution Nine",
    ];

    private IReadOnlyList<(uint TerritoryId, string PlaceName)> BellTerritories()
    {
        if (bellTerritories != null)
            return bellTerritories;

        bellTerritories = [];
        var wanted = BellCityPlaceNames.ToList();
        foreach (var territory in Plugin.DataManager.GetExcelSheet<TerritoryType>())
        {
            var place = territory.PlaceName.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrEmpty(place))
                continue;

            var rank = wanted.FindIndex(w => string.Equals(w, place, StringComparison.OrdinalIgnoreCase));
            if (rank < 0 || bellTerritories.Any(b => b.PlaceName == place))
                continue;

            bellTerritories.Add((territory.RowId, place));
        }

        bellTerritories.Sort((a, b) =>
            wanted.FindIndex(w => string.Equals(w, a.PlaceName, StringComparison.OrdinalIgnoreCase))
                .CompareTo(wanted.FindIndex(w => string.Equals(w, b.PlaceName, StringComparison.OrdinalIgnoreCase))));
        return bellTerritories;
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        var retainers = Retainers();
        yield return $"Retainers: {(retainers.Count == 0 ? "none loaded" : string.Join("; ", retainers.Select(DescribeRetainer)))}";
        EnsureVentures();
        yield return $"Ventures indexed for {ventureByItem!.Count} items; bell EObj ids [{string.Join(", ", BellDataIds())}]";
        yield return $"Bell cities: {string.Join(", ", BellTerritories().Select(b => $"{b.PlaceName} ({b.TerritoryId})"))}";
    }

    private string DescribeRetainer(RetainerSnapshot retainer)
    {
        var venture = VentureInFlight(retainer);
        var state = !retainer.OnVenture ? "idle"
            : retainer.VentureCompleteAt is { } due
                ? $"{venture?.ItemName ?? $"venture {retainer.VentureId}"} due {due:HH:mm}Z"
                : $"venture {retainer.VentureId}";
        return $"{retainer.Index}:{retainer.Name} job {retainer.ClassJobId} lv{retainer.Level} {retainer.ItemCount} items, {state}";
    }
}
