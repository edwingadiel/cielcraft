using System;
using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Crafting;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CielCraft.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;

    private int batchQuantity = 1;
    private string searchText = "";
    private IReadOnlyList<(uint RecipeId, uint ItemId, string Name)> searchResults = [];
    private (uint RecipeId, string Name)? searchTarget;

    private DateTime requirementsRefreshedAt = DateTime.MinValue;
    private ushort requirementsRecipeId;
    private IReadOnlyList<IngredientRequirement> requirements = [];
    private readonly Dictionary<uint, int> storedCounts = new();

    private ProductionPlan? plan;
    private string planError = "";

    public MainWindow(Plugin plugin) : base("CielCraft##Main")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        gameBridge = plugin.GameBridge;
    }

    public void Dispose() { }

    /// <summary>Draws the item's game icon inline, followed by SameLine.</summary>
    private void ItemIcon(uint itemId, float size = 20f)
    {
        var iconId = plugin.RecipeProvider.GetItemIconId(itemId);
        if (iconId == 0)
            return;

        var wrap = Plugin.TextureProvider
            .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(iconId))
            .GetWrapOrDefault();
        if (wrap == null)
            return;

        ImGui.Image(wrap.Handle, new Vector2(size, size));
        ImGui.SameLine(0, 5);
    }

    private uint EffectiveRecipeId => searchTarget?.RecipeId ?? gameBridge.SelectedRecipeId;

    public override void Draw()
    {
        DrawHeader();
        UiTheme.SectionHeader("Target");
        DrawTarget();
        UiTheme.SectionHeader("Production");
        DrawProduction();
        DrawMaterials();
        DrawFooter();
    }

    // ------------------------------------------------------------- header

    private void DrawHeader()
    {
        var player = gameBridge.GetPlayerState();
        if (player != null)
        {
            ImGui.TextUnformatted($"{player.Name}");
            ImGui.SameLine(0, 8);
            ImGui.TextColored(UiTheme.Accent, $"{player.ClassJobAbbreviation} {player.Level}");
            ImGui.SameLine(0, 14);
            ImGui.TextColored(
                UiTheme.Muted,
                $"{player.Craftsmanship} craft · {player.Control} control · {player.CurrentCp}/{player.MaxCp} CP");
        }
        else
        {
            ImGui.TextColored(UiTheme.Muted, "Not logged in.");
        }

        var raphael = CielCraft.Raphael.RaphaelSolver.IsAvailable;
        var nav = plugin.Navigation;
        UiTheme.StatusDot("Raphael", raphael ? UiTheme.Success : UiTheme.Danger,
            raphael ? "Solver ready" : "Native solver library missing — crafting automation disabled");
        ImGui.SameLine(0, 12);
        var navColor = !nav.IsAvailable ? UiTheme.Danger : nav.IsReady ? UiTheme.Success : UiTheme.Warning;
        UiTheme.StatusDot("vnavmesh", navColor,
            !nav.IsAvailable ? "vnavmesh unavailable — gathering automation disabled"
            : nav.IsReady ? "Navigation ready"
            : "Installed; navmesh still building for this zone");
        ImGui.SameLine(0, 12);
        UiTheme.StatusDot("Gathering", nav.IsAvailable ? UiTheme.Success : UiTheme.Muted,
            nav.IsAvailable ? "Gathering automation ready" : "Disabled without vnavmesh");
    }

    // ------------------------------------------------------------- target

    private void DrawTarget()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 110);
        if (ImGui.InputTextWithHint("##itemSearch", "Search craftable item…", ref searchText, 64))
            searchResults = plugin.RecipeProvider.SearchCraftable(searchText);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("##qty", ref batchQuantity))
            batchQuantity = Math.Clamp(batchQuantity, 1, 999);
        UiTheme.Tooltip("Quantity");

        if (searchResults.Count > 0)
        {
            using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child(
                "##searchResults", new Vector2(-1, Math.Min(searchResults.Count, 6) * 24f + 8), true);
            foreach (var result in searchResults)
            {
                ItemIcon(result.ItemId, 18f);
                if (ImGui.Selectable($"{result.Name}##r{result.RecipeId}"))
                {
                    searchTarget = (result.RecipeId, result.Name);
                    searchText = result.Name;
                    searchResults = [];
                    plan = null;
                }
            }
        }

        if (searchTarget is { } target)
        {
            ImGui.TextColored(UiTheme.Accent, "◈");
            ImGui.SameLine(0, 6);
            ImGui.TextUnformatted(target.Name);
            ImGui.SameLine(0, 10);
            if (ImGui.SmallButton("×##clearTarget"))
            {
                searchTarget = null;
                searchText = "";
                plan = null;
            }

            UiTheme.Tooltip("Clear and use the crafting log selection instead");
        }
        else if (gameBridge.SelectedRecipeId != 0)
        {
            ImGui.TextColored(UiTheme.Muted, "Using the crafting log selection.");
        }
        else
        {
            ImGui.TextColored(UiTheme.Faint, "Search above, or select a recipe in the crafting log.");
        }
    }

    // --------------------------------------------------------- production

    private void DrawProduction()
    {
        var runner = plugin.ProductionRunner;
        var batch = plugin.BatchCrafter;

        // Interrupted-run banner (roadmap 6.3).
        var saved = plugin.Configuration.SavedProduction;
        if (saved.Active && runner.State is ProductionState.Idle)
        {
            ImGui.TextColored(UiTheme.Warning,
                $"Unfinished production: {plugin.RecipeProvider.GetItemName(saved.ItemId)} ×{saved.Quantity}");
            if (UiTheme.TintedButton("Resume##saved", UiTheme.Success))
                runner.TryResumeSaved();
            ImGui.SameLine();
            if (UiTheme.TintedButton("Discard##saved", UiTheme.Danger))
                runner.DiscardSaved();
            ImGui.Spacing();
        }

        var runnerActive = runner.State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed);
        var batchActive = batch.State is not (BatchState.Idle or BatchState.Completed or BatchState.Failed);

        if (runnerActive)
            DrawRunnerActive(runner, batch);
        else if (batchActive)
            DrawBatchActive(batch);
        else
            DrawIdleControls(runner, batch);
    }

    private void DrawRunnerActive(ProductionRunner runner, BatchCrafter batch)
    {
        var stepFraction = batch.TargetQuantity > 0 ? (float)batch.CompletedCrafts / batch.TargetQuantity : 0f;
        var overall = runner.TotalSteps > 0
            ? (runner.CompletedSteps + Math.Clamp(stepFraction, 0f, 1f)) / runner.TotalSteps
            : 0f;

        UiTheme.ProgressBar(
            overall,
            $"{overall * 100:F0}%  ·  step {Math.Min(runner.CompletedSteps + 1, Math.Max(runner.TotalSteps, 1))}/{runner.TotalSteps}");
        DrawStateBadge(runner.State.ToString(), runner.State is ProductionState.Paused, runner.StatusText);

        if (batch.TargetQuantity > 0 && batch.State is not BatchState.Idle)
            UiTheme.ProgressBar(stepFraction, $"{batch.CompletedCrafts}/{batch.TargetQuantity} crafts", UiTheme.Info);

        DrawPauseResumeStop(
            paused: runner.State == ProductionState.Paused,
            onPause: () => runner.Pause("paused by user"),
            onResume: runner.Resume,
            onStop: runner.Stop);

        // Gentle stop (roadmap 7.20): finish the step, leave the run resumable.
        ImGui.SameLine();
        if (UiTheme.TintedButton(runner.StopAfterStep ? "Finishing step…" : "Stop after step", UiTheme.Warning))
            runner.StopGently();
        UiTheme.Tooltip(runner.StopAfterStep
            ? "Stops once the current step or gather task completes. Click again to cancel."
            : "Finish the current step or gather task, then stop; Resume continues from there.");

        // Keep the queue visible (and holdable) while it is driving the runner.
        if (plugin.ProductionQueue.Running || plugin.Configuration.QueueItems.Count > 0)
            DrawQueue();
    }

    private void DrawBatchActive(BatchCrafter batch)
    {
        var fraction = batch.TargetQuantity > 0 ? (float)batch.CompletedCrafts / batch.TargetQuantity : 0f;
        UiTheme.ProgressBar(fraction, $"{batch.CompletedCrafts}/{batch.TargetQuantity} crafts");
        DrawStateBadge(batch.State.ToString(), batch.State == BatchState.Paused, batch.StatusText);

        DrawPauseResumeStop(
            paused: batch.State == BatchState.Paused,
            onPause: () => batch.Pause("paused by user"),
            onResume: batch.Resume,
            onStop: batch.Stop);
    }

    private void DrawIdleControls(ProductionRunner runner, BatchCrafter batch)
    {
        var haveTarget = EffectiveRecipeId != 0;
        var raphael = CielCraft.Raphael.RaphaelSolver.IsAvailable;

        // Run resolves the plan itself; Preview only shows it. A separate
        // "plan first" click was pure ceremony.
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!haveTarget || !raphael))
        {
            if (UiTheme.TintedButton("Run", UiTheme.Success))
            {
                ComputePlan();
                if (plan != null)
                {
                    if (plan.RawMaterials.Count > 0 && !plugin.Navigation.IsAvailable)
                        planError = "Missing materials need vnavmesh to gather; install it or gather them by hand first.";
                    else if (!runner.Start(plan))
                        planError = runner.StatusText;
                }
            }
        }

        UiTheme.Tooltip("Gather missing materials, craft intermediates, then the target");

        ImGui.SameLine();
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!haveTarget))
        {
            if (UiTheme.TintedButton("Preview", UiTheme.Info))
                ComputePlan();
        }

        UiTheme.Tooltip("Show what Run would gather and craft, without starting");

        ImGui.SameLine();
        var canBatch = raphael && (gameBridge.IsReadyToStartCraft
                                   || (gameBridge.IsCrafting && plugin.CraftMonitor.Current is { Step: <= 1 }));
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!canBatch))
        {
            if (UiTheme.TintedButton($"Batch ×{batchQuantity}", UiTheme.Accent))
                batch.Start(batchQuantity);
        }

        UiTheme.Tooltip("Craft the crafting-log selection repeatedly (no sub-recipes)");

        ImGui.SameLine();
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!haveTarget))
        {
            if (UiTheme.TintedButton("Queue +", UiTheme.Muted))
            {
                var recipe = plugin.RecipeProvider.GetRecipeById(EffectiveRecipeId);
                if (recipe != null)
                    plugin.ProductionQueue.Add(recipe.ResultItemId, batchQuantity);
            }
        }

        UiTheme.Tooltip("Add the current target and quantity to the production queue");

        DrawQueue();

        if (runner.State is ProductionState.Completed or ProductionState.Failed)
            DrawStateBadge(runner.State.ToString(), false, runner.StatusText);
        else if (batch.State is BatchState.Completed or BatchState.Failed)
            DrawStateBadge(batch.State.ToString(), false, batch.StatusText);

        if (planError.Length > 0)
            ImGui.TextColored(UiTheme.Danger, planError);

        DrawPlanPreview();
    }

    private void DrawQueue()
    {
        var queue = plugin.ProductionQueue;
        var items = plugin.Configuration.QueueItems;
        if (items.Count == 0 && !queue.Running)
            return;

        ImGui.Spacing();
        ImGui.TextColored(UiTheme.Muted, $"Queue ({items.Count})");
        ImGui.SameLine();
        if (queue.Running)
        {
            if (ImGui.SmallButton("Hold##queue"))
                queue.StopQueue();
        }
        else if (items.Count > 0 && ImGui.SmallButton("Run queue"))
        {
            queue.StartQueue();
        }

        for (var i = 0; i < items.Count; i++)
        {
            ItemIcon(items[i].ItemId, 18f);
            ImGui.TextUnformatted($"{plugin.RecipeProvider.GetItemName(items[i].ItemId)} ×{items[i].Quantity}");
            if (queue.IsInFlight(i))
            {
                ImGui.SameLine();
                ImGui.TextColored(UiTheme.Info, "● producing");
                continue;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton($"×##q{i}"))
            {
                queue.RemoveAt(i);
                break;
            }
        }

        if (queue.StatusText.Length > 0)
            ImGui.TextColored(UiTheme.Muted, queue.StatusText);
    }

    private void ComputePlan()
    {
        plan = null;
        planError = "";

        var recipe = plugin.RecipeProvider.GetRecipeById(EffectiveRecipeId);
        if (recipe == null)
            planError = "Could not read the selected recipe.";
        else
            plan = DependencyResolver.Resolve(
                recipe.ResultItemId, batchQuantity, plugin.RecipeProvider, gameBridge.GetItemCount,
                plugin.Capabilities.Current);
    }

    private void DrawPlanPreview()
    {
        if (plan == null)
            return;

        var provider = plugin.RecipeProvider;
        ImGui.Spacing();
        ImGui.TextColored(UiTheme.Muted, $"Plan · {provider.GetItemName(plan.TargetItemId)} ×{plan.TargetQuantity}");

        if (plan.RawMaterials.Count > 0)
        {
            ImGui.TextColored(UiTheme.Warning, "Gather first:");
            foreach (var material in plan.RawMaterials)
                ImGui.BulletText($"{provider.GetItemName(material.ItemId)} ×{material.Amount}");
        }
        else
        {
            ImGui.TextColored(UiTheme.Success, "✓ all raw materials on hand");
        }

        foreach (var step in plan.CraftSteps)
        {
            ImGui.TextColored(UiTheme.Faint, "  ▸");
            ImGui.SameLine(0, 4);
            ItemIcon(step.ItemId, 18f);
            ImGui.TextUnformatted($"{provider.GetItemName(step.ItemId)} ×{step.TotalProduced}");
            ImGui.SameLine(0, 6);
            ImGui.TextColored(UiTheme.Muted, $"({step.Crafts} crafts)");
        }
    }

    private static void DrawStateBadge(string state, bool paused, string statusText)
    {
        // Every state enum shares the terminal names Failed/Completed; the
        // badge keys off those names deliberately so one renderer serves all.
        var color = state switch
        {
            "Failed" => UiTheme.Danger,
            "Completed" => UiTheme.Success,
            _ when paused => UiTheme.Warning,
            _ => UiTheme.Info,
        };

        ImGui.TextColored(color, $"● {state}");
        ImGui.SameLine(0, 8);
        ImGui.PushTextWrapPos();
        ImGui.TextColored(UiTheme.Muted, statusText);
        ImGui.PopTextWrapPos();
    }

    private void DrawPauseResumeStop(bool paused, Action onPause, Action onResume, Action onStop)
    {
        if (paused)
        {
            if (UiTheme.TintedButton("Resume", UiTheme.Success))
                onResume();
        }
        else if (UiTheme.TintedButton("Pause", UiTheme.Warning))
        {
            onPause();
        }

        ImGui.SameLine();
        if (UiTheme.TintedButton("Stop", UiTheme.Danger))
            onStop();
    }

    // ---------------------------------------------------------- materials

    private void DrawMaterials()
    {
        var recipeId = (ushort)EffectiveRecipeId;
        if (recipeId == 0)
        {
            requirements = [];
            return;
        }

        if (recipeId != requirementsRecipeId || DateTime.UtcNow - requirementsRefreshedAt > TimeSpan.FromSeconds(1))
        {
            requirements = gameBridge.GetRecipeRequirements(recipeId);
            storedCounts.Clear();
            foreach (var requirement in requirements)
            {
                var stored = gameBridge.GetStoredItemCount(requirement.ItemId);
                if (stored > 0)
                    storedCounts[requirement.ItemId] = stored;
            }

            requirementsRecipeId = recipeId;
            requirementsRefreshedAt = DateTime.UtcNow;
        }

        if (requirements.Count == 0)
            return;

        UiTheme.SectionHeader("Materials");
        var craftable = InventoryMath.CraftableCount(requirements);
        ImGui.TextColored(
            craftable >= batchQuantity ? UiTheme.Success : UiTheme.Warning,
            $"Craftable now: {craftable}");

        if (ImGui.BeginTable("##materials", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Ingredient");
            ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 54);
            ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed, 84);
            ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            foreach (var requirement in requirements)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ItemIcon(requirement.ItemId, 18f);
                ImGui.TextUnformatted(requirement.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{requirement.RequiredFor(batchQuantity)}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{requirement.Owned}");
                if (storedCounts.TryGetValue(requirement.ItemId, out var stored))
                {
                    ImGui.SameLine(0, 4);
                    ImGui.TextColored(UiTheme.Faint, $"+{stored}");
                    UiTheme.Tooltip("Also stored in saddlebags/retainers (not used by plans)");
                }

                ImGui.TableNextColumn();
                var missing = requirement.MissingFor(batchQuantity);
                if (missing > 0)
                    ImGui.TextColored(UiTheme.Danger, $"{missing}");
                else
                    ImGui.TextColored(UiTheme.Success, "✓");
            }

            ImGui.EndTable();
        }
    }

    // ------------------------------------------------------------- footer

    private void DrawFooter()
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Separator, UiTheme.AccentDim);
        ImGui.Separator();
        ImGui.PopStyleColor();

        if (ImGui.SmallButton("Debug"))
            plugin.ToggleDebugUi();
        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            plugin.ToggleConfigUi();
        ImGui.SameLine();
        if (ImGui.SmallButton("Report"))
            plugin.SaveAndCopyReport();
        UiTheme.Tooltip("Copy a diagnostic report (state + recent log) to the clipboard for bug reports (/cielcraft report)");
        ImGui.SameLine();
        if (ImGui.SmallButton("Stop everything"))
            plugin.StopEverything();
        UiTheme.Tooltip("Emergency stop: production, batch, gathering, navigation (/cielcraft stop)");
    }
}
