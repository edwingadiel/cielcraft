using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;

namespace CielCraft.Fishing;

/// <summary>
/// <see cref="IAutoHookControl"/> over AutoHook's Dalamud IPC (roadmap 7.4),
/// in the shape of <c>VNavmeshProvider</c>: every call is guarded, and when
/// AutoHook is missing or its IPC has changed the control simply reports
/// unavailable and CielCraft hooks for itself (spec §31's rule for optional
/// plugins).
/// <para>
/// <b>Unverified.</b> AutoHook is not installed on this machine
/// (<c>%APPDATA%\XIVLauncher\installedPlugins</c> holds only vnavmesh), so the
/// endpoint names below are taken from AutoHook's published IPC
/// (<c>AutoHook.SetPluginState</c> / <c>AutoHook.SetAutoGigState</c>) and have
/// not been called against a running copy. Availability is probed at run time,
/// never cached from load, because the user can enable or disable AutoHook
/// while CielCraft is loaded.
/// </para>
/// </summary>
public sealed class AutoHookIpc : IAutoHookControl
{
    private const string InternalName = "AutoHook";

    private readonly ICallGateSubscriber<bool, object> setPluginState;
    private string lastError = "";

    public AutoHookIpc()
    {
        setPluginState = Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoHook.SetPluginState");
    }

    /// <summary>AutoHook is installed and loaded; the IPC itself is only probed when a call is made.</summary>
    public bool IsAvailable =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == InternalName && p.IsLoaded);

    public bool SetEnabled(bool enabled)
    {
        if (!IsAvailable)
            return false;

        try
        {
            setPluginState.InvokeAction(enabled);
            lastError = "";
            return true;
        }
        catch (Exception e)
        {
            // Missing endpoint, wrong signature, or AutoHook unloaded mid-call.
            lastError = e.Message;
            return false;
        }
    }

    public IEnumerable<string> Describe()
    {
        yield return $"AutoHook: installed {IsAvailable}; IPC AutoHook.SetPluginState" +
                     (lastError.Length > 0 ? $"; last error {lastError}" : "");
    }
}
