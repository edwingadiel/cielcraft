using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Crafting;

public enum OrderRunState
{
    Idle,
    Running,

    /// <summary>Stopped advancing between groups; the runner may still be finishing or paused on the current one.</summary>
    Held,
    Completed,
}

/// <summary>
/// Runs the order book (roadmap 7.13): plans one enabled group at a time with
/// <see cref="OrderPlanner"/>, hands the plan to the production runner, moves
/// to the next group when it completes, holds on failure or pause, and in
/// perpetual mode starts over from the first group. Replaces ProductionQueue.
/// </summary>
public sealed class OrderRunner
{
    private readonly ProductionRunner runner;
    private readonly DalamudRecipeProvider recipeProvider;
    private readonly IGameBridge gameBridge;
    private readonly Configuration configuration;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly IUserNotifier notifier;
    private readonly ILog log;
    private readonly IClock clock;

    public OrderRunner(
        ProductionRunner runner,
        DalamudRecipeProvider recipeProvider,
        IGameBridge gameBridge,
        Configuration configuration,
        Func<CharacterCapabilities> capabilities,
        IUserNotifier notifier,
        ILog log,
        IClock clock)
    {
        this.runner = runner;
        this.recipeProvider = recipeProvider;
        this.gameBridge = gameBridge;
        this.configuration = configuration;
        this.capabilities = capabilities;
        this.notifier = notifier;
        this.log = log;
        this.clock = clock;
    }

    public OrderRunState State { get; private set; } = OrderRunState.Idle;

    public string StatusText { get; private set; } = "";

    public bool Running => State == OrderRunState.Running;

    /// <summary>The group whose plan the production runner is executing; null when none is in flight.</summary>
    public OrderGroup? CurrentGroup { get; private set; }

    /// <summary>The plan of the group in flight, with per-order outcomes for the UI.</summary>
    public GroupPlan? CurrentPlan { get; private set; }

    /// <summary>How many times the book has been restarted in perpetual mode.</summary>
    public int Cycle { get; private set; }

    /// <summary>Plans the first runnable group and starts it. False (with StatusText saying why) when nothing can run.</summary>
    public bool Start()
    {
        // Implemented by the runner agent; see docs/design/orders.md.
        throw new NotImplementedException();
    }

    /// <summary>Stop advancing to further groups; the current production keeps going (Hold on the run panel).</summary>
    public void Hold()
    {
        throw new NotImplementedException();
    }

    /// <summary>Stop the book and the production runner.</summary>
    public void Stop()
    {
        throw new NotImplementedException();
    }

    /// <summary>Plan a group without starting it (Preview).</summary>
    public GroupPlan Preview(OrderGroup group) =>
        OrderPlanner.PlanGroup(group, recipeProvider, gameBridge.GetItemCount, capabilities());

    /// <summary>Drive one frame. Exceptions are logged (rate-limited) and swallowed so one bad frame never kills the run.</summary>
    public void Tick()
    {
        try
        {
            OnTick();
        }
        catch (Exception e)
        {
            log.TickError(nameof(OrderRunner), e);
        }
    }

    private void OnTick()
    {
        // Implemented by the runner agent; see docs/design/orders.md.
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Orders {State} — {StatusText}";
    }
}
