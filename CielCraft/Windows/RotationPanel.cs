using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// Rotation visibility and manual rotations (roadmap 7.8): the solved or
/// manual action list with executed / current / pending marks and the
/// expected outcome, plus the per-recipe manual rotation editor and the
/// assist / lock-step controls (7.18). Hosted by the Status page ("Crafting
/// Steps"). Stub filled by the rotations package.
/// </summary>
public sealed class RotationPanel
{
    private readonly Plugin plugin;

    public RotationPanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextDisabled("Rotation view is coming in this milestone.");
    }
}
