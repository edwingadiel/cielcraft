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
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private const string CommandName = "/cielcraft";

    public Configuration Configuration { get; init; }
    public IGameBridge GameBridge { get; init; }
    public DalamudRecipeProvider RecipeProvider { get; init; }
    public GatheringDatabase GatheringDatabase { get; init; } = new();
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
    public ProductionQueue ProductionQueue { get; init; }

    public readonly WindowSystem WindowSystem = new("CielCraft");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private DebugWindow DebugWindow { get; init; }

    /// <summary>Crafting stays usable without vnavmesh; only gathering automation needs it (spec §31).</summary>
    internal static bool IsVNavmeshAvailable =>
        PluginInterface.InstalledPlugins.Any(p => p.InternalName == "vnavmesh" && p.IsLoaded);

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GameBridge = new DalamudGameBridge();
        RecipeProvider = new DalamudRecipeProvider(
            () => GameBridge.CurrentClassJobId, jobId => GameBridge.HasGearsetForJob(jobId));
        CraftMonitor = new CraftStateMonitor(GameBridge);
        ActionExecutor = new ActionExecutor(GameBridge, CraftMonitor);
        SolverService = new SolverService(new CielCraft.Raphael.RaphaelSolver());
        CraftAutomator = new CraftAutomator(GameBridge, CraftMonitor, ActionExecutor, Configuration);
        Maintenance = new MaintenanceService(GameBridge, Configuration);
        BatchCrafter = new BatchCrafter(
            GameBridge, CraftMonitor, CraftAutomator, SolverService, RecipeProvider, Configuration, Maintenance);
        Navigation = new Navigation.VNavmeshProvider();
        GatheringController = new Gathering.GatheringController(GameBridge, Navigation, Configuration);
        GatheringLoop = new Gathering.GatheringLoop(
            GameBridge, GatheringController, Navigation, Configuration, Maintenance);
        ProductionRunner = new ProductionRunner(
            GameBridge, BatchCrafter, RecipeProvider, GatheringLoop, GatheringDatabase, Navigation, Configuration);
        ProductionQueue = new ProductionQueue(GameBridge, ProductionRunner, RecipeProvider, Configuration);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        DebugWindow = new DebugWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DebugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the CielCraft window. \"/cielcraft config\" settings, \"/cielcraft debug\" debug window, \"/cielcraft stop\" emergency stop.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        ClientState.Login += OnLogin;

        Log.Information("[Plugin] CielCraft loaded.");
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        WindowSystem.RemoveAllWindows();

        ProductionQueue.Dispose();
        GatheringLoop.Dispose();
        GatheringController.Dispose();
        ProductionRunner.Dispose();
        BatchCrafter.Dispose();
        CraftAutomator.Dispose();
        ActionExecutor.Dispose();
        CraftMonitor.Dispose();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        DebugWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
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
            case "stop":
                StopEverything();
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    /// <summary>Emergency stop (spec §48): halts every automation layer at once.</summary>
    public void StopEverything()
    {
        Log.Information("[Plugin] Emergency stop requested.");
        ProductionQueue.StopQueue();
        Maintenance.Abort();
        ProductionRunner.Stop();
        BatchCrafter.Stop();
        GatheringLoop.Stop();
        GatheringController.Stop();
        CraftAutomator.Stop();
        Navigation.Stop();
    }

    private void OnLogin()
    {
        if (Configuration.OpenMainWindowOnLogin)
            MainWindow.IsOpen = true;
    }

    private void DrawUi() => WindowSystem.Draw();

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public void ToggleMainUi() => MainWindow.Toggle();

    public void ToggleDebugUi() => DebugWindow.Toggle();
}
