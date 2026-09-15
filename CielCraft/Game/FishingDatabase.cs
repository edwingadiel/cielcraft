using System;
using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;

namespace CielCraft.Game;

/// <summary>
/// One <c>FishingSpot</c> row as the reader hands it over: world position
/// already unpacked from the sheet's map-marker coordinates, the fish the
/// spot holds, and the spot's gathering level.
/// </summary>
public sealed record FishingSpotRow(
    uint SpotId,
    string Name,
    uint TerritoryId,
    Vector3 Position,
    float Radius,
    byte GatheringLevel,
    byte Category,
    bool Rare,
    IReadOnlyList<uint> ItemIds);

/// <summary>
/// One fish as the sheets describe it: the <c>FishParameter</c> flavour text,
/// and the <c>FishingNoteInfo</c> flags. The sheets carry no ET window or
/// weather list for fishing — only these booleans — so a restricted fish can
/// be named but not scheduled (roadmap 7.4, 7.15).
/// </summary>
public sealed record FishRow(
    uint ItemId,
    string Name,
    string LogText,
    bool TimeRestricted,
    bool WeatherRestricted,
    bool SpecialConditions,
    bool Collectable);

/// <summary>A spearfishing catch: indexed so an order for one can be refused with a reason (roadmap 7.4).</summary>
public sealed record SpearfishRow(uint ItemId, string Name, uint TerritoryId);

/// <summary>A bait item (<c>FishingBaitParameter</c>): tackle, or a fish used as mooch bait.</summary>
public sealed record BaitRow(uint ItemId, string Name, int ItemLevel, bool IsTackle);

/// <summary>
/// The fishing rows the database indexes. Kept behind an interface so the
/// database is testable with fake rows and so the Lumina-backed reader
/// (<see cref="FishingSheetReader"/>) is the only Dalamud-bound part.
/// </summary>
public interface IFishingSheetReader
{
    IEnumerable<FishingSpotRow> ReadSpots();

    IEnumerable<FishRow> ReadFish();

    IEnumerable<SpearfishRow> ReadSpearfish();

    IEnumerable<BaitRow> ReadBaits();
}

/// <summary>Where a fish comes from: the spot to stand at, the bait to apply, and the fish to mooch from when it needs one.</summary>
public sealed record FishingPlan(
    uint ItemId,
    string FishName,
    FishingSpotRow Spot,
    uint BaitItemId,
    string BaitName,
    uint MoochFromItemId,
    bool TimeRestricted,
    bool WeatherRestricted)
{
    public bool NeedsMooch => MoochFromItemId != 0;

    /// <summary>"Lordly Salmon at Mercantile Docks (Yanxia, level 67) with Rat Tail".</summary>
    public string Describe(Func<uint, string> zoneName) =>
        $"{FishName} at {Spot.Name} ({zoneName(Spot.TerritoryId)}, level {Spot.GatheringLevel}) with {BaitName}" +
        (NeedsMooch ? " (mooched)" : "");
}

/// <summary>
/// Bait per fish (roadmap 7.4). <b>The game sheets do not carry it</b>: the
/// only bait data in the EXD is <c>FishingBaitParameter</c>, a flat list of
/// which items *are* bait, and <c>FishParameter</c> holds flavour text only —
/// the fishing log's "bait" line is assembled client-side from data the sheets
/// do not expose. So the pairings below are bundled, taken from the in-game
/// Fishing Log and the community fish databases (GatherBuddy, GarlandTools),
/// and are <b>unverified in game by this plugin</b>. A fish that is not in the
/// table has no known bait: the source refuses to offer it rather than casting
/// blindly, and the controller gives up after
/// <see cref="FishingController.MaxCastsWithoutTarget"/> fruitless casts so a
/// wrong entry costs minutes, not an evening.
/// </summary>
public static class FishingBaitTable
{
    /// <summary>The bait, and the fish it must be mooched from (0 = cast directly).</summary>
    public sealed record Entry(uint BaitItemId, uint MoochFromItemId = 0);

    // Item ids read from the Item sheet on 2026-09-15; the pairings are the
    // bundled part. Keep sorted by fish item id.
    private static readonly Dictionary<uint, Entry> Table = new()
    {
        [4869] = new(2585), // Merlthor Goby      <- Lugworm
        [4874] = new(2585), // Harbor Herring     <- Lugworm
        [4925] = new(2586), // Crayfish           <- Moth Pupa
        [4927] = new(2585), // Striped Goby       <- Lugworm
        [4935] = new(2586), // Brass Loach        <- Moth Pupa
        [4936] = new(2586), // Maiden Carp        <- Moth Pupa
        [4940] = new(2592), // Rainbow Trout      <- Midge Basket
        [4947] = new(2588), // Moat Carp          <- Crayfish Ball
        [4951] = new(2588), // Tricolored Carp    <- Crayfish Ball
        [4955] = new(2592), // Warmwater Trout    <- Midge Basket
        [4958] = new(2591), // Black Eel          <- Rat Tail
        [4967] = new(2592), // Yugr'am Salmon     <- Midge Basket
        [4982] = new(2594), // Velodyna Carp      <- Butterworm
    };

    /// <summary>The bundled bait for a fish, or null when none is known.</summary>
    public static Entry? For(uint fishItemId) => Table.GetValueOrDefault(fishItemId);

    /// <summary>Fish the table covers, for the diagnostic report.</summary>
    public static int Count => Table.Count;
}

/// <summary>
/// Where fish come from (roadmap 7.4): the <c>FishingSpot</c> rows indexed by
/// the fish they hold, the <c>FishParameter</c> / <c>FishingNoteInfo</c> facts
/// about each fish, the bait items, and the spearfishing catches — indexed so
/// an order for one can be refused with a reason instead of hanging. Pure: the
/// sheet rows come from an <see cref="IFishingSheetReader"/>, so the lookups
/// are testable offline.
/// </summary>
public sealed class FishingDatabase
{
    private readonly IFishingSheetReader reader;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly Func<uint, bool> canTeleportTo;
    private readonly IReadOnlyDictionary<uint, FishingBaitChoice> baitOverrides;

    private Dictionary<uint, List<FishingSpotRow>>? fishToSpots;
    private Dictionary<uint, FishRow>? fish;
    private Dictionary<uint, SpearfishRow>? spearfish;
    private Dictionary<uint, BaitRow>? baits;

    /// <summary>
    /// canTeleportTo answers "is an aetheryte in this territory attuned"; the
    /// default says yes to everything, which only reorders the candidate spots
    /// (nothing is ever dropped for it).
    /// </summary>
    public FishingDatabase(
        IFishingSheetReader reader,
        Func<CharacterCapabilities>? capabilities = null,
        Func<uint, bool>? canTeleportTo = null,
        IReadOnlyDictionary<uint, FishingBaitChoice>? baitOverrides = null)
    {
        this.reader = reader;
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
        this.canTeleportTo = canTeleportTo ?? (_ => true);
        // AutomationSettings.FishingBait: the user's own pairings win over the
        // bundled table, which is the only place a wrong one can be corrected.
        this.baitOverrides = baitOverrides ?? new Dictionary<uint, FishingBaitChoice>();
    }

    /// <summary>
    /// How to catch the fish: the user's pairing when there is one, otherwise
    /// the bundled table's; null when neither knows it.
    /// </summary>
    public FishingBaitTable.Entry? BaitFor(uint fishItemId) =>
        baitOverrides.TryGetValue(fishItemId, out var user) && user.BaitItemId != 0
            ? new FishingBaitTable.Entry(user.BaitItemId, user.MoochFromItemId)
            : FishingBaitTable.For(fishItemId);

    /// <summary>The item is caught with a rod at a known fishing hole (what <c>IsGatherable</c> ORs in).</summary>
    public bool IsFish(uint itemId)
    {
        EnsureIndex();
        return fishToSpots!.ContainsKey(itemId);
    }

    /// <summary>The item is a spearfishing catch; spear fishing is not automated (see <see cref="RefusalReason"/>).</summary>
    public bool IsSpearfish(uint itemId)
    {
        EnsureIndex();
        return spearfish!.ContainsKey(itemId);
    }

    /// <summary>The item is a bait (tackle or a mooch fish).</summary>
    public bool IsBait(uint itemId)
    {
        EnsureIndex();
        return baits!.ContainsKey(itemId);
    }

    /// <summary>Display name of a bait; "item N" when the sheets do not know it.</summary>
    public string BaitName(uint itemId)
    {
        EnsureIndex();
        return baits!.TryGetValue(itemId, out var bait) ? bait.Name : $"item {itemId}";
    }

    /// <summary>Everything the sheets say about a fish; null when it is not one.</summary>
    public FishRow? Fish(uint itemId)
    {
        EnsureIndex();
        return fish!.GetValueOrDefault(itemId);
    }

    /// <summary>
    /// Why this item cannot be fished for automatically, or null when it can.
    /// Spear fishing is out of scope (a different minigame with its own addon,
    /// roadmap 7.4); a fish with no bundled bait would be cast for blindly.
    /// </summary>
    public string? RefusalReason(uint itemId)
    {
        EnsureIndex();
        if (spearfish!.TryGetValue(itemId, out var spear) && !fishToSpots!.ContainsKey(itemId))
            return $"{spear.Name} is caught by spear fishing, which CielCraft does not automate (roadmap 7.4)";

        if (!fishToSpots!.ContainsKey(itemId))
            return null; // not a fish at all: not this source's business

        if (BaitFor(itemId) == null)
            return $"no bait is known for {NameOf(itemId)} — the game sheets do not carry bait per fish " +
                   "and it is not in CielCraft's bundled table";

        return FindSpot(itemId) == null
            ? $"no reachable fishing hole is known for {NameOf(itemId)}"
            : null;
    }

    /// <summary>
    /// Where and how to fish for the item; null when no spot is known, no bait
    /// is known, or the character's FSH level is below every spot. Spots are
    /// ranked: the current territory first, then a territory with an attuned
    /// aetheryte, then the lowest spot level (the easiest water).
    /// </summary>
    public FishingPlan? FindSpot(uint itemId, uint currentTerritoryId = 0, int fisherLevel = int.MaxValue)
    {
        EnsureIndex();
        if (!fishToSpots!.TryGetValue(itemId, out var spots) || spots.Count == 0)
            return null;

        if (BaitFor(itemId) is not { } bait)
            return null;

        FishingSpotRow? best = null;
        foreach (var spot in spots)
        {
            if (spot.GatheringLevel > fisherLevel)
                continue;

            if (best == null || IsBetterSpot(spot, best, currentTerritoryId))
                best = spot;
        }

        if (best == null)
            return null;

        var row = fish!.GetValueOrDefault(itemId);
        return new FishingPlan(
            itemId,
            NameOf(itemId),
            best,
            bait.BaitItemId,
            BaitName(bait.BaitItemId),
            bait.MoochFromItemId,
            row?.TimeRestricted ?? false,
            row?.WeatherRestricted ?? false);
    }

    /// <summary>The character's Fisher level, or 0 when the capabilities have not been read yet.</summary>
    public int FisherLevel() => capabilities().LevelOf(GatheringActions.FisherJobId);

    private bool IsBetterSpot(FishingSpotRow candidate, FishingSpotRow existing, uint currentTerritoryId)
    {
        var candidateHere = candidate.TerritoryId == currentTerritoryId;
        var existingHere = existing.TerritoryId == currentTerritoryId;
        if (candidateHere != existingHere)
            return candidateHere;

        var candidateAttuned = canTeleportTo(candidate.TerritoryId);
        var existingAttuned = canTeleportTo(existing.TerritoryId);
        if (candidateAttuned != existingAttuned)
            return candidateAttuned;

        return candidate.GatheringLevel < existing.GatheringLevel;
    }

    private string NameOf(uint itemId)
    {
        EnsureIndex();
        if (fish!.TryGetValue(itemId, out var row) && row.Name.Length > 0)
            return row.Name;
        if (baits!.TryGetValue(itemId, out var bait))
            return bait.Name;
        return $"item {itemId}";
    }

    private void EnsureIndex()
    {
        if (fishToSpots != null)
            return;

        // A sheet the game data cannot hand over must not take the planner with
        // it: the index stays empty, no fish is offered, and the report says so
        // (crafting and gathering stay usable, spec §31's rule for optional data).
        try
        {
            BuildIndex();
        }
        catch (Exception e)
        {
            IndexError = e.Message;
            fish = new Dictionary<uint, FishRow>();
            spearfish = new Dictionary<uint, SpearfishRow>();
            baits = new Dictionary<uint, BaitRow>();
            fishToSpots = new Dictionary<uint, List<FishingSpotRow>>();
        }
    }

    /// <summary>Why the fishing sheets could not be read; empty when they were.</summary>
    public string IndexError { get; private set; } = "";

    private void BuildIndex()
    {
        var spots = new Dictionary<uint, List<FishingSpotRow>>();
        foreach (var spot in reader.ReadSpots())
        {
            // A spot with no place name is an unused / undiscovered row, and one
            // at the origin has no usable position to travel to.
            if (spot.Name.Length == 0 || (spot.Position.X == 0 && spot.Position.Z == 0))
                continue;

            foreach (var itemId in spot.ItemIds)
            {
                if (itemId == 0)
                    continue;

                if (!spots.TryGetValue(itemId, out var list))
                    spots[itemId] = list = [];

                list.Add(spot);
            }
        }

        var fishRows = new Dictionary<uint, FishRow>();
        foreach (var row in reader.ReadFish())
            fishRows.TryAdd(row.ItemId, row);

        var spears = new Dictionary<uint, SpearfishRow>();
        foreach (var row in reader.ReadSpearfish())
            spears.TryAdd(row.ItemId, row);

        var baitRows = new Dictionary<uint, BaitRow>();
        foreach (var row in reader.ReadBaits())
            baitRows.TryAdd(row.ItemId, row);

        fish = fishRows;
        spearfish = spears;
        baits = baitRows;
        fishToSpots = spots;
    }

    /// <summary>Index sizes for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        EnsureIndex();
        yield return $"Fishing database: {fishToSpots!.Count} fish at known holes, {fish!.Count} fish rows, " +
                     $"{spearfish!.Count} spearfishing catches (refused), {baits!.Count} baits, " +
                     $"{FishingBaitTable.Count} bundled bait pairings" +
                     (IndexError.Length > 0 ? $"; THE SHEETS COULD NOT BE READ: {IndexError}" : "");
        yield return $"Fisher level {FisherLevel()}";
    }
}
