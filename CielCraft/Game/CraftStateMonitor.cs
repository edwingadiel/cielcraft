using System;
using System.Collections.Generic;
using CielCraft.Core;

namespace CielCraft.Game;

/// <summary>
/// Polls the craft state once per framework tick and records observable
/// transitions (spec §11 groundwork). The transition log is the debugging
/// backbone for validating Milestone 1 and drives the future state machine.
/// </summary>
public sealed class CraftStateMonitor
{
    private const int MaxEvents = 100;

    private readonly IGameBridge gameBridge;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly List<string> events = new();

    private CraftSnapshot? previous;
    private bool wasCrafting;

    public CraftSnapshot? Current { get; private set; }

    public IReadOnlyList<string> RecentEvents => events;

    public CraftStateMonitor(IGameBridge gameBridge, ILog log, IClock clock)
    {
        this.gameBridge = gameBridge;
        this.log = log;
        this.clock = clock;
    }

    /// <summary>Drive one frame. Exceptions are logged (rate-limited) and swallowed so one bad frame never kills the run.</summary>
    public void Tick()
    {
        try
        {
            Observe();
        }
        catch (Exception e)
        {
            log.TickError(nameof(CraftStateMonitor), e);
        }
    }

    private void Observe()
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
        log.Information($"[Craft] {message}");

        events.Add($"{clock.UtcNow.ToLocalTime():HH:mm:ss} {message}");
        if (events.Count > MaxEvents)
            events.RemoveAt(0);
    }
}
