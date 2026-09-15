using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// Per-activity food and potion (roadmap 7.11): the crafting and gathering
/// consumable sets, each slot picked from the bag with a searchable picker
/// that shows the buff, the HQ preference, the remaining buff time and a
/// clear button. Every change saves straight away. Hosted by the Settings
/// page; the repair settings stay with the layout package.
/// </summary>
public sealed class ConsumablesPanel
{
    /// <summary>Search state of one picker; four of them (two sets × two slots).</summary>
    private sealed class Picker
    {
        public string Query = "";
        public IReadOnlyList<ConsumableItem> Results = [];
    }

    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;
    private readonly Picker craftingFood = new();
    private readonly Picker craftingPotion = new();
    private readonly Picker gatheringFood = new();
    private readonly Picker gatheringPotion = new();

    public ConsumablesPanel(Plugin plugin)
    {
        this.plugin = plugin;
        gameBridge = plugin.GameBridge;
    }

    private Configuration Configuration => plugin.Configuration;

    public void Draw()
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(UiTheme.Muted,
            "Before each craft step or gather task the matching set is kept up: food by Well Fed, the potion by Medicated. "
            + "A missing item pauses the run with its name; an item owned only in the other quality is used as is.");
        ImGui.PopTextWrapPos();

        UiTheme.SectionHeader("Crafting");
        DrawSet(Configuration.CraftingConsumables, craftingFood, craftingPotion, "crafting");

        UiTheme.SectionHeader("Gathering");
        DrawSet(Configuration.GatheringConsumables, gatheringFood, gatheringPotion, "gathering");
    }

    private void DrawSet(ConsumableSet set, Picker food, Picker potion, string id)
    {
        DrawSlot("Food", set.Food, ConsumableKind.Food, food, gameBridge.GetFoodBuffRemainingSeconds(), $"{id}Food");
        ImGui.Spacing();
        DrawSlot("Potion", set.Potion, ConsumableKind.Medicine, potion, gameBridge.GetMedicatedRemainingSeconds(), $"{id}Potion");
    }

    private void DrawSlot(string label, Consumable choice, ConsumableKind kind, Picker picker, float remainingSeconds, string id)
    {
        ImGui.TextColored(UiTheme.Muted, label);
        ImGui.SameLine(70);

        if (choice.ItemId != 0)
        {
            var owned = gameBridge.GetItemCount(choice.ItemId);
            var hqOwned = gameBridge.GetHqItemCount(choice.ItemId);
            ImGui.TextColored(UiTheme.Accent, "◈");
            ImGui.SameLine(0, 6);
            ImGui.TextUnformatted(plugin.RecipeProvider.GetItemName(choice.ItemId));
            UiTheme.Tooltip(BuffOf(choice.ItemId, choice.Hq));

            ImGui.SameLine(0, 10);
            var hq = choice.Hq;
            if (ImGui.Checkbox($"HQ##{id}Hq", ref hq))
            {
                choice.Hq = hq;
                Configuration.Save();
            }

            UiTheme.Tooltip("Prefer the HQ version; the other quality is used when it is the only one in the bag.");

            ImGui.SameLine(0, 10);
            if (owned == 0)
                ImGui.TextColored(UiTheme.Danger, "not in the bag");
            else
                ImGui.TextColored(UiTheme.Muted, $"×{owned - hqOwned} NQ / ×{hqOwned} HQ");

            ImGui.SameLine(0, 10);
            if (remainingSeconds > 0)
                ImGui.TextColored(UiTheme.Success, $"{(kind == ConsumableKind.Food ? "Well Fed" : "Medicated")} {FormatTime(remainingSeconds)}");
            else
                ImGui.TextColored(UiTheme.Faint, "buff inactive");

            ImGui.SameLine(0, 10);
            if (ImGui.SmallButton($"×##{id}Clear"))
            {
                choice.ItemId = 0;
                choice.Hq = true;
                picker.Query = "";
                picker.Results = [];
                Configuration.Save();
            }

            UiTheme.Tooltip("Clear this slot; the run continues without the buff.");
        }
        else
        {
            ImGui.TextDisabled(kind == ConsumableKind.Food ? "none — runs without a food buff" : "none — runs without a potion");
        }

        ImGui.Indent(70);
        ImGui.SetNextItemWidth(260);
        var hint = kind == ConsumableKind.Food ? "Search meals in the bag…" : "Search medicine in the bag…";
        if (ImGui.InputTextWithHint($"##{id}Search", hint, ref picker.Query, 64))
            picker.Results = Search(kind, picker.Query);

        // Results only while a query is typed; a pick settles item and HQ at once.
        if (picker.Query.Length > 0)
        {
            if (picker.Results.Count == 0)
                ImGui.TextDisabled("nothing in the bag matches");

            foreach (var item in picker.Results)
            {
                var line = $"{item.Name} ({(item.IsHq ? "HQ" : "NQ")} ×{item.Count})";
                if (ImGui.Selectable($"{line}##{id}{item.ItemId}{(item.IsHq ? "h" : "n")}"))
                {
                    choice.ItemId = item.ItemId;
                    choice.Hq = item.IsHq;
                    picker.Query = "";
                    picker.Results = [];
                    Configuration.Save();
                    break;
                }

                UiTheme.Tooltip(item.BuffSummary);
                ImGui.SameLine();
                ImGui.TextColored(UiTheme.Faint, item.BuffSummary);
            }
        }

        ImGui.Unindent(70);
    }

    /// <summary>The bag's stacks of the kind whose name contains the query; every stack when the query is blank.</summary>
    private IReadOnlyList<ConsumableItem> Search(ConsumableKind kind, string query)
    {
        var needle = query.Trim();
        var results = new List<ConsumableItem>();
        foreach (var item in gameBridge.ListConsumables())
        {
            if (item.Kind == kind && (needle.Length == 0 || item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                results.Add(item);
        }

        return results;
    }

    private string BuffOf(uint itemId, bool hq)
    {
        foreach (var item in gameBridge.ListConsumables())
        {
            if (item.ItemId == itemId && item.IsHq == hq)
                return item.BuffSummary;
        }

        return "Buff shown once the item is in the bag.";
    }

    private static string FormatTime(float seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }
}
