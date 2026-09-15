using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// Craft test (roadmap 7.18): solve for chosen stats and a recipe without
/// crafting, and show the rotation with expected progress / quality / HQ
/// chance and the solve time. Hosted by the Tools page. Stub filled by the
/// rotations package.
/// </summary>
public sealed class CraftTestPanel
{
    private readonly Plugin plugin;

    public CraftTestPanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextDisabled("Craft test is coming in this milestone.");
    }
}
