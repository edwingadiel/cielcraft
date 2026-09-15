using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Crafting;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CielCraft.Windows;

/// <summary>
/// The order book editor (roadmap 7.13): groups as collapsible headers,
/// orders as table rows, a search box that adds orders, and clipboard
/// import/export. Every edit saves the configuration straight away so a
/// reload never loses a list. The run controls stay in <see cref="MainWindow"/>.
/// </summary>
internal sealed class OrdersPanel
{
    private static readonly (AmountMode Value, string Label, string Tip)[] AmountModes =
    [
        (AmountMode.Absolute, "Absolute", "Produce exactly this many, whatever the bag holds"),
        (AmountMode.Restock, "Restock", "Top the bag up to N: produce N − owned, nothing when already there"),
    ];

    private static readonly (ProductionMode Value, string Label, string Tip)[] ProductionModes =
    [
        (ProductionMode.Any, "Any", "Solve for the configured target quality; NQ or HQ as it lands"),
        (ProductionMode.ForceHq, "Force HQ", "Solve at 100% with HQ materials and keep crafting until the HQ count rises by the amount"),
        (ProductionMode.QuickSynth, "Quick synth", "Quick synthesis for the final craft too (NQ, fast) when the game offers it"),
        (ProductionMode.Collectable, "Collectable", "Craft as a collectable: the solve targets the chosen tier's collectability (only for collectable recipes)"),
    ];

    /// <summary>A gather order has no craft, so only Any and Collectable mean anything (7.1).</summary>
    private static readonly (ProductionMode Value, string Label, string Tip)[] GatherModes =
    [
        (ProductionMode.Any, "Any", "Gather the item as it comes"),
        (ProductionMode.Collectable, "Collectable", "Gather as collectables: appraise to the chosen tier's collectability and count only those"),
    ];

    private static readonly (OrderKind Value, string Label, string Tip)[] Kinds =
    [
        (OrderKind.Craft, "Craft", "Craft the item (sub-crafts and missing materials are gathered first)"),
        (OrderKind.Gather, "Gather", "Gather the item itself with MIN/BTN: teleport, travel, timed windows, the node loop; no crafting"),
    ];

    private static readonly (CollectableTier Value, string Label, string Tip)[] Tiers =
    [
        (CollectableTier.Low, "Low", "The lowest collectability tier"),
        (CollectableTier.Mid, "Mid", "The middle collectability tier"),
        (CollectableTier.High, "High", "The highest collectability tier"),
    ];

    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;

    private string searchText = "";
    private OrderKind searchKind = OrderKind.Craft;
    private IReadOnlyList<(uint ItemId, string Name)> searchResults = [];

    private Guid? selectedGroupId;
    private Guid? renamingGroupId;
    private string renameText = "";
    private bool renameFocusPending;
    private Guid? confirmDeleteGroupId;
    private Guid? previewGroupId;
    private GroupPlan? preview;
    private readonly HashSet<Guid> openGroups = [];

    private OrderBook? pendingImport;
    private bool importEverything;
    private string notice = "";
    private string error = "";
    private List<string> unresolvedNames = [];

    public OrdersPanel(Plugin plugin)
    {
        this.plugin = plugin;
        gameBridge = plugin.GameBridge;
    }

    /// <summary>Quantity for orders added from the search box or the crafting log; MainWindow shares it with Batch ×N.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>The book has at least one order (of any state); otherwise MainWindow falls back to the crafting-log target.</summary>
    public bool HasOrders => plugin.Configuration.Orders.Groups.Any(g => g.Orders.Count > 0);

    private OrderBook Book => plugin.Configuration.Orders;

    private DalamudRecipeProvider Provider => plugin.RecipeProvider;

    private void Save() => plugin.Configuration.Save();

    public void Draw()
    {
        DrawToolbar();
        DrawSearchResults();
        DrawGroups();
        DrawImportExport();
    }

    // ------------------------------------------------------------ toolbar

    private void DrawToolbar()
    {
        // Kind of the search (7.1): craftable recipes or MIN/BTN gatherables.
        ImGui.SetNextItemWidth(78);
        var kind = searchKind;
        if (EnumCombo("##searchKind", ref kind, Kinds, fullWidth: false) && kind != searchKind)
        {
            searchKind = kind;
            searchResults = Search(searchText);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 110);
        var hint = searchKind == OrderKind.Gather ? "Add gatherable item…" : "Add craftable item…";
        if (ImGui.InputTextWithHint("##orderSearch", hint, ref searchText, 64))
            searchResults = Search(searchText);
        UiTheme.Tooltip("Adds to the highlighted group (click a group header to pick it)");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        var quantity = Quantity;
        if (ImGui.InputInt("##orderQty", ref quantity))
            Quantity = Math.Clamp(quantity, 1, 9999);
        UiTheme.Tooltip("Quantity for new orders and for Batch ×N");

        var selectedRecipe = gameBridge.SelectedRecipeId != 0 ? Provider.GetRecipeById(gameBridge.SelectedRecipeId) : null;
        if (selectedRecipe != null)
        {
            if (UiTheme.TintedButton("Add crafting-log selection", UiTheme.Accent))
                AddOrder(selectedRecipe.ResultItemId, OrderKind.Craft);
            UiTheme.Tooltip($"Add {Provider.GetItemName(selectedRecipe.ResultItemId)} ×{Quantity}");
            ImGui.SameLine();
        }

        if (UiTheme.TintedButton("New group", UiTheme.Muted))
        {
            var group = new OrderGroup { Name = $"Group {Book.Groups.Count + 1}" };
            Book.Groups.Add(group);
            Save();
            selectedGroupId = group.Id;
            StartRename(group);
        }

        UiTheme.Tooltip("Groups run one after another; orders inside a group are planned together");
    }

    private void DrawSearchResults()
    {
        if (searchResults.Count == 0)
            return;

        using var child = ImRaii.Child(
            "##orderSearchResults", new Vector2(-1, Math.Min(searchResults.Count, 6) * 24f + 8), true);
        if (!child.Success)
            return;

        foreach (var result in searchResults)
        {
            UiTheme.GameIcon(Provider.GetItemIconId(result.ItemId), 18f);
            if (ImGui.Selectable($"{result.Name}##r{result.ItemId}"))
            {
                AddOrder(result.ItemId, searchKind);
                searchText = "";
                searchResults = [];
                break;
            }
        }
    }

    /// <summary>Craftable names from the recipe sheet, or gatherable names from the node data (7.1), per the search kind.</summary>
    private IReadOnlyList<(uint ItemId, string Name)> Search(string query) =>
        searchKind == OrderKind.Gather
            ? plugin.GatheringDatabase.SearchGatherable(query)
            : Provider.SearchCraftable(query).Select(r => (r.ItemId, r.Name)).ToList();

    /// <summary>Appends an order to the highlighted group, else the last one, creating the first group when the book is empty.</summary>
    private void AddOrder(uint itemId, OrderKind kind)
    {
        var group = Book.Groups.FirstOrDefault(g => g.Id == selectedGroupId) ?? Book.Groups.LastOrDefault();
        if (group == null)
        {
            group = new OrderGroup();
            Book.Groups.Add(group);
        }

        group.Orders.Add(new Order { ItemId = itemId, Amount = Quantity, Kind = kind });
        selectedGroupId = group.Id;
        Save();
        notice = $"Added {Provider.GetItemName(itemId)} ×{Quantity}{(kind == OrderKind.Gather ? " (gather)" : "")} to '{group.Name}'.";
        error = "";
    }

    // ------------------------------------------------------------- groups

    private void DrawGroups()
    {
        var book = Book;

        // The list scrolls inside a bounded region so the run controls below
        // stay reachable at the window's default size.
        var maxHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.5f, 140f, 460f);
        var height = Math.Min(EstimateHeight(book), maxHeight);
        using var child = ImRaii.Child("##orderGroups", new Vector2(-1, height), true);
        if (!child.Success)
            return;

        if (book.Groups.Count == 0)
        {
            ImGui.TextColored(UiTheme.Faint, "No orders yet. Search above, add the crafting-log selection, or import a list.");
            return;
        }

        for (var i = 0; i < book.Groups.Count; i++)
        {
            if (DrawGroup(book, i))
                break; // the list changed under us; redraw next frame
        }
    }

    /// <summary>Rough content height so a short book does not leave an empty scroll box; the child scrolls when it is off.</summary>
    private float EstimateHeight(OrderBook book)
    {
        var style = ImGui.GetStyle();
        var row = ImGui.GetFrameHeight() + style.ItemSpacing.Y;
        var line = ImGui.GetTextLineHeightWithSpacing();
        if (book.Groups.Count == 0)
            return row + style.WindowPadding.Y * 2;

        var height = style.WindowPadding.Y * 2;
        foreach (var group in book.Groups)
        {
            height += row;
            if (confirmDeleteGroupId == group.Id)
                height += row;
            if (openGroups.Contains(group.Id))
                height += (group.Orders.Count + 1) * (row + style.CellPadding.Y * 2) + style.ItemSpacing.Y * 2;
            if (previewGroupId == group.Id && preview != null)
                height += (preview.Orders.Count + 3 + (preview.Plan?.RawMaterials.Count ?? 0) + (preview.Plan?.CraftSteps.Count ?? 0)) * line;
        }

        return height;
    }

    /// <summary>Draws one group; true when the group list was modified and iteration must stop.</summary>
    private bool DrawGroup(OrderBook book, int index)
    {
        var group = book.Groups[index];
        var changedList = false;
        var orderRunner = plugin.OrderRunner;
        var running = orderRunner.CurrentGroup?.Id == group.Id && orderRunner.State is OrderRunState.Running or OrderRunState.Held;
        var selected = selectedGroupId == group.Id;

        ImGui.PushID(group.Id.GetHashCode());
        var style = ImGui.GetStyle();
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var buttonsWidth = ImGui.GetFrameHeight() * 3
                           + ButtonWidth("Preview") + ButtonWidth("×")
                           + style.ItemSpacing.X * 4;

        bool open;
        if (renamingGroupId == group.Id)
        {
            // The header becomes the name field; Enter or leaving commits, Escape cancels.
            ImGui.SetNextItemWidth(contentWidth - buttonsWidth - style.ItemSpacing.X);
            if (renameFocusPending)
            {
                ImGui.SetKeyboardFocusHere();
                renameFocusPending = false;
            }

            var committed = ImGui.InputText("##rename", ref renameText, 48,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            if (committed || ImGui.IsItemDeactivatedAfterEdit())
                CommitRename(group);
            else if (ImGui.IsItemDeactivated() || ImGui.IsKeyPressed(ImGuiKey.Escape))
                renamingGroupId = null;

            open = openGroups.Contains(group.Id);
        }
        else
        {
            if (running)
                UiTheme.PushHeaderTint(UiTheme.Info);
            else if (selected)
                UiTheme.PushHeaderTint(UiTheme.Accent);
            if (!group.Enabled)
                ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.Muted);

            var label = running ? $"{group.Name}  ▸ producing###hdr" : $"{group.Name}###hdr";
            open = ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowItemOverlap);

            if (!group.Enabled)
                ImGui.PopStyleColor();
            if (running || selected)
                UiTheme.PopHeaderTint();

            if (ImGui.IsItemClicked())
                selectedGroupId = group.Id;
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                StartRename(group);
            UiTheme.Tooltip($"{group.Orders.Count} order(s). Double-click to rename; new orders go to the highlighted group.");
        }

        if (open)
            openGroups.Add(group.Id);
        else
            openGroups.Remove(group.Id);

        // Header-line controls, right-aligned over the header.
        ImGui.SameLine(contentWidth - buttonsWidth);
        var enabled = group.Enabled;
        if (ImGui.Checkbox("##groupOn", ref enabled))
        {
            group.Enabled = enabled;
            Save();
        }

        UiTheme.Tooltip(enabled ? "Enabled: runs in turn" : "Disabled: skipped by Run orders");

        ImGui.SameLine();
        using (ImRaii.Disabled(index == 0))
        {
            if (ImGui.ArrowButton("##up", ImGuiDir.Up))
            {
                (book.Groups[index - 1], book.Groups[index]) = (book.Groups[index], book.Groups[index - 1]);
                Save();
                changedList = true;
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(index == book.Groups.Count - 1))
        {
            if (ImGui.ArrowButton("##down", ImGuiDir.Down))
            {
                (book.Groups[index + 1], book.Groups[index]) = (book.Groups[index], book.Groups[index + 1]);
                Save();
                changedList = true;
            }
        }

        ImGui.SameLine();
        if (UiTheme.TintedButton("Preview", UiTheme.Info))
            TogglePreview(group);
        UiTheme.Tooltip(previewGroupId == group.Id
            ? "Hide the plan"
            : "Plan this group now (what each order gets, then what to gather and craft) without starting");

        ImGui.SameLine();
        if (UiTheme.TintedButton("×", UiTheme.Danger))
        {
            // An empty group is not worth a question.
            if (group.Orders.Count == 0)
            {
                RemoveGroup(book, group);
                changedList = true;
            }
            else
            {
                confirmDeleteGroupId = group.Id;
            }
        }

        UiTheme.Tooltip("Delete this group");

        if (!changedList && confirmDeleteGroupId == group.Id)
        {
            ImGui.TextColored(UiTheme.Danger, $"Delete '{group.Name}' and its {group.Orders.Count} order(s)?");
            ImGui.SameLine();
            if (UiTheme.TintedButton("Delete", UiTheme.Danger))
            {
                RemoveGroup(book, group);
                changedList = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
                confirmDeleteGroupId = null;
        }

        if (!changedList && open)
        {
            ImGui.Indent(8);
            DrawOrders(group);
            if (previewGroupId == group.Id && preview != null)
                DrawPreview(preview);
            ImGui.Unindent(8);
        }

        ImGui.PopID();
        return changedList;
    }

    private static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

    private void StartRename(OrderGroup group)
    {
        renamingGroupId = group.Id;
        renameText = group.Name;
        renameFocusPending = true;
    }

    private void CommitRename(OrderGroup group)
    {
        var name = renameText.Trim();
        if (name.Length > 0 && name != group.Name)
        {
            group.Name = name;
            Save();
        }

        renamingGroupId = null;
    }

    private void RemoveGroup(OrderBook book, OrderGroup group)
    {
        book.Groups.Remove(group);
        Save();
        confirmDeleteGroupId = null;
        openGroups.Remove(group.Id);
        if (selectedGroupId == group.Id)
            selectedGroupId = null;
        if (previewGroupId == group.Id)
        {
            previewGroupId = null;
            preview = null;
        }
    }

    // ------------------------------------------------------------- orders

    private void DrawOrders(OrderGroup group)
    {
        if (group.Orders.Count == 0)
        {
            ImGui.TextColored(UiTheme.Faint, "No orders — add one from the search box.");
            return;
        }

        // The tier column only appears when an order of the group needs it,
        // so the usual book keeps its width at the window's default size.
        var showTier = group.Orders.Any(o => o.Mode == ProductionMode.Collectable);
        if (!ImGui.BeginTable("##orders", showTier ? 9 : 8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 82);
        ImGui.TableSetupColumn("Production", ImGuiTableColumnFlags.WidthFixed, 100);
        if (showTier)
            ImGui.TableSetupColumn("Tier", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("Mats", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 26);
        ImGui.TableSetupColumn("##del", ImGuiTableColumnFlags.WidthFixed, 22);
        ImGui.TableHeadersRow();

        for (var i = 0; i < group.Orders.Count; i++)
        {
            var order = group.Orders[i];
            ImGui.PushID(order.Id.GetHashCode());
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            UiTheme.GameIcon(Provider.GetItemIconId(order.ItemId), 18f);
            if (order.Enabled)
                ImGui.TextUnformatted(Provider.GetItemName(order.ItemId));
            else
                ImGui.TextColored(UiTheme.Muted, Provider.GetItemName(order.ItemId));

            // Kind (7.1): a gather order keeps only the modes that apply to gathering.
            ImGui.TableNextColumn();
            var kind = order.Kind;
            if (EnumCombo("##kind", ref kind, Kinds))
            {
                order.Kind = kind;
                if (kind == OrderKind.Gather)
                {
                    order.MaterialsOnly = false;
                    if (order.Mode is not (ProductionMode.Any or ProductionMode.Collectable))
                        order.Mode = ProductionMode.Any;
                }

                Save();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var amount = order.Amount;
            if (ImGui.InputInt("##amount", ref amount, 0, 0))
            {
                order.Amount = Math.Clamp(amount, 1, 99999);
                Save();
            }

            UiTheme.Tooltip(order.AmountMode == AmountMode.Restock ? "Keep this many in the bag" : "Produce this many");

            ImGui.TableNextColumn();
            var amountMode = order.AmountMode;
            if (EnumCombo("##amountMode", ref amountMode, AmountModes))
            {
                order.AmountMode = amountMode;
                Save();
            }

            ImGui.TableNextColumn();
            var mode = order.Mode;
            if (EnumCombo("##mode", ref mode, order.Kind == OrderKind.Gather ? GatherModes : ProductionModes))
            {
                order.Mode = mode;
                Save();
            }

            // Tier (7.23 / 7.1): only read when the mode is Collectable.
            if (showTier)
            {
                ImGui.TableNextColumn();
                if (order.Mode == ProductionMode.Collectable)
                {
                    var tier = order.CollectableTier;
                    if (EnumCombo("##tier", ref tier, Tiers))
                    {
                        order.CollectableTier = tier;
                        Save();
                    }
                }
                else
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(UiTheme.Faint, "–");
                    UiTheme.Tooltip("Collectability tier; applies when the production mode is Collectable");
                }
            }

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(order.Kind == OrderKind.Gather))
            {
                var materialsOnly = order.MaterialsOnly;
                if (ImGui.Checkbox("##materialsOnly", ref materialsOnly))
                {
                    order.MaterialsOnly = materialsOnly;
                    Save();
                }
            }

            UiTheme.Tooltip(order.Kind == OrderKind.Gather
                ? "A gather order has no craft to skip"
                : "Materials only: gather and craft everything the item needs, skip its own final craft");

            ImGui.TableNextColumn();
            var enabled = order.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                order.Enabled = enabled;
                Save();
            }

            UiTheme.Tooltip(enabled ? "Enabled" : "Disabled: kept in the list, not planned");

            ImGui.TableNextColumn();
            var remove = ImGui.SmallButton("×");
            UiTheme.Tooltip("Remove this order");
            ImGui.PopID();

            if (remove)
            {
                group.Orders.RemoveAt(i);
                Save();
                break;
            }
        }

        ImGui.EndTable();
    }

    /// <summary>A combo over a labelled enum (full-width by default); the tooltip explains the current choice.</summary>
    private static bool EnumCombo<T>(string id, ref T value, (T Value, string Label, string Tip)[] items, bool fullWidth = true)
        where T : struct, Enum
    {
        var changed = false;
        var current = value;
        var index = Array.FindIndex(items, i => EqualityComparer<T>.Default.Equals(i.Value, current));
        var label = index >= 0 ? items[index].Label : current.ToString();

        if (fullWidth)
            ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo(id, label))
        {
            foreach (var item in items)
            {
                var isSelected = EqualityComparer<T>.Default.Equals(item.Value, current);
                if (ImGui.Selectable(item.Label, isSelected))
                {
                    value = item.Value;
                    changed = true;
                }

                UiTheme.Tooltip(item.Tip);
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        else if (index >= 0)
        {
            UiTheme.Tooltip(items[index].Tip);
        }

        return changed;
    }

    // ------------------------------------------------------------ preview

    private void TogglePreview(OrderGroup group)
    {
        if (previewGroupId == group.Id)
        {
            previewGroupId = null;
            preview = null;
            return;
        }

        previewGroupId = group.Id;
        RefreshPreview(group);
    }

    private void RefreshPreview(OrderGroup group)
    {
        try
        {
            preview = plugin.OrderRunner.Preview(group);
            PlanTreePanel.Show(preview.Plan); // Status › Breakdown follows the last preview (7.12)
            SchedulePanel.Show(preview.Plan); // Status › Schedule too (7.15)
            error = "";
        }
        catch (Exception e)
        {
            preview = null;
            previewGroupId = null;
            error = $"Preview failed: {e.Message}";
        }
    }

    private void DrawPreview(GroupPlan groupPlan)
    {
        ImGui.Spacing();
        ImGui.TextColored(UiTheme.Muted, "Plan");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
            RefreshPreview(groupPlan.Group);
        UiTheme.Tooltip("Re-plan with the current inventory");

        foreach (var outcome in groupPlan.Orders)
        {
            UiTheme.GameIcon(Provider.GetItemIconId(outcome.Order.ItemId), 18f);
            ImGui.TextUnformatted(Provider.GetItemName(outcome.Order.ItemId));
            ImGui.SameLine(0, 6);
            if (outcome.Planned)
            {
                var tag = (outcome.Order.Kind == OrderKind.Gather ? " · gather" : "")
                          + (outcome.Order.Mode == ProductionMode.Collectable ? $" · {outcome.Order.CollectableTier} collectable" : "");
                ImGui.TextColored(UiTheme.Success, $"×{outcome.PlannedQuantity}");
                if (tag.Length > 0)
                {
                    ImGui.SameLine(0, 4);
                    ImGui.TextColored(UiTheme.Muted, tag);
                }
            }
            else
            {
                ImGui.TextColored(IsBenignSkip(outcome.SkipReason) ? UiTheme.Muted : UiTheme.Danger, $"— {outcome.SkipReason}");
            }
        }

        if (groupPlan.Plan == null || groupPlan.IsEmpty)
            ImGui.TextColored(UiTheme.Muted, "Nothing to produce.");
        else
            DrawPlanPreview(groupPlan.Plan, Provider);
    }

    /// <summary>Skips that mean "fine, nothing to do" read muted; the rest point at a problem.</summary>
    private static bool IsBenignSkip(string? reason) =>
        reason is "disabled" or "already stocked" or "amount is zero";

    /// <summary>The gather-first list and craft steps of a plan; shared with the single-target preview.</summary>
    internal static void DrawPlanPreview(ProductionPlan plan, DalamudRecipeProvider provider)
    {
        if (plan.RawMaterials.Count > 0)
        {
            ImGui.TextColored(UiTheme.Warning, "Gather first:");
            foreach (var material in plan.RawMaterials)
                ImGui.BulletText($"{provider.GetItemName(material.ItemId)} ×{material.Amount}");
        }
        else
        {
            ImGui.TextColored(UiTheme.Success, "✓ all raw materials on hand");
        }

        foreach (var step in plan.CraftSteps)
        {
            ImGui.TextColored(UiTheme.Faint, "  ▸");
            ImGui.SameLine(0, 4);
            UiTheme.GameIcon(provider.GetItemIconId(step.ItemId), 18f);
            ImGui.TextUnformatted($"{provider.GetItemName(step.ItemId)} ×{step.TotalProduced}");
            ImGui.SameLine(0, 6);
            var modeTag = step.Mode switch
            {
                ProductionMode.ForceHq => " · HQ",
                ProductionMode.QuickSynth => " · quick",
                ProductionMode.Collectable => $" · {step.CollectableTier} collectable",
                _ => "",
            };
            ImGui.TextColored(UiTheme.Muted, $"({step.Crafts} crafts{modeTag})");
        }
    }

    // ------------------------------------------------------ import/export

    private void DrawImportExport()
    {
        if (UiTheme.TintedButton("Export JSON", UiTheme.Muted))
            ExportJson();
        UiTheme.Tooltip("Copy the whole book to the clipboard as JSON");

        ImGui.SameLine();
        if (UiTheme.TintedButton("Import JSON", UiTheme.Muted))
            ImportJson();
        UiTheme.Tooltip("Read a JSON export from the clipboard; you choose replace or append next");

        ImGui.SameLine();
        if (UiTheme.TintedButton("Import Teamcraft list", UiTheme.Muted))
            ImportTeamcraft();
        UiTheme.Tooltip("Read a Teamcraft \"copy list as text\" from the clipboard into a new group");

        ImGui.SameLine();
        ImGui.Checkbox("Every section", ref importEverything);
        UiTheme.Tooltip("Import every section, not only final items (materials become orders too)");

        if (pendingImport is { } incoming)
        {
            var orders = incoming.Groups.Sum(g => g.Orders.Count);
            ImGui.TextColored(UiTheme.Accent, $"Import {incoming.Groups.Count} group(s), {orders} order(s):");
            ImGui.SameLine();
            using (ImRaii.Disabled(plugin.OrderRunner.Running))
            {
                if (UiTheme.TintedButton("Replace", UiTheme.Danger))
                    ApplyImport(incoming, replace: true);
            }

            UiTheme.Tooltip(plugin.OrderRunner.Running ? "Stop the orders before replacing the book" : "Discard the current book first");
            ImGui.SameLine();
            if (UiTheme.TintedButton("Append", UiTheme.Success))
                ApplyImport(incoming, replace: false);
            UiTheme.Tooltip("Add the imported groups after the current ones");
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel##import"))
                pendingImport = null;
        }

        if (notice.Length > 0)
            ImGui.TextColored(UiTheme.Muted, notice);
        if (error.Length > 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(UiTheme.Danger, error);
            ImGui.PopTextWrapPos();
        }

        if (unresolvedNames.Count > 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(UiTheme.Danger, $"Not craftable / not found (skipped): {string.Join(", ", unresolvedNames)}");
            ImGui.PopTextWrapPos();
        }
    }

    private void ExportJson()
    {
        try
        {
            ImGui.SetClipboardText(OrderBookJson.Export(Book, Provider.GetItemName));
            SetNotice($"Copied {Book.Groups.Count} group(s), {Book.Groups.Sum(g => g.Orders.Count)} order(s) to the clipboard.");
        }
        catch (Exception e)
        {
            SetError($"Export failed: {e.Message}");
        }
    }

    private void ImportJson()
    {
        var text = ReadClipboard();
        if (text.Length == 0)
        {
            SetError("The clipboard is empty.");
            return;
        }

        try
        {
            var book = OrderBookJson.Import(text, out var importError);
            if (book == null)
            {
                SetError(importError.Length > 0 ? importError : "The clipboard does not hold an order book export.");
                return;
            }

            pendingImport = book;
            notice = "";
            error = "";
            unresolvedNames = [];
        }
        catch (Exception e)
        {
            SetError($"Import failed: {e.Message}");
        }
    }

    private void ApplyImport(OrderBook incoming, bool replace)
    {
        // Fresh ids even if Core kept the exported ones: appending a book to
        // itself must not produce two groups the UI state keys on the same way.
        foreach (var group in incoming.Groups)
        {
            group.Id = Guid.NewGuid();
            foreach (var order in group.Orders)
                order.Id = Guid.NewGuid();
        }

        if (replace)
        {
            Book.Groups = incoming.Groups;
            Book.Perpetual = incoming.Perpetual;
        }
        else
        {
            Book.Groups.AddRange(incoming.Groups);
        }

        Save();
        pendingImport = null;
        selectedGroupId = Book.Groups.LastOrDefault()?.Id;
        SetNotice($"{(replace ? "Replaced the book with" : "Appended")} {incoming.Groups.Count} group(s), {incoming.Groups.Sum(g => g.Orders.Count)} order(s).");
    }

    private void ImportTeamcraft()
    {
        var text = ReadClipboard();
        if (text.Length == 0)
        {
            SetError("The clipboard is empty.");
            return;
        }

        IReadOnlyList<ImportedLine> lines;
        try
        {
            var parsed = TeamcraftListParser.Parse(text);
            lines = importEverything ? parsed : TeamcraftListParser.FinalItems(parsed);
        }
        catch (Exception e)
        {
            SetError($"Import failed: {e.Message}");
            return;
        }

        if (lines.Count == 0)
        {
            SetError("No list lines found in the clipboard (use Teamcraft's \"copy list as text\").");
            return;
        }

        // Exact name first: a substring hit like "Iron Ingot" → "Iron Ingot Ring" would be the wrong order.
        var group = new OrderGroup { Name = $"Teamcraft {DateTime.Now:MMM d HH:mm}" };
        var byItem = new Dictionary<uint, Order>();
        var unresolved = new List<string>();
        foreach (var line in lines)
        {
            var hit = Provider.FindCraftableByName(line.Name);
            if (hit == null)
            {
                var results = Provider.SearchCraftable(line.Name, 1);
                hit = results.Count > 0 ? results[0] : null;
            }

            if (hit == null)
            {
                if (!unresolved.Contains(line.Name))
                    unresolved.Add(line.Name);
                continue;
            }

            if (byItem.TryGetValue(hit.Value.ItemId, out var existing))
            {
                existing.Amount += Math.Max(line.Amount, 1);
                continue;
            }

            var order = new Order { ItemId = hit.Value.ItemId, Amount = Math.Max(line.Amount, 1) };
            byItem[hit.Value.ItemId] = order;
            group.Orders.Add(order);
        }

        unresolvedNames = unresolved;
        if (group.Orders.Count == 0)
        {
            SetError($"None of the {lines.Count} line(s) matched a craftable item.");
            return;
        }

        Book.Groups.Add(group);
        Save();
        selectedGroupId = group.Id;
        notice = $"Imported {group.Orders.Count} order(s) into '{group.Name}'.";
        error = "";
    }

    private static string ReadClipboard()
    {
        try
        {
            return ImGui.GetClipboardText() ?? "";
        }
        catch
        {
            // No clipboard owner or non-text content: the button just reports nothing to read.
            return "";
        }
    }

    private void SetNotice(string text)
    {
        notice = text;
        error = "";
        unresolvedNames = [];
    }

    private void SetError(string text)
    {
        error = text;
        notice = "";
        unresolvedNames = [];
    }
}
