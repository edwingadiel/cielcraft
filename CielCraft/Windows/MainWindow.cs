using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using CielCraft.Core;
using CielCraft.Crafting;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace CielCraft.Windows;

/// <summary>
/// The one CielCraft window (roadmap 7.21): a sidebar of sections with
/// sub-pages on the left, the selected page on the right, a status bar
/// below. Orders is the order book and its run controls; Status the live
/// run, rotation, breakdown, report, log and debug views; Tools the
/// character checklist, craft test and solution cache; Settings the
/// configuration pages. The last page is remembered in the configuration.
/// </summary>
public class MainWindow : Window, IDisposable
{
    /// <summary>Page ids: "Section/Page"; the Plugin's command and config-UI hooks select by these.</summary>
    public static class Pages
    {
        public const string Orders = "Orders";
        public const string StatusProgress = "Status/Progress";
        public const string StatusSteps = "Status/Steps";
        public const string StatusBreakdown = "Status/Breakdown";
        public const string StatusSchedule = "Status/Schedule";
        public const string StatusReport = "Status/Report";
        public const string StatusLog = "Status/Log";
        public const string StatusDebug = "Status/Debug";
        public const string ToolsCharacter = "Tools/Character";
        public const string ToolsCraftTest = "Tools/CraftTest";
        public const string ToolsCache = "Tools/Cache";
        public const string SettingsGeneral = "Settings/General";
        public const string SettingsCrafting = "Settings/Crafting";
        public const string SettingsGathering = "Settings/Gathering";
        public const string SettingsConsumables = "Settings/Consumables";
        public const string SettingsHome = "Settings/Home";
        public const string SettingsAlerts = "Settings/Alerts";
        public const string SettingsSocial = "Settings/Social";
    }

    private sealed record PageDef(string Id, string Section, string Label);

    // Sidebar order. A section with a single page (Orders) is its own entry.
    private static readonly PageDef[] PageDefs =
    [
        new(Pages.Orders, "Orders", "Orders"),
        new(Pages.StatusProgress, "Status", "Progress"),
        new(Pages.StatusSteps, "Status", "Crafting Steps"),
        new(Pages.StatusBreakdown, "Status", "Breakdown"),
        new(Pages.StatusSchedule, "Status", "Schedule"),
        new(Pages.StatusReport, "Status", "Report"),
        new(Pages.StatusLog, "Status", "Log"),
        new(Pages.StatusDebug, "Status", "Debug"),
        new(Pages.ToolsCharacter, "Tools", "Character"),
        new(Pages.ToolsCraftTest, "Tools", "Craft Test"),
        new(Pages.ToolsCache, "Tools", "Solution cache"),
        new(Pages.SettingsGeneral, "Settings", "General"),
        new(Pages.SettingsCrafting, "Settings", "Crafting"),
        new(Pages.SettingsGathering, "Settings", "Gathering"),
        new(Pages.SettingsConsumables, "Settings", "Consumables"),
        new(Pages.SettingsHome, "Settings", "Home"),
        new(Pages.SettingsAlerts, "Settings", "Alerts"),
        new(Pages.SettingsSocial, "Settings", "Social"),
    ];

    private static readonly string[] Sections = ["Orders", "Status", "Tools", "Settings"];

    private static readonly TimeSpan ReportRefresh = TimeSpan.FromSeconds(5);

    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;
    private readonly OrdersPanel orders;
    private readonly RotationPanel rotation;
    private readonly PlanTreePanel planTree;
    private readonly CraftTestPanel craftTest;
    private readonly SetupPanel setup;
    private readonly DebugPanel debug;
    private readonly SettingsPanel settings;

    private PageDef current;
    private readonly Dictionary<string, string> lastInSection = new();

    // Materials table (crafting-log selection), refreshed once a second.
    private DateTime requirementsRefreshedAt = DateTime.MinValue;
    private ushort requirementsRecipeId;
    private IReadOnlyList<IngredientRequirement> requirements = [];
    private readonly Dictionary<uint, int> storedCounts = new();

    // Single-target fallback (crafting-log selection while the book is empty).
    private ProductionPlan? plan;
    private string planError = "";

    // Report page: the diagnostic report split into its sections.
    private List<(string Title, string Body)> reportSections = [];
    private DateTime reportBuiltAt = DateTime.MinValue;

    // A new ImGui id so the sidebar layout starts at its own default size
    // instead of the old single-column window's saved one.
    public MainWindow(Plugin plugin) : base("CielCraft##Main2")
    {
        // Sidebar plus the order rows (icon, amount, two combos, toggles) without clipping.
        Size = new Vector2(760, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        gameBridge = plugin.GameBridge;
        orders = new OrdersPanel(plugin);
        rotation = new RotationPanel(plugin);
        planTree = new PlanTreePanel(plugin);
        craftTest = new CraftTestPanel(plugin);
        setup = new SetupPanel(plugin, ShowPage);
        debug = new DebugPanel(plugin);
        settings = new SettingsPanel(plugin);

        // Page-enter work (capability refresh, report build) waits for OnOpen.
        current = Find(plugin.Configuration.LastPage) ?? PageDefs[0];
        lastInSection[current.Section] = current.Id;
    }

    public void Dispose() { }

    // --------------------------------------------------------- navigation

    /// <summary>Opens the window on the given page.</summary>
    public void ShowPage(string pageId)
    {
        // A closed window gets its page-enter work from OnOpen; only an open one needs it here.
        SelectPage(pageId, enter: IsOpen);
        IsOpen = true;
    }

    /// <summary>
    /// The old per-window toggles, kept as page selectors: closes the window
    /// when it already shows that page's section, otherwise opens it there.
    /// </summary>
    public void TogglePage(string pageId)
    {
        var target = Find(pageId) ?? PageDefs[0];
        if (IsOpen && current.Section == target.Section)
            IsOpen = false;
        else
            ShowPage(target.Id);
    }

    private void SelectPage(string pageId, bool enter = true)
    {
        var def = Find(pageId) ?? PageDefs[0];
        var changed = def != current;
        current = def;
        lastInSection[def.Section] = def.Id;
        // UI-only; persisted with the next config save (OnClose at the latest).
        plugin.Configuration.LastPage = def.Id;
        if (changed && enter)
            OnPageEnter(def.Id);
    }

    private static PageDef? Find(string? pageId) =>
        pageId == null ? null : Array.Find(PageDefs, p => p.Id == pageId);

    private void OnPageEnter(string pageId)
    {
        switch (pageId)
        {
            case Pages.ToolsCharacter:
                setup.OnShown();
                break;
            case Pages.StatusReport:
                BuildReport();
                break;
        }
    }

    public override void OnOpen() => OnPageEnter(current.Id);

    /// <summary>Persist the last page without a save per click.</summary>
    public override void OnClose() => plugin.Configuration.Save();

    // --------------------------------------------------------------- draw

    public override void Draw()
    {
        var style = ImGui.GetStyle();
        var statusBarHeight = ImGui.GetFrameHeight() + style.ItemSpacing.Y * 2 + 2;
        var bodyHeight = Math.Max(100f, ImGui.GetContentRegionAvail().Y - statusBarHeight);

        DrawSidebar(bodyHeight);
        ImGui.SameLine();
        DrawPage(bodyHeight);
        DrawStatusBar();
    }

    private void DrawSidebar(float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.18f));
        using var child = ImRaii.Child("##sidebar", new Vector2(UiTheme.SidebarWidth, height), false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleColor();
        if (!child)
            return;

        DrawCharacter();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        foreach (var section in Sections)
        {
            var pages = PageDefs.Where(p => p.Section == section).ToArray();
            var active = current.Section == section;
            var single = pages.Length == 1;
            if (UiTheme.SidebarItem(section, section, active && single, subPage: false, accent: active))
                SelectPage(lastInSection.TryGetValue(section, out var last) ? last : pages[0].Id);

            // Only the active section unfolds; all four unfolded would not fit 480 px.
            if (!active || single)
                continue;

            foreach (var page in pages)
            {
                if (UiTheme.SidebarItem(page.Label, page.Id, current.Id == page.Id, subPage: true))
                    SelectPage(page.Id);
            }
        }
    }

    private void DrawCharacter()
    {
        var player = gameBridge.GetPlayerState();
        if (player == null)
        {
            ImGui.TextColored(UiTheme.Muted, "Not logged in.");
            return;
        }

        ImGui.TextUnformatted(player.Name);
        ImGui.TextColored(UiTheme.Accent, $"{player.ClassJobAbbreviation} {player.Level}");
        ImGui.SameLine(0, 8);
        ImGui.TextColored(UiTheme.Muted, $"{player.CurrentCp}/{player.MaxCp} CP");
        UiTheme.Tooltip($"{player.Craftsmanship} craftsmanship · {player.Control} control · {player.CurrentCp}/{player.MaxCp} CP");
    }

    private void DrawPage(float height)
    {
        using var child = ImRaii.Child("##page", new Vector2(0, height), false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        if (!child)
            return;

        UiTheme.PageTitle(current.Section == current.Label ? null : current.Section, current.Label);

        switch (current.Id)
        {
            case Pages.Orders:
                DrawOrdersPage();
                break;
            case Pages.StatusProgress:
                DrawProgressPage();
                break;
            case Pages.StatusSteps:
                rotation.Draw();
                break;
            case Pages.StatusBreakdown:
                planTree.Draw();
                break;
            case Pages.StatusSchedule:
                ImGui.TextDisabled("Timed nodes arrive with 7.15.");
                UiTheme.Hint("The schedule will list the unspoiled and legendary nodes a plan needs, with their Eorzea-time windows, and the wait the runner plans around them.");
                break;
            case Pages.StatusReport:
                DrawReportPage();
                break;
            case Pages.StatusLog:
                debug.DrawLog();
                break;
            case Pages.StatusDebug:
                debug.DrawSections();
                break;
            case Pages.ToolsCharacter:
                setup.Draw();
                break;
            case Pages.ToolsCraftTest:
                craftTest.Draw();
                break;
            case Pages.ToolsCache:
                DrawCachePage();
                break;
            case Pages.SettingsGeneral:
                settings.DrawGeneral();
                break;
            case Pages.SettingsCrafting:
                settings.DrawCrafting();
                break;
            case Pages.SettingsGathering:
                settings.DrawGathering();
                break;
            case Pages.SettingsConsumables:
                settings.DrawConsumables();
                break;
            case Pages.SettingsHome:
                settings.DrawHome();
                break;
            case Pages.SettingsAlerts:
                settings.DrawAlerts();
                break;
            case Pages.SettingsSocial:
                settings.DrawSocial();
                break;
        }
    }

    // --------------------------------------------------------- status bar

    /// <summary>The old footer: dependency dots on the left, Report and the emergency stop on the right.</summary>
    private void DrawStatusBar()
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, UiTheme.AccentDim);
        ImGui.Separator();
        ImGui.PopStyleColor();

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

        // Right-aligned buttons; the small-button width is the text plus the frame padding.
        var style = ImGui.GetStyle();
        var width = ImGui.CalcTextSize("Report").X + ImGui.CalcTextSize("Stop everything").X
                    + style.FramePadding.X * 4 + style.ItemSpacing.X;
        ImGui.SameLine();
        var slack = ImGui.GetContentRegionAvail().X - width;
        if (slack > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + slack);

        if (UiTheme.LinkButton("Report"))
            plugin.SaveAndCopyReport();
        UiTheme.Tooltip("Copy a diagnostic report (state + recent log) to the clipboard for bug reports (/cielcraft report); Status › Report shows it");
        ImGui.SameLine();
        if (UiTheme.TintedButtonSmall("Stop everything", UiTheme.Danger))
            plugin.StopEverything();
        UiTheme.Tooltip("Emergency stop: production, batch, gathering, navigation (/cielcraft stop)");
    }

    // ------------------------------------------------------------- orders

    private uint SelectedRecipeId => gameBridge.SelectedRecipeId;

    private int Quantity => orders.Quantity;

    private bool RunnerActive => plugin.ProductionRunner.State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed);

    private bool BatchActive => plugin.BatchCrafter.State is not (BatchState.Idle or BatchState.Completed or BatchState.Failed);

    private void DrawOrdersPage()
    {
        DrawSavedBanner();
        DrawRunControls();
        DrawRunStatus();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        orders.Draw();
    }

    /// <summary>Interrupted-run banner (roadmap 6.3).</summary>
    private void DrawSavedBanner()
    {
        var runner = plugin.ProductionRunner;
        var saved = plugin.Configuration.SavedProduction;
        if (!saved.Active || runner.State is not ProductionState.Idle)
            return;

        ImGui.TextColored(UiTheme.Warning,
            $"Unfinished production: {plugin.RecipeProvider.GetItemName(saved.ItemId)} ×{saved.Quantity}");
        if (UiTheme.TintedButton("Resume##saved", UiTheme.Success))
            runner.TryResumeSaved();
        ImGui.SameLine();
        if (UiTheme.TintedButton("Discard##saved", UiTheme.Danger))
            runner.DiscardSaved();
        ImGui.Spacing();
    }

    /// <summary>Run orders / Hold / Stop / Perpetual and Batch ×N; the single-target fallback while the book is empty.</summary>
    private void DrawRunControls()
    {
        var raphael = CielCraft.Raphael.RaphaelSolver.IsAvailable;
        var runner = plugin.ProductionRunner;
        var batch = plugin.BatchCrafter;
        var book = plugin.OrderRunner;

        if (!orders.HasOrders && SelectedRecipeId != 0 && !RunnerActive && !BatchActive)
            DrawSingleTargetControls(runner, raphael);
        else
            DrawOrderControls(book, raphael);

        ImGui.SameLine();
        var canBatch = raphael && !RunnerActive && !BatchActive
                       && (gameBridge.IsReadyToStartCraft || (gameBridge.IsCrafting && plugin.CraftMonitor.Current != null));
        using (ImRaii.Disabled(!canBatch))
        {
            if (UiTheme.TintedButton($"Batch ×{Quantity}", UiTheme.Accent))
                batch.Start(Quantity);
        }

        UiTheme.Tooltip("Craft the crafting-log selection repeatedly (no sub-recipes)");
    }

    /// <summary>Run orders / Hold / Stop and the perpetual toggle. Stop also takes down a run the book is not driving.</summary>
    private void DrawOrderControls(OrderRunner book, bool raphael)
    {
        var runnable = OrderPlanner.RunnableGroups(plugin.Configuration.Orders).Any();
        using (ImRaii.Disabled(!runnable || !raphael || book.Running || RunnerActive || BatchActive))
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
        using (ImRaii.Disabled(!book.Running))
        {
            if (UiTheme.TintedButton("Hold", UiTheme.Warning))
                book.Hold();
        }

        UiTheme.Tooltip("Stop advancing to further groups; the current production keeps going");

        ImGui.SameLine();
        var bookStoppable = book.State is not (OrderRunState.Idle or OrderRunState.Completed);
        using (ImRaii.Disabled(!bookStoppable && !RunnerActive && !BatchActive))
        {
            if (UiTheme.TintedButton("Stop", UiTheme.Danger))
                StopRun();
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

    /// <summary>Stops whatever drives the run: the book when it is driving, else the runner, else the batch.</summary>
    private void StopRun()
    {
        var book = plugin.OrderRunner;
        if (book.State is OrderRunState.Running or OrderRunState.Held)
            book.Stop();
        else if (RunnerActive)
            plugin.ProductionRunner.Stop();
        else
            plugin.BatchCrafter.Stop();
    }

    /// <summary>Run / Preview for the crafting-log selection, offered only while the order book is empty.</summary>
    private void DrawSingleTargetControls(ProductionRunner runner, bool raphael)
    {
        // Run resolves the plan itself; Preview only shows it. A separate
        // "plan first" click was pure ceremony.
        using (ImRaii.Disabled(!raphael))
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

        UiTheme.Tooltip("Gather missing materials, craft intermediates, then the crafting-log selection (add orders below for more)");

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

    /// <summary>One line under the controls: the live run in short, or the last outcome; details live on Status › Progress.</summary>
    private void DrawRunStatus()
    {
        var runner = plugin.ProductionRunner;
        var batch = plugin.BatchCrafter;
        var book = plugin.OrderRunner;

        if (RunnerActive)
        {
            var (overall, label) = OverallProgress(runner, batch);
            UiTheme.ProgressBar(overall, label);
            UiTheme.StateBadge(runner.State.ToString(), runner.State is ProductionState.Paused, runner.StatusText);
            if (book.State == OrderRunState.Held)
                UiTheme.StateBadge("Held", true, book.StatusText);
            ProgressLink();
            return;
        }

        if (BatchActive)
        {
            var fraction = batch.TargetQuantity > 0 ? (float)batch.CompletedCrafts / batch.TargetQuantity : 0f;
            UiTheme.ProgressBar(fraction, $"{batch.CompletedCrafts}/{batch.TargetQuantity} crafts");
            UiTheme.StateBadge(batch.State.ToString(), batch.State == BatchState.Paused, batch.StatusText);
            ProgressLink();
            return;
        }

        if (book.State != OrderRunState.Idle)
            UiTheme.StateBadge(book.State.ToString(), book.State == OrderRunState.Held, book.StatusText);
        else if (book.StatusText.Length > 0)
            ImGui.TextColored(UiTheme.Muted, book.StatusText);
        else if (runner.State is ProductionState.Completed or ProductionState.Failed)
            UiTheme.StateBadge(runner.State.ToString(), false, runner.StatusText);
        else if (batch.State is BatchState.Completed or BatchState.Failed)
            UiTheme.StateBadge(batch.State.ToString(), false, batch.StatusText);

        if (planError.Length > 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(UiTheme.Danger, planError);
            ImGui.PopTextWrapPos();
        }

        if (plan != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.Muted, $"Plan · {plugin.RecipeProvider.GetItemName(plan.TargetItemId)} ×{plan.TargetQuantity}");
            OrdersPanel.DrawPlanPreview(plan, plugin.RecipeProvider);
        }
    }

    private void ProgressLink()
    {
        if (UiTheme.LinkButton("Progress ›"))
            SelectPage(Pages.StatusProgress);
        UiTheme.Tooltip("Pause, resume, stop after the step, materials");
    }

    private static (float Fraction, string Label) OverallProgress(ProductionRunner runner, BatchCrafter batch)
    {
        var stepFraction = batch.TargetQuantity > 0 ? (float)batch.CompletedCrafts / batch.TargetQuantity : 0f;
        var overall = runner.TotalSteps > 0
            ? (runner.CompletedSteps + Math.Clamp(stepFraction, 0f, 1f)) / runner.TotalSteps
            : 0f;
        var label = $"{overall * 100:F0}%  ·  step {Math.Min(runner.CompletedSteps + 1, Math.Max(runner.TotalSteps, 1))}/{runner.TotalSteps}";
        return (overall, label);
    }

    // ----------------------------------------------------------- progress

    private void DrawProgressPage()
    {
        var runner = plugin.ProductionRunner;
        var batch = plugin.BatchCrafter;
        var loop = plugin.GatheringLoop;

        if (RunnerActive)
            DrawRunnerActive(runner, batch);
        else if (BatchActive)
            DrawBatchActive(batch);
        else if (loop.State is Gathering.GatheringLoopState.Running or Gathering.GatheringLoopState.Paused)
            DrawGatherActive(loop);
        else
            DrawIdleStatus(runner, batch);

        DrawMaterials();

        ImGui.Spacing();
        if (UiTheme.Collapsible("Details"))
        {
            foreach (var line in plugin.OrderRunner.Describe())
                ImGui.BulletText(line);
            foreach (var line in runner.Describe())
                ImGui.BulletText(line);
            foreach (var line in batch.Describe())
                ImGui.BulletText(line);
            foreach (var line in loop.Describe())
                ImGui.BulletText(line);
        }
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

        var (overall, label) = OverallProgress(runner, batch);
        UiTheme.ProgressBar(overall, label);
        UiTheme.StateBadge(runner.State.ToString(), runner.State is ProductionState.Paused, runner.StatusText);
        if (bookDriving && book.State == OrderRunState.Held)
            UiTheme.StateBadge("Held", true, book.StatusText);

        if (batch.TargetQuantity > 0 && batch.State is not BatchState.Idle)
        {
            var stepFraction = batch.TargetQuantity > 0 ? (float)batch.CompletedCrafts / batch.TargetQuantity : 0f;
            UiTheme.ProgressBar(stepFraction, $"{batch.CompletedCrafts}/{batch.TargetQuantity} crafts", UiTheme.Info);
        }

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

    /// <summary>A standalone gather loop (started from the Debug page) gets the same controls.</summary>
    private void DrawGatherActive(Gathering.GatheringLoop loop)
    {
        var fraction = loop.TargetQuantity > 0 ? (float)loop.Gathered / loop.TargetQuantity : 0f;
        UiTheme.ProgressBar(fraction, $"{loop.Gathered}/{loop.TargetQuantity} gathered");
        UiTheme.StateBadge(loop.State.ToString(), loop.State == Gathering.GatheringLoopState.Paused, loop.StatusText);

        DrawPauseResumeStop(
            paused: loop.State == Gathering.GatheringLoopState.Paused,
            onPause: () => loop.Pause("paused by user"),
            onResume: loop.Resume,
            onStop: loop.Stop);
    }

    private void DrawIdleStatus(ProductionRunner runner, BatchCrafter batch)
    {
        var book = plugin.OrderRunner;
        if (book.State != OrderRunState.Idle)
            UiTheme.StateBadge(book.State.ToString(), book.State == OrderRunState.Held, book.StatusText);
        else if (runner.State is ProductionState.Completed or ProductionState.Failed)
            UiTheme.StateBadge(runner.State.ToString(), false, runner.StatusText);
        else if (batch.State is BatchState.Completed or BatchState.Failed)
            UiTheme.StateBadge(batch.State.ToString(), false, batch.StatusText);
        else
            ImGui.TextColored(UiTheme.Muted, "Nothing running.");

        if (UiTheme.LinkButton("Orders ›"))
            SelectPage(Pages.Orders);
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

        if (!ImGui.BeginTable("##materials", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("Ingredient");
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 54);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed, 84);
        ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        foreach (var requirement in requirements)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            UiTheme.GameIcon(plugin.RecipeProvider.GetItemIconId(requirement.ItemId), 18f);
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

    // ------------------------------------------------------------- report

    private void DrawReportPage()
    {
        if (UiTheme.TintedButton("Copy report", UiTheme.Accent))
            plugin.SaveAndCopyReport();
        UiTheme.Tooltip("Copies the full report to the clipboard and saves it in the plugin config folder (/cielcraft report). Paste it when reporting a problem.");
        ImGui.SameLine();
        if (UiTheme.LinkButton("Refresh"))
            BuildReport();
        ImGui.SameLine();
        ImGui.TextColored(UiTheme.Muted, $"built {reportBuiltAt.ToLocalTime():HH:mm:ss} · refreshes every {ReportRefresh.TotalSeconds:F0} s");

        // Cheap enough to rebuild while the page is in view; it is the same text the copy sends.
        if (DateTime.UtcNow - reportBuiltAt > ReportRefresh)
            BuildReport();

        ImGui.Spacing();
        for (var i = 0; i < reportSections.Count; i++)
        {
            var (title, body) = reportSections[i];
            if (!UiTheme.Collapsible($"{title}##report{i}", defaultOpen: i == 0))
                continue;

            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(body.Length == 0 ? "(empty)" : body);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
        }
    }

    /// <summary>Splits the report at its "=== title ===" rules; the header lines before the first rule form the first section.</summary>
    private void BuildReport()
    {
        var sections = new List<(string Title, string Body)>();
        string? title = null;
        var body = new StringBuilder();

        void Flush()
        {
            if (title != null)
                sections.Add((title, body.ToString().Trim()));
            body.Clear();
        }

        foreach (var raw in Diagnostics.DiagnosticReport.Build(plugin).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("=== ", StringComparison.Ordinal) && line.EndsWith(" ===", StringComparison.Ordinal))
            {
                Flush();
                title = line[4..^4];
                if (title == "end of report")
                {
                    title = null;
                    break;
                }

                continue;
            }

            body.AppendLine(line);
        }

        Flush();
        reportSections = sections;
        reportBuiltAt = DateTime.UtcNow;
    }

    // ----------------------------------------------------- solution cache

    private void DrawCachePage()
    {
        foreach (var line in plugin.SolverService.Cache.Describe())
            ImGui.BulletText(line);

        UiTheme.Hint("Solved rotations are reused when a recipe repeats with the same stats; clearing forces a fresh solve for every craft.");
        ImGui.Spacing();
        if (UiTheme.TintedButton("Clear solution cache", UiTheme.Danger))
            plugin.SolverService.ClearCache();
    }
}
