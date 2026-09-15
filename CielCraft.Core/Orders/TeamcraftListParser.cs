using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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
public static partial class TeamcraftListParser
{
    // Count first: "3x Iron Ingot", "3 x Iron Ingot", "3 Iron Ingot". The
    // multiplier needs a space after it so "3 Xelphatol Apple" keeps its X.
    [GeneratedRegex(@"^(\d+)\s*(?:[x×]\s+|\s+)(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CountFirst();

    // Count last: "Iron Ingot x3", "Iron Ingot ×3", "Iron Ingot HQ x2".
    [GeneratedRegex(@"^(.+?)\s+[x×]\s*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CountLast();

    // HQ markers carry no meaning for an order (production mode is chosen in
    // the UI), so they are stripped: "(HQ)", "[HQ]", a trailing " HQ" word.
    [GeneratedRegex(@"\s*(?:\(\s*hq\s*\)|\[\s*hq\s*\]|\bhq)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HqSuffix();

    /// <summary>Every parsable line, in order, with its section ("" before any heading).</summary>
    public static IReadOnlyList<ImportedLine> Parse(string text)
    {
        var lines = new List<ImportedLine>();
        if (string.IsNullOrEmpty(text))
            return lines;

        var section = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('-', '*', '•').Trim();
            if (line.Length == 0)
                continue;

            // A heading is any line ending with ':' ("Final items :", "Crystals:").
            if (line.EndsWith(':'))
            {
                section = line[..^1].Trim();
                continue;
            }

            if (TryParseLine(line, out var name, out var amount))
                lines.Add(new ImportedLine(name, amount, section));
        }

        return lines;
    }

    /// <summary>The lines that are orders rather than materials: the "Final items" section when present, else everything.</summary>
    public static IReadOnlyList<ImportedLine> FinalItems(IReadOnlyList<ImportedLine> lines)
    {
        var final = lines
            .Where(l => l.Section.Contains("final", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return final.Count > 0 ? final : lines;
    }

    private static bool TryParseLine(string line, out string name, out int amount)
    {
        name = "";
        amount = 0;

        var match = CountFirst().Match(line);
        string rawName;
        if (match.Success)
        {
            amount = ParseCount(match.Groups[1].Value);
            rawName = match.Groups[2].Value;
        }
        else
        {
            match = CountLast().Match(line);
            if (!match.Success)
                return false;
            amount = ParseCount(match.Groups[2].Value);
            rawName = match.Groups[1].Value;
        }

        name = HqSuffix().Replace(rawName, "").Trim();
        return amount > 0 && name.Length > 0;
    }

    // Absurd counts (or a stray digit run longer than an int) are not items.
    private static int ParseCount(string digits) => int.TryParse(digits, out var value) ? value : 0;
}
