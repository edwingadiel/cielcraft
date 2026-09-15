using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using CielCraft.Core;

namespace CielCraft.Diagnostics;

/// <summary>
/// One plain-text snapshot of everything needed to diagnose a problem from a
/// paste (spec §46): environment, settings, live game state, the internal
/// state of every automation layer, and the recent log. No character name
/// or world is included so reports can be shared publicly.
/// </summary>
public static class DiagnosticReport
{
    private const int LogLines = 200;
    private const int CraftEventLines = 40;

    public static string Build(Plugin plugin)
    {
        var sb = new StringBuilder(32 * 1024);
        var bridge = plugin.GameBridge;

        Section(sb, "CielCraft diagnostic report");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z (local {DateTime.Now:HH:mm:ss}); Eorzea time {EorzeaTime()}");
        sb.AppendLine($"Plugin {typeof(Plugin).Assembly.GetName().Version}; Dalamud {typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetName().Version}");
        sb.AppendLine($"Raphael native: {(Raphael.RaphaelSolver.IsAvailable ? "loaded" : "MISSING")}; vnavmesh: {Describe(plugin.Navigation)}");

        Section(sb, "Settings");
        foreach (var property in typeof(Configuration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Structured sections print themselves below (orders, saved run).
            if (property.Name is nameof(Configuration.SavedProduction) or nameof(Configuration.QueueItems)
                or nameof(Configuration.Orders) or nameof(Configuration.SocialBlacklist))
                continue;

            sb.AppendLine($"{property.Name} = {property.GetValue(plugin.Configuration)}");
        }

        Section(sb, "Game state");
        Safe(sb, () =>
        {
            var player = bridge.GetPlayerState();
            if (!bridge.IsLoggedIn || player == null)
            {
                sb.AppendLine("Not logged in.");
                return;
            }

            sb.AppendLine($"Job {player.ClassJobAbbreviation} ({player.ClassJobId}) Lv{player.Level}; craftsmanship {player.Craftsmanship}, control {player.Control}, CP {player.CurrentCp}/{player.MaxCp}, GP {player.CurrentGp}/{player.MaxGp}");
            sb.AppendLine($"Territory {player.TerritoryId} at {player.Position.X:F1}, {player.Position.Y:F1}, {player.Position.Z:F1}; mounted {bridge.IsMounted}; between areas {bridge.IsBetweenAreas}; free bag slots {bridge.GetFreeInventorySlots()}");
            sb.AppendLine($"Crafting {bridge.IsCrafting}; preparing to craft {bridge.IsPreparingToCraft}; gathering {bridge.IsGathering}; gather action in progress {bridge.IsGatheringActionInProgress}");
            sb.AppendLine($"Crafting log ready {bridge.IsReadyToStartCraft}; selected recipe {bridge.SelectedRecipeId}; quick synth available {bridge.IsQuickSynthAvailable}, active {bridge.IsQuickSynthesisActive}; quick gathering {bridge.IsQuickGatheringEnabled}");
            sb.AppendLine($"Recipe selection: {bridge.DescribeRecipeSelection()}");
            sb.AppendLine($"Windows: " + string.Join(", ",
                new[] { "RecipeNote", "Synthesis", "SynthesisSimple", "SynthesisSimpleDialog", "Gathering", "GatheringMasterpiece", "Repair", "SelectYesno", "Materialize" }
                    .Select(name => $"{name}={(bridge.IsAddonVisible(name) ? "open" : "-")}")));
            sb.AppendLine($"Gear condition {bridge.GetLowestEquipmentConditionPercent():F0}%; food remaining {bridge.GetFoodBuffRemainingSeconds():F0}s");

            var result = bridge.CurrentCraftResult;
            if (result != null)
                sb.AppendLine($"Current craft result: {plugin.RecipeProvider.GetItemName(result.Value.ItemId)} (item {result.Value.ItemId}) ×{result.Value.Amount}");
        });

        Section(sb, "Capabilities");
        Lines(sb, plugin.Capabilities.Describe);

        Section(sb, "Craft snapshot");
        Safe(sb, () =>
        {
            var craft = plugin.CraftMonitor.Current;
            if (craft == null)
            {
                sb.AppendLine("No active synthesis.");
                return;
            }

            sb.AppendLine($"rlvl {craft.RecipeLevel}; step {craft.Step}; progress {craft.Progress}/{craft.MaxProgress}; quality {craft.Quality}/{craft.MaxQuality} (required {craft.RequiredQuality}); durability {craft.Durability}/{craft.MaxDurability}; CP {craft.CurrentCp}/{craft.MaxCp}; condition {craft.Condition}");
            sb.AppendLine("Buffs: " + (craft.Buffs.Count == 0 ? "none" : string.Join(", ", craft.Buffs.Select(DescribeBuff))));
        });

        Section(sb, "Solver");
        Lines(sb, plugin.SolverService.Describe);

        Section(sb, "Action executor");
        Lines(sb, plugin.ActionExecutor.Describe);

        Section(sb, "Craft automator");
        Lines(sb, plugin.CraftAutomator.Describe);

        Section(sb, "Batch crafter");
        Lines(sb, plugin.BatchCrafter.Describe);

        Section(sb, "Maintenance");
        Lines(sb, plugin.Maintenance.Describe);

        Section(sb, "NPC layer (7.3)");
        Lines(sb, plugin.NpcDatabase.Describe);
        Lines(sb, plugin.NpcInteractor.Describe);

        Section(sb, "Vendor source (7.3b)");
        Lines(sb, plugin.VendorSource.Describe);

        Section(sb, "Retainers and cleanup (7.17)");
        Lines(sb, plugin.RetainerDatabase.Describe);
        Lines(sb, plugin.RetainerSource.Describe);
        Lines(sb, plugin.InventoryKeeper.Describe);

        Section(sb, "Spiritbond mode");
        Lines(sb, plugin.Spiritbond.Describe);

        Section(sb, "Production runner");
        Lines(sb, plugin.ProductionRunner.Describe);
        Lines(sb, plugin.Finisher.Describe);

        Section(sb, "Order runner");
        Lines(sb, plugin.OrderRunner.Describe);
        Safe(sb, () =>
        {
            var saved = plugin.Configuration.SavedProduction;
            sb.AppendLine($"Saved production: active {saved.Active}; {saved.Targets.Count} target(s)");
            foreach (var target in saved.Targets)
                sb.AppendLine($"  {plugin.RecipeProvider.GetItemName(target.ItemId)} (item {target.ItemId}) ×{target.Quantity}; initial {target.InitialCount} (HQ {target.InitialHqCount}); mode {target.Mode}{(target.MaterialsOnly ? "; materials only" : "")}");
        });

        // The breakdown tree of the group in flight or the last preview (7.12).
        Section(sb, "Plan");
        Lines(sb, () => Windows.PlanTreePanel.PlanText(plugin).Split('\n'));

        Section(sb, "Gathering loop");
        Lines(sb, plugin.GatheringLoop.Describe);

        Section(sb, "Gathering controller");
        Lines(sb, plugin.GatheringController.Describe);

        Section(sb, $"Social (last {SocialGuardCore.HistoryCapacity} events, UTC)");
        Lines(sb, plugin.SocialGuard.Describe);

        Section(sb, "Open gathering node");
        Safe(sb, () =>
        {
            var node = bridge.GetGatheringState();
            if (node == null)
            {
                sb.AppendLine("None.");
            }
            else
            {
                sb.AppendLine($"Integrity {node.IntegrityRemaining}/{node.IntegrityTotal}; GP {node.CurrentGp}/{node.MaxGp}");
                foreach (var slot in node.Items)
                    sb.AppendLine($"  [{slot.Index}] {plugin.RecipeProvider.GetItemName(slot.ItemId)} (item {slot.ItemId}){(slot.Enabled ? "" : " — not gatherable")}");
            }

            var collectable = bridge.GetCollectableGatheringState();
            if (collectable != null)
                sb.AppendLine($"Collectable: {collectable}");

            var nearest = bridge.FindNearestGatheringNode();
            sb.AppendLine(nearest == null
                ? "Nearest node: none in range"
                : $"Nearest node: {nearest.Name} #{nearest.ObjectId} at {nearest.Position.X:F1}, {nearest.Position.Y:F1}, {nearest.Position.Z:F1} ({nearest.Distance:F1}y)");
        });

        Section(sb, $"Recent craft events (last {CraftEventLines})");
        Safe(sb, () =>
        {
            var events = plugin.CraftMonitor.RecentEvents;
            foreach (var line in events.Skip(Math.Max(0, events.Count - CraftEventLines)))
                sb.AppendLine(line);
            if (events.Count == 0)
                sb.AppendLine("None.");
        });

        Section(sb, $"Log (last {LogLines} entries, UTC)");
        var entries = Plugin.Log.Snapshot();
        foreach (var entry in entries.Skip(Math.Max(0, entries.Count - LogLines)))
            sb.AppendLine(entry.ToString());
        if (entries.Count == 0)
            sb.AppendLine("Empty.");

        sb.AppendLine("=== end of report ===");
        return sb.ToString();
    }

    private static string DescribeBuff(CraftBuff buff)
    {
        var name = buff.StatusId switch
        {
            CraftBuffIds.InnerQuiet => "Inner Quiet",
            CraftBuffIds.WasteNot => "Waste Not",
            CraftBuffIds.WasteNot2 => "Waste Not II",
            CraftBuffIds.GreatStrides => "Great Strides",
            CraftBuffIds.Manipulation => "Manipulation",
            CraftBuffIds.Innovation => "Innovation",
            CraftBuffIds.FinalAppraisal => "Final Appraisal",
            CraftBuffIds.MuscleMemory => "Muscle Memory",
            CraftBuffIds.Veneration => "Veneration",
            CraftBuffIds.HeartAndSoul => "Heart and Soul",
            CraftBuffIds.TrainedPerfection => "Trained Perfection",
            _ => $"status {buff.StatusId}",
        };

        var stacks = buff.Stacks > 0 ? $" x{buff.Stacks}" : "";
        var steps = buff.RemainingSteps > 0 ? $" ({buff.RemainingSteps} steps)" : "";
        return name + stacks + steps;
    }

    private static string Describe(INavigationProvider navigation)
    {
        try
        {
            return !navigation.IsAvailable ? "unavailable"
                : $"available, mesh ready {navigation.IsReady}, moving {navigation.IsMoving}";
        }
        catch (Exception e)
        {
            return $"error ({e.GetType().Name}: {e.Message})";
        }
    }

    private static string EorzeaTime()
    {
        var minute = EorzeaClock.MinuteOfDay(DateTimeOffset.UtcNow);
        return $"{minute / 60:00}:{minute % 60:00}";
    }

    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine($"=== {title} ===");
    }

    private static void Lines(StringBuilder sb, Func<IEnumerable<string>> describe) =>
        Safe(sb, () =>
        {
            foreach (var line in describe())
                sb.AppendLine(line);
        });

    /// <summary>A section that throws must not lose the rest of the report.</summary>
    private static void Safe(StringBuilder sb, Action write)
    {
        try
        {
            write();
        }
        catch (Exception e)
        {
            sb.AppendLine($"!! section failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
