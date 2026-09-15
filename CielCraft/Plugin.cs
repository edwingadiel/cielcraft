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
    public ProductionQueue ProductionQueue { get; init; }
    public Social.SocialGuard SocialGuard { get; init; }

    public readonly WindowSystem WindowSystem = new("CielCraft");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private DebugWindow DebugWindow { get; init; }

    /// <summary>Crafting stays usable without vnavmesh; only gathering automation needs it (spec §31).</summary>
    internal static bool IsVNavmeshAvailable =>
        PluginInterface.InstalledPlugins.Any(p => p.InternalName == "vnavmesh" && p.IsLoaded);

    public Plugin()
    {
        Log = new Diagnostics.DiagnosticLog(PluginLog);
        // Dalamud loads plugin assemblies from memory, so the native solver can't find itself
        // via Assembly.Location; point it at the on-disk plugin folder instead.
        CielCraft.Raphael.RaphaelSolver.LibraryDirectory = PluginInterface.AssemblyLocation.DirectoryName;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GameBridge = new DalamudGameBridge();
        RecipeProvider = new DalamudRecipeProvider(
            () => GameBridge.CurrentClassJobId, jobId => GameBridge.HasGearsetForJob(jobId), () => Capabilities.Current);
        GatheringDatabase = new GatheringDatabase(() => Capabilities.Current);
        CraftMonitor = new CraftStateMonitor(GameBridge);
        ActionExecutor = new ActionExecutor(GameBridge, CraftMonitor);
        SolverService = new SolverService(new CielCraft.Raphael.RaphaelSolver(), LoadSolutionCache());
        CraftAutomator = new CraftAutomator(GameBridge, CraftMonitor, ActionExecutor, Configuration);
        Maintenance = new MaintenanceService(GameBridge, Configuration);
        BatchCrafter = new BatchCrafter(
            GameBridge, CraftMonitor, CraftAutomator, SolverService, RecipeProvider, Configuration, Maintenance);
        Navigation = new Navigation.VNavmeshProvider();
        GatheringController = new Gathering.GatheringController(
            GameBridge, Navigation, Configuration, () => Capabilities.Current);
        GatheringLoop = new Gathering.GatheringLoop(
            GameBridge, GatheringController, Navigation, Configuration, Maintenance);
        ProductionRunner = new ProductionRunner(
            GameBridge, BatchCrafter, RecipeProvider, GatheringLoop, GatheringDatabase, Navigation, Configuration,
            Capabilities);
        ProductionQueue = new ProductionQueue(
            GameBridge, ProductionRunner, RecipeProvider, Configuration, () => Capabilities.Current);
        SocialGuard = new Social.SocialGuard(this, GameBridge, Configuration);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        DebugWindow = new DebugWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DebugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the CielCraft window. \"/cielcraft config\" settings, \"/cielcraft debug\" debug window, \"/cielcraft report\" copy a diagnostic report, \"/cielcraft pause\" / \"/cielcraft resume\", \"/cielcraft stop\" emergency stop.",
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
            case "pause":
                PauseTopLayer();
                break;
            case "resume":
                ResumeTopLayer();
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
        SocialGuard.Core.CancelSettle();
        ProductionQueue.StopQueue();
        Maintenance.Abort();
        ProductionRunner.Stop();
        BatchCrafter.Stop();
        GatheringLoop.Stop();
        GatheringController.Stop();
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
        if (Configuration.OpenMainWindowOnLogin)
            MainWindow.IsOpen = true;
    }

    private void DrawUi() => WindowSystem.Draw();

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public void ToggleMainUi() => MainWindow.Toggle();

    public void ToggleDebugUi() => DebugWindow.Toggle();
}
