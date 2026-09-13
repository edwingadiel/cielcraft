using System;
using System.Numerics;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CielCraft.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;

    public MainWindow(Plugin plugin) : base("CielCraft##Main")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        gameBridge = plugin.GameBridge;
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawCharacter();
        ImGui.Separator();

        if (ImGui.Button("Debug"))
            plugin.ToggleDebugUi();

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();
    }

    private static void DrawStatus()
    {
        ImGui.TextUnformatted("Status");
        StatusLine("Dalamud", true, "Ready");
        var raphael = CielCraft.Raphael.RaphaelSolver.IsAvailable;
        StatusLine("Raphael", raphael, raphael ? "Ready" : "Native library missing");

        var nav = Plugin.IsVNavmeshAvailable;
        StatusLine("vnavmesh", nav, nav ? "Ready" : "Unavailable");
        StatusLine("Gathering automation", nav, nav ? "Ready" : "Disabled (vnavmesh missing)");
    }

    private static void StatusLine(string label, bool ok, string text)
    {
        var color = ok
            ? new Vector4(0.4f, 0.9f, 0.4f, 1f)
            : new Vector4(0.9f, 0.7f, 0.3f, 1f);

        ImGui.TextUnformatted($"{label}:");
        ImGui.SameLine(180);
        ImGui.TextColored(color, text);
    }

    private void DrawCharacter()
    {
        ImGui.TextUnformatted("Character");

        var player = gameBridge.GetPlayerState();
        if (player == null)
        {
            ImGui.TextUnformatted("Not logged in.");
            return;
        }

        ImGui.TextUnformatted($"{player.Name} — {player.ClassJobAbbreviation} Lv. {player.Level}");
        ImGui.TextUnformatted($"Craftsmanship: {player.Craftsmanship}   Control: {player.Control}   CP: {player.CurrentCp} / {player.MaxCp}");
    }
}
