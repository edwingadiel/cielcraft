using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// Production breakdown (roadmap 7.12): the resolved graph as a tree with
/// per-node crafts × yield, job, need / owned / missing, and roll-ups; live
/// progress ticks during a run. Hosted by the Status page; also the text
/// behind "/cielcraft plan" and the report. Stub filled by the tree package.
/// </summary>
public sealed class PlanTreePanel
{
    private readonly Plugin plugin;

    public PlanTreePanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextDisabled("Production breakdown is coming in this milestone.");
    }

    /// <summary>The current (or previewed) plan as indented text lines; "No plan." when there is none.</summary>
    public static string PlanText(Plugin plugin) => "No plan.";
}
