using System.Numerics;

namespace CielCraft.Game;

/// <summary>Snapshot of the logged-in character, read once per frame at most.</summary>
public sealed record PlayerSnapshot(
    string Name,
    uint TerritoryId,
    Vector3 Position,
    uint ClassJobId,
    string ClassJobAbbreviation,
    int Level,
    uint CurrentCp,
    uint MaxCp,
    uint Craftsmanship,
    uint Control);

/// <summary>FFXIV crafting conditions (spec §10).</summary>
public enum CraftCondition
{
    Unknown,
    Normal,
    Good,
    Excellent,
    Poor,
    Centered,
    Sturdy,
    Pliant,
    Malleable,
    Primed,
    GoodOmen,
}

/// <summary>Live state of the craft in progress (spec §10). Null when not crafting.</summary>
public sealed record CraftSnapshot(
    int Step,
    int Progress,
    int MaxProgress,
    int Quality,
    int MaxQuality,
    int Durability,
    int MaxDurability,
    uint CurrentCp,
    uint MaxCp,
    CraftCondition Condition);
