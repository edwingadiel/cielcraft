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

    private int batchQuantity = 1;

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawCharacter();
        ImGui.Separator();
        DrawBatch();
        ImGui.Separator();

        if (ImGui.Button("Debug"))
            plugin.ToggleDebugUi();

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();
    }

    private void DrawBatch()
    {
        ImGui.TextUnformatted("Batch craft");

        var batch = plugin.BatchCrafter;

        switch (batch.State)
        {
            case Crafting.BatchState.Solving:
            case Crafting.BatchState.StartingCraft:
            case Crafting.BatchState.Crafting:
                if (ImGui.Button("Pause"))
                    batch.Pause("paused by user");
                ImGui.SameLine();
                if (ImGui.Button("Stop"))
                    batch.Stop();
                break;

            case Crafting.BatchState.Paused:
                if (ImGui.Button("Resume"))
                    batch.Resume();
                ImGui.SameLine();
                if (ImGui.Button("Stop"))
                    batch.Stop();
                break;

            default:
                ImGui.SetNextItemWidth(100);
                if (ImGui.InputInt("Quantity", ref batchQuantity))
                    batchQuantity = Math.Clamp(batchQuantity, 1, 999);

                var canStart = CielCraft.Raphael.RaphaelSolver.IsAvailable
                               && (gameBridge.IsReadyToStartCraft
                                   || (gameBridge.IsCrafting && plugin.CraftMonitor.Current is { Step: <= 1, Quality: 0 }));

                using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!canStart))
                {
                    if (ImGui.Button("Start batch"))
                        batch.Start(batchQuantity);
                }

                if (!canStart)
                    ImGui.TextDisabled("Select a recipe in the crafting log to enable.");

                break;
        }

        ImGui.TextUnformatted($"Progress: {batch.CompletedCrafts}/{batch.TargetQuantity}   State: {batch.State}");
        ImGui.TextUnformatted(batch.StatusText);
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
