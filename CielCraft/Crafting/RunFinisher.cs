using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Crafting;

/// <summary>
/// "Exit the game when done" (roadmap 7.20): once a production completes and
/// the order book is not running or held with work left, wait a beat so the
/// completion notification lands, send /shutdown and confirm the game's
/// yes/no prompt. Gentle stops, failures and pauses never exit — those are the
/// cases the user wants to look at.
/// </summary>
public sealed class RunFinisher
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(20);

    private readonly ProductionRunner runner;
    private readonly OrderRunner orders;
    private readonly Configuration configuration;
    private readonly IGameBridge gameBridge;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly Throttle retry;

    private ProductionState lastState;
    private DateTime exitAt = DateTime.MinValue;
    private DateTime confirmingSince = DateTime.MinValue;

    public RunFinisher(
        ProductionRunner runner,
        OrderRunner orders,
        Configuration configuration,
        IGameBridge gameBridge,
        ILog log,
        IClock clock,
        Sourcing.InventoryKeeper? keeper = null)
    {
        this.keeper = keeper;
        this.runner = runner;
        this.orders = orders;
        this.configuration = configuration;
        this.gameBridge = gameBridge;
        this.log = log;
        this.clock = clock;
        retry = new Throttle(clock, TimeSpan.FromSeconds(2));
        lastState = runner.State;
    }

    public string StatusText { get; private set; } = "Idle.";

    /// <summary>Cancels a pending exit (the user pressed Stop everything, or started something new).</summary>
    public void Cancel()
    {
        if (exitAt == DateTime.MinValue && confirmingSince == DateTime.MinValue)
            return;

        exitAt = DateTime.MinValue;
        confirmingSince = DateTime.MinValue;
        StatusText = "Exit cancelled.";
        log.Information("[Finish] Exit cancelled.");
    }

    public void Tick()
    {
        try
        {
            OnTick();
        }
        catch (Exception e)
        {
            log.TickError(nameof(RunFinisher), e);
        }
    }

    private readonly Sourcing.InventoryKeeper? keeper;

    private void OnTick()
    {
        var state = runner.State;
        var changed = state != lastState;
        lastState = state;

        // Storage rules / desynth / trash after every completed run (7.17),
        // whether or not the book has more work; a busy cleanup delays the exit.
        if (changed && state == ProductionState.Completed)
            keeper?.RunAfter(null);
        if (keeper is { IsBusy: true })
            return;

        if (confirmingSince != DateTime.MinValue)
        {
            if (gameBridge.IsAddonVisible("SelectYesno"))
                retry.Try(() => gameBridge.FireAddonCallbackInt("SelectYesno", 0));

            if (clock.UtcNow - confirmingSince > ConfirmTimeout)
            {
                confirmingSince = DateTime.MinValue;
                StatusText = "The game did not exit; giving up.";
                log.Warning("[Finish] /shutdown was sent but the game is still running; giving up.");
            }

            return;
        }

        if (exitAt != DateTime.MinValue)
        {
            // Anything starting up again in the grace window cancels the exit.
            if (state is not (ProductionState.Completed or ProductionState.Idle) || orders.Running)
            {
                Cancel();
                return;
            }

            if (clock.UtcNow < exitAt)
                return;

            exitAt = DateTime.MinValue;
            confirmingSince = clock.UtcNow;
            retry.Reset();
            StatusText = "Exiting the game.";
            log.Information("[Finish] Production and orders complete; exiting the game.");
            gameBridge.ExecuteChatCommand("/shutdown");
            return;
        }

        if (!changed || state != ProductionState.Completed || !configuration.ExitGameWhenDone)
            return;

        // The book advances to its next group on this same completion; a held
        // book still has work the user wants to come back to (7.13).
        if (orders.State is OrderRunState.Running or OrderRunState.Held)
            return;

        exitAt = clock.UtcNow + Grace;
        StatusText = $"Exiting the game in {Grace.TotalSeconds:F0}s.";
        log.Information($"[Finish] Exiting the game in {Grace.TotalSeconds:F0}s (exit when done is on).");
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Finisher — {StatusText}";
        yield return $"exitWhenDone {configuration.ExitGameWhenDone}; exitAt {(exitAt == DateTime.MinValue ? "-" : exitAt.ToString("HH:mm:ss") + "Z")}; confirmingSince {(confirmingSince == DateTime.MinValue ? "-" : confirmingSince.ToString("HH:mm:ss") + "Z")}";
    }
}
