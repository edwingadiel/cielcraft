using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>
/// What the plugin reads about the open node for the rotation engine
/// (roadmap 7.14): the Gatherer's Boon chance of the chosen slot (-1 when
/// the window does not show it), the point's bonus conditions with whether
/// the character meets them, whether the point is timed (unspoiled /
/// legendary / ephemeral), and the gatherer statuses on the player.
/// </summary>
public sealed record GatheringNodeFacts(
    int BoonChance,
    IReadOnlyList<GatheringBonusCondition> Bonuses,
    IReadOnlySet<GatherStatus> Statuses,
    bool IsTimed,
    uint GatheringPointId)
{
    public static readonly GatheringNodeFacts Unknown = new(-1, [], new HashSet<GatherStatus>(), false, 0);

    /// <summary>The visible texts of the chosen slot's row (name, percentages), logged once per node so the boon reading can be checked in game.</summary>
    public IReadOnlyList<string> SlotTexts { get; init; } = [];

    public string Describe()
    {
        var bonuses = Bonuses.Count == 0
            ? "none"
            : string.Join("; ", System.Linq.Enumerable.Select(Bonuses, b => $"{b.Text} [{(b.Met ? "met" : "unmet")}]"));
        return $"point {GatheringPointId}{(IsTimed ? " (timed)" : "")}, boon {(BoonChance < 0 ? "?" : BoonChance + "%")}, " +
               $"bonuses {bonuses}, statuses {(Statuses.Count == 0 ? "none" : string.Join(", ", Statuses))}";
    }
}
