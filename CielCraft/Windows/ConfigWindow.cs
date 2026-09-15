using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CielCraft.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    private string foodSearch = "";
    private System.Collections.Generic.IReadOnlyList<(uint ItemId, string Name)> foodResults = [];

    public ConfigWindow(Plugin plugin) : base("CielCraft Settings##Config")
    {
        Size = new Vector2(420, 420);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        UiTheme.SectionHeader("General");
        var openOnLogin = configuration.OpenMainWindowOnLogin;
        if (ImGui.Checkbox("Open main window on login", ref openOnLogin))
        {
            configuration.OpenMainWindowOnLogin = openOnLogin;
            configuration.Save();
        }

        UiTheme.SectionHeader("Crafting");
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

        if (ImGui.Button("Clear solution cache"))
            plugin.SolverService.ClearCache();

        ImGui.SameLine(0, 10);
        ImGui.TextDisabled($"{plugin.SolverService.Cache.Count} cached rotations");
        ImGui.TextDisabled("Solved rotations are reused when a recipe repeats with the same\nstats; clearing forces a fresh solve for every craft.");

        UiTheme.SectionHeader("Gathering");
        var buffs = configuration.UseGatheringBuffs;
        if (ImGui.Checkbox("Gathering yield/integrity actions", ref buffs))
        {
            configuration.UseGatheringBuffs = buffs;
            configuration.Save();
        }

        var cordials = configuration.UseCordials;
        if (ImGui.Checkbox("Drink cordials between nodes", ref cordials))
        {
            configuration.UseCordials = cordials;
            configuration.Save();
        }

        var targetQuality = configuration.TargetQualityPercent;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Target quality %", ref targetQuality, 10, 100))
        {
            configuration.TargetQualityPercent = Math.Clamp(targetQuality, 10, 100);
            configuration.Save();
        }

        ImGui.TextDisabled("Solve and craft toward this fraction of the recipe's maximum\nquality instead of always aiming for 100%.");

        var preferHq = configuration.PreferHqMaterials;
        if (ImGui.Checkbox("Fill HQ materials automatically", ref preferHq))
        {
            configuration.PreferHqMaterials = preferHq;
            configuration.Save();
        }

        UiTheme.SectionHeader("Unattended runs");

        var autoRepair = configuration.AutoRepair;
        if (ImGui.Checkbox("Self-repair with Dark Matter", ref autoRepair))
        {
            configuration.AutoRepair = autoRepair;
            configuration.Save();
        }

        var threshold = configuration.RepairThresholdPercent;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Repair below %", ref threshold, 5, 90))
        {
            configuration.RepairThresholdPercent = Math.Clamp(threshold, 5, 90);
            configuration.Save();
        }

        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##foodSearch", "Search food to maintain…", ref foodSearch, 64))
            foodResults = plugin.RecipeProvider.SearchFood(foodSearch);

        foreach (var food in foodResults)
        {
            if (ImGui.Selectable($"{food.Name}##food{food.ItemId}"))
            {
                configuration.FoodItemId = food.ItemId;
                configuration.Save();
                foodSearch = "";
                foodResults = [];
            }
        }

        if (configuration.FoodItemId != 0)
        {
            ImGui.TextColored(UiTheme.Accent, "◈");
            ImGui.SameLine(0, 6);
            ImGui.TextUnformatted(plugin.RecipeProvider.GetItemName(configuration.FoodItemId));
            ImGui.SameLine(0, 10);
            if (ImGui.SmallButton("×##clearFood"))
            {
                configuration.FoodItemId = 0;
                configuration.Save();
            }

            var foodHq = configuration.FoodIsHq;
            if (ImGui.Checkbox("Use the HQ version", ref foodHq))
            {
                configuration.FoodIsHq = foodHq;
                configuration.Save();
            }
        }
        else
        {
            ImGui.TextDisabled("No food configured — automation runs without a food buff.");
        }

        var chat = configuration.ChatNotifications;
        if (ImGui.Checkbox("Chat notifications", ref chat))
        {
            configuration.ChatNotifications = chat;
            configuration.Save();
        }

        ImGui.TextDisabled("Completed/paused/failed production is announced in the game chat.");

        DrawSocialSection();
    }

    /// <summary>Roadmap 7.10: what to do when another player pokes a running character.</summary>
    private void DrawSocialSection()
    {
        UiTheme.SectionHeader("Social");

        var decline = configuration.SocialDeclineTrades;
        if (ImGui.Checkbox("Decline trade requests during a run", ref decline))
        {
            configuration.SocialDeclineTrades = decline;
            configuration.Save();
        }

        var blacklist = configuration.SocialBlacklistTraders;
        if (ImGui.Checkbox("Remember who sent them (plugin blacklist)", ref blacklist))
        {
            configuration.SocialBlacklistTraders = blacklist;
            configuration.Save();
        }

        ImGui.TextDisabled("The game blacklist cannot be written by plugins; repeat senders\nare declined on sight without another pause.");

        var pauseSeconds = configuration.SocialPauseSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Pause after a trade (s)", ref pauseSeconds, 0, 120))
        {
            configuration.SocialPauseSeconds = Math.Clamp(pauseSeconds, 0, 120);
            configuration.Save();
        }

        ImGui.TextDisabled("Hesitate before carrying on, like a person would; 0 keeps going.");

        var logTells = configuration.SocialLogTells;
        if (ImGui.Checkbox("Log tells from strangers", ref logTells))
        {
            configuration.SocialLogTells = logTells;
            configuration.Save();
        }

        var logInvites = configuration.SocialLogInvites;
        if (ImGui.Checkbox("Log party and free company invites", ref logInvites))
        {
            configuration.SocialLogInvites = logInvites;
            configuration.Save();
        }

        ImGui.TextDisabled("Logged only (sender and time); never answered automatically.");

        if (configuration.SocialBlacklist.Count == 0)
        {
            ImGui.TextDisabled("Blacklist is empty.");
            return;
        }

        ImGui.TextUnformatted($"Blacklist ({configuration.SocialBlacklist.Count})");
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
                configuration.Save();
                break;
            }
        }
    }
}
