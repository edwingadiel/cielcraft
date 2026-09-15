using System;
using System.Linq;
using CielCraft.Core;
using CielCraft.Core.Scheduling;
using CielCraft.Crafting;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// Schedule page (roadmap 7.15): the timed items of the plan in flight (or
/// the last preview, handed over by <see cref="Show"/> the way the plan tree
/// gets it), each with its next window in ET and as a real countdown, the
/// nodes and yield a visit is expected to give, the windows the amount
/// needs and the GP a node run wants; the runner's current wait; and the
/// plan order the runner follows. Hosted by Status › Schedule.
/// </summary>
public sealed class SchedulePanel
{
    private static readonly TimeSpan PreviewRefresh = TimeSpan.FromSeconds(30);

    private static ProductionPlan? previewPlan;

    private readonly Plugin plugin;
    private Schedule? preview;
    private ProductionPlan? previewFor;
    private DateTime previewBuiltAt = DateTime.MinValue;
    private string? previewError;

    public SchedulePanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>
    /// Sets the plan previewed when no run is in flight. The OrdersPanel
    /// calls it next to PlanTreePanel.Show after a Preview:
    /// <c>SchedulePanel.Show(groupPlan.Plan);</c> (null clears it).
    /// </summary>
    public static void Show(ProductionPlan? plan) => previewPlan = plan;

    public void Draw()
    {
        var runner = plugin.ProductionRunner;
        var active = runner.State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed);
        var inFlight = plugin.OrderRunner.CurrentPlan?.Plan;

        Schedule? schedule;
        if (active && runner.CurrentSchedule != null)
        {
            schedule = runner.CurrentSchedule;
            UiTheme.KeyValue("Schedule:", $"run in flight ({plugin.OrderRunner.CurrentGroup?.Name ?? "production"}), rebuilt {Ago(schedule.BuiltAt)} ago");
        }
        else
        {
            var plan = inFlight ?? previewPlan;
            if (plan == null)
            {
                ImGui.TextDisabled("No plan. Preview a group on the Orders page or run the orders.");
                ImGui.Spacing();
                DrawAssumptions();
                return;
            }

            schedule = Preview(plan);
            UiTheme.KeyValue("Schedule:", "preview of the last planned group, as if started now");
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh"))
                preview = null;
            UiTheme.Tooltip("Rebuild the preview from the current time, GP and bag (it refreshes on its own every 30 s)");

            if (schedule == null)
            {
                ImGui.TextColored(UiTheme.Danger, previewError ?? "The schedule could not be built.");
                return;
            }
        }

        DrawWait(runner, active);
        ImGui.Spacing();
        DrawTimedTable(schedule);
        ImGui.Spacing();
        DrawPlanOrder(schedule);
        ImGui.Spacing();
        DrawAssumptions();
    }

    /// <summary>The two wait-at-home fields (roadmap 7.15), for Settings › Gathering.</summary>
    public static void DrawSettings(Configuration configuration)
    {
        UiTheme.Toggle("Wait at home for node windows", configuration.WaitAtHomeForWindows,
            v => { configuration.WaitAtHomeForWindows = v; configuration.Save(); },
            "Long waits for an unspoiled or legendary window are spent at the home point chosen under Settings › Home; shorter ones idle beside the node area. Ready craft steps are done while waiting either way.");

        var minutes = configuration.WaitAtHomeMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Go home for waits longer than (min)", ref minutes))
        {
            configuration.WaitAtHomeMinutes = Math.Clamp(minutes, 1, 60);
            configuration.Save();
        }

        UiTheme.Hint("The teleport out and back costs a few minutes of its own, so very short thresholds only add loading screens.");
    }

    private void DrawWait(ProductionRunner runner, bool active)
    {
        if (runner.CurrentWait is { } wait)
        {
            var now = DateTime.UtcNow;
            var opens = wait.WindowOpensUtc is { } start
                ? (start <= now ? "open now" : $"opens in {Countdown(start - now)}")
                : "";
            ImGui.TextColored(UiTheme.Info, "●");
            ImGui.SameLine(0, 6);
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(
                $"Waiting {wait.Where} for {wait.ItemName}'s {wait.WindowLabel} window in {wait.ZoneName}: {opens}; " +
                $"leaving in {Countdown(wait.UntilUtc - now)}.");
            ImGui.PopTextWrapPos();
            return;
        }

        if (active)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(UiTheme.Muted, runner.StatusText);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawTimedTable(Schedule schedule)
    {
        UiTheme.SectionHeader("Timed nodes");
        if (schedule.Timed.Count == 0)
        {
            ImGui.TextDisabled("No timed nodes in this plan; gathering runs in plan order.");
            return;
        }

        if (!ImGui.BeginTable("##scheduleTimed", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollX))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Next window", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableSetupColumn("Opens in", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Nodes / visit", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Yield / visit", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Windows", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("GP / node", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();

        var provider = plugin.RecipeProvider;
        var now = DateTime.UtcNow;
        foreach (var item in schedule.Timed)
        {
            var next = item.Next;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            UiTheme.GameIcon(provider.GetItemIconId(item.Task.ItemId), 16f);
            ImGui.TextUnformatted($"{item.Task.Name} ×{item.Task.Amount}");
            UiTheme.Tooltip(item.Task.Collectable ? "Gathered as collectables" : "Gathered normally");

            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, item.Task.Kind.ToString());

            ImGui.TableNextColumn();
            if (next != null)
            {
                ImGui.TextUnformatted($"{next.EtLabel} · {GatheringDatabase.GetTerritoryName(item.Task.TerritoryId)}");
                UiTheme.Tooltip($"Real time {next.Start.ToLocalTime():HH:mm}–{next.End.ToLocalTime():HH:mm}; leave at {next.Depart.ToLocalTime():HH:mm:ss}" +
                                (item.Visits.Count > 1 ? $"\nThen {string.Join(", ", item.Visits.Skip(1).Select(v => v.Start.ToLocalTime().ToString("HH:mm")))}" : ""));
            }
            else
            {
                ImGui.TextColored(UiTheme.Warning, "no window found");
            }

            ImGui.TableNextColumn();
            if (next != null)
            {
                if (next.IsOpenAt(now))
                    ImGui.TextColored(UiTheme.Success, $"open, {Countdown(next.End - now)} left");
                else
                    ImGui.TextUnformatted(Countdown(next.Start - now));
            }

            ImGui.TableNextColumn();
            if (next != null)
            {
                ImGui.TextUnformatted($"{next.NodesPlanned}");
                ImGui.SameLine(0, 4);
                ImGui.TextColored(UiTheme.Muted, $"({next.NodesByTime} time, {next.NodesByGp} GP)");
                UiTheme.Tooltip($"GP at arrival ≈ {next.GpAtArrival}" + (next.CordialsPlanned > 0 ? $", {next.CordialsPlanned} cordial(s)" : "") +
                                "\nNodes planned stop once the amount is covered; the window length and the GP for a full rotation bound them.");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(next != null ? $"≈{next.ExpectedYield}" : "-");

            ImGui.TableNextColumn();
            ImGui.TextColored(item.CoveredWithinHorizon ? UiTheme.Success : UiTheme.Warning, $"{item.WindowsNeeded}{(item.CoveredWithinHorizon ? "" : "+")}");
            if (!item.CoveredWithinHorizon)
                UiTheme.Tooltip("Extrapolated: not every window needed falls within the next three hours.");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.GpWanted.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawPlanOrder(Schedule schedule)
    {
        if (schedule.Entries.Count == 0)
            return;

        if (!UiTheme.Collapsible($"Plan order ({schedule.Entries.Count})", defaultOpen: true))
            return;

        foreach (var entry in schedule.Entries)
        {
            var (label, color) = entry.Kind switch
            {
                ScheduleEntryKind.TimedVisit => ("window", UiTheme.Accent),
                ScheduleEntryKind.UntimedGather => ("gather", UiTheme.Info),
                ScheduleEntryKind.CraftStep => ("craft", UiTheme.Success),
                ScheduleEntryKind.GoHome => ("home", UiTheme.Warning),
                _ => ("wait", UiTheme.Muted),
            };
            ImGui.TextColored(UiTheme.Faint, entry.From.ToLocalTime().ToString("HH:mm"));
            ImGui.SameLine(0, 8);
            ImGui.TextColored(color, label.PadRight(6));
            ImGui.SameLine(0, 8);
            ImGui.TextUnformatted(entry.Text);
            if (entry.Duration > TimeSpan.Zero)
                UiTheme.Tooltip($"{entry.From.ToLocalTime():HH:mm:ss} – {entry.Until.ToLocalTime():HH:mm:ss} (≈{Countdown(entry.Duration)})");
        }
    }

    private static void DrawAssumptions()
    {
        UiTheme.Hint("Estimates are conservative: ≈1 minute per node, 4 swings a node, the yield buff only when the GP for a full rotation is there, " +
                     "and a 2-minute travel lead before each window. The run re-schedules by itself when a window closes short.");
    }

    /// <summary>The preview schedule for the plan, rebuilt when the plan changes, on Refresh, and every 30 s (windows move).</summary>
    private Schedule? Preview(ProductionPlan plan)
    {
        if (preview != null && ReferenceEquals(previewFor, plan) && DateTime.UtcNow - previewBuiltAt < PreviewRefresh)
            return preview;

        try
        {
            preview = plugin.ProductionRunner.PreviewSchedule(plan);
            previewError = preview == null ? "A raw material of this plan cannot be gathered; the run would refuse to start." : null;
        }
        catch (Exception e)
        {
            preview = null;
            previewError = $"Could not build the schedule: {e.GetType().Name}: {e.Message}";
        }

        previewFor = plan;
        previewBuiltAt = DateTime.UtcNow;
        return preview;
    }

    private static string Countdown(TimeSpan span) =>
        span < TimeSpan.Zero ? "0s"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:D2}m"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:D2}s"
        : $"{span.Seconds}s";

    private static string Ago(DateTime utc) => Countdown(DateTime.UtcNow - utc);
}
