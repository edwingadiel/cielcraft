using System;
using System.Linq;
using CielCraft.Core;
using Dalamud.Plugin.Ipc;

namespace CielCraft.Combat;

/// <summary>
/// The Dalamud call gates of the two combat plugins (roadmap 7.5), bound the
/// way <see cref="CielCraft.Navigation.VNavmeshProvider"/> binds vnavmesh:
/// every subscriber is created once, every invocation is wrapped, and a
/// missing or unloaded plugin simply makes the call fail instead of throwing
/// into a tick. Nothing here decides anything — <see cref="CombatDrivers"/>'s
/// drivers do.
/// </summary>
internal static class CombatPluginIpc
{
    /// <summary>Rotation Solver Reborn's Dalamud internal name (its manifest.json).</summary>
    public const string RotationSolverInternalName = "RotationSolver";

    /// <summary>
    /// BossMod Reborn's internal name, and the original BossMod's: both
    /// register the same <c>BossMod.</c> gates, so either will drive.
    /// </summary>
    public static readonly string[] BossModInternalNames = ["BossModReborn", "BossMod"];

    public static bool IsPluginLoaded(params string[] internalNames) =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && internalNames.Contains(p.InternalName));

    /// <summary>Rotation Solver Reborn's gates; see its RotationSolver/IPC/IPCProvider.cs (EzIPC prefix "RotationSolverReborn").</summary>
    public sealed class RotationSolver : IRotationSolverIpc
    {
        private readonly ICallGateSubscriber<bool> autorotationActive;
        private readonly ICallGateSubscriber<int, object> changeOperatingMode;
        private readonly ICallGateSubscriber<uint, object> addPriority;
        private readonly ICallGateSubscriber<uint, object> removePriority;
        private readonly ICallGateSubscriber<string, object> test;

        public RotationSolver()
        {
            var ipc = Plugin.PluginInterface;
            autorotationActive = ipc.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
            // The gate declares StateCommandType (a byte enum in the plugin's
            // own assembly, which cannot be referenced from here). Dalamud
            // converts mismatched IPC arguments through JSON
            // (CallGateChannel.CheckAndConvertArgs / ConvertObject), so the
            // plain number arrives as the right enum member.
            changeOperatingMode = ipc.GetIpcSubscriber<int, object>("RotationSolverReborn.ChangeOperatingMode");
            addPriority = ipc.GetIpcSubscriber<uint, object>("RotationSolverReborn.AddPriorityNameID");
            removePriority = ipc.GetIpcSubscriber<uint, object>("RotationSolverReborn.RemovePriorityNameID");
            test = ipc.GetIpcSubscriber<string, object>("RotationSolverReborn.Test");
        }

        public bool IsLoaded => IsPluginLoaded(RotationSolverInternalName);

        public bool? AutorotationActive()
        {
            try
            {
                return autorotationActive.InvokeFunc();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool ChangeOperatingMode(int state) => Try(() => changeOperatingMode.InvokeAction(state));

        public bool SetPriorityNameId(uint bnpcNameId, bool add) =>
            Try(() => (add ? addPriority : removePriority).InvokeAction(bnpcNameId));

        /// <summary>The plugin's own connectivity probe; it only writes a line to RSR's log.</summary>
        public bool Ping(string message) => Try(() => test.InvokeAction(message));
    }

    /// <summary>BossMod Reborn's gates; see its BossMod/Framework/IPCProvider.cs (prefix "BossMod.").</summary>
    public sealed class BossMod : IBossModIpc
    {
        private readonly ICallGateSubscriber<string?> getActive;
        private readonly ICallGateSubscriber<string, bool> setActive;
        private readonly ICallGateSubscriber<bool> clearActive;
        private readonly ICallGateSubscriber<bool> hasQueuedActions;

        public BossMod()
        {
            var ipc = Plugin.PluginInterface;
            getActive = ipc.GetIpcSubscriber<string?>("BossMod.Presets.GetActive");
            setActive = ipc.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
            clearActive = ipc.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
            hasQueuedActions = ipc.GetIpcSubscriber<bool>("BossMod.Rotation.ActionQueue.HasEntries");
        }

        public bool IsLoaded => IsPluginLoaded(BossModInternalNames);

        /// <summary>Empty = no preset is active; null = the gate did not answer, which is how the driver spots an absent plugin.</summary>
        public string? GetActivePreset()
        {
            try
            {
                return getActive.InvokeFunc() ?? "";
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool SetActivePreset(string name)
        {
            try
            {
                return setActive.InvokeFunc(name);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool ClearActivePreset()
        {
            try
            {
                return clearActive.InvokeFunc();
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool? HasQueuedActions()
        {
            try
            {
                return hasQueuedActions.InvokeFunc();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static bool Try(Action call)
    {
        try
        {
            call();
            return true;
        }
        catch (Exception)
        {
            // The plugin is missing, unloaded, or its gate is not registered.
            return false;
        }
    }

    /// <summary>
    /// The selector the coordinator registers: Rotation Solver Reborn first,
    /// then BossMod Reborn, else the no-op driver. Constructing the
    /// subscribers is safe with neither plugin installed.
    /// </summary>
    public static CombatDriverSelector CreateSelector(ILog log, IClock clock, string? bossModPreset = null) =>
        new(log, clock,
            new RotationSolverDriver(new RotationSolver(), log),
            new BossModDriver(new BossMod(), log, bossModPreset));
}
