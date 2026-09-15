using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly OrdersPanel orders;

    private DateTime requirementsRefreshedAt = DateTime.MinValue;
    private ushort requirementsRecipeId;
    private IReadOnlyList<IngredientRequirement> requirements = [];
    private readonly Dictionary<uint, int> storedCounts = new();

    // Single-target fallback (crafting-log selection while the book is empty).
    private ProductionPlan? plan;
    private string planError = "";

    public MainWindow(Plugin plugin) : base("CielCraft##Main")
    {
        // Wide enough for the order rows (icon, amount, two combos, toggles) without clipping.
        Size = new Vector2(600, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        gameBridge = plugin.GameBridge;
        orders = new OrdersPanel(plugin);
    }

    public void Dispose() { }

    /// <summary>Draws the item's game icon inline, followed by SameLine.</summary>
    private void ItemIcon(uint itemId, float size = 20f) =>
        UiTheme.GameIcon(plugin.RecipeProvider.GetItemIconId(itemId), size);

    /// <summary>The crafting-log selection: Batch ×N, the materials table and the empty-book fallback use it.</summary>
    private uint SelectedRecipeId => gameBridge.SelectedRecipeId;

    private int Quantity => orders.Quantity;

    public override void Draw()
    {
        DrawHeader();
        UiTheme.SectionHeader("Orders");
        orders.Draw();
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
        var book = plugin.OrderRunner;
        var bookDriving = book.State is OrderRunState.Running or OrderRunState.Held;

        // Which group the production belongs to, and Hold while the book is advancing.
        if (bookDriving && book.CurrentGroup is { } group)
        {
            var groups = plugin.Configuration.Orders.Groups;
            var index = groups.IndexOf(group);
            ImGui.TextColored(UiTheme.Info, $"◈ {group.Name}");
            ImGui.SameLine(0, 8);
            var position = index >= 0 ? $"group {index + 1}/{groups.Count}" : "group";
            var cycle = book.Cycle > 0 ? $" · cycle {book.Cycle + 1}" : "";
            ImGui.TextColored(UiTheme.Muted, position + cycle);
            if (book.Running)
            {
                ImGui.SameLine(0, 10);
                if (UiTheme.TintedButton("Hold", UiTheme.Warning))
                    book.Hold();
                UiTheme.Tooltip("Finish this group, then stop advancing to the next");
            }
        }

        var stepFraction = batch.TargetQuantity > 0 ? (float)batch.CompletedCrafts / batch.TargetQuantity : 0f;
        var overall = runner.TotalSteps > 0
            ? (runner.CompletedSteps + Math.Clamp(stepFraction, 0f, 1f)) / runner.TotalSteps
            : 0f;

        UiTheme.ProgressBar(
            overall,
            $"{overall * 100:F0}%  ·  step {Math.Min(runner.CompletedSteps + 1, Math.Max(runner.TotalSteps, 1))}/{runner.TotalSteps}");
        UiTheme.StateBadge(runner.State.ToString(), runner.State is ProductionState.Paused, runner.StatusText);
        if (bookDriving && book.State == OrderRunState.Held)
            UiTheme.StateBadge("Held", true, book.StatusText);

        if (batch.TargetQuantity > 0 && batch.State is not BatchState.Idle)
            UiTheme.ProgressBar(stepFraction, $"{batch.CompletedCrafts}/{batch.TargetQuantity} crafts", UiTheme.Info);

        // Stop takes the book down with the production when the book is driving it.
        DrawPauseResumeStop(
            paused: runner.State == ProductionState.Paused,
            onPause: () => runner.Pause("paused by user"),
            onResume: runner.Resume,
            onStop: () =>
            {
                if (bookDriving)
                    book.Stop();
                else
                    runner.Stop();
            });

        // Gentle stop (roadmap 7.20): finish the step, leave the run resumable.
        ImGui.SameLine();
        if (UiTheme.TintedButton(runner.StopAfterStep ? "Finishing step…" : "Stop after step", UiTheme.Warning))
            runner.StopGently();
        UiTheme.Tooltip(runner.StopAfterStep
            ? "Stops once the current step or gather task completes. Click again to cancel."
            : "Finish the current step or gather task, then stop; Resume continues from there.");
    }

    private void DrawBatchActive(BatchCrafter batch)
    {
        var fraction = batch.TargetQuantity > 0 ? (float)batch.CompletedCrafts / batch.TargetQuantity : 0f;
        UiTheme.ProgressBar(fraction, $"{batch.CompletedCrafts}/{batch.TargetQuantity} crafts");
        UiTheme.StateBadge(batch.State.ToString(), batch.State == BatchState.Paused, batch.StatusText);

        DrawPauseResumeStop(
            paused: batch.State == BatchState.Paused,
            onPause: () => batch.Pause("paused by user"),
            onResume: batch.Resume,
            onStop: batch.Stop);
    }

    private void DrawIdleControls(ProductionRunner runner, BatchCrafter batch)
    {
        var raphael = CielCraft.Raphael.RaphaelSolver.IsAvailable;
        var book = plugin.OrderRunner;

        if (!orders.HasOrders && SelectedRecipeId != 0)
            DrawSingleTargetControls(runner, raphael);
        else
            DrawOrderControls(book, raphael);

        ImGui.SameLine();
        var canBatch = raphael && (gameBridge.IsReadyToStartCraft
                                   || (gameBridge.IsCrafting && plugin.CraftMonitor.Current is { Step: <= 1 }));
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!canBatch))
        {
            if (UiTheme.TintedButton($"Batch ×{Quantity}", UiTheme.Accent))
                batch.Start(Quantity);
        }

        UiTheme.Tooltip("Craft the crafting-log selection repeatedly (no sub-recipes)");

        if (book.State != OrderRunState.Idle)
            UiTheme.StateBadge(book.State.ToString(), book.State == OrderRunState.Held, book.StatusText);
        else if (book.StatusText.Length > 0)
            ImGui.TextColored(UiTheme.Muted, book.StatusText);
        else if (runner.State is ProductionState.Completed or ProductionState.Failed)
            UiTheme.StateBadge(runner.State.ToString(), false, runner.StatusText);
        else if (batch.State is BatchState.Completed or BatchState.Failed)
            UiTheme.StateBadge(batch.State.ToString(), false, batch.StatusText);

        if (planError.Length > 0)
            ImGui.TextColored(UiTheme.Danger, planError);

        if (plan != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.Muted, $"Plan · {plugin.RecipeProvider.GetItemName(plan.TargetItemId)} ×{plan.TargetQuantity}");
            OrdersPanel.DrawPlanPreview(plan, plugin.RecipeProvider);
        }
    }

    /// <summary>Run orders / Hold / Stop and the perpetual toggle; the normal controls once the book has anything in it.</summary>
    private void DrawOrderControls(OrderRunner book, bool raphael)
    {
        var runnable = OrderPlanner.RunnableGroups(plugin.Configuration.Orders).Any();
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!runnable || !raphael || book.Running))
        {
            if (UiTheme.TintedButton("Run orders", UiTheme.Success))
            {
                plan = null;
                planError = "";
                if (!book.Start())
                    planError = book.StatusText;
            }
        }

        UiTheme.Tooltip(runnable
            ? "Plan the first enabled group, gather and craft it, then move on to the next"
            : "Enable a group with at least one enabled order first");

        ImGui.SameLine();
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!book.Running))
        {
            if (UiTheme.TintedButton("Hold", UiTheme.Warning))
                book.Hold();
        }

        UiTheme.Tooltip("Stop advancing to further groups; the current production keeps going");

        ImGui.SameLine();
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(book.State is OrderRunState.Idle or OrderRunState.Completed))
        {
            if (UiTheme.TintedButton("Stop", UiTheme.Danger))
                book.Stop();
        }

        UiTheme.Tooltip("Stop the book and the production");

        ImGui.SameLine();
        var perpetual = plugin.Configuration.Orders.Perpetual;
        if (ImGui.Checkbox("Perpetual", ref perpetual))
        {
            plugin.Configuration.Orders.Perpetual = perpetual;
            plugin.Configuration.Save();
        }

        UiTheme.Tooltip("Restart from the first group when the last completes; restock orders keep it idle-safe");
    }

    /// <summary>Run / Preview for the crafting-log selection, offered only while the order book is empty.</summary>
    private void DrawSingleTargetControls(ProductionRunner runner, bool raphael)
    {
        // Run resolves the plan itself; Preview only shows it. A separate
        // "plan first" click was pure ceremony.
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!raphael))
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

        UiTheme.Tooltip("Gather missing materials, craft intermediates, then the crafting-log selection (add orders above for more)");

        ImGui.SameLine();
        if (UiTheme.TintedButton("Preview", UiTheme.Info))
            ComputePlan();
        UiTheme.Tooltip("Show what Run would gather and craft, without starting");
    }

    private void ComputePlan()
    {
        plan = null;
        planError = "";

        var recipe = plugin.RecipeProvider.GetRecipeById(SelectedRecipeId);
        if (recipe == null)
            planError = "Could not read the selected recipe.";
        else
            plan = DependencyResolver.Resolve(
                recipe.ResultItemId, Quantity, plugin.RecipeProvider, gameBridge.GetItemCount,
                plugin.Capabilities.Current);
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
        var recipeId = (ushort)SelectedRecipeId;
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
        ImGui.TextColored(UiTheme.Muted, $"Crafting-log selection · {plugin.RecipeProvider.GetItemName(plugin.RecipeProvider.GetRecipeById(recipeId)?.ResultItemId ?? 0)} ×{Quantity}");
        var craftable = InventoryMath.CraftableCount(requirements);
        ImGui.TextColored(
            craftable >= Quantity ? UiTheme.Success : UiTheme.Warning,
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
                ImGui.TextUnformatted($"{requirement.RequiredFor(Quantity)}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{requirement.Owned}");
                if (storedCounts.TryGetValue(requirement.ItemId, out var stored))
                {
                    ImGui.SameLine(0, 4);
                    ImGui.TextColored(UiTheme.Faint, $"+{stored}");
                    UiTheme.Tooltip("Also stored in saddlebags/retainers (not used by plans)");
                }

                ImGui.TableNextColumn();
                var missing = requirement.MissingFor(Quantity);
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
