using System;
using CielCraft.Core;
using CielCraft.Game;
using System.Collections.Generic;

namespace CielCraft.Crafting;

/// <summary>
/// A persisted queue of production targets (roadmap 6.8): while running, each
/// entry is planned and handed to the ProductionRunner as the previous one
/// completes. A failure or pause holds the queue for the user.
/// </summary>
public sealed class ProductionQueue
{
    private readonly IGameBridge gameBridge;
    private readonly ProductionRunner runner;
    private readonly DalamudRecipeProvider recipeProvider;
    private readonly Configuration configuration;
    private readonly IUserNotifier notifier;
    private readonly ILog log;

    private bool startedCurrent;

    public bool Running { get; private set; }
    public string StatusText { get; private set; } = "";

    public ProductionQueue(
        IGameBridge gameBridge,
        ProductionRunner runner,
        DalamudRecipeProvider recipeProvider,
        Configuration configuration,
        IUserNotifier notifier,
        ILog log)
    {
        this.gameBridge = gameBridge;
        this.runner = runner;
        this.recipeProvider = recipeProvider;
        this.configuration = configuration;
        this.notifier = notifier;
        this.log = log;
    }

    public int Count => configuration.QueueItems.Count;

    public void Add(uint itemId, int quantity)
    {
        configuration.QueueItems.Add(new Configuration.QueuedTarget { ItemId = itemId, Quantity = quantity });
        configuration.Save();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= configuration.QueueItems.Count)
            return;

        configuration.QueueItems.RemoveAt(index);
        configuration.Save();
    }

    /// <summary>Row 0 while an entry is being produced — not removable.</summary>
    public bool IsInFlight(int index) => startedCurrent && index == 0;

    public void StartQueue()
    {
        if (configuration.QueueItems.Count == 0)
            return;

        // An entry already in flight stays tracked; if the runner is merely
        // paused on it, running the queue means resuming production.
        if (startedCurrent && runner.State == ProductionState.Paused)
            runner.Resume();

        Running = true;
        StatusText = $"Queue running ({Count} target(s)).";
        log.Information($"[Production] {StatusText}");
    }

    public void StopQueue()
    {
        Running = false;
        StatusText = "Queue stopped.";
    }

    private void TrimInFlightEntry()
    {
        if (configuration.QueueItems.Count == 0)
            return;

        var entry = configuration.QueueItems[0];
        var saved = configuration.SavedProduction;
        if (saved.ItemId != entry.ItemId)
            return;

        var produced = Math.Max(0, gameBridge.GetItemCount(entry.ItemId) - saved.InitialCount);
        if (produced >= entry.Quantity)
            configuration.QueueItems.RemoveAt(0);
        else
            entry.Quantity -= produced;

        configuration.Save();
    }

    /// <summary>Drive one frame. Exceptions are logged (rate-limited) and swallowed so one bad frame never kills the run.</summary>
    public void Tick()
    {
        try
        {
            Advance();
        }
        catch (Exception e)
        {
            log.TickError(nameof(ProductionQueue), e);
        }
    }

    private void Advance()
    {
        if (!Running)
            return;

        switch (runner.State)
        {
            case ProductionState.Completed when startedCurrent:
                // Current entry finished — advance to the next.
                configuration.QueueItems.RemoveAt(0);
                configuration.Save();
                startedCurrent = false;
                break;

            case ProductionState.Idle when startedCurrent:
                // The runner was stopped underneath the queue: keep the entry
                // for a later run, trimmed by whatever was already produced.
                TrimInFlightEntry();
                Running = false;
                startedCurrent = false;
                StatusText = "Queue held: production was stopped.";
                return;

            case ProductionState.Failed when startedCurrent:
            case ProductionState.Paused when startedCurrent:
                // Hold, but keep tracking the entry so its completion still
                // advances the queue after the user resumes.
                Running = false;
                StatusText = $"Queue held: {runner.StatusText}";
                return;
        }

        if (startedCurrent || runner.State is not (ProductionState.Idle or ProductionState.Completed))
            return;

        if (configuration.QueueItems.Count == 0)
        {
            Running = false;
            StatusText = "Queue complete.";
            if (configuration.ChatNotifications)
                notifier.Print("Production queue complete.");
            return;
        }

        var next = configuration.QueueItems[0];
        var plan = DependencyResolver.Resolve(next.ItemId, next.Quantity, recipeProvider, gameBridge.GetItemCount);
        if (runner.Start(plan))
        {
            startedCurrent = true;
            StatusText = $"Queue: producing {recipeProvider.GetItemName(next.ItemId)} ×{next.Quantity} ({Count} target(s) left).";
        }
        else
        {
            Running = false;
            StatusText = $"Queue held: {runner.StatusText}";
        }
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"Running {Running}; startedCurrent {startedCurrent}; status: {StatusText}";
        for (var i = 0; i < configuration.QueueItems.Count; i++)
        {
            var entry = configuration.QueueItems[i];
            yield return $"  {i}: {recipeProvider.GetItemName(entry.ItemId)} (item {entry.ItemId}) ×{entry.Quantity}{(IsInFlight(i) ? " (in flight)" : "")}";
        }
    }
}
