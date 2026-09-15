using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CielCraft.Core.Rotations;

/// <summary>Outcome of parsing rotation text: the ids in order, and one message per problem line.</summary>
public sealed record RotationParse(IReadOnlyList<uint> ActionIds, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0 && ActionIds.Count > 0;
}

/// <summary>
/// Manual rotation text (roadmap 7.8): action names one per line or comma
/// separated, or Teamcraft macro lines (<c>/ac "Basic Synthesis" &lt;wait.3&gt;</c>,
/// <c>/action Innovation &lt;wait.2&gt;</c>). Names are matched against
/// <see cref="RaphaelActionNames"/> case-insensitively; the ids are the ones
/// Raphael emits, so a parsed rotation runs through the same executor path
/// as a solved one.
/// </summary>
public static partial class RotationText
{
    [GeneratedRegex(@"^\s*\d+\s*[.):]\s*")]
    private static partial Regex ListNumbering();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex AngleTags();

    public static RotationParse Parse(string? text)
    {
        var ids = new List<uint>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add("no actions");
            return new RotationParse(ids, errors);
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            var lineNumber = i + 1;
            if (line.StartsWith('/'))
            {
                ParseCommand(line, lineNumber, ids, errors);
                continue;
            }

            // Plain names: one per line, or several separated by commas / semicolons.
            foreach (var token in line.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
                AddName(token, lineNumber, ids, errors);
        }

        if (ids.Count == 0 && errors.Count == 0)
            errors.Add("no actions");

        return new RotationParse(ids, errors);
    }

    private static void ParseCommand(string line, int lineNumber, List<uint> ids, List<string> errors)
    {
        var space = line.IndexOf(' ');
        var command = (space < 0 ? line : line[..space]).ToLowerInvariant();
        var argument = space < 0 ? "" : line[(space + 1)..];

        switch (command)
        {
            case "/ac":
            case "/action":
                AddName(argument, lineNumber, ids, errors);
                break;
            case "/echo":
            case "/e":
            case "/wait":
                // Teamcraft ends every macro with "/echo Craft finished <se.1>".
                break;
            default:
                errors.Add($"line {lineNumber}: unsupported command \"{command}\"");
                break;
        }
    }

    private static void AddName(string token, int lineNumber, List<uint> ids, List<string> errors)
    {
        var name = CleanName(token);
        if (name.Length == 0)
            return;

        if (RaphaelActionNames.TryGetId(name, out var id))
            ids.Add(id);
        else
            errors.Add($"line {lineNumber}: unknown action \"{name}\"");
    }

    /// <summary>Strips list numbering ("3. "), macro tags (&lt;wait.3&gt;) and quotes.</summary>
    private static string CleanName(string token)
    {
        var name = AngleTags().Replace(token, "");
        name = ListNumbering().Replace(name, "");
        return name.Trim().Trim('"', '“', '”').Trim();
    }

    /// <summary>Names one per line (the editor's canonical form).</summary>
    public static string Format(IReadOnlyList<uint> actionIds, string separator = "\n")
    {
        var sb = new StringBuilder();
        foreach (var id in actionIds)
        {
            if (sb.Length > 0)
                sb.Append(separator);
            sb.Append(RaphaelActionNames.NameOf(id));
        }

        return sb.ToString();
    }

    /// <summary>Teamcraft-style macro lines, with the game's action wait times.</summary>
    public static string FormatMacro(IReadOnlyList<uint> actionIds)
    {
        var sb = new StringBuilder();
        foreach (var id in actionIds)
            sb.Append("/ac \"").Append(RaphaelActionNames.NameOf(id)).Append("\" <wait.").Append(WaitSeconds(id)).Append(">\n");
        return sb.ToString();
    }

    /// <summary>Buff actions animate for 2 s, everything else for 3 s (raphael-sim time_cost).</summary>
    public static int WaitSeconds(uint actionId) => actionId switch
    {
        4631 or 19297 or 260 or 19004 or 4639 or 4574 or 46843 => 2,
        _ => 3,
    };
}
