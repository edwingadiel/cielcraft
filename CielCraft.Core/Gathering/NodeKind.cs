namespace CielCraft.Core;

/// <summary>
/// How a gathering point spawns (roadmap 7.14 / 7.15): the rotation engine
/// picks its table by it and the scheduler only plans windows for the timed
/// kinds. Legendary (folklore) nodes behave like unspoiled ones; the split is
/// kept so the schedule can say which book a node needs.
/// </summary>
public enum NodeKind
{
    /// <summary>Always up.</summary>
    Normal,

    /// <summary>Unspoiled: up in fixed Eorzea-time windows, one visit per window.</summary>
    Unspoiled,

    /// <summary>Legendary (folklore book): windows like unspoiled.</summary>
    Legendary,

    /// <summary>Ephemeral: timed collectables for aetherial reduction.</summary>
    Ephemeral,
}

/// <summary>
/// GP a node run is expected to cost, for the scheduler's slot maths
/// (roadmap 7.15). The rotation engine (7.14) owns the numbers; before it
/// lands these are the conservative defaults of the hard-coded buffs.
/// </summary>
public static class GatheringRotationCost
{
    /// <summary>
    /// GP the rotation wants at the node to run in full; a run with less GP
    /// still works, it only skips buffs. maxGp caps the answer.
    /// </summary>
    public static int GpPerNode(NodeKind kind, bool collectable, int maxGp)
    {
        var wanted = collectable
            ? 600                                  // Scrutiny ×2 + a Collector's buff
            : kind is NodeKind.Unspoiled or NodeKind.Legendary
                ? 800                              // Yield II + Gift II + Tidings
                : 500;                             // Yield II (or Yield I + Solid Reason)
        return maxGp > 0 ? System.Math.Min(wanted, maxGp) : wanted;
    }
}
