using System;
using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Core.Rotations;
using CielCraft.Crafting;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CielCraft.Windows;

/// <summary>
/// Rotation visibility and manual rotations (roadmap 7.8): the solved or
/// manual action list with executed / current / pending marks and the
/// expected outcome, plus the per-recipe manual rotation editor and the
/// assist / lock-step controls (7.18). Hosted by the Status page ("Crafting
/// Steps"). Expected numbers come from <see cref="RotationSimulator"/>: the
/// native solver only reports base progress/quality, so the per-step values
/// are a Normal-condition replay, not a promise.
/// </summary>
public sealed class RotationPanel
{
    private readonly Plugin plugin;

    // Replay cache: the list instance and base values it was computed for.
    private IReadOnlyList<uint>? simulatedList;
    private int simulatedBaseProgress;
    private RotationSimulation? simulation;

    // Manual rotation editor state.
    private uint editorRecipeId;
    private string editorText = "";
    private string savedText = "";
    private RotationParse? editorParse;
    private RotationSimulation? editorSimulation;
    private string editorSimulatedText = "";
    private uint editorSimulatedRecipe;

    public RotationPanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private Configuration Configuration => plugin.Configuration;

    public void Draw()
    {
        DrawControls();
        DrawActiveRotation();
        DrawManualEditor();
    }

    // ---------------------------------------------------------- controls

    /// <summary>Assist / lock-step toggles (also on the Settings page) and the Step / Continue buttons.</summary>
    private void DrawControls()
    {
        var automator = plugin.CraftAutomator;

        var assist = Configuration.AssistMode;
        if (ImGui.Checkbox("Assist mode", ref assist))
        {
            Configuration.AssistMode = assist;
            Configuration.Save();
        }

        UiTheme.Tooltip("A synthesis you start by hand is solved and run by the plugin (roadmap 7.18)");

        ImGui.SameLine(0, 16);
        var lockStep = Configuration.LockStep;
        if (ImGui.Checkbox("Lock-step", ref lockStep))
        {
            Configuration.LockStep = lockStep;
            Configuration.Save();
        }

        UiTheme.Tooltip("Pause before every craft action and wait for Step; Resume also steps once while this is on");

        if (automator.WaitingForStep)
        {
            ImGui.SameLine(0, 16);
            if (UiTheme.TintedButton("Step", UiTheme.Accent))
                automator.Step();
            UiTheme.Tooltip("Execute the next action, then hold again");

            ImGui.SameLine();
            if (UiTheme.TintedButton("Continue", UiTheme.Success))
            {
                // Continue = run freely: lock-step off, then resume.
                Configuration.LockStep = false;
                Configuration.Save();
                automator.Resume();
            }

            UiTheme.Tooltip("Turn lock-step off and run the rest of the rotation");
        }
    }

    // --------------------------------------------------- active rotation

    private void DrawActiveRotation()
    {
        UiTheme.SectionHeader("Rotation");

        var automator = plugin.CraftAutomator;
        var rotation = CurrentRotation();
        if (rotation == null)
        {
            if (automator.Rotation.Count > 0)
            {
                // "Run rotation" from the debug page: a list without solve numbers.
                DrawRotationTable(automator.Rotation, null, automator);
                return;
            }

            ImGui.TextColored(UiTheme.Faint, "No rotation yet. Start a batch or an order, or solve a craft from the Craft Test page.");
            return;
        }

        UiTheme.KeyValue("Source", rotation.Source);
        ImGui.SameLine(0, 12);
        UiTheme.KeyValue("Base", $"{rotation.BaseProgress} progress / {rotation.BaseQuality} quality per 100%");
        ImGui.SameLine(0, 12);
        UiTheme.KeyValue("Target quality", rotation.TargetQuality > 0 ? rotation.TargetQuality.ToString() : "max");

        var paused = automator.State == AutomationState.Paused;
        UiTheme.StateBadge(automator.State.ToString(), paused, automator.StatusText);

        var sim = Simulate(rotation);
        DrawExpectedOutcome(sim, rotation);
        DrawRotationTable(rotation.ActionIds, sim, automator);
    }

    /// <summary>The batch's rotation, else the solver page's last solution (which knows its setup).</summary>
    private ActiveRotation? CurrentRotation()
    {
        if (plugin.BatchCrafter.Rotation is { } active)
            return active;

        var solver = plugin.SolverService;
        if (solver.Solution is { Success: true } solved && solver.LastSetup is { } setup && solver.LastObjective is { } objective)
        {
            return new ActiveRotation(
                solved.ActionIds, "solved (not running)", setup, solved.BaseProgress, solved.BaseQuality,
                objective.TargetQuality, objective.InitialQuality);
        }

        return null;
    }

    private RotationSimulation? Simulate(ActiveRotation rotation)
    {
        if (rotation.BaseProgress <= 0 || rotation.BaseQuality <= 0)
            return null;

        if (ReferenceEquals(simulatedList, rotation.ActionIds) && simulatedBaseProgress == rotation.BaseProgress)
            return simulation;

        simulation = rotation.StartState != null && rotation.StartEffects != null
            ? RotationSimulator.Run(rotation.Setup, rotation.BaseProgress, rotation.BaseQuality, rotation.ActionIds, rotation.StartState, rotation.StartEffects)
            : RotationSimulator.Run(rotation.Setup, rotation.BaseProgress, rotation.BaseQuality, rotation.ActionIds, rotation.InitialQuality);
        simulatedList = rotation.ActionIds;
        simulatedBaseProgress = rotation.BaseProgress;
        return simulation;
    }

    private static void DrawExpectedOutcome(RotationSimulation? sim, ActiveRotation rotation)
    {
        if (sim == null)
        {
            ImGui.TextColored(UiTheme.Faint, "Expected outcome unavailable: the solve carried no base values.");
            return;
        }

        DrawOutcomeLine(sim, rotation.TargetQuality);
    }

    /// <summary>"Expected: progress … quality … HQ …" with the target colored by whether the replay reaches it.</summary>
    internal static void DrawOutcomeLine(RotationSimulation sim, int targetQuality)
    {
        var goal = targetQuality > 0 ? Math.Min(targetQuality, sim.MaxQuality) : sim.MaxQuality;
        var reaches = sim.Finished && sim.Quality >= goal;
        ImGui.TextColored(UiTheme.Muted, "Expected");
        ImGui.SameLine(0, 6);
        ImGui.TextColored(sim.Finished ? UiTheme.Success : UiTheme.Warning, $"progress {sim.Progress}/{sim.MaxProgress}");
        ImGui.SameLine(0, 10);
        ImGui.TextColored(reaches ? UiTheme.Success : UiTheme.Warning, $"quality {sim.Quality}/{sim.MaxQuality} ({sim.QualityPercent}%)");
        ImGui.SameLine(0, 10);
        ImGui.TextUnformatted($"HQ {sim.HqChancePercent}%");
        ImGui.SameLine(0, 10);
        ImGui.TextColored(UiTheme.Muted, $"durability {sim.Durability} · CP {sim.Cp} left · {sim.Steps.Count} steps");
        UiTheme.Tooltip("A Normal-condition replay of the rotation; Good/Excellent steps and the adaptive rules can only do better");

        if (sim.Error != null)
            ImGui.TextColored(UiTheme.Danger, $"Replay stops: {sim.Error}");
    }

    /// <summary>The action list with ✓ / ▶ / · marks against the automator's position, and the replayed numbers when known.</summary>
    private static void DrawRotationTable(IReadOnlyList<uint> actions, RotationSimulation? sim, CraftAutomator automator)
    {
        // Marks only mean something while the automator drives this very list.
        var driving = ReferenceEquals(automator.Rotation, actions)
                      && automator.State is AutomationState.Running or AutomationState.Paused or AutomationState.Completed;
        var next = driving ? automator.NextIndex : -1;
        var live = automator.State is AutomationState.Running or AutomationState.Paused;

        var rows = Math.Max(1, actions.Count);
        var height = Math.Min(rows + 1, 14) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        using var child = ImRaii.Child("##rotationRows", new Vector2(-1, height), true);
        if (!child.Success)
            return;

        var columns = sim != null ? 7 : 3;
        if (!ImGui.BeginTable("##rotation", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 26);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        if (sim != null)
        {
            ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Dur", ImGuiTableColumnFlags.WidthFixed, 36);
            ImGui.TableSetupColumn("CP", ImGuiTableColumnFlags.WidthFixed, 40);
        }

        ImGui.TableHeadersRow();

        for (var i = 0; i < actions.Count; i++)
        {
            var executed = driving && i < next;
            var current = driving && live && i == next;
            var (mark, color) = executed ? ("✓", UiTheme.Success)
                : current ? ("▶", UiTheme.Accent)
                : ("·", UiTheme.Muted);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Faint, $"{i + 1}");
            ImGui.TableNextColumn();
            ImGui.TextColored(color, mark);
            ImGui.TableNextColumn();
            if (executed)
                ImGui.TextColored(UiTheme.Muted, RaphaelActionNames.NameOf(actions[i]));
            else if (current)
                ImGui.TextColored(UiTheme.Accent, RaphaelActionNames.NameOf(actions[i]));
            else
                ImGui.TextUnformatted(RaphaelActionNames.NameOf(actions[i]));

            if (sim == null)
                continue;

            var step = i < sim.Steps.Count ? sim.Steps[i] : null;
            if (step?.Note != null)
            {
                ImGui.SameLine(0, 6);
                ImGui.TextColored(UiTheme.Warning, "!");
                UiTheme.Tooltip(step.Note);
            }

            if (step?.Error != null)
            {
                ImGui.SameLine(0, 6);
                ImGui.TextColored(UiTheme.Danger, step.Error);
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, step != null && step.Error == null ? $"{step.Progress}" : "");
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, step != null && step.Error == null ? $"{step.Quality}" : "");
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, step != null && step.Error == null ? $"{step.Durability}" : "");
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, step != null && step.Error == null ? $"{step.Cp}" : "");
        }

        ImGui.EndTable();
    }

    // ----------------------------------------------------- manual editor

    private void DrawManualEditor()
    {
        UiTheme.SectionHeader("Manual rotation");

        var recipeId = EditorRecipe();
        if (recipeId == 0)
        {
            ImGui.TextColored(UiTheme.Faint, "Open the crafting log on a recipe (or start a batch) to edit its manual rotation.");
            DrawSavedCount();
            return;
        }

        if (recipeId != editorRecipeId)
        {
            editorRecipeId = recipeId;
            savedText = Configuration.ManualRotations.TryGetValue(recipeId, out var saved) ? saved : "";
            editorText = savedText;
            editorParse = editorText.Length > 0 ? RotationText.Parse(editorText) : null;
        }

        var recipe = plugin.RecipeProvider.GetRecipeById(recipeId);
        var name = recipe != null ? plugin.RecipeProvider.GetItemName(recipe.ResultItemId) : $"recipe {recipeId}";
        var hasSaved = Configuration.ManualRotations.ContainsKey(recipeId);
        var dirty = editorText != savedText;

        UiTheme.KeyValue("Recipe", name);
        ImGui.SameLine(0, 10);
        ImGui.TextColored(
            dirty ? UiTheme.Warning : hasSaved ? UiTheme.Success : UiTheme.Faint,
            dirty ? "unsaved changes" : hasSaved ? "saved — replaces the solver for this recipe" : "not set — the solver is used");

        if (ImGui.InputTextMultiline("##manualRotation", ref editorText, 4096, new Vector2(-1, 110)))
            editorParse = editorText.Trim().Length > 0 ? RotationText.Parse(editorText) : null;
        UiTheme.Tooltip("Action names one per line or comma separated, or a Teamcraft macro (/ac \"Name\" <wait.3>)");

        DrawEditorFeedback(recipeId);

        var canSave = editorParse is { Success: true } && dirty;
        using (ImRaii.Disabled(!canSave))
        {
            if (UiTheme.TintedButton("Save", UiTheme.Accent))
            {
                Configuration.ManualRotations[recipeId] = editorText;
                Configuration.Save();
                savedText = editorText;
                Plugin.Log.Information($"[Production] Manual rotation saved for recipe {recipeId} ({editorParse!.ActionIds.Count} actions).");
            }
        }

        UiTheme.Tooltip("Store the rotation for this recipe; the next craft of it skips the solve");

        ImGui.SameLine();
        using (ImRaii.Disabled(!hasSaved))
        {
            if (UiTheme.TintedButton("Clear", UiTheme.Danger))
            {
                Configuration.ManualRotations.Remove(recipeId);
                Configuration.Save();
                savedText = "";
                editorText = "";
                editorParse = null;
                Plugin.Log.Information($"[Production] Manual rotation cleared for recipe {recipeId}.");
            }
        }

        UiTheme.Tooltip("Forget the manual rotation; the solver takes over again");

        if (plugin.BatchCrafter.Rotation is { } active && plugin.BatchCrafter.RecipeId == recipeId
            && active.Source != "manual rotation")
        {
            ImGui.SameLine();
            if (UiTheme.TintedButton("Use current", UiTheme.Muted))
            {
                editorText = RotationText.Format(active.ActionIds);
                editorParse = RotationText.Parse(editorText);
            }

            UiTheme.Tooltip("Copy the rotation above into the editor as a starting point");
        }

        ImGui.TextColored(UiTheme.Faint, "Runs with the same adaptive rules; a bad condition mid-craft still re-solves with Raphael.");
        DrawSavedCount();
    }

    /// <summary>Parse errors, or the action count plus a replay against the character's stats for this recipe.</summary>
    private void DrawEditorFeedback(uint recipeId)
    {
        if (editorParse == null)
            return;

        if (!editorParse.Success)
        {
            foreach (var error in editorParse.Errors)
                ImGui.TextColored(UiTheme.Danger, error);
            if (editorParse.ActionIds.Count > 0)
                ImGui.TextColored(UiTheme.Muted, $"{editorParse.ActionIds.Count} action(s) recognized so far.");
            return;
        }

        ImGui.TextColored(UiTheme.Success, $"{editorParse.ActionIds.Count} actions.");

        var setup = EditorSetup(recipeId);
        if (setup == null)
            return;

        if (editorSimulatedText != editorText || editorSimulatedRecipe != recipeId)
        {
            editorSimulatedText = editorText;
            editorSimulatedRecipe = recipeId;
            var (baseProgress, baseQuality) = RecipeSheet.BaseValues(setup) ?? (0, 0);
            editorSimulation = baseProgress > 0
                ? RotationSimulator.Run(setup, baseProgress, baseQuality, editorParse.ActionIds)
                : null;
        }

        if (editorSimulation != null)
            DrawOutcomeLine(editorSimulation, 0);
    }

    /// <summary>The live craft's numbers when one is up, else the recipe sheet with the character's current stats.</summary>
    private CraftSetup? EditorSetup(uint recipeId)
    {
        if (plugin.BatchCrafter.Rotation is { } active && plugin.BatchCrafter.RecipeId == recipeId)
            return active.Setup;

        var parameters = RecipeSheet.Read(recipeId);
        return parameters == null ? null : RecipeSheet.SetupForPlayer(parameters, plugin.GameBridge.GetPlayerState());
    }

    /// <summary>The in-flight batch's recipe, else the crafting-log selection.</summary>
    private uint EditorRecipe()
    {
        var batch = plugin.BatchCrafter;
        if (batch.State is not (BatchState.Idle or BatchState.Completed or BatchState.Failed) && batch.RecipeId != 0)
            return batch.RecipeId;

        var selected = plugin.GameBridge.SelectedRecipeId;
        return selected != 0 ? selected : batch.RecipeId;
    }

    private void DrawSavedCount()
    {
        var count = Configuration.ManualRotations.Count;
        if (count > 0)
            ImGui.TextColored(UiTheme.Faint, $"{count} manual rotation(s) saved.");
    }
}
