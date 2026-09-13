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
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/cielcraft";

    public Configuration Configuration { get; init; }
    public IGameBridge GameBridge { get; init; }
    public DalamudRecipeProvider RecipeProvider { get; init; } = new();
    public CraftStateMonitor CraftMonitor { get; init; }
    public ActionExecutor ActionExecutor { get; init; }
    public SolverService SolverService { get; init; }
    public CraftAutomator CraftAutomator { get; init; }
    public BatchCrafter BatchCrafter { get; init; }
    public ProductionRunner ProductionRunner { get; init; }
    public INavigationProvider Navigation { get; init; }

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
        CraftMonitor = new CraftStateMonitor(GameBridge);
        ActionExecutor = new ActionExecutor(GameBridge, CraftMonitor);
        SolverService = new SolverService(new CielCraft.Raphael.RaphaelSolver());
        CraftAutomator = new CraftAutomator(GameBridge, CraftMonitor, ActionExecutor, Configuration);
        BatchCrafter = new BatchCrafter(GameBridge, CraftMonitor, CraftAutomator, SolverService);
        ProductionRunner = new ProductionRunner(GameBridge, BatchCrafter, RecipeProvider);
        Navigation = new Navigation.VNavmeshProvider();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        DebugWindow = new DebugWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DebugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the CielCraft window. \"/cielcraft config\" for settings, \"/cielcraft debug\" for the debug window.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("[Plugin] CielCraft loaded.");
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();

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
            default:
                ToggleMainUi();
                break;
        }
    }

    private void DrawUi() => WindowSystem.Draw();

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public void ToggleMainUi() => MainWindow.Toggle();

    public void ToggleDebugUi() => DebugWindow.Toggle();
}
