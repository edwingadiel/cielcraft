using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Game;
using CielCraft.Sourcing;
using Dalamud.Bindings.ImGui;

namespace CielCraft.Windows;

/// <summary>
/// Tools › Inventory (roadmap 7.17): who the retainers are and what their
/// ventures are doing, the storage rules that decide what happens to a run's
/// leftovers, and a button that runs the cleanup right now. The rules editor
/// writes straight into the configuration; the preview under it is exactly
/// what <see cref="StorageKeeper.Decide"/> would do with the bag as it stands.
/// </summary>
public sealed class InventoryPanel
{
    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;
    private readonly RetainerDatabase retainers;
    private readonly InventoryKeeper keeper;

    private string search = "";
    private IReadOnlyList<(uint ItemId, string Name)> results = [];

    public InventoryPanel(Plugin plugin, RetainerDatabase retainers, InventoryKeeper keeper)
    {
        this.plugin = plugin;
        this.retainers = retainers;
        this.keeper = keeper;
        gameBridge = plugin.GameBridge;
    }

    private Configuration Configuration => plugin.Configuration;

    public void Draw()
    {
        DrawRetainers();
        DrawRules();
        DrawCleanup();
    }

    // ----------------------------------------------------------- retainers

    private void DrawRetainers()
    {
        UiTheme.SectionHeader("Retainers");

        var list = SafeRetainers();
        if (list == null)
        {
            ImGui.TextColored(UiTheme.Danger, "The retainer list is not readable right now.");
            return;
        }

        if (list.Count == 0)
        {
            UiTheme.Hint("No retainers are loaded. Ring a summoning bell once after logging in so the client caches them.");
            return;
        }

        if (!ImGui.BeginTable("##retainers", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Venture", ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableHeadersRow();

        var now = DateTime.UtcNow;
        foreach (var retainer in list)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(retainer.Name);

            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, $"{JobName(retainer.ClassJobId)} {retainer.Level}");

            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, retainer.ItemCount.ToString());

            ImGui.TableNextColumn();
            if (!retainer.OnVenture)
            {
                ImGui.TextColored(UiTheme.Faint, "idle");
            }
            else
            {
                var venture = SafeVenture(retainer);
                var label = venture == null ? $"venture {retainer.VentureId}" : $"{venture.ItemName} ×{venture.GuaranteedAmount}+";
                ImGui.TextUnformatted(label);
                if (venture != null)
                    UiTheme.Tooltip($"RetainerTask {venture.TaskId}; {venture.Minutes} min; retainer level {venture.RetainerLevel}, gathering {venture.RequiredGathering}.");
            }

            ImGui.TableNextColumn();
            if (!retainer.OnVenture)
                ImGui.TextColored(UiTheme.Faint, "-");
            else if (retainer.VentureReady(now))
                ImGui.TextColored(UiTheme.Success, "ready to collect");
            else if (retainer.VentureCompleteAt is { } due)
                ImGui.TextColored(UiTheme.Info, $"back in {Format(due - now)}");
            else
                ImGui.TextColored(UiTheme.Muted, "in progress");
        }

        ImGui.EndTable();

        UiTheme.Hint(
            "The retainer source withdraws stock a retainer already holds. With \"retainer ventures\" on it also collects a "
            + "venture that has already come back; a venture still running is never waited for.");
    }

    // --------------------------------------------------------------- rules

    private void DrawRules()
    {
        UiTheme.SectionHeader("Storage rules");
        ImGui.PushTextWrapPos();
        ImGui.TextColored(UiTheme.Muted,
            "After a run the keeper looks only at the items listed here. Whatever is over \"keep\" — plus anything the "
            + "finished plan still reserves — is deposited, desynthesized or discarded.");
        ImGui.PopTextWrapPos();

        var rules = Configuration.StorageRules;
        if (rules.Count > 0 && ImGui.BeginTable("##storageRules", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.6f);
            ImGui.TableSetupColumn("Keep in bag", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Retainer cap", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Surplus", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableHeadersRow();

            StorageRule? remove = null;
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                ImGui.TableNextRow();
                ImGui.PushID(i);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(plugin.RecipeProvider.GetItemName(rule.ItemId));
                UiTheme.Tooltip($"Item {rule.ItemId}; in the bag ×{SafeCount(rule.ItemId)}, stored ×{SafeStored(rule.ItemId)}.");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var keepBag = rule.KeepInBag;
                if (ImGui.InputInt("##keepBag", ref keepBag, 1, 10))
                {
                    rule.KeepInBag = Math.Max(0, keepBag);
                    Configuration.Save();
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var keepRetainer = rule.KeepInRetainer;
                if (ImGui.InputInt("##keepRetainer", ref keepRetainer, 1, 10))
                {
                    rule.KeepInRetainer = Math.Max(0, keepRetainer);
                    Configuration.Save();
                }

                UiTheme.Tooltip("Stop depositing once the retainers hold this many; 0 = no cap.");

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var action = (int)rule.Action;
                if (ImGui.Combo("##action", ref action, "Keep\0Deposit\0Desynth\0Discard\0"))
                {
                    rule.Action = (StorageAction)action;
                    Configuration.Save();
                }

                UiTheme.Tooltip(ActionHint(rule.Action));

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("×"))
                    remove = rule;
                UiTheme.Tooltip("Remove this rule; the item is then never touched.");

                ImGui.PopID();
            }

            ImGui.EndTable();

            if (remove != null)
            {
                rules.Remove(remove);
                Configuration.Save();
            }
        }
        else if (rules.Count == 0)
        {
            ImGui.TextDisabled("No rules — nothing is ever deposited, desynthesized or discarded.");
        }

        DrawRulePicker();
        DrawSwitchWarnings();
    }

    private void DrawRulePicker()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(280);
        if (ImGui.InputTextWithHint("##ruleSearch", "Add a rule: search an item…", ref search, 64))
            results = Search(search);

        if (results.Count == 0)
            return;

        foreach (var (itemId, name) in results)
        {
            if (!ImGui.Selectable($"{name}##rule{itemId}"))
                continue;

            if (Configuration.StorageRules.All(r => r.ItemId != itemId))
            {
                Configuration.StorageRules.Add(new StorageRule { ItemId = itemId, Action = StorageAction.Deposit });
                Configuration.Save();
            }

            search = "";
            results = [];
            break;
        }
    }

    private void DrawSwitchWarnings()
    {
        var rules = Configuration.StorageRules;
        if (!Configuration.DesynthUnusedByproducts && rules.Any(r => r.Action == StorageAction.Desynth))
        {
            ImGui.TextColored(UiTheme.Warning,
                "Desynth rules are listed but \"desynthesize unused byproducts\" is off — those rules do nothing.");
        }

        if (!Configuration.TrashCleanup && rules.Any(r => r.Action == StorageAction.Discard))
        {
            ImGui.TextColored(UiTheme.Warning,
                "Discard rules are listed but \"trash cleanup\" is off — those rules do nothing.");
        }
    }

    // ------------------------------------------------------------- cleanup

    private void DrawCleanup()
    {
        UiTheme.SectionHeader("Cleanup");

        var preview = SafePreview();
        if (preview == null)
        {
            ImGui.TextColored(UiTheme.Danger, "The bag is not readable right now.");
            return;
        }

        if (preview.Count == 0)
        {
            ImGui.TextDisabled("Nothing over the reserve: a cleanup now would do nothing.");
        }
        else
        {
            foreach (var action in preview)
            {
                UiTheme.StatusDot(
                    $"{action.Action}: {action.Reason}",
                    action.Action == StorageAction.Deposit ? UiTheme.Info : UiTheme.Warning,
                    action.Action == StorageAction.Deposit
                        ? "Deposited at the next summoning-bell visit."
                        : "Destroys the item; only what a rule names is ever touched.");
            }
        }

        ImGui.Spacing();
        UiTheme.StateBadge(keeper.State.ToString(), keeper.State == InventoryKeeperState.Paused, keeper.StatusText);

        ImGui.Spacing();
        if (keeper.IsBusy)
        {
            if (UiTheme.TintedButton("Stop the cleanup", UiTheme.Danger))
                keeper.Abort();
        }
        else
        {
            ImGui.BeginDisabled(preview.Count == 0);
            if (UiTheme.TintedButton("Run cleanup now", UiTheme.Accent))
                keeper.RunNow();
            ImGui.EndDisabled();
            UiTheme.Tooltip("Runs the rules against the bag as it stands; deposits travel to a summoning bell.");
        }

        if (keeper.LastPass.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.Muted, "Last pass:");
            foreach (var line in keeper.LastPass)
                ImGui.BulletText(line);
        }
    }

    // ------------------------------------------------------------- helpers

    private static string ActionHint(StorageAction action) => action switch
    {
        StorageAction.Keep => "Reserve only: the item is counted but never moved.",
        StorageAction.Deposit => "Put the surplus in a retainer at the next summoning-bell visit.",
        StorageAction.Desynth => "Desynthesize the surplus; needs Settings › Sourcing \"desynthesize unused byproducts\".",
        _ => "Throw the surplus away; needs Settings › Sourcing \"trash cleanup\".",
    };

    private static string Format(TimeSpan left) =>
        left.TotalMinutes < 1 ? "under a minute" : $"{(int)left.TotalHours:00}:{left.Minutes:00}";

    private static string JobName(uint classJobId) => classJobId switch
    {
        16 => "MIN",
        17 => "BTN",
        18 => "FSH",
        0 => "-",
        _ => $"job {classJobId}",
    };

    /// <summary>
    /// Craftables first, then node items: a byproduct worth a rule is almost
    /// always one or the other, and both searches are already indexed.
    /// </summary>
    private IReadOnlyList<(uint ItemId, string Name)> Search(string query)
    {
        if (query.Trim().Length < 2)
            return [];

        var found = new List<(uint ItemId, string Name)>();
        var seen = new HashSet<uint>();
        foreach (var (_, itemId, name) in plugin.RecipeProvider.SearchCraftable(query, 8))
        {
            if (seen.Add(itemId))
                found.Add((itemId, name));
        }

        foreach (var entry in plugin.GatheringDatabase.SearchGatherable(query, 8))
        {
            if (seen.Add(entry.ItemId))
                found.Add(entry);
        }

        return found;
    }

    // The panel is drawn every frame; a bridge read that throws while the
    // client is between zones must not take the window down with it.
    private IReadOnlyList<RetainerSnapshot>? SafeRetainers() => Safe(() => retainers.Retainers());

    private VentureOption? SafeVenture(RetainerSnapshot retainer) => Safe(() => retainers.VentureInFlight(retainer));

    private IReadOnlyList<KeeperAction>? SafePreview() => Safe(() => keeper.Preview(null));

    private int SafeCount(uint itemId) => Safe(() => (int?)gameBridge.GetItemCount(itemId)) ?? 0;

    private int SafeStored(uint itemId) => Safe(() => (int?)gameBridge.GetStoredItemCount(itemId)) ?? 0;

    private static T? Safe<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? Safe(Func<int?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
