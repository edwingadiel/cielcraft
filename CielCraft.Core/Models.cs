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
    uint Control,
    uint CurrentGp = 0,
    uint MaxGp = 0);

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

/// <summary>An active crafting buff (spec §10).</summary>
public sealed record CraftBuff(uint StatusId, int Stacks);

/// <summary>Crafting status-effect ids.</summary>
public static class CraftBuffIds
{
    public const uint InnerQuiet = 251;
    public const uint WasteNot = 252;
    public const uint GreatStrides = 254;
    public const uint WasteNot2 = 257;
    public const uint Manipulation = 1164;
    public const uint Innovation = 2189;
    public const uint FinalAppraisal = 2190;
    public const uint MuscleMemory = 2191;
    public const uint Veneration = 2226;
    public const uint HeartAndSoul = 2665;
    public const uint TrainedPerfection = 3813;

    public static readonly uint[] All =
    [
        InnerQuiet, WasteNot, GreatStrides, WasteNot2, Manipulation, Innovation,
        FinalAppraisal, MuscleMemory, Veneration, HeartAndSoul, TrainedPerfection,
    ];
}

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
    CraftCondition Condition)
{
    public IReadOnlyList<CraftBuff> Buffs { get; init; } = [];

    public bool HasBuff(uint statusId)
    {
        foreach (var buff in Buffs)
        {
            if (buff.StatusId == statusId)
                return true;
        }

        return false;
    }

    public static bool BuffsEqual(IReadOnlyList<CraftBuff> a, IReadOnlyList<CraftBuff> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }
}
