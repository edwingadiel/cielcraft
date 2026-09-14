using System;
using System.Collections.Generic;
using CielCraft.Core;
using Dalamud.Plugin.Services;

namespace CielCraft.Game;

/// <summary>
/// Polls the craft state once per framework tick and records observable
/// transitions (spec §11 groundwork). The transition log is the debugging
/// backbone for validating Milestone 1 and drives the future state machine.
/// </summary>
public sealed class CraftStateMonitor : IDisposable
{
    private const int MaxEvents = 100;

    private readonly IGameBridge gameBridge;
    private readonly List<string> events = new();

    private CraftSnapshot? previous;
    private bool wasCrafting;

    public CraftSnapshot? Current { get; private set; }

    public IReadOnlyList<string> RecentEvents => events;

    public CraftStateMonitor(IGameBridge gameBridge)
    {
        this.gameBridge = gameBridge;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            Tick(framework);
        }
        catch (Exception e)
        {
            Plugin.Log.TickError(nameof(CraftStateMonitor), e);
        }
    }

    private void Tick(IFramework framework)
    {
        var isCrafting = gameBridge.IsCrafting;

        if (isCrafting && !wasCrafting)
            Record("Craft started.");
        else if (!isCrafting && wasCrafting)
            Record("Craft ended.");

        wasCrafting = isCrafting;

        Current = gameBridge.GetCraftState();

        if (Current != null && previous != null && Current != previous)
        {
            if (Current.Step != previous.Step)
                Record($"Step {previous.Step} -> {Current.Step}: " +
                       $"progress {Current.Progress}/{Current.MaxProgress}, " +
                       $"quality {Current.Quality}/{Current.MaxQuality}, " +
                       $"durability {Current.Durability}/{Current.MaxDurability}, " +
                       $"CP {Current.CurrentCp}/{Current.MaxCp}, " +
                       $"condition {Current.Condition}");
            else if (Current.Condition != previous.Condition)
                Record($"Condition {previous.Condition} -> {Current.Condition}");
        }

        previous = Current;
    }

    private void Record(string message)
    {
        Plugin.Log.Information($"[Craft] {message}");

        events.Add($"{DateTime.Now:HH:mm:ss} {message}");
        if (events.Count > MaxEvents)
            events.RemoveAt(0);
    }
}
