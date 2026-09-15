using System.Collections.Generic;
using System.Numerics;

namespace CielCraft.Core;

/// <summary>Which combat plugin drives the fight (roadmap 7.5); CielCraft never casts combat actions itself.</summary>
public enum CombatDriverKind
{
    None,
    RotationSolverReborn,
    BossModReborn,
}

/// <summary>
/// The combat plugin behind a hunt (roadmap 7.5): engaged for the duration of
/// a kill, disengaged between targets and on every stop. Availability is
/// read at runtime (installed and loaded), never assumed.
/// </summary>
public interface ICombatDriver
{
    CombatDriverKind Kind { get; }

    string Name { get; }

    /// <summary>The plugin is installed, loaded and answers its IPC.</summary>
    bool IsAvailable { get; }

    /// <summary>Turn the rotation on for the current target (auto mode).</summary>
    void Engage();

    /// <summary>Turn the rotation off; the character stops attacking.</summary>
    void Disengage();

    bool IsEngaged { get; }

    IEnumerable<string> Describe();
}

/// <summary>A monster that drops an item and where it lives (roadmap 7.5): bundled data plus spots remembered in game.</summary>
public sealed record MobDrop(
    uint ItemId,
    uint BNpcNameId,
    string MobName,
    int Level,
    uint TerritoryId,
    string ZoneName,
    Vector3? Position);

/// <summary>Where to hunt an item; the combat database (bundled table + remembered spots) implements it.</summary>
public interface IHuntSpots
{
    /// <summary>Every known mob for the item, best first (a remembered spot, then the lowest level in a reachable zone).</summary>
    IReadOnlyList<MobDrop> DropsOf(uint itemId);

    /// <summary>Remember where a mob was actually found, so the next hunt starts there.</summary>
    void RememberSpot(uint bnpcNameId, uint territoryId, Vector3 position);
}

/// <summary>
/// One live candidate for the next kill (roadmap 7.5, package B): a battle NPC
/// of the wanted BNpcName as the object table sees it right now. The hunt run
/// ranks these; <see cref="TargetedByOthers"/> is the etiquette flag (someone
/// who is not the player or a party / free company mate already has it), and
/// <see cref="Distance"/> is always measured from the player even when the
/// list was ranked around another origin.
/// </summary>
public sealed record HuntTargetSnapshot(
    ulong ObjectId,
    string Name,
    int Level,
    Vector3 Position,
    float Distance,
    float HpPercent,
    bool TargetedByOthers,
    bool IsAlive);
