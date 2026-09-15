using System;
using System.Collections.Generic;
using System.Linq;
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

    private int groupIndex;          // position in Orders.Groups of the group in flight (or next to try)
    private bool startedCurrent;     // the runner was handed the current group's plan
    private DateTime startedAt = DateTime.MinValue;

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

    private OrderBook Book => configuration.Orders;

    /// <summary>
    /// Plans the first runnable group and starts it. False (with StatusText
    /// saying why) when nothing can run. While held, resumes a paused (or
    /// gently stopped) runner on the current group; otherwise the current
    /// group is planned again from a fresh inventory read.
    /// </summary>
    public bool Start()
    {
        if (State == OrderRunState.Running)
            return true;

        if (State == OrderRunState.Held && startedCurrent)
        {
            switch (runner.State)
            {
                case ProductionState.Paused:
                    runner.Resume();
                    SetRunning($"Resumed group {GroupLabel()}.");
                    return true;

                case ProductionState.Completed:
                    // Held while production was finishing: pick up at the next group.
                    startedCurrent = false;
                    return StartFrom(groupIndex + 1, "Nothing left to run.");

                case ProductionState.Idle when configuration.SavedProduction.Active && runner.TryResumeSaved():
                    // A gentle stop (7.20) leaves the run saved; continue it
                    // rather than re-planning the group from scratch.
                    SetRunning($"Resumed group {GroupLabel()} from its saved progress.");
                    return true;

                case ProductionState.Idle:
                case ProductionState.Failed:
                    break; // re-plan the current group below

                default:
                    // Held while production was still going: advance again when it completes.
                    SetRunning($"Running group {GroupLabel()}.");
                    return true;
            }
        }

        var fresh = State != OrderRunState.Held;
        if (fresh)
        {
            Cycle = 0;
            startedAt = clock.UtcNow;
        }

        startedCurrent = false;
        return StartFrom(fresh ? 0 : groupIndex, "Nothing to run: no enabled group has an order that needs producing.");
    }

    /// <summary>Stop advancing to further groups; the current production keeps going (Hold on the run panel).</summary>
    public void Hold()
    {
        if (State != OrderRunState.Running)
            return;

        State = OrderRunState.Held;
        StatusText = startedCurrent && runner.State is not (ProductionState.Idle or ProductionState.Completed or ProductionState.Failed)
            ? $"Held; group {GroupLabel()} finishes, then nothing more starts."
            : "Held.";
        log.Information($"[Orders] {StatusText}");
    }

    /// <summary>Stop the book and the production runner.</summary>
    public void Stop()
    {
        var wasActive = State is OrderRunState.Running or OrderRunState.Held;
        if (startedCurrent)
            runner.Stop();

        startedCurrent = false;
        CurrentGroup = null;
        CurrentPlan = null;
        State = OrderRunState.Idle;
        StatusText = "Stopped.";
        if (wasActive)
            log.Information("[Orders] Stopped.");
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
        if (State != OrderRunState.Running || !startedCurrent)
            return;

        switch (runner.State)
        {
            case ProductionState.Completed:
                log.Information($"[Orders] Group {GroupLabel()} complete.");
                startedCurrent = false;
                StartFrom(groupIndex + 1, "Nothing left to run.");
                break;

            case ProductionState.Failed:
            case ProductionState.Paused:
                // Hold, but keep tracking the group so Start resumes it and
                // its completion still advances the book.
                State = OrderRunState.Held;
                StatusText = $"Held: {runner.StatusText}";
                log.Information($"[Orders] {StatusText}");
                break;

            case ProductionState.Idle:
                // Stopped underneath the book (user Stop, gentle stop); the
                // group stays current so Start can pick it up again.
                State = OrderRunState.Held;
                StatusText = "Held: production was stopped.";
                log.Information($"[Orders] {StatusText}");
                break;
        }
    }

    /// <summary>
    /// Starts the first group at or after <paramref name="from"/> that plans to
    /// something; empty and disabled groups are skipped with a log line. Past
    /// the last group the book completes, or restarts in perpetual mode.
    /// </summary>
    private bool StartFrom(int from, string nothingToRunText)
    {
        var groups = Book.Groups;
        for (var i = from; i < groups.Count; i++)
        {
            var group = groups[i];
            if (!group.Enabled)
            {
                log.Information($"[Orders] Skipping group \"{group.Name}\" ({i + 1}/{groups.Count}): disabled.");
                continue;
            }

            if (!group.Orders.Any(o => o.Enabled))
            {
                log.Information($"[Orders] Skipping group \"{group.Name}\" ({i + 1}/{groups.Count}): no enabled orders.");
                continue;
            }

            // Fresh planning per group: the inventory read happens now, so
            // restock orders see what earlier groups produced.
            var groupPlan = Preview(group);
            if (groupPlan.IsEmpty)
            {
                log.Information(
                    $"[Orders] Skipping group \"{group.Name}\" ({i + 1}/{groups.Count}): nothing to produce " +
                    $"({string.Join("; ", groupPlan.Orders.Select(DescribeOutcome))}).");
                continue;
            }

            groupIndex = i;
            CurrentGroup = group;
            CurrentPlan = groupPlan;
            if (!runner.Start(groupPlan.Plan!))
            {
                State = OrderRunState.Held;
                StatusText = $"Held: {runner.StatusText}";
                log.Information($"[Orders] Could not start group \"{group.Name}\": {runner.StatusText}");
                return false;
            }

            startedCurrent = true;
            var planned = groupPlan.Orders.Count(o => o.Planned);
            SetRunning(
                $"Group {GroupLabel()}: {planned} order(s), {groupPlan.Plan!.CraftSteps.Count} craft step(s)" +
                (groupPlan.Plan.RawMaterials.Count > 0 ? $", {groupPlan.Plan.RawMaterials.Count} material(s) to gather" : "") +
                (Book.Perpetual ? $"; cycle {Cycle + 1}" : "") + ".");
            if (configuration.ChatNotifications)
                notifier.Notify(NotificationKind.Info, $"Orders: starting group \"{group.Name}\" ({planned} order(s)).");
            return true;
        }

        // Past the last group.
        CurrentGroup = null;
        CurrentPlan = null;
        if (Book.Perpetual && from > 0)
        {
            Cycle++;
            log.Information($"[Orders] Book complete; perpetual mode restarts from the first group (cycle {Cycle + 1}).");
            if (configuration.ChatNotifications)
                notifier.Notify(NotificationKind.Info, $"Orders: cycle {Cycle} complete; starting over.");
            // Everything stocked on the restart means there is nothing to do
            // until the bag changes; finishing here keeps the book idle-safe
            // instead of re-planning every frame.
            return StartFrom(0, "Every order is stocked; nothing to do this cycle.");
        }

        if (from == 0 && State != OrderRunState.Running)
        {
            // A fresh Start found nothing: stay idle rather than claim completion.
            State = OrderRunState.Idle;
            StatusText = nothingToRunText;
            log.Information($"[Orders] {StatusText}");
            return false;
        }

        var elapsed = clock.UtcNow - startedAt;
        State = OrderRunState.Completed;
        StatusText = Book.Perpetual && from == 0
            ? nothingToRunText
            : $"All orders complete in {(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" + (Cycle > 0 ? $" ({Cycle} cycle(s))" : "") + ".";
        log.Information($"[Orders] {StatusText}");
        if (configuration.ChatNotifications)
            notifier.Notify(NotificationKind.Completed, $"Orders: {StatusText}");
        return false;
    }

    private void SetRunning(string statusText)
    {
        State = OrderRunState.Running;
        StatusText = statusText;
        log.Information($"[Orders] {statusText}");
    }

    private string GroupLabel() =>
        CurrentGroup == null ? "?" : $"\"{CurrentGroup.Name}\" ({groupIndex + 1}/{Book.Groups.Count})";

    private string DescribeOutcome(OrderOutcome outcome)
    {
        var name = recipeProvider.GetItemName(outcome.Order.ItemId);
        return outcome.Planned
            ? $"{name} ×{outcome.PlannedQuantity} planned"
            : $"{name}: {outcome.SkipReason}";
    }

    /// <summary>Internal state for the diagnostic report: the book with every order's outcome.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"Orders {State} — {StatusText}";
        yield return $"groupIndex {groupIndex}; startedCurrent {startedCurrent}; cycle {Cycle}; perpetual {Book.Perpetual}; started {(startedAt == DateTime.MinValue ? "-" : startedAt.ToString("HH:mm:ss") + "Z")}";

        var groups = Book.Groups;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var current = ReferenceEquals(group, CurrentGroup);
            yield return $"  group {i + 1}/{groups.Count} \"{group.Name}\"{(group.Enabled ? "" : " (disabled)")}{(current ? " (current)" : "")}: {group.Orders.Count} order(s)";
            foreach (var order in group.Orders)
            {
                // The in-flight group reports what was planned; the others what would be.
                var outcome = current && CurrentPlan != null
                    ? CurrentPlan.Orders.FirstOrDefault(o => o.Order.Id == order.Id)
                    : null;
                var (quantity, skip) = outcome != null
                    ? (outcome.PlannedQuantity, outcome.SkipReason)
                    : OrderPlanner.Evaluate(order, recipeProvider, gameBridge.GetItemCount);
                yield return $"    {recipeProvider.GetItemName(order.ItemId)} (item {order.ItemId}) ×{order.Amount} {order.AmountMode}/{order.Mode}" +
                             (order.MaterialsOnly ? " materials-only" : "") +
                             (skip != null ? $" — skipped: {skip}" : $" — planned ×{quantity}");
            }
        }
    }
}
