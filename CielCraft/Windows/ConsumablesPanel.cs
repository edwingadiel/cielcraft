using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// Per-activity food and potion (roadmap 7.11): the crafting and gathering
/// consumable sets, each picked from the inventory with a searchable picker
/// that shows the buff, plus the repair settings that share the "keep the
/// character ready" theme. Hosted by the Settings page. Stub filled by the
/// consumables package.
/// </summary>
public sealed class ConsumablesPanel
{
    private readonly Plugin plugin;

    public ConsumablesPanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextDisabled("Consumable sets are coming in this milestone.");
    }
}
