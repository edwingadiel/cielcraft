using System;
using System.Linq;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Core.Planning;
using CielCraft.Crafting;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace CielCraft.Windows;

/// <summary>
/// Production breakdown (roadmap 7.12): the resolved graph as a tree with
/// per-node crafts × yield, job, need / owned / missing, and roll-ups; live
/// progress ticks during a run. Hosted by the Status page; also the text
/// behind "/cielcraft plan" and the report's Plan section. Shows the group in
/// flight, else the last plan handed to <see cref="Show"/> (the OrdersPanel
/// calls it after a Preview).
/// </summary>
public sealed class PlanTreePanel
{
    private static readonly Vector4 Done = UiTheme.Success;
    private static readonly Vector4 InFlight = UiTheme.Info;
    private static readonly Vector4 Pending = UiTheme.Faint;

    // One tree per plan instance, shared by the panel, the command and the
    // report; rebuilt only when the plan changes or the user asks, because
    // the inventory read behind it is not free and the plan's counts are a
    // snapshot of planning time anyway.
    private static ProductionPlan? previewPlan;
    private static PlanTree? tree;
    private static string? buildError;

    private readonly Plugin plugin;

    public PlanTreePanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>
    /// Sets the plan shown when no group is in flight. The OrdersPanel can
    /// call this after Preview: <c>PlanTreePanel.Show(groupPlan.Plan);</c>
    /// (null clears it).
    /// </summary>
    public static void Show(ProductionPlan? plan) => previewPlan = plan;

    public void Draw()
    {
        var inFlight = plugin.OrderRunner.CurrentPlan?.Plan;
        var plan = inFlight ?? previewPlan;
        if (plan == null)
        {
            ImGui.TextDisabled("No plan. Preview a group on the Orders page or run the orders.");
            return;
        }

        var current = Tree(plugin, plan);
        var progress = inFlight != null ? Progress(plugin) : null;

        if (inFlight != null)
            UiTheme.KeyValue("Group in flight:", plugin.OrderRunner.CurrentGroup?.Name ?? "?");
        else
            UiTheme.KeyValue("Preview:", "last planned group");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
            tree = null;
        UiTheme.Tooltip("Rebuild the tree from the current inventory (counts otherwise reflect planning time)");

        if (current == null)
        {
            ImGui.TextColored(UiTheme.Danger, buildError ?? "The tree could not be built.");
            return;
        }

        ImGui.TextColored(UiTheme.Muted,
            $"{plan.Targets.Count} target(s) · {plan.CraftSteps.Count} step(s), {current.TotalCrafts} craft(s)"
            + (current.TotalHqCrafts > 0 ? $" ({current.TotalHqCrafts} HQ intermediate)" : "")
            + $" · {plan.RawMaterials.Count} raw material(s)"
            + (progress != null && plan.CraftSteps.Count > 0 ? $" · step {Math.Min(progress.CompletedSteps + 1, plan.CraftSteps.Count)}/{plan.CraftSteps.Count}" : ""));
        ImGui.Spacing();

        for (var i = 0; i < current.Targets.Count; i++)
        {
            ImGui.PushID(i);
            DrawNode(current.Targets[i], progress, ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.PopID();
        }

        DrawRollups(current);
    }

    private void DrawNode(PlanNode node, PlanProgress? progress, ImGuiTreeNodeFlags extraFlags = ImGuiTreeNodeFlags.None)
    {
        var provider = plugin.RecipeProvider;
        var leaf = node.Children.Count == 0;
        var flags = extraFlags | ImGuiTreeNodeFlags.SpanAvailWidth
                    | (leaf ? ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.Bullet : ImGuiTreeNodeFlags.None);

        var mark = PlanTree.Mark(node, progress);
        var open = ImGui.TreeNodeEx($"{provider.GetItemName(node.ItemId)} ×{node.Need}##node", flags);
        ImGui.SameLine(0, 6);
        UiTheme.GameIcon(provider.GetItemIconId(node.ItemId), 16f);
        DrawDetail(node, mark, progress);
        if (open && !leaf)
        {
            for (var i = 0; i < node.Children.Count; i++)
            {
                ImGui.PushID(i);
                DrawNode(node.Children[i], progress);
                ImGui.PopID();
            }

            ImGui.TreePop();
        }
    }

    private void DrawDetail(PlanNode node, string mark, PlanProgress? progress)
    {
        var detail = PlanTree.Describe(node, plugin.RecipeProvider.GetJobAbbreviation, ZoneName);
        var color = node.StepIndex < 0 && !node.MaterialsOnly && node.Missing > 0 ? UiTheme.Warning : UiTheme.Muted;
        ImGui.TextColored(color, detail);

        if (mark.Length == 0)
            return;

        ImGui.SameLine(0, 8);
        switch (mark)
        {
            case "✓":
                ImGui.TextColored(Done, "✓");
                break;
            case "▶" when node.StepIndex >= 0 && progress!.BatchTarget > 0:
                ImGui.TextColored(InFlight, $"▶ {progress.BatchDone}/{progress.BatchTarget}");
                break;
            case "▶":
                ImGui.TextColored(InFlight, "▶");
                break;
            default:
                ImGui.TextColored(Pending, "·");
                break;
        }
    }

    private void DrawRollups(PlanTree current)
    {
        var provider = plugin.RecipeProvider;
        ImGui.Spacing();

        if (current.Zones.Count > 0 && ImGui.TreeNodeEx($"Gathering by zone ({current.Zones.Count})##zones", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var zone in current.Zones)
            {
                var label = zone.TerritoryId == 0 ? "unknown zone" : ZoneName(zone.TerritoryId);
                ImGui.TextColored(zone.TerritoryId == 0 ? UiTheme.Warning : UiTheme.Accent, label);
                ImGui.SameLine(0, 6);
                ImGui.TextColored(UiTheme.Muted, $"{zone.TotalAmount} item(s)");
                foreach (var item in zone.Items)
                {
                    ImGui.BulletText($"{provider.GetItemName(item.ItemId)} ×{item.Amount}");
                    if (item.JobId != 0 || item.Timed)
                    {
                        ImGui.SameLine(0, 6);
                        ImGui.TextColored(UiTheme.Muted,
                            (item.JobId != 0 ? provider.GetJobAbbreviation(item.JobId) : "") + (item.Timed ? " · timed" : ""));
                    }
                }
            }

            ImGui.TreePop();
        }

        if (current.Jobs.Count > 0 && ImGui.TreeNodeEx($"Crafts by job ({current.Jobs.Count})##jobs", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var job in current.Jobs)
                ImGui.BulletText($"{provider.GetJobAbbreviation(job.JobId)}: {job.Crafts} craft(s) in {job.Steps} step(s)");
            ImGui.TreePop();
        }

        if (current.HqMaterials.Count > 0 && ImGui.TreeNodeEx($"HQ materials consumed ({current.HqMaterials.Count})##hq", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var use in current.HqMaterials)
                ImGui.BulletText($"{provider.GetItemName(use.ItemId)} ×{use.Amount} HQ → {provider.GetItemName(use.ConsumerItemId)}");
            ImGui.TreePop();
        }
    }

    /// <summary>The current (or previewed) plan as indented text lines; "No plan." when there is none.</summary>
    public static string PlanText(Plugin plugin)
    {
        var inFlight = plugin.OrderRunner.CurrentPlan?.Plan;
        var plan = inFlight ?? previewPlan;
        if (plan == null)
            return "No plan.";

        var current = Tree(plugin, plan);
        if (current == null)
            return buildError ?? "The tree could not be built.";

        var header = inFlight != null
            ? $"Group in flight: \"{plugin.OrderRunner.CurrentGroup?.Name ?? "?"}\""
            : "Preview of the last planned group";
        var progress = inFlight != null ? Progress(plugin) : null;
        return header + "\n" + current.Render(plugin.RecipeProvider, plugin.RecipeProvider.GetJobAbbreviation, ZoneName, progress);
    }

    /// <summary>The cached tree for the plan, built on first use; null (with buildError set) when building threw.</summary>
    private static PlanTree? Tree(Plugin plugin, ProductionPlan plan)
    {
        if (tree != null && ReferenceEquals(tree.Plan, plan))
            return tree;

        try
        {
            var bridge = plugin.GameBridge;
            tree = PlanTree.Build(plan, plugin.RecipeProvider, bridge.GetItemCount, bridge.GetHqItemCount, plugin.GatheringDatabase.FindLocation);
            buildError = null;
        }
        catch (Exception e)
        {
            tree = null;
            buildError = $"Could not build the breakdown: {e.GetType().Name}: {e.Message}";
        }

        return tree;
    }

    /// <summary>
    /// Where the runner is over the plan in flight: steps done, the batch of
    /// the current step, and whether gathering is behind it. Null when the
    /// runner is idle (nothing to tick). Ticks follow the runner's step index,
    /// so after a mid-run replan they refer to the replanned step order.
    /// </summary>
    private static PlanProgress? Progress(Plugin plugin)
    {
        var runner = plugin.ProductionRunner;
        var state = runner.State;
        if (state == ProductionState.Idle)
            return null;

        var gathering = state is ProductionState.PreparingGather or ProductionState.WaitingForWindow
            or ProductionState.Teleporting or ProductionState.MovingToArea or ProductionState.RunningGather;
        var gatheringDone = state is ProductionState.PreparingStep or ProductionState.RunningBatch or ProductionState.Completed
            || runner.CompletedSteps > 0;
        var batch = plugin.BatchCrafter;
        var inBatch = state is ProductionState.RunningBatch || (state == ProductionState.Paused && batch.TargetQuantity > 0);
        return new PlanProgress(
            runner.CompletedSteps,
            inBatch ? batch.CompletedCrafts : 0,
            inBatch ? batch.TargetQuantity : 0,
            gathering,
            gatheringDone);
    }

    private static string ZoneName(uint territoryId) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory)
            ? territory.PlaceName.Value.Name.ExtractText()
            : $"zone {territoryId}";
}
