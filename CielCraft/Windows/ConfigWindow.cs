using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CielCraft.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("CielCraft Settings##Config")
    {
        Size = new Vector2(360, 160);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var openOnLogin = configuration.OpenMainWindowOnLogin;
        if (ImGui.Checkbox("Open main window on login", ref openOnLogin))
        {
            configuration.OpenMainWindowOnLogin = openOnLogin;
            configuration.Save();
        }

        var adaptive = configuration.AdaptiveCrafting;
        if (ImGui.Checkbox("Adaptive crafting", ref adaptive))
        {
            configuration.AdaptiveCrafting = adaptive;
            configuration.Save();
        }

        ImGui.TextDisabled("Deviate from the solved rotation when the live craft state\nmakes it safe and profitable (e.g. finish early once quality caps).");

        var quick = configuration.QuickSynthIntermediates;
        if (ImGui.Checkbox("Quick synthesis for intermediates", ref quick))
        {
            configuration.QuickSynthIntermediates = quick;
            configuration.Save();
        }

        ImGui.TextDisabled("Bulk-produce intermediate materials with quick synthesis\n(much faster; intermediates come out normal quality).");
    }
}
