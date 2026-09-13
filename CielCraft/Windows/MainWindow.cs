using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CielCraft.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("CielCraft##Main")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextUnformatted("CielCraft is up and running.");
        ImGui.Spacing();

        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();
    }
}
