using System;
using System.Linq;
using CielCraft.Core;
using CielCraft.Crafting;
using CielCraft.Game;
using CielCraft.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CielCraft;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static Dalamud.Plugin.Services.ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

    /// <summary>All plugin logging goes through here so the diagnostic report can include it.</summary>
    internal static Diagnostics.DiagnosticLog Log { get; private set; } = null!;

    private const string CommandName = "/cielcraft";

    public Infrastructure.FrameworkDriver Driver { get; init; }
    public Infrastructure.ChatNotifier Notifier { get; init; }
    public Configuration Configuration { get; init; }
    public IGameBridge GameBridge { get; init; }
    /// <summary>What the character can do (roadmap 7.16); refreshed on load, login and demand.</summary>
    public CapabilityReader Capabilities { get; init; } = new();
    public DalamudRecipeProvider RecipeProvider { get; init; }
    public GatheringDatabase GatheringDatabase { get; init; }
    public CraftStateMonitor CraftMonitor { get; init; }
    public ActionExecutor ActionExecutor { get; init; }
    public SolverService SolverService { get; init; }
    public CraftAutomator CraftAutomator { get; init; }
    public BatchCrafter BatchCrafter { get; init; }
    public ProductionRunner ProductionRunner { get; init; }
    public INavigationProvider Navigation { get; init; }
    public Gathering.GatheringController GatheringController { get; init; }
    public Gathering.GatheringLoop GatheringLoop { get; init; }
    public MaintenanceService Maintenance { get; init; }
    /// <summary>NPC placements and menders (roadmap 7.3).</summary>
    public NpcDatabase NpcDatabase { get; init; }
    /// <summary>Goes to NPCs and drives their dialogs (roadmap 7.3); ticked by whoever started the interaction.</summary>
    public Npc.NpcInteractor NpcInteractor { get; init; }
    /// <summary>Gil vendors as a material source (roadmap 7.3b).</summary>
    public Sourcing.VendorSource VendorSource { get; init; }
    /// <summary>Retainers as a material source (roadmap 7.17).</summary>
    public Sourcing.RetainerSource RetainerSource { get; init; }
    public RetainerDatabase RetainerDatabase { get; init; }
    /// <summary>Storage rules, desynthesis and trash cleanup after a run (roadmap 7.17).</summary>
    public Sourcing.InventoryKeeper InventoryKeeper { get; init; }
    /// <summary>Order book runner (roadmap 7.13); replaces the production queue.</summary>
    public OrderRunner OrderRunner { get; init; }
    public Social.SocialGuard SocialGuard { get; init; }
    /// <summary>Exit-when-done behaviour (roadmap 7.20).</summary>
    public RunFinisher Finisher { get; init; }

    public readonly WindowSystem WindowSystem = new("CielCraft");
    /// <summary>The one window (roadmap 7.21); settings, debug and setup are pages in it.</summary>
    private MainWindow MainWindow { get; init; }

    /// <summary>Spiritbond mode (roadmap 7.2); lives with its Tools page.</summary>
    public SpiritbondMode Spiritbond => MainWindow.Spiritbond;

    /// <summary>Crafting stays usable without vnavmesh; only gathering automation needs it (spec §31).</summary>
    internal static bool IsVNavmeshAvailable =>
        PluginInterface.InstalledPlugins.Any(p => p.InternalName == "vnavmesh" && p.IsLoaded);

    public Plugin()
    {
        Log = new Diagnostics.DiagnosticLog(PluginLog);
        Driver = new Infrastructure.FrameworkDriver(Framework);
        // Dalamud loads plugin assemblies from memory, so the native solver can't find itself
        // via Assembly.Location; point it at the on-disk plugin folder instead.
        CielCraft.Raphael.RaphaelSolver.LibraryDirectory = PluginInterface.AssemblyLocation.DirectoryName;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        MigrateConfiguration(Configuration);
        GameBridge = new DalamudGameBridge();
        Notifier = new Infrastructure.ChatNotifier(ChatGui, Configuration, GameBridge, Log);
        RecipeProvider = new DalamudRecipeProvider(
            () => GameBridge.CurrentClassJobId, jobId => GameBridge.HasGearsetForJob(jobId), () => Capabilities.Current);
        GatheringDatabase = new GatheringDatabase(() => Capabilities.Current);
        var actionResolver = new DalamudActionResolver();
        CraftMonitor = new CraftStateMonitor(GameBridge, Log, SystemClock.Instance);
        ActionExecutor = new ActionExecutor(GameBridge, CraftMonitor, Log, SystemClock.Instance);
        SolverService = new SolverService(new CielCraft.Raphael.RaphaelSolver(), LoadSolutionCache(), Log);
        CraftAutomator = new CraftAutomator(
            GameBridge, CraftMonitor, ActionExecutor, Configuration, actionResolver, Log, SystemClock.Instance);
        Navigation = new Navigation.VNavmeshProvider();
        // NPC layer (7.3): the mender trip (7.3a) and every M3 source travel through it.
        NpcDatabase = new NpcDatabase(GameBridge.CanTeleportTo, GatheringDatabase.GetTerritoryName);
        NpcInteractor = new Npc.NpcInteractor(
            GameBridge, Navigation, Log, SystemClock.Instance, () => Capabilities.Current, GatheringDatabase.GetTerritoryName);
        Maintenance = new MaintenanceService(
            GameBridge, Configuration, Log, SystemClock.Instance, RecipeProvider.GetItemName, NpcInteractor, NpcDatabase);
        BatchCrafter = new BatchCrafter(
            GameBridge, CraftMonitor, CraftAutomator, SolverService, RecipeProvider, Configuration, Maintenance,
            actionResolver, Log, SystemClock.Instance);
        // Material sources beyond nodes (M3), asked in this order for what no node yields.
        VendorSource = new Sourcing.VendorSource(
            new ShopDatabase(GameBridge, NpcDatabase), NpcDatabase, NpcInteractor, GameBridge, Configuration,
            Log, SystemClock.Instance, RecipeProvider.GetItemName);
        RetainerDatabase = new RetainerDatabase(GameBridge);
        RetainerSource = new Sourcing.RetainerSource(
            GameBridge, RetainerDatabase, Configuration, Log, SystemClock.Instance, NpcInteractor, RecipeProvider.GetItemName);
        InventoryKeeper = new Sourcing.InventoryKeeper(
            GameBridge, RetainerDatabase, Configuration, Log, SystemClock.Instance, NpcInteractor, RecipeProvider.GetItemName);
        // Retainers last: a node, a vendor or an exchange beats a bell trip.
        var sources = new IMaterialSource[] { VendorSource, RetainerSource };
        Windows.PlanTreePanel.UseSources(sources);
        // Gathering action ids resolved by name from the Action sheet (7.14); one catalogue for the controller, the loop and the settings page.
        var gatheringCatalog = new Gathering.GatheringActionCatalog(Log);
        Windows.GatheringRotationPanel.Catalog = gatheringCatalog;
        GatheringController = new Gathering.GatheringController(
            GameBridge, Navigation, Configuration, Log, SystemClock.Instance, () => Capabilities.Current, gatheringCatalog);
        GatheringLoop = new Gathering.GatheringLoop(
            GameBridge, GatheringController, Navigation, Configuration, Maintenance, Log, SystemClock.Instance,
            () => Capabilities.Current, gatheringCatalog);
        ProductionRunner = new ProductionRunner(
            GameBridge, BatchCrafter, RecipeProvider, GatheringLoop, GatheringDatabase, Maintenance, Navigation, Configuration,
            Capabilities, Log, SystemClock.Instance, Notifier, sources);
        SocialGuard = new Social.SocialGuard(this, GameBridge, Configuration);
        OrderRunner = new OrderRunner(
            ProductionRunner, RecipeProvider, GameBridge, Configuration, () => Capabilities.Current, Notifier, Log, SystemClock.Instance,
            SolverService); // planning solves (7.22) land in the same disk cache the batch reads
        Finisher = new RunFinisher(ProductionRunner, OrderRunner, Configuration, GameBridge, Log, SystemClock.Instance, InventoryKeeper);

        // One Framework.Update subscription for the automation layers, ticked in
        // the order they used to subscribe in (monitor before executor before
        // automator before batch before controller before loop before runner
        // before the order book).
        Driver.Add(CraftMonitor.Tick);
        Driver.Add(ActionExecutor.Tick);
        Driver.Add(CraftAutomator.Tick);
        Driver.Add(BatchCrafter.Tick);
        Driver.Add(GatheringController.Tick);
        Driver.Add(GatheringLoop.Tick);
        Driver.Add(ProductionRunner.Tick);
        Driver.Add(OrderRunner.Tick);
        Driver.Add(Finisher.Tick);
        Driver.Add(InventoryKeeper.Tick); // no-op while idle (7.17)

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the CielCraft window. \"/cielcraft run\" start the order book, \"/cielcraft hold\" hold it after the current group, \"/cielcraft pause\" / \"/cielcraft resume\" / \"/cielcraft step\" (lock-step), \"/cielcraft stop\" emergency stop, \"/cielcraft config\" settings page, \"/cielcraft setup\" character checklist, \"/cielcraft debug\" debug page, \"/cielcraft plan\" print the production breakdown, \"/cielcraft report\" copy a diagnostic report.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        ClientState.Login += OnLogin;

        // A plugin (re)load while logged in never sees the login event.
        if (ClientState.IsLoggedIn)
            Capabilities.Refresh();

        Log.Information("[Plugin] CielCraft loaded.");
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        WindowSystem.RemoveAllWindows();

        SocialGuard.Dispose();
        CraftAutomator.Dispose();
        Notifier.Dispose();

        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        Driver.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
                ToggleConfigUi();
                break;
            case "debug":
                ToggleDebugUi();
                break;
            case "setup":
                ToggleSetupUi();
                break;
            case "stop":
                StopEverything();
                break;
            case "run":
                if (!OrderRunner.Start())
                    ChatGui.Print(OrderRunner.StatusText, "CielCraft");
                break;
            case "hold":
                OrderRunner.Hold();
                ChatGui.Print(OrderRunner.StatusText.Length > 0 ? OrderRunner.StatusText : "The order book is not running.", "CielCraft");
                break;
            case "pause":
                PauseTopLayer();
                break;
            case "resume":
                ResumeTopLayer();
                break;
            case "step":
                // Lock-step (roadmap 7.18): let exactly one craft action through.
                ChatGui.Print(CraftAutomator.Step() ? CraftAutomator.StatusText : "Nothing is waiting for a step.", "CielCraft");
                break;
            case "plan":
                // Production breakdown as text (roadmap 7.12).
                foreach (var line in Windows.PlanTreePanel.PlanText(this).Split('\n'))
                    ChatGui.Print(line, "CielCraft");
                break;
            case "report":
                SaveAndCopyReport();
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    /// <summary>
    /// Pauses whichever automation layer is driving right now (runner, else
    /// batch, else gather loop); false when nothing was running. The reason
    /// ends up in the layer's status text, which is how a later
    /// <see cref="ResumeTopLayer"/> can tell its own pause apart.
    /// </summary>
    public bool PauseTopLayer(string reason = "paused by command")
    {
        if (ProductionRunner.State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed or ProductionState.Paused))
            ProductionRunner.Pause(reason);
        else if (BatchCrafter.State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting or BatchState.QuickStarting or BatchState.QuickRunning)
            BatchCrafter.Pause(reason);
        else if (GatheringLoop.State == Gathering.GatheringLoopState.Running)
            GatheringLoop.Pause(reason);
        else
            return false;

        return true;
    }

    /// <summary>
    /// Resumes whichever layer is paused (runner, else batch, else gather
    /// loop). With <paramref name="onlyIfReason"/> set, only a pause whose
    /// status carries that reason is lifted — the social guard must not
    /// resume a run the user paused themselves (roadmap 7.10).
    /// </summary>
    public void ResumeTopLayer(string? onlyIfReason = null)
    {
        if (onlyIfReason == null)
            SocialGuard.Core.CancelSettle();

        bool Matches(string status) => onlyIfReason == null || status.Contains(onlyIfReason, StringComparison.OrdinalIgnoreCase);

        if (ProductionRunner.State == ProductionState.Paused && Matches(ProductionRunner.StatusText))
            ProductionRunner.Resume();
        else if (BatchCrafter.State == BatchState.Paused && Matches(BatchCrafter.StatusText))
            BatchCrafter.Resume();
        else if (GatheringLoop.State == Gathering.GatheringLoopState.Paused && Matches(GatheringLoop.StatusText))
            GatheringLoop.Resume();
    }

    /// <summary>Emergency stop (spec §48): halts every automation layer at once.</summary>
    public void StopEverything()
    {
        Log.Information("[Plugin] Emergency stop requested.");
        Finisher.Cancel();
        SocialGuard.Core.CancelSettle();
        OrderRunner.Stop();
        Maintenance.Abort();
        ProductionRunner.Stop();
        BatchCrafter.Stop();
        GatheringLoop.Stop();
        GatheringController.Stop();
        Spiritbond.Stop();
        CraftAutomator.Stop();
        Navigation.Stop();
    }

    /// <summary>
    /// Builds the diagnostic report, copies it to the clipboard and saves it
    /// in the plugin config directory; returns the report text.
    /// </summary>
    public string SaveAndCopyReport()
    {
        var report = Diagnostics.DiagnosticReport.Build(this);

        var copied = true;
        try
        {
            Dalamud.Bindings.ImGui.ImGui.SetClipboardText(report);
        }
        catch (Exception e)
        {
            copied = false;
            Log.Warning($"[Plugin] Could not copy the report to the clipboard: {e.Message}");
        }

        string? path = null;
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            System.IO.Directory.CreateDirectory(directory);
            path = System.IO.Path.Combine(directory, $"cielcraft-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
            System.IO.File.WriteAllText(path, report);
        }
        catch (Exception e)
        {
            Log.Warning($"[Plugin] Could not save the report: {e.Message}");
        }

        ChatGui.Print(
            (copied ? "Diagnostic report copied to the clipboard" : "Diagnostic report generated")
            + (path != null ? $" and saved to {path}" : "") + ".",
            "CielCraft");
        return report;
    }

    /// <summary>
    /// Config shape changes (roadmap 7.13): the production queue becomes one
    /// "Queue" order group, and a pre-7.13 single-target saved production
    /// becomes a one-entry target list. Saved only when something moved.
    /// </summary>
    private static void MigrateConfiguration(Configuration configuration)
    {
        var changed = false;

        if (configuration.QueueItems.Count > 0 && configuration.Orders.Groups.Count == 0)
        {
            var group = new OrderGroup { Name = "Queue" };
            foreach (var item in configuration.QueueItems)
                group.Orders.Add(new Order { ItemId = item.ItemId, Amount = Math.Max(1, item.Quantity) });
            configuration.Orders.Groups.Add(group);
            Log.Information($"[Plugin] Moved {configuration.QueueItems.Count} production queue entries into the \"Queue\" order group.");
            configuration.QueueItems.Clear();
            changed = true;
        }

        var saved = configuration.SavedProduction;
        if (saved.Targets.Count == 0 && saved.ItemId != 0)
        {
            saved.Targets.Add(new Configuration.SavedTarget
            {
                ItemId = saved.ItemId,
                Quantity = saved.Quantity,
                InitialCount = saved.InitialCount,
            });
            changed = true;
        }

        // Pre-7.11 single food becomes the food of both consumable sets
        // (roadmap 7.11); the legacy fields are cleared so they stop shadowing.
        if (configuration.FoodItemId != 0
            && configuration.CraftingConsumables.Food.ItemId == 0
            && configuration.GatheringConsumables.Food.ItemId == 0)
        {
            configuration.CraftingConsumables.Food = new Consumable { ItemId = configuration.FoodItemId, Hq = configuration.FoodIsHq };
            configuration.GatheringConsumables.Food = new Consumable { ItemId = configuration.FoodItemId, Hq = configuration.FoodIsHq };
            Log.Information($"[Plugin] Moved the food (item {configuration.FoodItemId}) into the crafting and gathering consumable sets.");
            configuration.FoodItemId = 0;
            configuration.FoodIsHq = true;
            changed = true;
        }

        if (changed)
            configuration.Save();
    }

    /// <summary>
    /// Cached rotations live next to the config (roadmap 7.7), tagged with the
    /// plugin version so a newer solver starts fresh. An unreadable file only
    /// costs a re-solve, so it is logged and skipped.
    /// </summary>
    private static SolutionCache LoadSolutionCache()
    {
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";
        var path = System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "solutions.json");
        var cache = new SolutionCache(version, path);
        try
        {
            var loaded = cache.Load();
            if (loaded > 0)
                Log.Information($"[Raphael] Loaded {loaded} cached rotations.");
        }
        catch (Exception e)
        {
            Log.Warning($"[Raphael] Could not load the solution cache ({e.Message}); starting empty.");
        }

        return cache;
    }

    private void OnLogin()
    {
        Capabilities.Refresh();
        // First run on this install: open on the checklist once (roadmap 7.20); it moves on to Orders when done.
        if (!Configuration.SetupCompleted)
            MainWindow.ShowPage(MainWindow.Pages.ToolsCharacter);
        else if (Configuration.OpenMainWindowOnLogin)
            MainWindow.IsOpen = true;
    }

    private void DrawUi() => WindowSystem.Draw();

    // The settings, debug and setup windows became pages (roadmap 7.21); the
    // toggles stay as page selectors for Dalamud's config button and the commands.
    public void ToggleConfigUi() => MainWindow.TogglePage(MainWindow.Pages.SettingsGeneral);

    public void ToggleMainUi() => MainWindow.Toggle();

    public void ToggleDebugUi() => MainWindow.TogglePage(MainWindow.Pages.StatusDebug);

    public void ToggleSetupUi() => MainWindow.TogglePage(MainWindow.Pages.ToolsCharacter);
}
