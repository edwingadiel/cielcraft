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

    public DebugWindow(Plugin plugin) : base("CielCraft Debug##Debug")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        gameBridge = plugin.GameBridge;
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

        var craft = gameBridge.GetCraftState();
        if (craft == null)
        {
            ImGui.BulletText("Craft state: n/a (reader lands in Milestone 1)");
            return;
        }

        ImGui.BulletText($"Step: {craft.Step}");
        ImGui.BulletText($"Progress: {craft.Progress} / {craft.MaxProgress}");
        ImGui.BulletText($"Quality: {craft.Quality} / {craft.MaxQuality}");
        ImGui.BulletText($"Durability: {craft.Durability} / {craft.MaxDurability}");
        ImGui.BulletText($"CP: {craft.CurrentCp} / {craft.MaxCp}");
        ImGui.BulletText($"Condition: {craft.Condition}");
    }
}
