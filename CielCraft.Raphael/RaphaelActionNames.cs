using System.Collections.Generic;

namespace CielCraft.Raphael;

/// <summary>
/// Display names for the action ids Raphael emits (CRP-flavored for craft
/// actions, shared ids for buff actions). Mirrors Action::action_id in
/// raphael-sim; per-job id translation for execution happens elsewhere.
/// </summary>
public static class RaphaelActionNames
{
    public static readonly IReadOnlyDictionary<uint, string> ById = new Dictionary<uint, string>
    {
        [100001] = "Basic Synthesis",
        [100002] = "Basic Touch",
        [100003] = "Master's Mend",
        [100010] = "Observe",
        [100371] = "Tricks of the Trade",
        [4631] = "Waste Not",
        [19297] = "Veneration",
        [100004] = "Standard Touch",
        [260] = "Great Strides",
        [19004] = "Innovation",
        [4639] = "Waste Not II",
        [100339] = "Byregot's Blessing",
        [100128] = "Precise Touch",
        [100379] = "Muscle Memory",
        [100203] = "Careful Synthesis",
        [4574] = "Manipulation",
        [100227] = "Prudent Touch",
        [100411] = "Advanced Touch",
        [100387] = "Reflect",
        [100299] = "Preparatory Touch",
        [100403] = "Groundwork",
        [100323] = "Delicate Synthesis",
        [100315] = "Intensive Synthesis",
        [100283] = "Trained Eye",
        [100419] = "Heart and Soul",
        [100427] = "Prudent Synthesis",
        [100435] = "Trained Finesse",
        [100443] = "Refined Touch",
        [100459] = "Quick Innovation",
        [100467] = "Immaculate Mend",
        [100475] = "Trained Perfection",
        [46843] = "Stellar Steady Hand",
        [100363] = "Rapid Synthesis",
        [100355] = "Hasty Touch",
        [100451] = "Daring Touch",
    };

    public static string NameOf(uint actionId) =>
        ById.TryGetValue(actionId, out var name) ? name : $"Unknown action {actionId}";
}
