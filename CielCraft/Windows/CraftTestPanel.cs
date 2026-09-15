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
/// Craft test (roadmap 7.18): solve for chosen stats and a recipe without
/// crafting, and show the rotation with expected progress / quality / HQ
/// chance and the solve time. Hosted by the Tools page. Uses its own
/// <see cref="SolverService"/> over a cache that is never written to disk,
/// so a running batch's solver is never disturbed.
/// </summary>
public sealed class CraftTestPanel
{
    private readonly Plugin plugin;
    private readonly SolverService solver;

    private string searchText = "";
    private IReadOnlyList<(uint RecipeId, uint ItemId, string Name)> searchResults = [];

    private RecipeSheet.RecipeParameters? recipe;
    private string recipeName = "";

    private int craftsmanship, control, cp, level;
    private bool statsLoaded;
    private bool manipulation, heartAndSoul, quickInnovation;
    private int targetPercent;
    private int initialQuality;

    private CraftSetup? testSetup;
    private CraftObjective? testObjective;
    private CraftSolution? simulatedSolution;
    private RotationSimulation? simulation;
    private string notice = "";

    public CraftTestPanel(Plugin plugin)
    {
        this.plugin = plugin;
        // Same solver type and cache shape as Plugin builds, minus the file path (roadmap 7.7 cache stays untouched).
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";
        solver = new SolverService(new CielCraft.Raphael.RaphaelSolver(), new SolutionCache(version, path: null), Plugin.Log);
        targetPercent = Math.Clamp(plugin.Configuration.TargetQualityPercent, 1, 100);
    }

    public void Draw()
    {
        if (!CielCraft.Raphael.RaphaelSolver.IsAvailable)
            ImGui.TextColored(UiTheme.Danger, "The native Raphael library is not loaded; solves will fail.");

        DrawRecipePicker();
        DrawStats();
        DrawSolve();
        DrawResult();
    }

    // ------------------------------------------------------------ recipe

    private void DrawRecipePicker()
    {
        UiTheme.SectionHeader("Recipe");

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 190);
        if (ImGui.InputTextWithHint("##craftTestSearch", "Search craftable item…", ref searchText, 64))
            searchResults = plugin.RecipeProvider.SearchCraftable(searchText);

        ImGui.SameLine();
        var selected = plugin.GameBridge.SelectedRecipeId;
        using (ImRaii.Disabled(selected == 0))
        {
            if (UiTheme.TintedButton("Crafting-log selection", UiTheme.Muted))
                SelectRecipe(selected);
        }

        UiTheme.Tooltip("Use the recipe selected in the crafting log");

        if (searchResults.Count > 0)
        {
            using var child = ImRaii.Child("##craftTestResults", new Vector2(-1, Math.Min(searchResults.Count, 6) * 24f + 8), true);
            if (child.Success)
            {
                foreach (var result in searchResults)
                {
                    UiTheme.GameIcon(plugin.RecipeProvider.GetItemIconId(result.ItemId), 18f);
                    if (ImGui.Selectable($"{result.Name}##ct{result.RecipeId}"))
                    {
                        SelectRecipe(result.RecipeId);
                        searchText = "";
                        searchResults = [];
                        break;
                    }
                }
            }
        }

        if (recipe == null)
        {
            ImGui.TextColored(UiTheme.Faint, "Pick a recipe to solve for.");
            return;
        }

        UiTheme.KeyValue("Recipe", recipeName);
        ImGui.SameLine(0, 10);
        ImGui.TextColored(UiTheme.Muted, $"rlvl {recipe.RecipeLevel} · Lv{recipe.ClassJobLevel}{new string('★', recipe.Stars)}"
                                         + (recipe.IsExpert ? " · expert" : "") + (recipe.CanHq ? "" : " · no HQ"));
        UiTheme.KeyValue("Progress", $"{recipe.MaxProgress}");
        ImGui.SameLine(0, 10);
        UiTheme.KeyValue("Quality", $"{recipe.MaxQuality}");
        ImGui.SameLine(0, 10);
        UiTheme.KeyValue("Durability", $"{recipe.MaxDurability}");
        ImGui.SameLine(0, 10);
        UiTheme.KeyValue("Suggested craftsmanship", $"{recipe.SuggestedCraftsmanship}");
    }

    private void SelectRecipe(uint recipeId)
    {
        recipe = RecipeSheet.Read(recipeId);
        if (recipe == null)
        {
            notice = $"Recipe {recipeId} could not be read.";
            return;
        }

        var info = plugin.RecipeProvider.GetRecipeById(recipeId);
        recipeName = info != null ? plugin.RecipeProvider.GetItemName(info.ResultItemId) : $"recipe {recipeId}";
        notice = "";
        // A new recipe invalidates the shown result, but keeps the stats.
        simulatedSolution = null;
        simulation = null;
    }

    // ------------------------------------------------------------- stats

    private void DrawStats()
    {
        UiTheme.SectionHeader("Crafter");

        var player = plugin.GameBridge.GetPlayerState();
        if (!statsLoaded && player != null)
            LoadStats(player);

        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("Craftsmanship", ref craftsmanship);
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("Control", ref control);
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("CP", ref cp);
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(70);
        ImGui.InputInt("Level", ref level);

        craftsmanship = Math.Clamp(craftsmanship, 0, 9999);
        control = Math.Clamp(control, 0, 9999);
        cp = Math.Clamp(cp, 0, 9999);
        level = Math.Clamp(level, 1, 100);

        ImGui.Checkbox("Manipulation", ref manipulation);
        ImGui.SameLine(0, 12);
        ImGui.Checkbox("Heart and Soul", ref heartAndSoul);
        UiTheme.Tooltip("Specialist one-shot available at craft start");
        ImGui.SameLine(0, 12);
        ImGui.Checkbox("Quick Innovation", ref quickInnovation);
        UiTheme.Tooltip("Specialist one-shot available at craft start");

        ImGui.SameLine(0, 16);
        using (ImRaii.Disabled(player == null))
        {
            if (UiTheme.TintedButton("Use my stats", UiTheme.Muted))
                LoadStats(player!);
        }

        UiTheme.Tooltip("Copy the character's current craftsmanship / control / CP / level (with food if active)");

        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("Target quality %", ref targetPercent);
        targetPercent = Math.Clamp(targetPercent, 1, 100);
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("Initial quality", ref initialQuality);
        UiTheme.Tooltip("Quality granted by HQ materials at synthesis start");
        initialQuality = Math.Clamp(initialQuality, 0, recipe?.MaxQuality ?? 0);
    }

    private void LoadStats(PlayerSnapshot player)
    {
        craftsmanship = (int)player.Craftsmanship;
        control = (int)player.Control;
        cp = (int)player.MaxCp;
        level = player.Level;
        manipulation = player.Level >= 65;
        statsLoaded = true;
    }

    // ------------------------------------------------------------- solve

    private void DrawSolve()
    {
        UiTheme.SectionHeader("Solve");

        var canSolve = recipe != null && CielCraft.Raphael.RaphaelSolver.IsAvailable && solver.Status != SolverStatus.Solving;
        using (ImRaii.Disabled(!canSolve))
        {
            if (UiTheme.TintedButton("Solve", UiTheme.Accent) && recipe != null)
                BeginSolve(recipe);
        }

        UiTheme.Tooltip("Runs the solver for these stats without touching the game or a running batch");

        ImGui.SameLine(0, 10);
        var color = solver.Status switch
        {
            SolverStatus.Solving => UiTheme.Info,
            SolverStatus.Failed => UiTheme.Danger,
            SolverStatus.Done => UiTheme.Success,
            _ => UiTheme.Muted,
        };
        ImGui.TextColored(color, solver.StatusText);

        if (notice.Length > 0)
            ImGui.TextColored(UiTheme.Warning, notice);
    }

    private void BeginSolve(RecipeSheet.RecipeParameters parameters)
    {
        var setup = RecipeSheet.SetupFor(parameters, craftsmanship, control, cp, level, manipulation, heartAndSoul, quickInnovation);
        var target = (ushort)Math.Max(1, Math.Min(parameters.MaxQuality, (int)((long)parameters.MaxQuality * targetPercent / 100)));
        var objective = new CraftObjective(TargetQuality: target, InitialQuality: (ushort)Math.Min(initialQuality, target));

        testSetup = setup;
        testObjective = objective;
        simulatedSolution = null;
        simulation = null;
        notice = "";
        if (!solver.BeginSolve(setup, objective))
            notice = "A test solve is already running.";
    }

    // ------------------------------------------------------------ result

    private void DrawResult()
    {
        if (solver.Status != SolverStatus.Done || solver.Solution is not { Success: true } solution
            || testSetup is not { } setup || testObjective is not { } objective)
        {
            return;
        }

        if (!ReferenceEquals(simulatedSolution, solution))
        {
            simulatedSolution = solution;
            simulation = RotationSimulator.Run(setup, solution.BaseProgress, solution.BaseQuality, solution.ActionIds, objective.InitialQuality);
        }

        UiTheme.SectionHeader("Result");

        var seconds = solver.LastSolveTime.TotalSeconds;
        UiTheme.KeyValue("Solve time", solver.LastSolveCached ? "cached (0.0s)" : $"{seconds:F1}s");
        ImGui.SameLine(0, 12);
        UiTheme.KeyValue("Actions", $"{solution.ActionIds.Count}");
        ImGui.SameLine(0, 12);
        UiTheme.KeyValue("Base", $"{solution.BaseProgress} / {solution.BaseQuality}");
        ImGui.SameLine(0, 12);
        UiTheme.KeyValue("Target quality", $"{objective.TargetQuality}");

        if (simulation is { } sim)
        {
            RotationPanel.DrawOutcomeLine(sim, objective.TargetQuality);
            if (recipe is { CanHq: false })
                ImGui.TextColored(UiTheme.Faint, "This recipe has no HQ result; the HQ chance is informational.");
        }

        if (UiTheme.TintedButton("Copy names", UiTheme.Muted))
            ImGui.SetClipboardText(RotationText.Format(solution.ActionIds));
        UiTheme.Tooltip("Copy the rotation as action names, one per line");
        ImGui.SameLine();
        if (UiTheme.TintedButton("Copy macro", UiTheme.Muted))
            ImGui.SetClipboardText(RotationText.FormatMacro(solution.ActionIds));
        UiTheme.Tooltip("Copy the rotation as Teamcraft-style macro lines");

        if (recipe != null)
        {
            ImGui.SameLine();
            if (UiTheme.TintedButton("Save as manual rotation", UiTheme.Accent))
            {
                plugin.Configuration.ManualRotations[recipe.RecipeId] = RotationText.Format(solution.ActionIds);
                plugin.Configuration.Save();
                notice = $"Saved as the manual rotation for {recipeName}.";
                Plugin.Log.Information($"[Production] Manual rotation saved from the craft test for recipe {recipe.RecipeId} ({solution.ActionIds.Count} actions).");
            }

            UiTheme.Tooltip("Store this rotation for the recipe (roadmap 7.8); crafts of it then skip the solve");
        }

        DrawSteps(solution.ActionIds, simulation);
    }

    private static void DrawSteps(IReadOnlyList<uint> actions, RotationSimulation? sim)
    {
        var height = Math.Min(actions.Count + 1, 16) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        using var child = ImRaii.Child("##craftTestSteps", new Vector2(-1, height), true);
        if (!child.Success)
            return;

        if (!ImGui.BeginTable("##craftTestTable", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 26);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Dur", ImGuiTableColumnFlags.WidthFixed, 36);
        ImGui.TableSetupColumn("CP", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableHeadersRow();

        for (var i = 0; i < actions.Count; i++)
        {
            var step = sim != null && i < sim.Steps.Count ? sim.Steps[i] : null;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Faint, $"{i + 1}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(RaphaelActionNames.NameOf(actions[i]));
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

            var ok = step != null && step.Error == null;
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, ok ? $"{step!.Progress}" : "");
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, ok ? $"{step!.Quality}" : "");
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, ok && sim != null ? $"{HqChance.For(step!.Quality, sim.MaxQuality)}%" : "");
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, ok ? $"{step!.Durability}" : "");
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, ok ? $"{step!.Cp}" : "");
        }

        ImGui.EndTable();
    }
}
