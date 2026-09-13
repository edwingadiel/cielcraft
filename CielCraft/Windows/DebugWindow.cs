using System;
using System.Numerics;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CielCraft.Windows;

/// <summary>Developer window showing live game/plugin state (spec §46).</summary>
public class DebugWindow : Window, IDisposable
{
    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;

    public DebugWindow(Plugin plugin) : base("CielCraft Debug##Debug")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

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
    }

    private void DrawDependencies()
    {
        ImGui.TextUnformatted("Dependencies");
        ImGui.BulletText($"Dalamud: Ready");
        ImGui.BulletText($"Raphael: Not integrated (Milestone 3)");
        ImGui.BulletText($"vnavmesh: {(Plugin.IsVNavmeshAvailable ? "Available" : "Unavailable — gathering automation disabled")}");
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
}
