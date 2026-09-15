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
/// (roadmap 7.15): the rotation table's estimate (7.14) — the once-per-node
/// buffs its top rules fire at full GP plus two swings' worth of per-swing
/// GP actions — for the class the node falls in.
/// </summary>
public static class GatheringRotationCost
{
    /// <summary>
    /// GP the built-in rotation wants at the node to run in full; a run with
    /// less GP still works, it only skips buffs. maxGp caps the answer.
    /// </summary>
    public static int GpPerNode(NodeKind kind, bool collectable, int maxGp) =>
        GpPerNode(kind, collectable, maxGp, null);

    /// <summary>As above, honouring the user's rotation overrides when settings are given.</summary>
    public static int GpPerNode(NodeKind kind, bool collectable, int maxGp, AutomationSettings? settings, bool crystal = false)
    {
        var nodeClass = GatheringRotationTable.ClassFor(kind, collectable, crystal);
        var table = settings != null
            ? new GatheringRotationSet(settings).For(nodeClass)
            : GatheringRotationTable.BuiltIn(nodeClass);
        return table.EstimateGpPerNode(maxGp);
    }
}
