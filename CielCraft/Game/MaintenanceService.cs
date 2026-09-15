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
/// Keeps long unattended runs healthy (roadmap 6.1/6.2/7.11/7.2): self-repairs
/// with Dark Matter when gear condition drops below the configured threshold,
/// extracts materia from one piece at 100% spiritbond per pass, and keeps up
/// the food and potion of the consumable set that matches the activity about
/// to start. Driven by callers between crafts/nodes via <see cref="Tick"/>;
/// never acts mid-craft or mid-node.
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
        // Materia extraction (roadmap 7.2): open Materialize and pick the piece,
        // say Yes, watch the spiritbond drop, close the window.
        Extracting,
        ConfirmingExtract,
        WaitingForExtract,
        ClosingMaterialize,
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

    // The piece in flight during the extraction phases (roadmap 7.2).
    private int pendingSpiritbondSlot;
    private uint pendingSpiritbondItemId;
    private int readySlotsBefore;

    /// <summary>Set when an extraction phase timed out: the feature is off until the next plugin load, the run goes on.</summary>
    private bool extractionDisabled;
    private bool bagFullNoted;

    /// <summary>Materia extracted this session (roadmap 7.2); the spiritbond mode stops at its target on this count.</summary>
    public int MateriaExtracted { get; private set; }

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

    /// <summary>A piece is at 100% spiritbond and extraction is on and has not failed this session (roadmap 7.2).</summary>
    private bool NeedsExtraction =>
        configuration.AutoExtractMateria && !extractionDisabled && gameBridge.GetSpiritbondReadySlots().Count > 0;

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
                if (NeedsExtraction)
                    return StartExtraction();
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
                    // Repair, then materia, then consumables — all before the phase starts.
                    return NeedsExtraction ? StartExtraction() : StartDueConsumable();
                }

                attempts.Try(CloseRepairUi);
                if (TimedOut("the repair window did not close"))
                    return false;

                return true;

            case Phase.Extracting:
                if (gameBridge.IsAddonVisible("MaterializeDialog"))
                {
                    EnterPhase(Phase.ConfirmingExtract, "Confirming the materia extraction...");
                    return true;
                }

                if (ExtractTimedOut("the materia extraction window did not respond"))
                    return StartDueConsumable();

                if (!gameBridge.IsAddonVisible("Materialize"))
                {
                    // Like repair: the general action is refused while the
                    // crafting log is up; the batch reopens its recipe afterwards.
                    attempts.Try(() =>
                    {
                        gameBridge.CloseRecipeNote();
                        gameBridge.OpenMaterialize();
                    });
                    return true;
                }

                attempts.Try(() => gameBridge.ExtractMateria(pendingSpiritbondSlot));
                return true;

            case Phase.ConfirmingExtract:
                if (!gameBridge.IsAddonVisible("MaterializeDialog"))
                {
                    EnterPhase(Phase.WaitingForExtract, "Waiting for the materia...");
                    return true;
                }

                attempts.Try(() => gameBridge.ConfirmMaterializeDialog());
                if (ExtractTimedOut("the materia extraction confirmation did not respond"))
                    return StartDueConsumable();

                return true;

            case Phase.WaitingForExtract:
                if (ExtractionLanded() && !gameBridge.IsMaterializing)
                {
                    MateriaExtracted++;
                    log.Information(
                        $"[Maintenance] Materia extracted from {itemName(pendingSpiritbondItemId)} ({MateriaExtracted} this session).");
                    EnterPhase(Phase.ClosingMaterialize, "Closing the materia window...");
                    return true;
                }

                if (ExtractTimedOut("the spiritbond did not drop after the extraction"))
                    return StartDueConsumable();

                return true;

            case Phase.ClosingMaterialize:
                if (!gameBridge.IsAddonVisible("Materialize"))
                {
                    phase = Phase.Idle;
                    // One piece per pass (roadmap 7.2): the next ready piece
                    // waits for the next Tick between crafts / nodes.
                    return StartDueConsumable();
                }

                attempts.Try(gameBridge.CloseMaterialize);
                if (ExtractTimedOut("the materia window did not close"))
                    return StartDueConsumable();

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

    /// <summary>Starts extracting from the first piece at 100% spiritbond; false when the bag cannot take the materia.</summary>
    private bool StartExtraction()
    {
        if (gameBridge.GetFreeInventorySlots() < 1)
        {
            // Not blocking: the run itself pauses on a full bag where it must.
            if (!bagFullNoted)
            {
                bagFullNoted = true;
                log.Warning("[Maintenance] A piece is at 100% spiritbond but the bag is full; skipping materia extraction.");
            }

            return StartDueConsumable();
        }

        bagFullNoted = false;
        var ready = gameBridge.GetSpiritbondReadySlots();
        if (ready.Count == 0)
            return StartDueConsumable();

        pendingSpiritbondSlot = ready[0];
        readySlotsBefore = ready.Count;
        pendingSpiritbondItemId = 0;
        foreach (var piece in gameBridge.GetEquipmentSpiritbond())
        {
            if (piece.Slot == pendingSpiritbondSlot)
                pendingSpiritbondItemId = piece.ItemId;
        }

        log.Information(
            $"[Maintenance] {itemName(pendingSpiritbondItemId)} (slot {pendingSpiritbondSlot}) is at 100% spiritbond; extracting materia" +
            (ready.Count > 1 ? $" ({ready.Count - 1} more piece{(ready.Count > 2 ? "s" : "")} ready)." : "."));
        EnterPhase(Phase.Extracting, "Extracting materia...");
        return true;
    }

    /// <summary>
    /// The extraction happened: the chosen slot's spiritbond fell below
    /// 100%, or — should the Materialize row index not map to the equipment
    /// slot one-to-one — fewer pieces are ready than before it.
    /// </summary>
    private bool ExtractionLanded()
    {
        var ready = gameBridge.GetSpiritbondReadySlots();
        if (ready.Count < readySlotsBefore)
            return true;

        foreach (var slot in ready)
        {
            if (slot == pendingSpiritbondSlot)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Extraction's timeout is non-fatal (roadmap 7.2): the window is
    /// dismissed, the feature is off for the session and the run goes on
    /// without a <see cref="BlockedReason"/>.
    /// </summary>
    private bool ExtractTimedOut(string reason)
    {
        if (clock.UtcNow - phaseStartedAt <= PhaseTimeout)
            return false;

        log.Warning($"[Maintenance] {reason}; materia extraction is off for this session.");
        gameBridge.CloseMaterialize();
        extractionDisabled = true;
        phase = Phase.Idle;
        return true;
    }

    private void EnterPhase(Phase next, string statusText)
    {
        phase = next;
        phaseStartedAt = clock.UtcNow;
        attempts.Reset();
        StatusText = statusText;
    }

    /// <summary>Abandons any in-flight phase, dismissing any repair or materia UI left open.</summary>
    public void Abort()
    {
        if (phase == Phase.Idle)
            return;

        if (phase is Phase.Extracting or Phase.ConfirmingExtract or Phase.WaitingForExtract or Phase.ClosingMaterialize)
            gameBridge.CloseMaterialize();
        else
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
        yield return $"Materia extracted this session {MateriaExtracted} (auto-extract {configuration.AutoExtractMateria}, disabled {extractionDisabled}); spiritbond-ready slots [{string.Join(", ", gameBridge.GetSpiritbondReadySlots())}]; pending slot {pendingSpiritbondSlot} (item {pendingSpiritbondItemId}, ready before {readySlotsBefore})";
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
