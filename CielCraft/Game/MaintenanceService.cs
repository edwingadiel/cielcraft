using System;
using CielCraft.Core;
using System.Collections.Generic;

namespace CielCraft.Game;

/// <summary>What the character is about to do; each has its own food and potion (roadmap 7.11).</summary>
public enum MaintenanceActivity
{
    Crafting,
    Gathering,
}

/// <summary>
/// Keeps long unattended runs healthy (roadmap 6.1/6.2/7.11): self-repairs
/// with Dark Matter when gear condition drops below the configured threshold,
/// and keeps up the food and potion of the consumable set that matches the
/// activity about to start. Driven by callers between crafts/nodes via
/// <see cref="Tick"/>; never acts mid-craft or mid-node.
/// </summary>
public sealed class MaintenanceService
{
    private static readonly uint[] DarkMatter = [33916, 10386]; // grade 8, grade 7
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>Re-apply a consumable when its buff has less than this left; a craft or node run never outlasts it.</summary>
    private const float RefreshBelowSeconds = 300f;

    private enum Phase
    {
        Idle,
        OpeningRepair,
        RepairingAll,
        Confirming,
        WaitingForRepair,
        ClosingRepair,
        Consuming,
    }

    /// <summary>The two slots of a <see cref="ConsumableSet"/>, each tracked by its own buff (Well Fed / Medicated).</summary>
    private enum Slot
    {
        Food,
        Potion,
    }

    private readonly IGameBridge gameBridge;
    private readonly AutomationSettings configuration;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly Throttle attempts;
    private readonly Func<uint, string> itemName;

    private Phase phase = Phase.Idle;
    private DateTime phaseStartedAt;
    private DateTime lastIdleCheckAt;

    // The consumable in flight during Phase.Consuming.
    private Slot pendingSlot;
    private uint pendingItemId;
    private bool pendingHq;

    /// <summary>Items that could not be used this session; retried on the next plugin load, not every craft.</summary>
    private readonly HashSet<uint> failedItems = [];

    public string StatusText { get; private set; } = "";

    /// <summary>Set when maintenance is required but impossible; callers should pause with this reason.</summary>
    public string? BlockedReason { get; private set; }

    /// <param name="itemName">Item names for log lines and the blocked reason; defaults to "item N".</param>
    public MaintenanceService(
        IGameBridge gameBridge, AutomationSettings configuration, ILog log, IClock clock, Func<uint, string>? itemName = null)
    {
        this.gameBridge = gameBridge;
        this.configuration = configuration;
        this.log = log;
        this.clock = clock;
        this.itemName = itemName ?? (id => $"item {id}");
        attempts = new Throttle(clock, RetryInterval);
    }

    private bool NeedsRepair =>
        configuration.AutoRepair
        && gameBridge.GetLowestEquipmentConditionPercent() < Math.Clamp(configuration.RepairThresholdPercent, 1, 99);

    /// <summary>The kind of work about to start; selects the consumable set (roadmap 7.11).</summary>
    public MaintenanceActivity Activity { get; private set; } = MaintenanceActivity.Crafting;

    /// <summary>
    /// Called by the production runner before a craft step or a gather task
    /// (roadmap 7.11): the next Tick keeps up the food and potion of that
    /// activity's set.
    /// </summary>
    public void PrepareFor(MaintenanceActivity activity) => Activity = activity;

    private ConsumableSet ActiveSet =>
        Activity == MaintenanceActivity.Gathering ? configuration.GatheringConsumables : configuration.CraftingConsumables;

    private static bool IsEmpty(ConsumableSet set) => set.Food.ItemId == 0 && set.Potion.ItemId == 0;

    /// <summary>
    /// The item chosen for the slot of the active set; the pre-7.11 single
    /// food still serves as the food of both activities while neither set has
    /// anything (a config the migration has not seen yet). Id 0 = nothing.
    /// </summary>
    private (uint ItemId, bool Hq) Chosen(Slot slot)
    {
        var set = ActiveSet;
        var consumable = slot == Slot.Food ? set.Food : set.Potion;
        if (consumable.ItemId != 0)
            return (consumable.ItemId, consumable.Hq);

        if (slot == Slot.Food && configuration.FoodItemId != 0
            && IsEmpty(configuration.CraftingConsumables) && IsEmpty(configuration.GatheringConsumables))
            return (configuration.FoodItemId, configuration.FoodIsHq);

        return (0, false);
    }

    private float Remaining(Slot slot) =>
        slot == Slot.Food ? gameBridge.GetFoodBuffRemainingSeconds() : gameBridge.GetMedicatedRemainingSeconds();

    private static string Label(Slot slot) => slot == Slot.Food ? "food" : "potion";

    /// <summary>
    /// Runs maintenance when due. Returns true while busy (the caller should
    /// hold its own work); false when nothing is needed. Checks
    /// <see cref="BlockedReason"/> after a false return.
    /// </summary>
    public bool Tick()
    {
        BlockedReason = null;

        if (gameBridge.IsCrafting || gameBridge.IsGathering)
            return false;

        switch (phase)
        {
            case Phase.Idle:
                // Gear condition and buffs change on a minutes timescale;
                // polling the native containers every frame is pure waste.
                if (clock.UtcNow - lastIdleCheckAt < TimeSpan.FromSeconds(1))
                    return false;

                lastIdleCheckAt = clock.UtcNow;
                if (NeedsRepair)
                    return StartRepair();
                return StartDueConsumable();

            case Phase.OpeningRepair:
                if (gameBridge.IsAddonVisible("Repair"))
                {
                    EnterPhase(Phase.RepairingAll, "Repairing all equipment...");
                    return true;
                }

                if (TimedOut("the repair window did not open"))
                    return false;

                attempts.Try(() =>
                {
                    gameBridge.CloseRecipeNote();
                    gameBridge.OpenRepairWindow();
                });
                return true;

            case Phase.RepairingAll:
                if (gameBridge.IsAddonVisible("SelectYesno"))
                {
                    EnterPhase(Phase.Confirming, "Confirming repair...");
                    return true;
                }

                if (TimedOut("repair-all did not respond"))
                    return false;

                attempts.Try(() => gameBridge.FireAddonCallbackInt("Repair", 0));
                return true;

            case Phase.Confirming:
                if (!gameBridge.IsAddonVisible("SelectYesno"))
                {
                    EnterPhase(Phase.WaitingForRepair, "Waiting for repairs...");
                    return true;
                }

                attempts.Try(() => gameBridge.FireAddonCallbackInt("SelectYesno", 0));
                if (TimedOut("the repair confirmation did not respond"))
                    return false;

                return true;

            case Phase.WaitingForRepair:
                if (!NeedsRepair)
                {
                    log.Information("[Maintenance] Equipment repaired.");
                    EnterPhase(Phase.ClosingRepair, "Closing the repair window...");
                    return true;
                }

                if (TimedOut("repairs did not restore condition (out of Dark Matter?)"))
                    return false;

                return true;

            case Phase.ClosingRepair:
                if (!gameBridge.IsAddonVisible("Repair"))
                {
                    phase = Phase.Idle;
                    return StartDueConsumable();
                }

                attempts.Try(CloseRepairUi);
                if (TimedOut("the repair window did not close"))
                    return false;

                return true;

            case Phase.Consuming:
                if (Remaining(pendingSlot) >= RefreshBelowSeconds)
                {
                    log.Information($"[Maintenance] {Capitalize(Label(pendingSlot))} refreshed ({itemName(pendingItemId)}).");
                    phase = Phase.Idle;
                    // The other slot may be due too; both belong before the phase starts.
                    return StartDueConsumable();
                }

                if (clock.UtcNow - phaseStartedAt > PhaseTimeout)
                {
                    // Non-fatal: keep running without the buff rather than stalling.
                    log.Warning(
                        $"[Maintenance] Could not use {itemName(pendingItemId)} as {Label(pendingSlot)}; continuing without it this session.");
                    failedItems.Add(pendingItemId);
                    phase = Phase.Idle;
                    return StartDueConsumable();
                }

                attempts.Try(() =>
                {
                    // HQ consumables are addressed as item id + 1,000,000.
                    if (!pendingHq || !gameBridge.UseItem(pendingItemId + 1_000_000))
                        gameBridge.UseItem(pendingItemId);
                });
                return true;

            default:
                return false;
        }
    }

    /// <summary>Starts the first slot of the active set whose buff is under the threshold; false (maybe blocked) when none is.</summary>
    private bool StartDueConsumable()
    {
        foreach (var slot in new[] { Slot.Food, Slot.Potion })
        {
            var (itemId, hq) = Chosen(slot);
            if (itemId == 0 || failedItems.Contains(itemId) || Remaining(slot) >= RefreshBelowSeconds)
                continue;

            return StartConsuming(slot, itemId, hq);
        }

        return false;
    }

    private bool StartConsuming(Slot slot, uint itemId, bool preferHq)
    {
        var hqOwned = gameBridge.GetHqItemCount(itemId);
        var nqOwned = gameBridge.GetItemCount(itemId) - hqOwned;
        if (hqOwned <= 0 && nqOwned <= 0)
        {
            // Pre-flight (roadmap 7.11): the runner shows this as its pause reason.
            BlockedReason = $"{Activity.ToString().ToLowerInvariant()} {Label(slot)} {itemName(itemId)} is not in the inventory";
            return false;
        }

        // Prefer the configured quality, fall back to whichever is owned.
        var useHq = preferHq ? hqOwned > 0 : nqOwned <= 0;
        if (useHq != preferHq)
            log.Information($"[Maintenance] Only the {(useHq ? "HQ" : "NQ")} {itemName(itemId)} is in the bag; using it.");

        pendingSlot = slot;
        pendingItemId = itemId;
        pendingHq = useHq;
        log.Information(
            $"[Maintenance] {(slot == Slot.Food ? "Eating" : "Drinking")} {itemName(itemId)}{(useHq ? " HQ" : "")} for {Activity.ToString().ToLowerInvariant()} ({Remaining(slot):F0}s left).");
        EnterPhase(Phase.Consuming, slot == Slot.Food ? "Eating food..." : "Drinking a potion...");
        return true;
    }

    private bool StartRepair()
    {
        var hasDarkMatter = false;
        foreach (var id in DarkMatter)
        {
            if (gameBridge.GetItemCount(id) > 0)
            {
                hasDarkMatter = true;
                break;
            }
        }

        if (!hasDarkMatter)
        {
            BlockedReason = "equipment needs repair but there is no Dark Matter in the inventory";
            return false;
        }

        log.Information(
            $"[Maintenance] Gear at {gameBridge.GetLowestEquipmentConditionPercent():F0}%; self-repairing.");
        EnterPhase(Phase.OpeningRepair, "Opening the repair window...");
        return true;
    }

    private void EnterPhase(Phase next, string statusText)
    {
        phase = next;
        phaseStartedAt = clock.UtcNow;
        attempts.Reset();
        StatusText = statusText;
    }

    /// <summary>Abandons any in-flight phase, dismissing any repair UI left open.</summary>
    public void Abort()
    {
        if (phase == Phase.Idle)
            return;

        CloseRepairUi();
        phase = Phase.Idle;
        StatusText = "";
    }

    /// <summary>Dismisses the repair confirmation (No) and the repair window, whichever are open.</summary>
    private void CloseRepairUi()
    {
        gameBridge.FireAddonCallbackInt("SelectYesno", 1);
        gameBridge.FireAddonCallbackInt("Repair", -1);
    }

    private bool TimedOut(string reason)
    {
        if (clock.UtcNow - phaseStartedAt <= PhaseTimeout)
            return false;

        log.Warning($"[Maintenance] {reason}.");
        // Never leave repair UI open behind a timeout — it blocks gearset
        // swaps and crafting-log interaction downstream.
        CloseRepairUi();
        BlockedReason = reason;
        phase = Phase.Idle;
        return true;
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"Phase {phase} since {phaseStartedAt:HH:mm:ss}Z; last attempt {attempts.LastAttempt:HH:mm:ss}Z; last idle check {lastIdleCheckAt:HH:mm:ss}Z; status: {StatusText}; blocked: {BlockedReason ?? "-"}; failed items [{string.Join(", ", failedItems)}]";
        yield return $"Gear condition {gameBridge.GetLowestEquipmentConditionPercent():F0}% (auto-repair {configuration.AutoRepair}, threshold {configuration.RepairThresholdPercent}%)";
        yield return $"Activity {Activity}: food {DescribeChoice(Slot.Food)}, Well Fed {gameBridge.GetFoodBuffRemainingSeconds():F0}s; potion {DescribeChoice(Slot.Potion)}, Medicated {gameBridge.GetMedicatedRemainingSeconds():F0}s";
        yield return $"Crafting set: {DescribeSet(configuration.CraftingConsumables)}; gathering set: {DescribeSet(configuration.GatheringConsumables)}; legacy food {configuration.FoodItemId} (HQ {configuration.FoodIsHq})";
    }

    private string DescribeChoice(Slot slot)
    {
        var (itemId, hq) = Chosen(slot);
        return itemId == 0 ? "none" : $"{itemName(itemId)} ({(hq ? "HQ" : "NQ")}, owned {gameBridge.GetItemCount(itemId)})";
    }

    private string DescribeSet(ConsumableSet set) =>
        $"food {(set.Food.ItemId == 0 ? "none" : itemName(set.Food.ItemId) + (set.Food.Hq ? " HQ" : " NQ"))}, potion {(set.Potion.ItemId == 0 ? "none" : itemName(set.Potion.ItemId) + (set.Potion.Hq ? " HQ" : " NQ"))}";
}
