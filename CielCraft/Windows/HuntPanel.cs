using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Combat;
using CielCraft.Core;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// Tools › Hunting (roadmap 7.5): which combat plugin is driving, what the
/// hunt run is doing, and a button that pins the spot the character is
/// standing on to the mob it is targeting so the next hunt starts there.
/// CielCraft never casts a combat action itself — everything here is status
/// plus the two things a player can usefully do by hand.
/// </summary>
public sealed class HuntPanel
{
    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;
    private readonly CombatDriverSelector drivers;

    private string lastRemembered = "";

    public HuntPanel(Plugin plugin, CombatDriverSelector drivers)
    {
        this.plugin = plugin;
        this.drivers = drivers;
        gameBridge = plugin.GameBridge;
    }

    /// <summary>
    /// The combat database, once it exists (package A of M4). Null until the
    /// coordinator sets it, which only greys out the "remember this spot"
    /// button — the rest of the panel works without it.
    /// </summary>
    public IHuntSpots? Spots { get; set; }

    /// <summary>
    /// The hunt run in flight, when one is. The coordinator points this at the
    /// combat source's current run; null means "no hunt is running".
    /// </summary>
    public Func<ISourceRun?>? ActiveRun { get; set; }

    private Configuration Configuration => plugin.Configuration;

    public void Draw()
    {
        DrawDriver();
        DrawCharacter();
        DrawRun();
        DrawSpot();
    }

    // ------------------------------------------------------------- driver

    private void DrawDriver()
    {
        UiTheme.SectionHeader("Combat plugin");

        var driver = Safe<ICombatDriver>(() => drivers.Current, NoCombatDriver.Instance);
        var available = Safe(() => driver.IsAvailable, false);
        var engaged = Safe(() => driver.IsEngaged, false);

        UiTheme.StatusDot(
            available ? $"{driver.Name} is driving" : "no combat plugin",
            available ? engaged ? UiTheme.Info : UiTheme.Success : UiTheme.Muted,
            available
                ? "CielCraft targets, travels and stops; this plugin casts the actions."
                : "Install Rotation Solver Reborn or BossMod Reborn and reload it; hunting stays unavailable until one answers.");

        ImGui.SameLine();
        if (UiTheme.LinkButton("Probe again"))
        {
            SafeDo(() => drivers.Refresh());
            Plugin.Log.Information("[Combat] Driver probe requested from the Hunting page.");
        }

        if (engaged)
        {
            ImGui.SameLine();
            if (UiTheme.TintedButtonSmall("Disengage", UiTheme.Danger))
                SafeDo(drivers.DisengageAll);
            UiTheme.Tooltip("Turns the rotation off in every combat plugin, as the emergency stop does.");
        }

        foreach (var line in DescribeDrivers())
            ImGui.TextColored(UiTheme.Faint, line);
    }

    private IReadOnlyList<string> DescribeDrivers()
    {
        try
        {
            return drivers.Describe().ToList();
        }
        catch (Exception e)
        {
            return [$"The driver probe threw: {e.Message}"];
        }
    }

    // ---------------------------------------------------------- character

    private void DrawCharacter()
    {
        UiTheme.SectionHeader("Hunting job");

        if (!Configuration.HuntingEnabled)
        {
            UiTheme.Hint("Hunting is off. Turn it on under Settings › Sourcing; nothing here will run until you do.");
        }

        var caps = plugin.Capabilities.Current;
        var chosen = Configuration.CombatJobId;
        var best = caps.BestCombatJob(jobId => Safe(() => gameBridge.HasGearsetForJob(jobId), false));
        var jobId = chosen != 0 ? chosen : best;

        if (jobId == 0)
        {
            ImGui.TextColored(UiTheme.Warning, "No combat job with a gearset was found.");
            UiTheme.Hint(caps.IsKnown
                ? "Save a gearset for the job you want to hunt on; the hunt equips it before it travels."
                : "The character's capabilities have not been read yet — log in, or press Refresh on the Character page.");
        }
        else
        {
            UiTheme.KeyValue("Job:", $"{JobName(jobId)} (level {caps.LevelOf(jobId)})"
                + (chosen == 0 ? " — chosen automatically" : " — pinned in the settings"));
            if (chosen != 0 && best != 0 && best != chosen)
                UiTheme.Hint($"The highest combat job with a gearset is {JobName(best)} (level {caps.LevelOf(best)}).");
        }

        var ceiling = jobId == 0 ? 0 : caps.LevelOf(jobId) + Math.Max(0, Configuration.HuntMaxLevelAbove);
        if (ceiling > 0)
            UiTheme.KeyValue("Mobs up to:", $"level {ceiling}");
        UiTheme.KeyValue("Retreat below:", $"{Configuration.HuntRetreatHpPercent}% HP");

        var hp = Safe(() => gameBridge.PlayerHpPercent, 0f);
        var inCombat = Safe(() => gameBridge.IsInCombat, false);
        var dead = Safe(() => gameBridge.IsDead, false);
        var enemies = Safe(() => gameBridge.EnemiesTargetingMe(), 0);
        ImGui.TextColored(
            dead ? UiTheme.Danger : hp < Configuration.HuntRetreatHpPercent ? UiTheme.Warning : UiTheme.Muted,
            dead ? "Knocked out." : $"HP {hp:F0}%, {(inCombat ? "in combat" : "out of combat")}, {enemies} enemies on you.");
    }

    // ---------------------------------------------------------------- run

    private void DrawRun()
    {
        UiTheme.SectionHeader("Hunt run");

        var run = ActiveRun == null ? null : Safe(() => ActiveRun(), null);
        if (run == null)
        {
            UiTheme.Hint("No hunt is running. A hunt starts by itself when the order book needs a material only a monster drops and hunting is on.");
            return;
        }

        var state = Safe(() => run.State, SourceRunState.Idle);
        UiTheme.StateBadge(state.ToString(), state == SourceRunState.Paused, Safe(() => run.StatusText, ""));
        UiTheme.KeyValue("Obtained:", Safe(() => run.Obtained, 0).ToString());

        foreach (var line in DescribeRun(run))
            ImGui.TextColored(UiTheme.Faint, line);
    }

    private static IReadOnlyList<string> DescribeRun(ISourceRun run)
    {
        try
        {
            return run.Describe().ToList();
        }
        catch (Exception e)
        {
            return [$"Describing the run threw: {e.Message}"];
        }
    }

    // --------------------------------------------------------------- spot

    private void DrawSpot()
    {
        UiTheme.SectionHeader("Remember a spot");

        var mob = Safe(() => gameBridge.CurrentTargetMob, null);
        var position = Safe(() => gameBridge.PlayerPosition, null);
        var territory = Safe(() => gameBridge.CurrentTerritoryId, 0u);

        if (Spots == null)
        {
            UiTheme.Hint("The hunting database is not loaded, so there is nowhere to remember a spot yet.");
            return;
        }

        if (mob == null || position == null)
        {
            UiTheme.Hint("Target the monster you want remembered and stand where you want the hunt to start.");
            return;
        }

        var label = $"Remember this spot for {mob.Value.Name}";
        if (UiTheme.TintedButton(label, UiTheme.Accent))
        {
            try
            {
                Spots.RememberSpot(mob.Value.BNpcNameId, territory, position.Value);
                lastRemembered = $"{mob.Value.Name} at {position.Value.X:F0}, {position.Value.Y:F0}, {position.Value.Z:F0} "
                    + $"in {ZoneName(territory)}.";
                Plugin.Log.Information($"[Combat] Remembered a hunting spot for {mob.Value.Name} (BNpcName {mob.Value.BNpcNameId}) in territory {territory}.");
            }
            catch (Exception e)
            {
                lastRemembered = $"Could not remember the spot: {e.Message}";
                Plugin.Log.Warning($"[Combat] Remembering the spot failed: {e.Message}");
            }
        }

        UiTheme.Tooltip("The next hunt for anything this monster drops teleports here first instead of sweeping the zone.");

        if (lastRemembered.Length > 0)
            ImGui.TextColored(UiTheme.Success, lastRemembered);
    }

    // ----------------------------------------------------------- settings

    /// <summary>The M4 hunting block (roadmap 7.5); the coordinator draws it under Settings › Sourcing.</summary>
    public static void DrawSettings(Configuration configuration)
    {
        UiTheme.Toggle("Hunt monsters for drops", configuration.HuntingEnabled,
            v => { configuration.HuntingEnabled = v; configuration.Save(); },
            "Off by default. When on, a material that no node, vendor, exchange or retainer supplies may be taken from the monsters that drop it — "
            + "but only while Rotation Solver Reborn or BossMod Reborn is installed and loaded, because CielCraft never casts a combat action itself.");

        if (!configuration.HuntingEnabled)
            return;

        DrawJobCombo(configuration);

        var retreat = configuration.HuntRetreatHpPercent;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Retreat below (% HP)", ref retreat, 5, 90))
        {
            configuration.HuntRetreatHpPercent = Math.Clamp(retreat, 5, 90);
            configuration.Save();
        }

        UiTheme.Hint("The hunt disengages, runs back to the last safe point and waits to heal up.");

        var above = configuration.HuntMaxLevelAbove;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Fight mobs up to (levels above the job)", ref above, 0, 20))
        {
            configuration.HuntMaxLevelAbove = Math.Clamp(above, 0, 20);
            configuration.Save();
        }

        UiTheme.Hint("0 keeps to mobs at or below the job's own level, which is where the fights are short and the deaths are none.");

        UiTheme.Toggle("Leave mobs other players are fighting", configuration.HuntSkipMobsTargetedByOthers,
            v => { configuration.HuntSkipMobsTargetedByOthers = v; configuration.Save(); },
            "Etiquette: a monster already targeted by someone who is not you or a party or free company mate is skipped.");
    }

    private static void DrawJobCombo(Configuration configuration)
    {
        var jobs = CombatJobs();
        var current = configuration.CombatJobId;
        var preview = current == 0
            ? "Automatic (highest with a gearset)"
            : jobs.FirstOrDefault(j => j.JobId == current).Label ?? $"job {current}";

        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Combat job", preview))
        {
            if (ImGui.Selectable("Automatic (highest with a gearset)", current == 0))
            {
                configuration.CombatJobId = 0;
                configuration.Save();
            }

            foreach (var (jobId, label) in jobs)
            {
                if (!ImGui.Selectable(label, jobId == current))
                    continue;

                configuration.CombatJobId = jobId;
                configuration.Save();
            }

            ImGui.EndCombo();
        }

        UiTheme.Hint("The hunt equips this job's best gearset before it travels. Automatic picks the highest-level combat job that has one saved.");
    }

    /// <summary>
    /// Every ClassJob that fights, jobs before the classes they grew out of
    /// (DohDolJobIndex is -1 for all of them; an empty abbreviation marks the
    /// sheet's placeholder rows).
    /// </summary>
    private static IReadOnlyList<(uint JobId, string Label)> CombatJobs()
    {
        var jobs = new List<(uint JobId, string Label)>();
        foreach (var job in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>())
        {
            var abbreviation = job.Abbreviation.ExtractText();
            if (abbreviation.Length == 0 || !CharacterCapabilities.IsCombatJob(job.RowId))
                continue;

            var name = job.Name.ExtractText();
            jobs.Add((job.RowId, name.Length == 0 ? abbreviation : $"{abbreviation} — {name}"));
        }

        return jobs;
    }

    private static string JobName(uint jobId) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(jobId, out var row)
            ? row.Abbreviation.ExtractText()
            : $"job {jobId}";

    private static string ZoneName(uint territoryId) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(territoryId, out var row)
            ? row.PlaceName.Value.Name.ExtractText()
            : $"territory {territoryId}";

    // The panel draws every frame; a bridge read that throws between zones
    // must not take the window down with it (the same guard InventoryPanel uses).
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void SafeDo(Action act)
    {
        try
        {
            act();
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[Combat] The hunting panel's action threw: {e.Message}");
        }
    }
}
