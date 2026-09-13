using System.Numerics;

namespace CielCraft.Core;

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

/// <summary>A gathering point in the world.</summary>
public sealed record GatheringNodeSnapshot(ulong ObjectId, string Name, Vector3 Position, float Distance);

/// <summary>One selectable item slot of an open gathering node.</summary>
public sealed record GatheringItemSlot(int Index, uint ItemId, bool Enabled);

/// <summary>Live state of an open gathering node (spec §64). Null when none is open.</summary>
public sealed record GatheringSnapshot(
    int IntegrityRemaining,
    int IntegrityTotal,
    uint CurrentGp,
    uint MaxGp,
    IReadOnlyList<GatheringItemSlot> Items);

/// <summary>Live state of the craft in progress (spec §10). Null when not crafting.</summary>
public sealed record CraftSnapshot(
    ushort RecipeLevel,
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
