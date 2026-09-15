using System;
using CielCraft.Core;
using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// The character checklist (roadmap 7.20), now the Tools › Character page
/// (7.21): what the automation relies on and whether this character has it —
/// gearsets per job, navigation, flight, master books, tribal reputations.
/// Read-only: it points at what to fix in the game rather than changing
/// anything. Shown on first login until "Done" is clicked.
/// </summary>
internal sealed class SetupPanel
{
    private static readonly (uint JobId, string Name)[] CraftingJobs =
    [
        (8, "Carpenter"), (9, "Blacksmith"), (10, "Armorer"), (11, "Goldsmith"),
        (12, "Leatherworker"), (13, "Weaver"), (14, "Alchemist"), (15, "Culinarian"),
    ];

    private static readonly (uint JobId, string Name)[] GatheringJobs =
    [
        (16, "Miner"), (17, "Botanist"),
    ];

    private readonly Plugin plugin;
    private readonly Action<string> navigate;

    /// <param name="navigate">Page selector of the hosting window; "Done" leaves the checklist for the orders page.</param>
    public SetupPanel(Plugin plugin, Action<string> navigate)
    {
        this.plugin = plugin;
        this.navigate = navigate;
    }

    /// <summary>The page was just selected: re-read the character so the checks are current.</summary>
    public void OnShown() => plugin.Capabilities.Refresh();

    public void Draw()
    {
        var caps = plugin.Capabilities.Current;
        var bridge = plugin.GameBridge;

        UiTheme.Hint(
            "CielCraft drives the game through your own gearsets, mounts and unlocks. " +
            "Everything below is read from the character; fix the red items in the game, then Refresh.");
        ImGui.Spacing();

        if (!bridge.IsLoggedIn)
        {
            ImGui.TextColored(UiTheme.Warning, "Log in to a character to run the checks.");
            return;
        }

        UiTheme.SectionHeader("Gearsets");
        UiTheme.Hint("Every job a plan touches needs a saved gear set; the run refuses to start otherwise.");
        DrawJobs(CraftingJobs, caps);
        DrawJobs(GatheringJobs, caps);

        UiTheme.SectionHeader("Movement");
        Check(Plugin.IsVNavmeshAvailable, "vnavmesh installed and loaded", "Gathering needs it for pathing; crafting works without it.");
        Check(
            caps.IsKnown && caps.FlightTerritories.Count > 0,
            caps.IsKnown ? $"Flight unlocked in {caps.FlightTerritories.Count} zone(s)" : "Flight status unknown",
            "Zones without all aether currents are travelled on the ground; nothing to fix unless a gather zone is slow.");

        UiTheme.SectionHeader("Recipes");
        Check(
            caps.IsKnown,
            caps.IsKnown ? $"{caps.UnlockedRecipeBooks.Count} master recipe book(s) learned" : "Master books unknown",
            "Recipes from a book you have not learned are planned around when an alternative exists; otherwise the run says which book is missing.");

        UiTheme.SectionHeader("Reputations");
        var tribes = 0;
        foreach (var rank in caps.TribeRanks.Values)
            if (rank > 0)
                tribes++;
        Check(
            caps.IsKnown,
            caps.IsKnown ? $"{tribes} tribe(s) with reputation" : "Tribal reputations unknown",
            "Tribal vendors are only used as a material source when the rank allows it.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (UiTheme.TintedButton("Refresh", UiTheme.Info))
            plugin.Capabilities.Refresh();
        ImGui.SameLine();
        if (UiTheme.TintedButton("Settings", UiTheme.Muted))
            navigate(MainWindow.Pages.SettingsGeneral);
        if (!plugin.Configuration.SetupCompleted)
        {
            // First run: the page opened by itself on login; "Done" stops that and moves on to the orders.
            ImGui.SameLine();
            if (UiTheme.TintedButton("Done — don't show again", UiTheme.Success))
            {
                plugin.Configuration.SetupCompleted = true;
                plugin.Configuration.Save();
                navigate(MainWindow.Pages.Orders);
            }
        }

        if (caps.IsKnown)
            ImGui.TextDisabled($"Read at {caps.ReadAt.ToLocalTime():HH:mm:ss}.");
    }

    private void DrawJobs((uint JobId, string Name)[] jobs, CharacterCapabilities caps)
    {
        foreach (var (jobId, name) in jobs)
        {
            var level = caps.LevelOf(jobId);
            var hasGearset = plugin.GameBridge.HasGearsetForJob(jobId);
            var label = level > 0 ? $"{name} (level {level})" : $"{name} (not unlocked)";
            Check(hasGearset || level == 0, label + (hasGearset ? " — gearset saved" : level > 0 ? " — no gearset" : ""), null, muted: level == 0);
        }
    }

    private static void Check(bool ok, string label, string? hint, bool muted = false)
    {
        ImGui.TextColored(muted ? UiTheme.Faint : ok ? UiTheme.Success : UiTheme.Danger, ok ? "●" : "○");
        ImGui.SameLine(0, 6);
        if (muted)
            ImGui.TextDisabled(label);
        else
            ImGui.TextUnformatted(label);
        if (hint != null)
            UiTheme.Tooltip(hint);
    }
}
