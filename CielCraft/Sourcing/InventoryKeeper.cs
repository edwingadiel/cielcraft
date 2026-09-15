using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Sourcing;

public enum InventoryKeeperState
{
    Idle,
    Desynthesizing,
    ConfirmingDesynth,
    Discarding,
    ConfirmingDiscard,
    GoingToBell,
    Depositing,
    LeavingBell,
    Paused,
}

/// <summary>
/// The storage policy in action (roadmap 7.17). After a run completes the
/// coordinator calls <see cref="RunAfter"/> from the run finisher and ticks
/// this: surplus a rule marks for the retainer is deposited at the next
/// summoning-bell visit, a rule marked Desynth is desynthesized when
/// <see cref="AutomationSettings.DesynthUnusedByproducts"/> is on, and a rule
/// marked Discard is thrown away when <see cref="AutomationSettings.TrashCleanup"/>
/// is on.
///
/// Only items a <see cref="StorageRule"/> names are ever touched, and never
/// below what the rule keeps in the bag plus what the finished plan still
/// reserves — the decision itself is <see cref="StorageKeeper.Decide"/>, which
/// is pure and tested.
/// </summary>
public sealed class InventoryKeeper : AutomationMachine<InventoryKeeperState>
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1.5);

    private readonly IGameBridge gameBridge;
    private readonly RetainerDatabase retainers;
    private readonly AutomationSettings configuration;
    private readonly Throttle attempts;
    private readonly Func<uint, string> itemName;
    private readonly BellSession bell;

    private readonly Queue<KeeperAction> pending = new();
    private readonly List<string> lastPass = [];

    private KeeperAction? current;
    private int currentBaseline;
    private int progressMark;
    private DateTime phaseStartedAt;
    private DateTime settleUntil;
    private InventoryKeeperState resumeTo = InventoryKeeperState.Idle;
    private bool requested;
    private bool bellStarted;

    public InventoryKeeper(
        IGameBridge gameBridge,
        RetainerDatabase retainers,
        AutomationSettings configuration,
        ILog log,
        IClock clock,
        INpcInteractor? npc = null,
        Func<uint, string>? itemName = null)
        : base(log, clock, "[Inventory]", InventoryKeeperState.Idle, "Idle.")
    {
        this.gameBridge = gameBridge;
        this.retainers = retainers;
        this.configuration = configuration;
        this.itemName = itemName ?? (id => $"item {id}");
        attempts = new Throttle(clock, RetryInterval);
        bell = new BellSession(gameBridge, retainers, npc, log, clock, "[Inventory]");
    }

    /// <summary>A cleanup pass is in flight; the caller should hold its own work.</summary>
    public bool IsBusy => State != InventoryKeeperState.Idle;

    /// <summary>What the last pass did, for the panel and the report.</summary>
    public IReadOnlyList<string> LastPass => lastPass;

    /// <summary>What the rules would do right now, without doing it (the panel's preview).</summary>
    public IReadOnlyList<KeeperAction> Preview(ProductionPlan? plan) =>
        StorageKeeper.Decide(
            configuration.StorageRules, configuration, new BridgeStorageView(gameBridge),
            StorageKeeper.Reserved(plan), itemName);

    /// <summary>
    /// Queues the cleanup the rules call for after a finished production. No
    /// rules, or nothing over the reserve, means nothing happens at all.
    /// Returns how many actions were queued.
    /// </summary>
    public int RunAfter(ProductionPlan? plan)
    {
        if (IsBusy)
            return 0;

        var actions = Preview(plan);
        pending.Clear();
        lastPass.Clear();
        foreach (var action in actions)
            pending.Enqueue(action);

        if (pending.Count == 0)
            return 0;

        Log.Information(
            $"[Inventory] Cleanup after the run: {string.Join(", ", actions.Select(a => $"{a.Action} {itemName(a.ItemId)} ×{a.Amount}"))}.");
        Next();
        return actions.Count;
    }

    /// <summary>The panel's "run cleanup now": the same pass with nothing reserved for a plan.</summary>
    public int RunNow() => RunAfter(null);

    /// <summary>Abandons the pass, closing whatever it left open.</summary>
    public void Abort()
    {
        if (!IsBusy)
            return;

        pending.Clear();
        current = null;
        bellStarted = false;
        bell.Abort();
        gameBridge.ConfirmDesynthesis();
        gameBridge.FireAddonCallbackInt("SelectYesno", 1);
        SetState(InventoryKeeperState.Idle, "Idle.");
    }

    public void Pause(string reason)
    {
        if (State is InventoryKeeperState.Idle or InventoryKeeperState.Paused)
            return;

        resumeTo = State;
        bell.Pause(reason);
        Transition(InventoryKeeperState.Paused, $"Paused: {reason}");
    }

    public void Resume()
    {
        if (State != InventoryKeeperState.Paused)
            return;

        bell.Resume();
        Enter(resumeTo, "Resumed.");
    }

    protected override void OnTick()
    {
        if (State is InventoryKeeperState.Idle or InventoryKeeperState.Paused)
            return;

        if (Clock.UtcNow < settleUntil)
            return;

        switch (State)
        {
            case InventoryKeeperState.Desynthesizing:
                TickDesynth();
                break;

            case InventoryKeeperState.ConfirmingDesynth:
                TickConfirmDesynth();
                break;

            case InventoryKeeperState.Discarding:
                TickDiscard();
                break;

            case InventoryKeeperState.ConfirmingDiscard:
                TickConfirmDiscard();
                break;

            case InventoryKeeperState.GoingToBell:
            case InventoryKeeperState.Depositing:
            case InventoryKeeperState.LeavingBell:
                TickDeposit();
                break;
        }
    }

    // ------------------------------------------------------------- desynth

    private void TickDesynth()
    {
        if (Remaining() <= 0)
        {
            Done($"desynthesized {itemName(current!.ItemId)} ×{current.Amount}");
            return;
        }

        if (gameBridge.IsAddonVisible("SalvageDialog") || gameBridge.IsAddonVisible("SalvageResult"))
        {
            Settle(Pacing.AfterNodeOpen);
            Enter(InventoryKeeperState.ConfirmingDesynth, $"Confirming the desynthesis of {itemName(current!.ItemId)}...");
            return;
        }

        // Non-fatal: a byproduct that cannot be desynthesized (no level, not
        // desynthesizable) is left alone and the pass moves on.
        if (SoftTimedOut($"{itemName(current!.ItemId)} could not be desynthesized"))
            return;

        attempts.Try(() => gameBridge.Desynthesize(current!.ItemId));
    }

    private void TickConfirmDesynth()
    {
        if (Removed() > progressMark)
        {
            // One item per desynthesis: back to Desynthesizing for the rest.
            progressMark = Removed();
            Settle(Pacing.BeforeInteract);
            Enter(InventoryKeeperState.Desynthesizing, $"Desynthesizing {itemName(current!.ItemId)} (×{Remaining()} left)...");
            return;
        }

        if (SoftTimedOut($"the desynthesis of {itemName(current!.ItemId)} did not complete"))
            return;

        attempts.Try(() => gameBridge.ConfirmDesynthesis());
    }

    // ------------------------------------------------------------- discard

    private void TickDiscard()
    {
        if (Remaining() <= 0)
        {
            Done($"discarded {itemName(current!.ItemId)} ×{current.Amount}");
            return;
        }

        if (gameBridge.IsAddonVisible("SelectYesno"))
        {
            Settle(Pacing.AfterNodeOpen);
            Enter(InventoryKeeperState.ConfirmingDiscard, $"Confirming the discard of {itemName(current!.ItemId)}...");
            return;
        }

        if (SoftTimedOut($"{itemName(current!.ItemId)} could not be discarded"))
            return;

        attempts.Try(() => gameBridge.DiscardItem(current!.ItemId));
    }

    private void TickConfirmDiscard()
    {
        if (Removed() > progressMark)
        {
            progressMark = Removed();
            Settle(Pacing.BeforeInteract);
            Enter(InventoryKeeperState.Discarding, $"Discarding {itemName(current!.ItemId)} (×{Remaining()} left)...");
            return;
        }

        if (SoftTimedOut($"the discard of {itemName(current!.ItemId)} was not confirmed"))
            return;

        attempts.Try(() => gameBridge.FireAddonCallbackInt("SelectYesno", 0));
    }

    // ------------------------------------------------------------- deposit

    private void TickDeposit()
    {
        if (!bellStarted)
        {
            var target = retainers.Retainers().FirstOrDefault(r => r.Available);
            if (target == null)
            {
                Skip("no retainer is available for the deposit");
                return;
            }

            bellStarted = bell.Start(target.Index, $"depositing {itemName(current!.ItemId)} ×{current.Amount}");
            if (!bellStarted)
            {
                Skip("a bell visit is already in flight");
                return;
            }
        }

        bell.Tick();
        switch (bell.State)
        {
            case BellSessionState.Failed:
                bellStarted = false;
                Skip(bell.FailureReason);
                return;

            case BellSessionState.Paused:
                Pause(bell.StatusText);
                return;

            case BellSessionState.Done:
                bellStarted = false;
                Done($"deposited {itemName(current!.ItemId)} ×{Removed()}");
                return;
        }

        if (bell.State != BellSessionState.Ready)
        {
            StatusText = bell.StatusText;
            return;
        }

        if (State == InventoryKeeperState.GoingToBell)
        {
            Enter(InventoryKeeperState.Depositing, $"Depositing {itemName(current!.ItemId)} ×{current.Amount}...");
            requested = false;
            return;
        }

        if (State == InventoryKeeperState.LeavingBell)
        {
            StatusText = bell.StatusText;
            return;
        }

        if (!gameBridge.IsRetainerInventoryOpen)
        {
            if (SoftTimedOut("the retainer's bag did not open for the deposit"))
                bell.Finish();
            else
                attempts.Try(() => gameBridge.SelectRetainerMenuOption("item"));

            return;
        }

        if (Remaining() <= 0 || SoftTimedOut($"only part of {itemName(current!.ItemId)} was deposited"))
        {
            Enter(InventoryKeeperState.LeavingBell, "Leaving the bell...");
            bell.Finish();
            return;
        }

        if (!requested)
        {
            attempts.Try(() =>
            {
                gameBridge.DepositToRetainer(current!.ItemId, Remaining());
                requested = true;
            });
        }
        else if (attempts.IsReady)
        {
            requested = false;
        }
    }

    // --------------------------------------------------------------- queue

    /// <summary>How much of the item has left the bag since the action started; the bag count is the truth.</summary>
    private int Removed() => current == null ? 0 : Math.Max(0, currentBaseline - gameBridge.GetItemCount(current.ItemId));

    /// <summary>How much of the current action is still to do.</summary>
    private int Remaining() => current == null ? 0 : current.Amount - Removed();

    private void Next()
    {
        current = null;
        requested = false;
        bellStarted = false;
        if (pending.Count == 0)
        {
            if (State != InventoryKeeperState.Idle)
                Transition(InventoryKeeperState.Idle, lastPass.Count == 0 ? "Idle." : $"Cleanup done: {string.Join("; ", lastPass)}.");
            else
                SetState(InventoryKeeperState.Idle, "Idle.");

            return;
        }

        current = pending.Dequeue();
        currentBaseline = gameBridge.GetItemCount(current.ItemId);
        switch (current.Action)
        {
            case StorageAction.Desynth:
                Enter(InventoryKeeperState.Desynthesizing, $"Desynthesizing {current.Reason}...");
                break;

            case StorageAction.Discard:
                Enter(InventoryKeeperState.Discarding, $"Discarding {current.Reason}...");
                break;

            case StorageAction.Deposit:
                Enter(InventoryKeeperState.GoingToBell, $"Depositing {current.Reason}...");
                break;

            default:
                Next();
                break;
        }
    }

    private void Done(string what)
    {
        lastPass.Add(what);
        Log.Information($"[Inventory] {char.ToUpperInvariant(what[0])}{what[1..]}.");
        Next();
    }

    /// <summary>The action could not be carried out; the pass goes on with the next one.</summary>
    private void Skip(string reason)
    {
        lastPass.Add($"skipped {itemName(current?.ItemId ?? 0)}: {reason}");
        Log.Warning($"[Inventory] Skipped {itemName(current?.ItemId ?? 0)}: {reason}.");
        Next();
    }

    private void Enter(InventoryKeeperState next, string statusText)
    {
        phaseStartedAt = Clock.UtcNow;
        attempts.Reset();
        Transition(next, statusText);
    }

    private void Settle(TimeSpan delay) => settleUntil = Clock.UtcNow + delay;

    /// <summary>
    /// Cleanup is housekeeping, never the point of the run: a step that
    /// stalls is logged and skipped instead of failing anything.
    /// </summary>
    private bool SoftTimedOut(string reason)
    {
        if (Clock.UtcNow - phaseStartedAt <= StepTimeout)
            return false;

        gameBridge.ConfirmDesynthesis();
        gameBridge.FireAddonCallbackInt("SelectYesno", 1);
        Skip(reason);
        return true;
    }

    public override IEnumerable<string> Describe()
    {
        yield return $"Inventory keeper {State} — {StatusText}";
        yield return $"rules {configuration.StorageRules.Count} (desynth {configuration.DesynthUnusedByproducts}, trash {configuration.TrashCleanup}); " +
                     $"queued {pending.Count}; current {(current == null ? "-" : $"{current.Action} {itemName(current.ItemId)} ×{current.Amount} (baseline {currentBaseline})")}";
        yield return $"last pass: {(lastPass.Count == 0 ? "-" : string.Join("; ", lastPass))}";
        if (State is InventoryKeeperState.GoingToBell or InventoryKeeperState.Depositing or InventoryKeeperState.LeavingBell)
        {
            foreach (var line in bell.Describe())
                yield return "  " + line;
        }
    }
}

/// <summary>The live bag and storage counts, for <see cref="StorageKeeper.Decide"/>.</summary>
internal sealed class BridgeStorageView(IGameBridge gameBridge) : IStorageView
{
    public int InBag(uint itemId) => gameBridge.GetItemCount(itemId);

    public int InStorage(uint itemId) => gameBridge.GetStoredItemCount(itemId);

    public int FreeBagSlots => gameBridge.GetFreeInventorySlots();
}
