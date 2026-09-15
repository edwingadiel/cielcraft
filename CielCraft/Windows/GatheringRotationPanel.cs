using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Gathering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CielCraft.Windows;

/// <summary>
/// Settings › Gathering block for the rotation tables (roadmap 7.14): one
/// collapsible section per node class with the built-in table read-only,
/// an override editor with parse feedback, and Save / Clear. Overrides live
/// in <see cref="AutomationSettings.GatheringRotationOverrides"/> keyed by
/// the class name; the controller picks them up at the next node.
/// </summary>
internal static class GatheringRotationPanel
{
    /// <summary>Max GP the "wants N GP" preview is computed for.</summary>
    private const int PreviewMaxGp = 1000;

    private static readonly NodeClass[] Classes =
        [NodeClass.Normal, NodeClass.Unspoiled, NodeClass.Crystal, NodeClass.Collectable];

    private static readonly Dictionary<NodeClass, Editor> Editors = new();

    private sealed class Editor
    {
        public string Text = "";
        public string Saved = "";
        public GatheringRotationParse? Parse;
        public bool Loaded;
    }

    /// <summary>The plugin's action catalogue, when the coordinator sets it: shows what did not resolve from the sheets.</summary>
    public static GatheringActionCatalog? Catalog { get; set; }

    public static void DrawSettings(Configuration configuration)
    {
        UiTheme.SectionHeader("Rotation tables");
        UiTheme.Hint("One rule per line, read top-down at every decision at the node: when <conditions>: <action>. " +
                     "Conditions: gp >= N, integrity < max, remaining > integrity*yield, boon < N, bonus:boon, " +
                     "status:eureka, used:YieldII (negate with !), kind:crystal, collectability >= goal, lastAttempt, always. " +
                     "Actions: Yield I/II, Restore Integrity, Bountiful Yield, Gift I/II, Tidings, Twelve's Bounty, Giving Land, " +
                     "Wise to the World, Luck, Scour, Brazen, Meticulous, Scrutiny, Collector's Focus, Priming Touch, Collect.");

        if (Catalog is { } catalog)
        {
            if (catalog.Unresolved.Count == 0)
                ImGui.TextColored(UiTheme.Faint, $"{catalog.ResolvedCount} gathering actions resolved from the game data.");
            else
            {
                ImGui.PushTextWrapPos();
                ImGui.TextColored(UiTheme.Warning, $"Not found in the game data (never used): {string.Join(", ", catalog.Unresolved)}");
                ImGui.PopTextWrapPos();
            }
        }

        foreach (var nodeClass in Classes)
            DrawClass(configuration, nodeClass);
    }

    private static void DrawClass(Configuration configuration, NodeClass nodeClass)
    {
        var name = GatheringRotationTable.ClassName(nodeClass);
        var editor = EditorFor(configuration, nodeClass, name);
        var hasSaved = editor.Saved.Length > 0;

        if (!UiTheme.Collapsible($"{Title(nodeClass)}{(hasSaved ? "  (override)" : "")}##rotation{name}"))
            return;

        using var indent = ImRaii.PushIndent(8f);

        ImGui.TextColored(UiTheme.Muted, "Built-in");
        var builtIn = GatheringRotationTable.BuiltInText(nodeClass);
        ImGui.InputTextMultiline($"##builtin{name}", ref builtIn, builtIn.Length + 1, new Vector2(-1, 120), ImGuiInputTextFlags.ReadOnly);
        ImGui.TextColored(UiTheme.Faint, $"Wants {GatheringRotationTable.BuiltIn(nodeClass).EstimateGpPerNode(PreviewMaxGp)} GP per node at {PreviewMaxGp} max GP.");

        ImGui.Spacing();
        ImGui.TextColored(UiTheme.Muted, "Override");
        ImGui.SameLine(0, 10);
        var dirty = editor.Text != editor.Saved;
        ImGui.TextColored(
            dirty ? UiTheme.Warning : hasSaved ? UiTheme.Success : UiTheme.Faint,
            dirty ? "unsaved changes" : hasSaved ? "saved — replaces the built-in table" : "not set — the built-in table is used");

        if (ImGui.InputTextMultiline($"##override{name}", ref editor.Text, 8192, new Vector2(-1, 120)))
            editor.Parse = editor.Text.Trim().Length > 0 ? GatheringRotationTable.Parse(editor.Text, nodeClass) : null;
        UiTheme.Tooltip("Paste the built-in text to start from it; empty = built-in");

        DrawFeedback(editor);

        var canSave = dirty && editor.Parse is { Success: true } parsed && parsed.Table.Rules.Count > 0;
        using (ImRaii.Disabled(!canSave))
        {
            if (UiTheme.TintedButton($"Save##{name}", UiTheme.Accent))
            {
                configuration.GatheringRotationOverrides[name] = editor.Text;
                configuration.Save();
                editor.Saved = editor.Text;
                Plugin.Log.Information($"[Gather] Rotation override saved for {name} ({editor.Parse!.Table.Rules.Count} rules).");
            }
        }

        UiTheme.Tooltip("Use this table for the class from the next node on");

        ImGui.SameLine();
        using (ImRaii.Disabled(!hasSaved && editor.Text.Length == 0))
        {
            if (UiTheme.TintedButton($"Clear##{name}", UiTheme.Danger))
            {
                configuration.GatheringRotationOverrides.Remove(name);
                configuration.Save();
                editor.Saved = "";
                editor.Text = "";
                editor.Parse = null;
                Plugin.Log.Information($"[Gather] Rotation override cleared for {name}.");
            }
        }

        UiTheme.Tooltip("Forget the override; the built-in table takes over again");

        ImGui.SameLine();
        if (UiTheme.TintedButton($"Copy built-in##{name}", UiTheme.Muted))
        {
            editor.Text = builtIn;
            editor.Parse = GatheringRotationTable.Parse(editor.Text, nodeClass);
        }

        UiTheme.Tooltip("Put the built-in text into the editor as a starting point");
        ImGui.Spacing();
    }

    private static void DrawFeedback(Editor editor)
    {
        if (editor.Parse == null)
            return;

        if (!editor.Parse.Success)
        {
            foreach (var error in editor.Parse.Errors)
                ImGui.TextColored(UiTheme.Danger, error);
            if (editor.Parse.Table.Rules.Count > 0)
                ImGui.TextColored(UiTheme.Muted, $"{editor.Parse.Table.Rules.Count} rule(s) parsed so far.");
            return;
        }

        var table = editor.Parse.Table;
        ImGui.TextColored(
            table.Rules.Count > 0 ? UiTheme.Success : UiTheme.Warning,
            table.Rules.Count > 0
                ? $"{table.Rules.Count} rule(s); wants {table.EstimateGpPerNode(PreviewMaxGp)} GP per node at {PreviewMaxGp} max GP."
                : "No rules (comments only); nothing to save.");
    }

    private static Editor EditorFor(Configuration configuration, NodeClass nodeClass, string name)
    {
        if (!Editors.TryGetValue(nodeClass, out var editor))
            Editors[nodeClass] = editor = new Editor();

        if (!editor.Loaded)
        {
            editor.Loaded = true;
            editor.Saved = configuration.GatheringRotationOverrides.TryGetValue(name, out var saved) ? saved : "";
            editor.Text = editor.Saved;
            editor.Parse = editor.Text.Trim().Length > 0 ? GatheringRotationTable.Parse(editor.Text, nodeClass) : null;
        }

        return editor;
    }

    private static string Title(NodeClass nodeClass) => nodeClass switch
    {
        NodeClass.Normal => "Normal nodes",
        NodeClass.Unspoiled => "Unspoiled and legendary nodes",
        NodeClass.Crystal => "Crystal nodes",
        NodeClass.Collectable => "Collectables (and ephemeral nodes)",
        _ => nodeClass.ToString(),
    };
}
