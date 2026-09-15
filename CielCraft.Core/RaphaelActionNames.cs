using System;
using System.Collections.Generic;
using System.Text;

namespace CielCraft.Core;

/// <summary>
/// Display names for the action ids Raphael emits (CRP-flavored for craft
/// actions, shared ids for buff actions). Mirrors Action::action_id in
/// raphael-sim; per-job id translation for execution happens elsewhere.
/// The reverse lookup serves manual rotation text (roadmap 7.8).
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

    /// <summary>Normalized name → id; built once from <see cref="ById"/>.</summary>
    private static readonly Dictionary<string, uint> ByNormalizedName = BuildReverse();

    public static string NameOf(uint actionId) =>
        ById.TryGetValue(actionId, out var name) ? name : $"Unknown action {actionId}";

    /// <summary>
    /// Case-insensitive name lookup, tolerant of typographic apostrophes,
    /// stray whitespace and the "Waste Not 2" spelling.
    /// </summary>
    public static bool TryGetId(string name, out uint actionId) =>
        ByNormalizedName.TryGetValue(Normalize(name), out actionId);

    private static Dictionary<string, uint> BuildReverse()
    {
        var reverse = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (id, name) in ById)
            reverse[Normalize(name)] = id;

        // Common alternative spellings people paste from guides.
        reverse[Normalize("Waste Not 2")] = 4639;
        reverse[Normalize("Masters Mend")] = 100003;
        reverse[Normalize("Byregots Blessing")] = 100339;
        return reverse;
    }

    /// <summary>Lower-case, straight apostrophes, single spaces.</summary>
    private static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        var pendingSpace = false;
        foreach (var raw in name.Trim())
        {
            var c = raw switch
            {
                '’' or '‘' or '`' => '\'',
                _ => raw,
            };

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
