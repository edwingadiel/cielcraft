using System;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Crafting;
using CielCraft.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace CielCraft.Windows;

public enum SpiritbondState
{
    Idle,
    Preparing,
    Teleporting,
    MovingToArea,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Spiritbond mode (roadmap 7.2): craft a cheap recipe or gather an item
/// purely to bond the equipped set, until the maintenance service has
/// extracted the wanted number of materia. Runs the plugin's normal batch
/// crafter or gathering loop (so repair, food and the extraction itself
/// happen between crafts / nodes as in any run) and restarts them as they
/// complete; stops at the target or on Stop. Ticked by the framework driver.
/// </summary>
public sealed class SpiritbondMode : AutomationMachine<SpiritbondState>
{
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AreaTimeout = TimeSpan.FromMinutes(5);
    private const float NodeAreaArrivalRange = 60f;

    /// <summary>Crafts per batch; a batch that completes is simply started again.</summary>
    private const int CraftsPerBatch = 99;

    /// <summary>The gather loop's target; it pauses on a full bag long before this is reached.</summary>
    private const int GatherAmount = 999;

    private readonly Plugin plugin;
    private readonly IGameBridge gameBridge;
    private readonly MaintenanceService maintenance;
    private readonly INavigationProvider navigation;
    private readonly TravelDriver travel;
    private readonly Throttle retry;

    private bool gather;
    private uint itemId;
    private uint recipeId;
    private uint jobId;
    private string itemName = "";
    private GatheringLocation? location;
    private int baselineExtracted;
    private DateTime phaseStartedAt;
    private bool gearsetRequested;
    private bool sawLoadingScreen;
    private DateTime zoneArrivedAt;
    private Vector3? areaDestination;
    private bool lastNodeProbe;
    private DateTime lastNodeProbeAt;

    public SpiritbondMode(Plugin plugin)
        : base(Plugin.Log, SystemClock.Instance, "[Spiritbond]", SpiritbondState.Idle, "Idle.")
    {
        this.plugin = plugin;
        gameBridge = plugin.GameBridge;
        maintenance = plugin.Maintenance;
        navigation = plugin.Navigation;
        travel = new TravelDriver(navigation, gameBridge, Clock, Log, "[Spiritbond]");
        retry = new Throttle(Clock, TimeSpan.FromSeconds(2));
    }

    public int TargetMateria { get; private set; }

    /// <summary>Materia extracted since this run started (the maintenance count is per session).</summary>
    public int Extracted => Math.Max(0, maintenance.MateriaExtracted - baselineExtracted);

    public bool IsGatherRun => gather;

    public bool IsActive => State is SpiritbondState.Preparing or SpiritbondState.Teleporting
        or SpiritbondState.MovingToArea or SpiritbondState.Running;

    /// <summary>The batch or loop under the run is paused (by the user or a blocked reason); resume it from the panel.</summary>
    public bool UnderlyingPaused =>
        State == SpiritbondState.Running
        && (gather ? plugin.GatheringLoop.State == Gathering.GatheringLoopState.Paused : plugin.BatchCrafter.State == BatchState.Paused);

    /// <summary>Why a run cannot start right now; null when it can.</summary>
    public string? Blocker()
    {
        if (!gameBridge.IsLoggedIn)
            return "not logged in";
        if (plugin.OrderRunner.Running || plugin.ProductionRunner.State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed))
            return "the order book / production runner is busy";
        if (plugin.BatchCrafter.State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting
            or BatchState.QuickStarting or BatchState.QuickRunning or BatchState.Paused)
            return "a batch is running";
        if (plugin.GatheringLoop.State is Gathering.GatheringLoopState.Running or Gathering.GatheringLoopState.Paused)
            return "the gathering loop is running";
        return null;
    }

    public bool StartCraft(uint craftRecipeId, int targetMateria)
    {
        if (IsActive || !Refuse(Blocker()))
            return false;

        var info = plugin.RecipeProvider.GetRecipeById(craftRecipeId);
        if (info == null)
            return Refuse($"recipe {craftRecipeId} could not be read");

        gather = false;
        recipeId = craftRecipeId;
        itemId = info.ResultItemId;
        jobId = info.ClassJobId;
        itemName = plugin.RecipeProvider.GetItemName(itemId);
        location = null;
        return Begin(targetMateria, $"crafting {itemName}");
    }

    public bool StartGather(uint gatherItemId, int targetMateria)
    {
        if (IsActive || !Refuse(Blocker()))
            return false;

        var job = plugin.GatheringDatabase.GetGatheringJob(gatherItemId);
        var where = plugin.GatheringDatabase.FindLocation(gatherItemId);
        if (job == null || where == null)
            return Refuse("no known node for that item");
        if (where.IsTimed)
            return Refuse("pick an item from an untimed node; timed nodes are for the order book (7.15)");
        if (!Plugin.IsVNavmeshAvailable)
            return Refuse("vnavmesh is required for gathering");

        gather = true;
        itemId = gatherItemId;
        recipeId = 0;
        jobId = job.Value;
        itemName = plugin.RecipeProvider.GetItemName(itemId);
        location = where;
        return Begin(targetMateria, $"gathering {itemName}");
    }

    private bool Refuse(string? reason)
    {
        if (reason == null)
            return true;

        Transition(SpiritbondState.Idle, $"Cannot start: {reason}.");
        return false;
    }

    private bool Begin(int targetMateria, string what)
    {
        TargetMateria = Math.Max(1, targetMateria);
        baselineExtracted = maintenance.MateriaExtracted;
        gearsetRequested = false;
        areaDestination = null;
        retry.Reset();
        Transition(SpiritbondState.Preparing, $"Spiritbond mode: {what} until {TargetMateria} materia are extracted.");
        return true;
    }

    public void Stop()
    {
        if (!IsActive)
            return;

        StopMachines();
        Transition(SpiritbondState.Idle, $"Stopped by user after {Extracted}/{TargetMateria} materia.");
    }

    /// <summary>Lifts a pause of the batch / loop under the run (a blocked maintenance reason, a manual pause).</summary>
    public void ResumeUnderlying()
    {
        if (!UnderlyingPaused)
            return;

        if (gather)
            plugin.GatheringLoop.Resume();
        else
            plugin.BatchCrafter.Resume();
    }

    private void StopMachines()
    {
        travel.Stop();
        if (gather)
            plugin.GatheringLoop.Stop();
        else
            plugin.BatchCrafter.Stop();
    }

    protected override void OnTransitioned(SpiritbondState previous, SpiritbondState current)
    {
        phaseStartedAt = Clock.UtcNow;
        retry.Reset();
    }

    protected override void OnTick()
    {
        switch (State)
        {
            case SpiritbondState.Preparing:
                TickPreparing();
                break;
            case SpiritbondState.Teleporting:
                TickTeleporting();
                break;
            case SpiritbondState.MovingToArea:
                TickMovingToArea();
                break;
            case SpiritbondState.Running:
                TickRunning();
                break;
        }
    }

    private bool ReachedTarget()
    {
        if (Extracted < TargetMateria)
            return false;

        StopMachines();
        Transition(SpiritbondState.Completed, $"Completed: {Extracted} materia extracted.");
        plugin.Notifier.Notify(NotificationKind.Completed, $"Spiritbond mode done: {Extracted} materia extracted.");
        return true;
    }

    private void Fail(string reason)
    {
        StopMachines();
        Transition(SpiritbondState.Failed, $"Failed: {reason}.");
        plugin.Notifier.Notify(NotificationKind.Attention, $"Spiritbond mode stopped: {reason}.");
    }

    // The preparation mirrors the production runner's (7.1 / 7.6 pacing kept):
    // leave any node, get on the job, then open the recipe / reach the nodes.
    private void TickPreparing()
    {
        if (ReachedTarget())
            return;

        if (Clock.UtcNow - phaseStartedAt > PrepareTimeout)
        {
            Fail(gather ? "could not start gathering in time" : $"could not start the batch in time ({plugin.BatchCrafter.StatusText})");
            return;
        }

        if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
        {
            retry.Try(gameBridge.CloseGatheringWindow);
            phaseStartedAt = Clock.UtcNow;
            return;
        }

        if (gameBridge.IsCrafting)
            return; // the previous batch's last craft is still ending

        if (!EnsureJob())
            return;

        // A job change just happened: let it settle before touching the log (pacing).
        if (gearsetRequested && Clock.UtcNow - retry.LastAttempt < Pacing.AfterJobChange)
            return;

        if (gather)
            PrepareGather();
        else
            PrepareCraft();
    }

    private bool EnsureJob()
    {
        if (gameBridge.CurrentClassJobId == jobId)
            return true;

        if (gameBridge.IsPreparingToCraft || gameBridge.IsAddonVisible("RecipeNote"))
        {
            retry.Try(gameBridge.CloseRecipeNote);
            return false;
        }

        retry.Try(() =>
        {
            gearsetRequested = true;
            if (!gameBridge.EquipGearsetForJob(jobId))
                Fail($"no gearset found for job {plugin.RecipeProvider.GetJobAbbreviation(jobId)}");
        });
        return false;
    }

    private void PrepareCraft()
    {
        maintenance.PrepareFor(MaintenanceActivity.Crafting);

        if (gameBridge.SelectedRecipeId != recipeId || !gameBridge.IsReadyToStartCraft)
        {
            retry.Try(() => gameBridge.OpenRecipe(recipeId));
            StatusText = $"Opening the crafting log on {itemName}...";
            return;
        }

        var craftable = InventoryMath.CraftableCount(gameBridge.GetRecipeRequirements((ushort)recipeId));
        if (craftable < 1)
        {
            Fail($"no materials left for {itemName}");
            return;
        }

        // Normal synthesis only: spiritbond comes from the crafts themselves,
        // and quick synthesis would also skip the crafter's own rotation.
        if (!retry.IsReady)
            return;

        retry.Touch();
        if (plugin.BatchCrafter.Start(Math.Min(craftable, CraftsPerBatch), quickSynth: false))
        {
            Log.Information($"[Spiritbond] Batch of {Math.Min(craftable, CraftsPerBatch)} × {itemName} for spiritbond ({Extracted}/{TargetMateria} materia so far).");
            Transition(SpiritbondState.Running, Progress());
        }
    }

    private void PrepareGather()
    {
        maintenance.PrepareFor(MaintenanceActivity.Gathering);
        var where = location!;

        if (where.TerritoryId != 0 && gameBridge.CurrentTerritoryId != where.TerritoryId)
        {
            retry.Try(() =>
            {
                sawLoadingScreen = false;
                if (gameBridge.TeleportToTerritory(where.TerritoryId))
                    Transition(SpiritbondState.Teleporting, $"Teleporting to the nodes of {itemName}...");
                else
                    Fail($"no attuned aetheryte in territory {where.TerritoryId} for {itemName}");
            });
            return;
        }

        if (where.Position != default && !(NearNodeArea() && NodeNearby()))
        {
            areaDestination = null;
            Transition(SpiritbondState.MovingToArea, $"Traveling to the node area of {itemName}...");
            return;
        }

        if (!retry.IsReady)
            return;

        retry.Touch();
        if (plugin.GatheringLoop.Start(itemId, GatherAmount, AreaCenter()))
        {
            Log.Information($"[Spiritbond] Gathering {itemName} for spiritbond ({Extracted}/{TargetMateria} materia so far).");
            Transition(SpiritbondState.Running, Progress());
        }
    }

    private void TickTeleporting()
    {
        var where = location!;
        if (Clock.UtcNow - phaseStartedAt > TeleportTimeout)
        {
            Fail("teleport did not complete (cast interrupted or loading took too long)");
            return;
        }

        if (gameBridge.IsBetweenAreas)
        {
            sawLoadingScreen = true;
            zoneArrivedAt = DateTime.MinValue;
            return;
        }

        if (sawLoadingScreen && gameBridge.CurrentTerritoryId == where.TerritoryId && gameBridge.GetPlayerState() != null)
        {
            // Let the zone settle before the next server-visible action (pacing).
            if (zoneArrivedAt == DateTime.MinValue)
                zoneArrivedAt = Clock.UtcNow;
            if (Clock.UtcNow - zoneArrivedAt < Pacing.AfterZoneChange)
                return;

            Transition(SpiritbondState.Preparing, $"Arrived; preparing to gather {itemName}.");
        }
    }

    private void TickMovingToArea()
    {
        if (Clock.UtcNow - phaseStartedAt > AreaTimeout)
        {
            Fail("could not reach the node area in time");
            return;
        }

        if (NodeNearby() && NearNodeArea())
        {
            navigation.Stop();
            Transition(SpiritbondState.Preparing, $"Node area reached; preparing to gather {itemName}.");
            return;
        }

        if (!navigation.IsReady)
            return; // navmesh still building after the zone change

        var player = gameBridge.GetPlayerState();
        if (player == null)
            return;

        if (areaDestination == null)
        {
            var area = location!.Position;
            var approximate = new Vector3(area.X, player.Position.Y, area.Y);
            areaDestination = navigation.FindNearestMeshPoint(approximate, 40f, 500f);
            if (areaDestination == null)
            {
                Fail($"could not project the node area ({area.X:F0}, {area.Y:F0}) onto the navmesh");
                return;
            }

            travel.Start(
                areaDestination.Value,
                NodeAreaArrivalRange,
                plugin.Capabilities.Current.CanFlyIn(gameBridge.CurrentTerritoryId),
                preciseArrival: false,
                AreaTimeout,
                "the node area");
        }

        travel.Tick();
        StatusText = travel.StatusText;
        if (travel.State == TravelState.Failed)
            Fail(travel.FailureReason);
    }

    private void TickRunning()
    {
        if (ReachedTarget())
            return;

        if (gather)
        {
            switch (plugin.GatheringLoop.State)
            {
                case Gathering.GatheringLoopState.Completed:
                    Transition(SpiritbondState.Preparing, "Gather loop done; starting the next.");
                    return;
                case Gathering.GatheringLoopState.Failed:
                    Fail(plugin.GatheringLoop.StatusText);
                    return;
                case Gathering.GatheringLoopState.Idle:
                    Transition(SpiritbondState.Idle, $"The gathering loop was stopped ({Extracted}/{TargetMateria} materia).");
                    return;
                case Gathering.GatheringLoopState.Paused:
                    StatusText = $"Paused: {plugin.GatheringLoop.StatusText}";
                    return;
            }
        }
        else
        {
            switch (plugin.BatchCrafter.State)
            {
                case BatchState.Completed:
                    Transition(SpiritbondState.Preparing, "Batch done; starting the next.");
                    return;
                case BatchState.Failed:
                    Fail(plugin.BatchCrafter.StatusText);
                    return;
                case BatchState.Idle:
                    Transition(SpiritbondState.Idle, $"The batch was stopped ({Extracted}/{TargetMateria} materia).");
                    return;
                case BatchState.Paused:
                    StatusText = $"Paused: {plugin.BatchCrafter.StatusText}";
                    return;
            }
        }

        StatusText = Progress();
    }

    private string Progress() =>
        gather
            ? $"Gathering {itemName} ({plugin.GatheringLoop.Gathered} taken); materia {Extracted}/{TargetMateria}."
            : $"Crafting {itemName} ({plugin.BatchCrafter.CompletedCrafts}/{plugin.BatchCrafter.TargetQuantity} this batch); materia {Extracted}/{TargetMateria}.";

    /// <summary>Object-table scans are costly; cache "is a node visible?" for a second.</summary>
    private bool NodeNearby()
    {
        if (Clock.UtcNow - lastNodeProbeAt > TimeSpan.FromSeconds(1))
        {
            lastNodeProbeAt = Clock.UtcNow;
            lastNodeProbe = gameBridge.FindNearestGatheringNode() != null;
        }

        return lastNodeProbe;
    }

    private bool NearNodeArea()
    {
        var area = location!.Position;
        if (area == default)
            return true;

        var player = gameBridge.GetPlayerState();
        if (player == null)
            return false;

        var target = areaDestination is { } projected ? new Vector2(projected.X, projected.Z) : area;
        return Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), target) <= NodeAreaArrivalRange * 2;
    }

    private Vector3? AreaCenter()
    {
        if (areaDestination != null)
            return areaDestination;

        var area = location!.Position;
        var player = gameBridge.GetPlayerState();
        if (area == default || player == null)
            return null;

        var approximate = new Vector3(area.X, player.Position.Y, area.Y);
        return navigation.IsReady ? navigation.FindNearestMeshPoint(approximate, 40f, 500f) ?? approximate : approximate;
    }

    public override System.Collections.Generic.IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"{(gather ? "gather" : "craft")} item {itemId} (recipe {recipeId}, job {jobId}); target {TargetMateria}, extracted {Extracted} (baseline {baselineExtracted}); phase since {phaseStartedAt:HH:mm:ss}Z; areaDestination {areaDestination?.ToString() ?? "-"}";
    }
}

/// <summary>
/// Tools › Spiritbond (roadmap 7.2): pick a cheap recipe or a gather item, a
/// target materia count, Start / Stop, and watch the equipped pieces bond.
/// <see cref="DrawSettings"/> is the auto-extract toggle for Settings › Crafting.
/// </summary>
public sealed class SpiritbondPanel
{
    private readonly Plugin plugin;
    private readonly SpiritbondMode mode;

    private bool gatherKind;
    private string searchText = "";
    private System.Collections.Generic.IReadOnlyList<(uint ItemId, uint RecipeId, string Name)> searchResults = [];
    private uint selectedItemId;
    private uint selectedRecipeId;
    private string selectedName = "";
    private int targetMateria = 5;

    public SpiritbondPanel(Plugin plugin)
    {
        this.plugin = plugin;
        mode = new SpiritbondMode(plugin);
        // The run must outlive the window being open; it is ticked with the machines.
        plugin.Driver.Add(mode.Tick);
    }

    /// <summary>The mode's machine, for the diagnostic report and the emergency stop.</summary>
    public SpiritbondMode Mode => mode;

    public void Draw()
    {
        UiTheme.Hint(
            "Bond the equipped set by crafting a cheap recipe or gathering an item; between crafts / nodes the maintenance "
            + "service extracts materia from every piece that reaches 100%. The run stops once the target count is extracted.");

        DrawTarget();
        DrawRun();
        DrawEquipment();
    }

    // ------------------------------------------------------------ target

    private void DrawTarget()
    {
        UiTheme.SectionHeader("What to repeat");

        using (ImRaii.Disabled(mode.IsActive))
        {
            if (ImGui.RadioButton("Craft a recipe", !gatherKind) && gatherKind)
                SwitchKind(false);
            ImGui.SameLine(0, 12);
            if (ImGui.RadioButton("Gather an item", gatherKind) && !gatherKind)
                SwitchKind(true);

            ImGui.SetNextItemWidth(Math.Min(360f, ImGui.GetContentRegionAvail().X));
            var hint = gatherKind ? "Search gatherable item…" : "Search craftable item…";
            if (ImGui.InputTextWithHint("##spiritbondSearch", hint, ref searchText, 64))
                searchResults = Search(searchText);

            if (!gatherKind)
            {
                ImGui.SameLine();
                var selected = plugin.GameBridge.SelectedRecipeId;
                using (ImRaii.Disabled(selected == 0))
                {
                    if (UiTheme.TintedButton("Crafting-log selection", UiTheme.Muted))
                        SelectRecipe(selected);
                }

                UiTheme.Tooltip("Use the recipe selected in the crafting log");
            }
        }

        if (searchResults.Count > 0)
        {
            using var child = ImRaii.Child("##spiritbondResults", new Vector2(-1, Math.Min(searchResults.Count, 6) * 24f + 8), true);
            if (child.Success)
            {
                foreach (var result in searchResults)
                {
                    UiTheme.GameIcon(plugin.RecipeProvider.GetItemIconId(result.ItemId), 18f);
                    if (ImGui.Selectable($"{result.Name}##sb{result.ItemId}"))
                    {
                        if (gatherKind)
                            SelectGatherItem(result.ItemId, result.Name);
                        else
                            SelectRecipe(result.RecipeId);
                        searchText = "";
                        searchResults = [];
                        break;
                    }
                }
            }
        }

        if (selectedItemId == 0)
        {
            ImGui.TextColored(UiTheme.Faint, gatherKind
                ? "Pick an item from an untimed node; the character teleports and walks to it."
                : "Pick a recipe whose materials you have plenty of; it is crafted normally, never quick-synthesized.");
            return;
        }

        UiTheme.GameIcon(plugin.RecipeProvider.GetItemIconId(selectedItemId), 20f);
        UiTheme.KeyValue(gatherKind ? "Item" : "Recipe", selectedName);
        ImGui.SameLine(0, 10);
        ImGui.TextColored(UiTheme.Muted, DescribeSelection());
    }

    private void SwitchKind(bool gatherItems)
    {
        gatherKind = gatherItems;
        selectedItemId = 0;
        selectedRecipeId = 0;
        selectedName = "";
        searchText = "";
        searchResults = [];
    }

    private System.Collections.Generic.IReadOnlyList<(uint ItemId, uint RecipeId, string Name)> Search(string query)
    {
        var results = new System.Collections.Generic.List<(uint, uint, string)>();
        if (gatherKind)
        {
            foreach (var (id, name) in plugin.GatheringDatabase.SearchGatherable(query))
                results.Add((id, 0u, name));
        }
        else
        {
            foreach (var (recipe, id, name) in plugin.RecipeProvider.SearchCraftable(query))
                results.Add((id, recipe, name));
        }

        return results;
    }

    private void SelectRecipe(uint recipeId)
    {
        var info = plugin.RecipeProvider.GetRecipeById(recipeId);
        if (info == null)
            return;

        selectedRecipeId = recipeId;
        selectedItemId = info.ResultItemId;
        selectedName = plugin.RecipeProvider.GetItemName(info.ResultItemId);
    }

    private void SelectGatherItem(uint itemId, string name)
    {
        selectedRecipeId = 0;
        selectedItemId = itemId;
        selectedName = name;
    }

    private string DescribeSelection()
    {
        if (gatherKind)
        {
            var job = plugin.GatheringDatabase.GetGatheringJob(selectedItemId);
            var where = plugin.GatheringDatabase.FindLocation(selectedItemId);
            if (job == null || where == null)
                return "no known node";

            return $"{plugin.RecipeProvider.GetJobAbbreviation(job.Value)} · {ZoneName(where.TerritoryId)}"
                   + (where.IsTimed ? " · timed node (not supported here)" : "");
        }

        var info = plugin.RecipeProvider.GetRecipeById(selectedRecipeId);
        if (info == null)
            return "";

        var craftable = InventoryMath.CraftableCount(plugin.GameBridge.GetRecipeRequirements((ushort)selectedRecipeId));
        return $"{plugin.RecipeProvider.GetJobAbbreviation(info.ClassJobId)} · materials for {craftable} craft{(craftable == 1 ? "" : "s")}";
    }

    private static string ZoneName(uint territoryId) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(territoryId, out var territory)
            ? territory.PlaceName.Value.Name.ExtractText()
            : $"zone {territoryId}";

    // --------------------------------------------------------------- run

    private void DrawRun()
    {
        UiTheme.SectionHeader("Run");

        if (!plugin.Configuration.AutoExtractMateria)
        {
            ImGui.TextColored(UiTheme.Warning, "Materia extraction is off in Settings › Crafting: the gear would bond but nothing gets extracted.");
            ImGui.SameLine();
            if (UiTheme.TintedButtonSmall("Turn it on", UiTheme.Accent))
            {
                plugin.Configuration.AutoExtractMateria = true;
                plugin.Configuration.Save();
            }
        }

        using (ImRaii.Disabled(mode.IsActive))
        {
            ImGui.SetNextItemWidth(110);
            ImGui.InputInt("Target materia", ref targetMateria);
            targetMateria = Math.Clamp(targetMateria, 1, 999);
        }

        UiTheme.Tooltip("Stop once this many materia have been extracted during the run");

        ImGui.SameLine(0, 16);
        if (mode.IsActive)
        {
            if (UiTheme.TintedButton("Stop", UiTheme.Danger))
                mode.Stop();

            if (mode.UnderlyingPaused)
            {
                ImGui.SameLine();
                if (UiTheme.TintedButton("Resume", UiTheme.Success))
                    mode.ResumeUnderlying();
                UiTheme.Tooltip("Lift the pause of the batch / gathering loop under the run");
            }
        }
        else
        {
            var blocker = selectedItemId == 0 ? "pick a recipe or item first" : mode.Blocker();
            using (ImRaii.Disabled(blocker != null))
            {
                if (UiTheme.TintedButton("Start", UiTheme.Accent))
                {
                    if (gatherKind)
                        mode.StartGather(selectedItemId, targetMateria);
                    else
                        mode.StartCraft(selectedRecipeId, targetMateria);
                }
            }

            if (blocker != null)
                UiTheme.Tooltip($"Cannot start: {blocker}");
        }

        var paused = mode.UnderlyingPaused;
        UiTheme.StateBadge(mode.State.ToString(), paused, mode.StatusText);

        if (mode.IsActive || mode.State is SpiritbondState.Completed)
        {
            var fraction = mode.TargetMateria == 0 ? 0f : Math.Clamp(mode.Extracted / (float)mode.TargetMateria, 0f, 1f);
            UiTheme.ProgressBar(fraction, $"{mode.Extracted} / {mode.TargetMateria} materia", mode.State == SpiritbondState.Completed ? UiTheme.Success : null);
        }

        if (plugin.Maintenance.StatusText.Length > 0 && mode.IsActive)
        {
            ImGui.TextColored(UiTheme.Info, "Maintenance:");
            ImGui.SameLine(0, 6);
            ImGui.TextColored(UiTheme.Muted, plugin.Maintenance.StatusText);
        }

        UiTheme.KeyValue("Extracted this session", plugin.Maintenance.MateriaExtracted.ToString());
    }

    // --------------------------------------------------------- equipment

    private void DrawEquipment()
    {
        UiTheme.SectionHeader("Equipped gear");

        var pieces = plugin.GameBridge.GetEquipmentSpiritbond();
        if (pieces.Count == 0)
        {
            ImGui.TextColored(UiTheme.Faint, "Nothing equipped (or not logged in).");
            return;
        }

        if (!ImGui.BeginTable("##spiritbondGear", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Piece", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Spiritbond", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();

        foreach (var piece in pieces)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            UiTheme.GameIcon(plugin.RecipeProvider.GetItemIconId(piece.ItemId), 18f);
            ImGui.TextUnformatted(plugin.RecipeProvider.GetItemName(piece.ItemId));
            ImGui.TableNextColumn();
            UiTheme.ProgressBar(piece.Spiritbond / (float)EquippedSpiritbond.Full, $"{piece.Percent:F1}%", piece.IsFull ? UiTheme.Success : null);
        }

        ImGui.EndTable();
    }

    // ---------------------------------------------------------- settings

    /// <summary>The auto-extract toggle (roadmap 7.2); the coordinator draws it under Settings › Crafting.</summary>
    public static void DrawSettings(Configuration configuration)
    {
        UiTheme.Toggle("Extract materia at 100% spiritbond", configuration.AutoExtractMateria,
            v => { configuration.AutoExtractMateria = v; configuration.Save(); },
            "Between crafts and between nodes, one piece per pass; a failure turns it off until the next plugin load.");
    }
}
