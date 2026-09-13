using System;
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

    public override void Draw()
    {
        DrawDependencies();
        ImGui.Separator();
        DrawPlayer();
        ImGui.Separator();
        DrawCraft();
        ImGui.Separator();
        DrawNavigation();
    }

    private Vector3 navDestination;

    private void DrawNavigation()
    {
        ImGui.TextUnformatted("Navigation (Milestone 10)");

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
        ImGui.TextUnformatted("Dependencies");
        ImGui.BulletText($"Dalamud: Ready");
        ImGui.BulletText($"Raphael: {(CielCraft.Raphael.RaphaelSolver.IsAvailable ? "Ready" : "Native library missing")}");
        var nav = plugin.Navigation;
        ImGui.BulletText($"vnavmesh: {(!nav.IsAvailable ? "Unavailable — gathering automation disabled" : nav.IsReady ? "Ready" : "Installed, navmesh not ready")}");
    }

    private void DrawPlayer()
    {
        ImGui.TextUnformatted("Player");

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

    private void DrawCraft()
    {
        ImGui.TextUnformatted("Crafting");
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
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Action execution (Milestone 2)");

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
        ImGui.TextUnformatted("Recent craft events");

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

    private void DrawSolver(PlayerSnapshot? player, bool onCrafterJob)
    {
        ImGui.TextUnformatted("Raphael solver (Milestone 3)");

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
                ImGui.TextUnformatted($"{i + 1,2}. {CielCraft.Raphael.RaphaelActionNames.NameOf(solution.ActionIds[i])}");
        }

        ImGui.EndChild();

        ImGui.Separator();
        DrawAutomation(player, solution);
    }

    private void DrawAutomation(PlayerSnapshot? player, CraftSolution solution)
    {
        ImGui.TextUnformatted("Auto craft (Milestone 4)");

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
            ImGui.BulletText($"Next action: {CielCraft.Raphael.RaphaelActionNames.NameOf(next)}");
    }
}
