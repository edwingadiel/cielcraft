using System.Numerics;

namespace CielCraft.Core;

/// <summary>
/// Who can answer "where do I get this gear repaired?" (roadmap 7.3a). The
/// plugin's NpcDatabase (7.3) implements it; it lives in Core so the
/// maintenance service — which compiles into the offline tests without the
/// Dalamud-bound plugin project — can take it.
/// </summary>
public interface IMenderLocator
{
    /// <summary>
    /// The mender to walk to from this zone and position, preferring one in
    /// the current territory and then a reachable one; null when none is.
    /// </summary>
    NpcTarget? NearestMender(uint territoryId, Vector3 position);

    /// <summary>Zone name for log lines and the report.</summary>
    string TerritoryName(uint territoryId);
}
