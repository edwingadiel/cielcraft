using System;
using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Where NPCs stand, from game data (roadmap 7.3): the Level sheet places
/// ENpcResident rows in a territory, and <see cref="ENpcBase.ENpcData"/> says
/// which event handlers an NPC offers — the mender's repair talk among them.
/// The index is built once per session and kept.
/// </summary>
public sealed class NpcDatabase : INpcLocator, IMenderLocator
{
    /// <summary>
    /// Event handler id of "Repair Gear": CustomTalk row 720915 = 0x000B0013
    /// (handler type 11 = CustomTalk, verified in the sheets: MainOption
    /// "Repair Gear", Name "CmnDefNpcRepair_00019"). Every mender carries it in
    /// <see cref="ENpcBase.ENpcData"/>; the "Mender" title in
    /// <see cref="ENpcResident.Title"/> is only a partial marker (248 NPCs
    /// carry the handler, 48 carry the title), so the handler is what this uses.
    /// </summary>
    public const uint RepairEventHandler = 0x000B0013;

    /// <summary>Level rows of this type place an ENpc (verified against known menders' placements).</summary>
    private const byte EventNpcLevelType = 8;

    /// <summary>TerritoryType.TerritoryIntendedUse of a town: an aetheryte is close and nothing attacks on the way.</summary>
    private const uint TownIntendedUse = 0;

    private readonly Func<uint, bool> canTeleportTo;
    private readonly Func<uint, string> territoryName;

    private Dictionary<uint, NpcTarget>? byId;
    private List<NpcTarget>? menders;
    private HashSet<uint>? townTerritories;

    /// <param name="canTeleportTo">
    /// Whether the character has an aetheryte of that territory in the teleport
    /// list; a zone it cannot reach is no candidate. Everything is reachable
    /// when omitted.
    /// </param>
    /// <param name="territoryName">Zone names for log lines; "zone N" when omitted.</param>
    public NpcDatabase(Func<uint, bool>? canTeleportTo = null, Func<uint, string>? territoryName = null)
    {
        this.canTeleportTo = canTeleportTo ?? (_ => true);
        this.territoryName = territoryName ?? GatheringDatabase.GetTerritoryName;
    }

    /// <summary>NPCs the Level sheet places; a resident with no placement (many city NPCs) is not among them.</summary>
    public int PlacedNpcCount
    {
        get
        {
            EnsureIndex();
            return byId!.Count;
        }
    }

    public NpcTarget? Locate(uint npcId)
    {
        EnsureIndex();
        return byId!.TryGetValue(npcId, out var target) ? target : null;
    }

    /// <summary>Every placed NPC that offers the repair talk (roadmap 7.3a).</summary>
    public IReadOnlyList<NpcTarget> FindMenders()
    {
        EnsureIndex();
        return menders!;
    }

    /// <summary>Zone name for log lines and the report.</summary>
    public string TerritoryName(uint id) => territoryName(id);

    /// <summary>
    /// The mender to walk to from here (roadmap 7.3a): one in this very zone
    /// first (nearest by distance), then one in a town whose aetheryte is
    /// attuned — a town mender stands near its aetheryte and nothing fights
    /// back on the way — then any other attuned zone. Null when none is
    /// reachable.
    /// </summary>
    public NpcTarget? NearestMender(uint territoryId, Vector3 position)
    {
        EnsureIndex();
        NpcTarget? best = null;
        var bestRank = (Rank: int.MaxValue, Distance: float.MaxValue, NpcId: uint.MaxValue);
        foreach (var mender in menders!)
        {
            var here = mender.TerritoryId == territoryId;
            if (!here && !canTeleportTo(mender.TerritoryId))
                continue;

            var rank = (
                Rank: here ? 0 : townTerritories!.Contains(mender.TerritoryId) ? 1 : 2,
                Distance: here ? Vector3.Distance(position, mender.Position) : 0f,
                mender.NpcId);
            if (best == null || rank.CompareTo(bestRank) < 0)
            {
                best = mender;
                bestRank = rank;
            }
        }

        return best;
    }

    private void EnsureIndex()
    {
        if (byId != null)
            return;

        // Every NPC whose event handlers include the repair talk.
        var repairNpcs = new HashSet<uint>();
        foreach (var npcBase in Plugin.DataManager.GetExcelSheet<ENpcBase>())
        {
            foreach (var handler in npcBase.ENpcData)
            {
                if (handler.RowId != RepairEventHandler)
                    continue;

                repairNpcs.Add(npcBase.RowId);
                break;
            }
        }

        townTerritories = [];
        foreach (var territory in Plugin.DataManager.GetExcelSheet<TerritoryType>())
        {
            if (territory.RowId > 1 && territory.TerritoryIntendedUse.RowId == TownIntendedUse)
                townTerritories.Add(territory.RowId);
        }

        // Level rows of type 8 place an ENpcResident: Object is the resident's
        // row (which is also the object table's DataId), Territory/X/Y/Z where
        // it stands. The first placement of an NPC wins; the sheet carries no
        // instance information to choose between duplicates.
        var residents = Plugin.DataManager.GetExcelSheet<ENpcResident>();
        var index = new Dictionary<uint, NpcTarget>();
        var found = new List<NpcTarget>();
        foreach (var level in Plugin.DataManager.GetExcelSheet<Level>())
        {
            var npcId = level.Object.RowId;
            var territory = level.Territory.RowId;
            if (level.Type != EventNpcLevelType || npcId == 0 || territory <= 1 || index.ContainsKey(npcId))
                continue;

            var name = residents.TryGetRow(npcId, out var resident) ? resident.Singular.ExtractText() : "";
            if (string.IsNullOrWhiteSpace(name))
                name = $"NPC {npcId}";

            var target = new NpcTarget(npcId, name, territory, new Vector3(level.X, level.Y, level.Z), npcId);
            index[npcId] = target;
            if (repairNpcs.Contains(npcId))
                found.Add(target);
        }

        byId = index;
        menders = found;
        Plugin.Log.Information(
            $"[Npc] Indexed {index.Count} placed NPCs, {found.Count} of them menders (of {repairNpcs.Count} that offer repairs).");
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        EnsureIndex();
        yield return $"NPC index: {byId!.Count} placed NPCs, {menders!.Count} menders, {townTerritories?.Count ?? 0} town territories";
        foreach (var mender in menders!.GetRange(0, Math.Min(5, menders!.Count)))
            yield return $"  mender {mender.NpcId} {mender.Name} in {TerritoryName(mender.TerritoryId)} at {mender.Position.X:F0}, {mender.Position.Y:F0}, {mender.Position.Z:F0}";
    }
}
