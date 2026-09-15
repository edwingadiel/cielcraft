using System;
using System.Collections.Generic;
using CielCraft.Core;
using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// The Settings pages (7.21): one draw method per sub-page, each a plain
/// list of controls that save on change. The consumable sets are drawn by
/// <see cref="ConsumablesPanel"/>; the repair settings and the legacy single
/// food live on that page too, the food folded away until 7.11 migrates it.
/// </summary>
internal sealed class SettingsPanel
{
    private static readonly (CraftingLocation Value, string Label, string Explanation)[] Locations =
    [
        (CraftingLocation.Stay, "Stay where you are", "Craft steps run wherever the character is; after gathering, at the zone's aetheryte."),
        (CraftingLocation.EstateHall, "Estate hall", "Teleport to the free company or private estate hall before the first craft step."),
        (CraftingLocation.Apartment, "Apartment", "Teleport to the apartment before the first craft step."),
        (CraftingLocation.InnRoom, "Inn room", "Teleport to the nearest city and craft in the inn room; falls back to staying put when the inn cannot be reached."),
    ];

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly ConsumablesPanel consumables;

    private string foodSearch = "";
    private IReadOnlyList<(uint ItemId, string Name)> foodResults = [];

    public SettingsPanel(Plugin plugin)
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;
        consumables = new ConsumablesPanel(plugin);
    }

    private void Save() => configuration.Save();

    // ------------------------------------------------------------ general

    public void DrawGeneral()
    {
        UiTheme.Toggle("Open the main window on login", configuration.OpenMainWindowOnLogin,
            v => { configuration.OpenMainWindowOnLogin = v; Save(); });

        UiTheme.Toggle("Chat notifications", configuration.ChatNotifications,
            v => { configuration.ChatNotifications = v; Save(); },
            "Completed, paused and failed production is announced in the game chat.");

        UiTheme.Toggle("Exit the game when the orders complete", configuration.ExitGameWhenDone,
            v => { configuration.ExitGameWhenDone = v; Save(); });
        if (configuration.ExitGameWhenDone)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(UiTheme.Warning, "Sends /shutdown a few seconds after the last production completes. Failures and gentle stops never exit.");
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        UiTheme.Hint("Sounds and speech are under Alerts; the first-run checklist is Tools › Character.");
    }

    // ----------------------------------------------------------- crafting

    public void DrawCrafting()
    {
        UiTheme.Toggle("Adaptive crafting", configuration.AdaptiveCrafting,
            v => { configuration.AdaptiveCrafting = v; Save(); },
            "Deviate from the solved rotation when the live craft state makes it safe and profitable (e.g. finish early once quality caps).");

        UiTheme.Toggle("Quick synthesis for intermediates", configuration.QuickSynthIntermediates,
            v => { configuration.QuickSynthIntermediates = v; Save(); },
            "Bulk-produce intermediate materials with quick synthesis (much faster; intermediates come out normal quality).");

        var targetQuality = configuration.TargetQualityPercent;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Target quality %", ref targetQuality, 10, 100))
        {
            configuration.TargetQualityPercent = Math.Clamp(targetQuality, 10, 100);
            Save();
        }

        UiTheme.Hint("Solve and craft toward this fraction of the recipe's maximum quality instead of always aiming for 100%. Force HQ orders and collectable tiers override it.");

        UiTheme.Toggle("Fill HQ materials automatically", configuration.PreferHqMaterials,
            v => { configuration.PreferHqMaterials = v; Save(); },
            "Use HQ ingredients from the bag where they raise the starting quality.");

        UiTheme.Toggle("HQ intermediates when needed", configuration.HqIntermediates,
            v => { configuration.HqIntermediates = v; Save(); },
            "Before a run, check whether each final recipe reaches its quality target from NQ materials; if not, craft just enough intermediates HQ to seed the starting quality (7.22).");

        UiTheme.SectionHeader("Hands-on (7.18)");

        UiTheme.Toggle("Assist mode", configuration.AssistMode,
            v => { configuration.AssistMode = v; Save(); },
            "A synthesis you start by hand is picked up and run by the plugin; nothing starts on its own.");

        UiTheme.Toggle("Lock-step", configuration.LockStep,
            v => { configuration.LockStep = v; Save(); },
            "Pause before every craft action and wait for Step on the Crafting Steps page; for watching a rotation one action at a time.");

        UiTheme.SectionHeader("Materia (7.2)");
        SpiritbondPanel.DrawSettings(configuration);
    }

    // ---------------------------------------------------------- gathering

    public void DrawGathering()
    {
        UiTheme.Toggle("Gathering yield/integrity actions", configuration.UseGatheringBuffs,
            v => { configuration.UseGatheringBuffs = v; Save(); },
            "Spend GP on the job's yield and integrity actions when the node is worth it.");

        UiTheme.Toggle("Drink cordials between nodes", configuration.UseCordials,
            v => { configuration.UseCordials = v; Save(); },
            "Uses a cordial from the bag while walking to the next node when GP is low.");

        UiTheme.SectionHeader("Timed nodes (7.15)");
        SchedulePanel.DrawSettings(configuration);

        UiTheme.SectionHeader("Rotations (7.14)");
        GatheringRotationPanel.DrawSettings(configuration);
    }

    // -------------------------------------------------------- consumables

    public void DrawConsumables()
    {
        consumables.Draw();

        UiTheme.SectionHeader("Repair");

        UiTheme.Toggle("Self-repair with Dark Matter", configuration.AutoRepair,
            v => { configuration.AutoRepair = v; Save(); });

        var threshold = configuration.RepairThresholdPercent;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Repair below %", ref threshold, 5, 90))
        {
            configuration.RepairThresholdPercent = Math.Clamp(threshold, 5, 90);
            Save();
        }

        UiTheme.Hint("Checked between crafts and nodes, never mid-action.");

        ImGui.Spacing();
        DrawLegacyFood();
    }

    /// <summary>
    /// The pre-7.11 single food. Folded away rather than removed: the
    /// consumables package migrates it into the sets above and empties it.
    /// </summary>
    private void DrawLegacyFood()
    {
        var configured = configuration.FoodItemId != 0;
        if (!ImGui.CollapsingHeader(configured ? "Legacy food (pre-7.11, still applied)" : "Legacy food (pre-7.11)"))
            return;

        UiTheme.Hint("The single food buff from before per-activity consumables; it moves into the sets above automatically.");

        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##foodSearch", "Search food to maintain…", ref foodSearch, 64))
            foodResults = plugin.RecipeProvider.SearchFood(foodSearch);

        foreach (var food in foodResults)
        {
            if (ImGui.Selectable($"{food.Name}##food{food.ItemId}"))
            {
                configuration.FoodItemId = food.ItemId;
                Save();
                foodSearch = "";
                foodResults = [];
            }
        }

        if (!configured)
        {
            ImGui.TextDisabled("No legacy food configured.");
            return;
        }

        ImGui.TextColored(UiTheme.Accent, "◈");
        ImGui.SameLine(0, 6);
        ImGui.TextUnformatted(plugin.RecipeProvider.GetItemName(configuration.FoodItemId));
        ImGui.SameLine(0, 10);
        if (ImGui.SmallButton("×##clearFood"))
        {
            configuration.FoodItemId = 0;
            Save();
        }

        UiTheme.Toggle("Use the HQ version", configuration.FoodIsHq,
            v => { configuration.FoodIsHq = v; Save(); });
    }

    // --------------------------------------------------------------- home

    /// <summary>Roadmap 7.6: where craft steps happen.</summary>
    public void DrawHome()
    {
        var current = Array.FindIndex(Locations, l => l.Value == configuration.CraftingLocation);
        if (current < 0)
            current = 0;

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Craft at", Locations[current].Label))
        {
            foreach (var location in Locations)
            {
                var selected = location.Value == configuration.CraftingLocation;
                if (ImGui.Selectable(location.Label, selected))
                {
                    configuration.CraftingLocation = location.Value;
                    Save();
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        UiTheme.Hint(Locations[current].Explanation);
        ImGui.Spacing();
        UiTheme.Hint("The teleport happens once per run, after gathering and before the first craft step; when no such aetheryte exists the run crafts in place and says so in the log.");
    }

    // ------------------------------------------------------------- alerts

    /// <summary>Sound and speech when a run needs the user (roadmap 7.20).</summary>
    public void DrawAlerts()
    {
        var sound = configuration.AlertSoundEffect;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Sound effect##alertSound", ref sound, 0, 16, sound == 0 ? "off" : $"<se.{sound}>"))
        {
            configuration.AlertSoundEffect = Math.Clamp(sound, 0, 16);
            Save();
        }

        UiTheme.Tooltip("Played in game when a run completes or stops for you (the same <se.N> sounds as chat macros).");

        UiTheme.Toggle("Read alerts aloud (Windows speech)", configuration.SpeakAlerts,
            v => { configuration.SpeakAlerts = v; Save(); });

        ImGui.SameLine();
        if (ImGui.SmallButton("Test##alert"))
            plugin.Notifier.Alert("CielCraft test alert.");

        UiTheme.Hint("Alerts fire on completion, on a pause that needs you (materials, social, a failed step) and on failure.");
    }

    // ------------------------------------------------------------- social

    /// <summary>Roadmap 7.10: what to do when another player pokes a running character.</summary>
    public void DrawSocial()
    {
        UiTheme.Toggle("Decline trade requests during a run", configuration.SocialDeclineTrades,
            v => { configuration.SocialDeclineTrades = v; Save(); });

        UiTheme.Toggle("Remember who sent them (plugin blacklist)", configuration.SocialBlacklistTraders,
            v => { configuration.SocialBlacklistTraders = v; Save(); },
            "The game blacklist cannot be written by plugins; repeat senders are declined on sight without another pause.");

        var pauseSeconds = configuration.SocialPauseSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Pause after a trade (s)", ref pauseSeconds, 0, 120))
        {
            configuration.SocialPauseSeconds = Math.Clamp(pauseSeconds, 0, 120);
            Save();
        }

        UiTheme.Hint("Hesitate before carrying on, like a person would; 0 keeps going.");

        UiTheme.Toggle("Log tells from strangers", configuration.SocialLogTells,
            v => { configuration.SocialLogTells = v; Save(); });

        UiTheme.Toggle("Log party and free company invites", configuration.SocialLogInvites,
            v => { configuration.SocialLogInvites = v; Save(); },
            "Logged only (sender and time); never answered automatically.");

        UiTheme.SectionHeader("Blacklist");

        if (configuration.SocialBlacklist.Count == 0)
        {
            ImGui.TextDisabled("Blacklist is empty.");
            return;
        }

        ImGui.TextUnformatted($"{configuration.SocialBlacklist.Count} remembered");
        for (var i = 0; i < configuration.SocialBlacklist.Count; i++)
        {
            var entry = configuration.SocialBlacklist[i];
            ImGui.TextColored(UiTheme.Muted, "•");
            ImGui.SameLine(0, 6);
            ImGui.TextUnformatted(entry.World != null ? $"{entry.Name} @ {entry.World}" : entry.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{entry.Reason}\n{entry.AddedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            ImGui.SameLine(0, 10);
            if (ImGui.SmallButton($"×##blacklist{i}"))
            {
                configuration.SocialBlacklist.RemoveAt(i);
                Save();
                break;
            }
        }
    }
}
