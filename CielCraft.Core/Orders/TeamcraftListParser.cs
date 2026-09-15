using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>One line of an imported list: an item name and a count, plus the section it came from.</summary>
public sealed record ImportedLine(string Name, int Amount, string Section);

/// <summary>
/// Parses the text Teamcraft produces for "copy list as text" (roadmap 7.13):
/// section headings such as "Final items :", "Items :", "Crystals :",
/// "Gathering :" and lines like "3x Iron Ingot" / "Iron Ingot x3". Free text
/// that is not a count + name is ignored. Name → item id happens in the
/// plugin, which has the item sheet.
/// </summary>
public static class TeamcraftListParser
{
    /// <summary>Every parsable line, in order, with its section ("" before any heading).</summary>
    public static IReadOnlyList<ImportedLine> Parse(string text)
    {
        // Implemented by the Core orders agent; see docs/design/orders.md.
        throw new System.NotImplementedException();
    }

    /// <summary>The lines that are orders rather than materials: the "Final items" section when present, else everything.</summary>
    public static IReadOnlyList<ImportedLine> FinalItems(IReadOnlyList<ImportedLine> lines)
    {
        throw new System.NotImplementedException();
    }
}
