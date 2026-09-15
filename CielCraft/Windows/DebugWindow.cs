using System;
using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CielCraft.Windows;

/// <summary>Developer window showing live game/plugin state (spec §46).</summary>
public class DebugWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;

    public DebugWindow(Plugin plugin) : base("CielCraft Debug##Debug")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        gameBridge = plugin.GameBridge;
        craftMonitor = plugin.CraftMonitor;
    }

    public void Dispose() { }

    private string logFilter = "";

    public override void Draw()
    {
        if (UiTheme.TintedButton("Copy diagnostic report", UiTheme.Accent))
            plugin.SaveAndCopyReport();
        UiTheme.Tooltip("Copies a full state + log report to the clipboard and saves it in the plugin config folder (/cielcraft report). Paste it when reporting a problem.");
        ImGui.SameLine();
        ImGui.TextColored(UiTheme.Muted, $"log entries: {Plugin.Log.Snapshot().Count}");
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##debugTabs"))
            return;

        if (ImGui.BeginTabItem("Overview"))
        {
            DrawDependencies();
            UiTheme.SectionHeader("Player");
            DrawPlayer();
            UiTheme.SectionHeader("Capabilities");
            DrawCapabilities();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Crafting"))
        {
            DrawCraft();
            ImGui.Separator();
            DrawOrders();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Gathering"))
        {
            DrawGathering();
            UiTheme.SectionHeader("Automation");
            DrawGatherAutomation();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Navigation"))
        {
            DrawNavigation();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Log"))
        {
            DrawLog();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawLog()
    {
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##logFilter", "filter (e.g. [Batch], [Raphael], ERR)", ref logFilter, 64);
        ImGui.SameLine();
        ImGui.TextColored(UiTheme.Muted, "newest last; times are UTC");

        if (ImGui.BeginChild("##logEntries", new Vector2(0, 0), true))
        {
            var entries = Plugin.Log.Snapshot();
            var shown = 0;
            foreach (var entry in entries)
            {
                if (logFilter.Length > 0 && !entry.Message.Contains(logFilter, StringComparison.OrdinalIgnoreCase)
                    && !entry.Level.Contains(logFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var color = entry.Level switch
                {
                    "ERR" => UiTheme.Danger,
                    "WRN" => UiTheme.Warning,
                    "DBG" => UiTheme.Muted,
                    _ => new Vector4(0.90f, 0.90f, 0.92f, 1f),
                };
                ImGui.TextColored(color, entry.ToString());
                shown++;
            }

            if (shown == 0)
                ImGui.TextDisabled("Nothing logged yet.");

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4)
                ImGui.SetScrollHereY(1.0f);
        }

        ImGui.EndChild();
    }

    private void DrawGathering()
    {
        UiTheme.SectionHeader("Open node");

        var gathering = gameBridge.GetGatheringState();
        if (gathering == null)
        {
            ImGui.BulletText("No gathering node open.");
            return;
        }

        ImGui.BulletText($"Integrity: {gathering.IntegrityRemaining} / {gathering.IntegrityTotal}");
        ImGui.BulletText($"GP: {gathering.CurrentGp} / {gathering.MaxGp}");
        ImGui.BulletText($"Items ({gathering.Items.Count}):");

        foreach (var slot in gathering.Items)
        {
            ImGui.TextUnformatted(
                $"    [{slot.Index}] {plugin.RecipeProvider.GetItemName(slot.ItemId)} (id {slot.ItemId})" +
                (slot.Enabled ? "" : " — not gatherable"));
        }
    }

    private int gatherItemId;
    private int gatherQuantity = 1;

    private void DrawGatherAutomation()
    {
        

        var controller = plugin.GatheringController;
        var loop = plugin.GatheringLoop;
        var loopActive = loop.State is Gathering.GatheringLoopState.Running or Gathering.GatheringLoopState.Paused;
        var nodeActive = controller.State is Gathering.GatheringState.MovingToNode
            or Gathering.GatheringState.Interacting or Gathering.GatheringState.GatheringNode
            or Gathering.GatheringState.CollectableNode or Gathering.GatheringState.Paused;

        if (loopActive)
        {
            if (loop.State == Gathering.GatheringLoopState.Running)
            {
                if (ImGui.Button("Pause##gather"))
                    loop.Pause("paused by user");
            }
            else if (ImGui.Button("Resume##gather"))
            {
                loop.Resume();
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop##gather"))
                loop.Stop();
        }
        else if (nodeActive)
        {
            if (controller.State == Gathering.GatheringState.Paused)
            {
                if (ImGui.Button("Resume##gather"))
                    controller.Resume();
            }
            else if (ImGui.Button("Pause##gather"))
            {
                controller.Pause("paused by user");
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop##gather"))
                controller.Stop();
        }
        else
        {
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Item id (0 = first)", ref gatherItemId);
            if (gatherItemId < 0)
                gatherItemId = 0;

            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Quantity", ref gatherQuantity);
            gatherQuantity = Math.Clamp(gatherQuantity, 1, 9999);

            if (ImGui.Button("Gather nearest node"))
                controller.Start((uint)gatherItemId);

            ImGui.SameLine();
            using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(gatherItemId == 0))
            {
                if (ImGui.Button($"Gather ×{gatherQuantity}"))
                    loop.Start((uint)gatherItemId, gatherQuantity);
            }

            if (gatherItemId == 0)
                ImGui.TextDisabled("The loop needs a specific item id (see the open-node list above).");
        }

        ImGui.BulletText($"Loop: {loop.State} ({loop.Gathered}/{loop.TargetQuantity}) — {loop.StatusText}");
        ImGui.BulletText($"Node run: {controller.State} — {controller.StatusText}");
    }

    private Vector3 navDestination;

    private void DrawNavigation()
    {
        UiTheme.SectionHeader("Navigation");

        var nav = plugin.Navigation;
        ImGui.BulletText($"Available: {nav.IsAvailable}   Mesh ready: {nav.IsReady}   Moving: {nav.IsMoving}");

        ImGui.SetNextItemWidth(260);
        ImGui.InputFloat3("Destination", ref navDestination);

        if (ImGui.Button("Use current position"))
        {
            var player = gameBridge.GetPlayerState();
            if (player != null)
                navDestination = player.Position;
        }

        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!nav.IsReady))
        {
            if (ImGui.Button("Go (walk)"))
                nav.MoveTo(navDestination, fly: false);

            ImGui.SameLine();
            if (ImGui.Button("Go (fly)"))
                nav.MoveTo(navDestination, fly: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop##nav"))
            nav.Stop();
    }

    private void DrawDependencies()
    {
        UiTheme.SectionHeader("Dependencies");
        ImGui.BulletText($"Dalamud: Ready");
        ImGui.BulletText($"Raphael: {(CielCraft.Raphael.RaphaelSolver.IsAvailable ? "Ready" : "Native library missing")}");
        var nav = plugin.Navigation;
        ImGui.BulletText($"vnavmesh: {(!nav.IsAvailable ? "Unavailable — gathering automation disabled" : nav.IsReady ? "Ready" : "Installed, navmesh not ready")}");
    }

    private void DrawPlayer()
    {
        
        if (!gameBridge.IsLoggedIn)
        {
            ImGui.BulletText("Not logged in.");
            return;
        }

        var player = gameBridge.GetPlayerState();
        if (player == null)
        {
            ImGui.BulletText("No local player.");
            return;
        }

        ImGui.BulletText($"Name: {player.Name}");
        ImGui.BulletText($"Territory: {player.TerritoryId}");
        ImGui.BulletText($"Position: {player.Position.X:F1}, {player.Position.Y:F1}, {player.Position.Z:F1}");
        ImGui.BulletText($"Job: {player.ClassJobAbbreviation} (id {player.ClassJobId}) Lv. {player.Level}");
        ImGui.BulletText($"CP: {player.CurrentCp} / {player.MaxCp}");
        ImGui.BulletText($"Craftsmanship: {player.Craftsmanship}");
        ImGui.BulletText($"Control: {player.Control}");
    }

    /// <summary>Character capability snapshot (roadmap 7.16) with an on-demand refresh.</summary>
    private void DrawCapabilities()
    {
        if (ImGui.Button("Refresh##capabilities"))
            plugin.Capabilities.Refresh();
        UiTheme.Tooltip("Re-reads flight zones, master books, tribe ranks, GP-regen traits and job levels from the game. Also happens on login and before every production run.");

        foreach (var line in plugin.Capabilities.Describe())
            ImGui.BulletText(line);
    }

    private void DrawCraft()
    {
        UiTheme.SectionHeader("Craft state");
        ImGui.BulletText($"Preparing to craft: {gameBridge.IsPreparingToCraft}");
        ImGui.BulletText($"Crafting active: {gameBridge.IsCrafting}");
        ImGui.BulletText($"Gathering active: {gameBridge.IsGathering}");

        var craft = craftMonitor.Current;
        if (craft == null)
        {
            ImGui.BulletText("Craft state: n/a (no active synthesis)");
        }
        else
        {
            ImGui.BulletText($"Step: {craft.Step}");
            ImGui.BulletText($"Progress: {craft.Progress} / {craft.MaxProgress}");
            ImGui.BulletText($"Quality: {craft.Quality} / {craft.MaxQuality}");
            ImGui.BulletText($"Durability: {craft.Durability} / {craft.MaxDurability}");
            ImGui.BulletText($"CP: {craft.CurrentCp} / {craft.MaxCp}");
            ImGui.BulletText($"Condition: {craft.Condition}");
            if (craft.Buffs.Count > 0)
            {
                var parts = new List<string>();
                foreach (var buff in craft.Buffs)
                {
                    var stacks = buff.Stacks > 0 ? $"x{buff.Stacks}" : "";
                    var steps = buff.RemainingSteps > 0 ? $" ({buff.RemainingSteps} steps)" : "";
                    parts.Add($"{buff.StatusId}{stacks}{steps}");
                }

                ImGui.BulletText($"Buffs: {string.Join(", ", parts)}");
            }
        }

        ImGui.Separator();
        UiTheme.SectionHeader("Action execution");

        var executor = plugin.ActionExecutor;
        var player = gameBridge.GetPlayerState();
        var actionId = player != null ? CraftActionIds.BasicSynthesis(player.ClassJobId) : null;

        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(
                   actionId == null || !gameBridge.IsCrafting || executor.State != Crafting.ExecutorState.Idle))
        {
            if (ImGui.Button("Execute Basic Synthesis") && actionId != null)
                executor.TryExecute(actionId.Value);
        }

        ImGui.BulletText($"Executor: {executor.State}");
        ImGui.BulletText($"Last result: {executor.LastResult}");

        ImGui.Separator();
        DrawSolver(player, actionId != null);

        ImGui.Separator();
        UiTheme.SectionHeader("Recent craft events");

        if (ImGui.BeginChild("##craftEvents", new Vector2(0, 150), true))
        {
            var events = craftMonitor.RecentEvents;
            if (events.Count == 0)
                ImGui.TextUnformatted("None yet.");

            for (var i = events.Count - 1; i >= 0; i--)
                ImGui.TextUnformatted(events[i]);
        }

        ImGui.EndChild();
    }

    /// <summary>Order book runner state (roadmap 7.13): the book with every order's outcome, as the report prints it.</summary>
    private void DrawOrders()
    {
        UiTheme.SectionHeader("Orders");

        var orders = plugin.OrderRunner;
        if (orders.State is Crafting.OrderRunState.Running or Crafting.OrderRunState.Held)
        {
            if (orders.Running)
            {
                if (ImGui.Button("Hold##orders"))
                    orders.Hold();
            }
            else if (ImGui.Button("Run##orders"))
            {
                orders.Start();
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop##orders"))
                orders.Stop();
        }
        else if (ImGui.Button("Run orders##orders"))
        {
            orders.Start();
        }

        foreach (var line in orders.Describe())
            ImGui.BulletText(line);
    }

    private void DrawSolver(PlayerSnapshot? player, bool onCrafterJob)
    {
        UiTheme.SectionHeader("Raphael solver");

        var solverService = plugin.SolverService;
        var craft = craftMonitor.Current;
        var canSolve = CielCraft.Raphael.RaphaelSolver.IsAvailable
                       && craft != null && player != null && onCrafterJob
                       && solverService.Status != Crafting.SolverStatus.Solving;

        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!canSolve))
        {
            if (ImGui.Button("Solve current craft") && craft != null && player != null)
            {
                var setup = new CraftSetup(
                    RecipeLevel: craft.RecipeLevel,
                    MaxProgress: (ushort)craft.MaxProgress,
                    MaxQuality: (ushort)craft.MaxQuality,
                    MaxDurability: (ushort)craft.MaxDurability,
                    IsExpert: false,
                    Craftsmanship: (ushort)player.Craftsmanship,
                    Control: (ushort)player.Control,
                    Cp: (ushort)player.MaxCp,
                    Level: (byte)player.Level,
                    Manipulation: player.Level >= 65,
                    HeartAndSoul: false,
                    QuickInnovation: false);

                var objective = new CraftObjective(
                    TargetQuality: (ushort)craft.MaxQuality,
                    InitialQuality: (ushort)craft.Quality);

                solverService.BeginSolve(setup, objective);
            }
        }

        ImGui.BulletText($"Status: {solverService.StatusText}");

        var solution = solverService.Solution;
        if (solution is not { Success: true })
            return;

        if (ImGui.BeginChild("##raphaelSolution", new Vector2(0, 150), true))
        {
            for (var i = 0; i < solution.ActionIds.Count; i++)
                ImGui.TextUnformatted($"{i + 1,2}. {RaphaelActionNames.NameOf(solution.ActionIds[i])}");
        }

        ImGui.EndChild();

        ImGui.Separator();
        DrawAutomation(player, solution);
    }

    private void DrawAutomation(PlayerSnapshot? player, CraftSolution solution)
    {
        UiTheme.SectionHeader("Auto craft");

        var automator = plugin.CraftAutomator;

        switch (automator.State)
        {
            case Crafting.AutomationState.Running:
                if (ImGui.Button("Pause"))
                    automator.Pause("paused by user");
                ImGui.SameLine();
                if (ImGui.Button("Stop"))
                    automator.Stop();
                break;

            case Crafting.AutomationState.Paused:
                if (ImGui.Button("Resume"))
                    automator.Resume();
                ImGui.SameLine();
                if (ImGui.Button("Stop"))
                    automator.Stop();
                break;

            default:
                var canStart = player != null && gameBridge.IsCrafting;
                using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!canStart))
                {
                    if (ImGui.Button("Run rotation") && player != null)
                        automator.Start(solution.ActionIds, player.ClassJobId, solution.BaseProgress);
                }

                break;
        }

        ImGui.BulletText($"State: {automator.State} ({automator.CompletedActions}/{automator.TotalActions})");
        ImGui.BulletText($"Status: {automator.StatusText}");

        if (automator.NextRaphaelAction is { } next)
            ImGui.BulletText($"Next action: {RaphaelActionNames.NameOf(next)}");
    }
}
