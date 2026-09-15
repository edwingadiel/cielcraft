using System;
using CielCraft.Core;
using System.Collections.Generic;

namespace CielCraft.Game;

/// <summary>
/// Keeps long unattended runs healthy (roadmap 6.1/6.2): self-repairs with
/// Dark Matter when gear condition drops below the configured threshold, and
/// re-eats the configured food before the buff runs out. Driven by callers
/// between crafts/nodes via <see cref="Tick"/>; never acts mid-craft.
/// </summary>
/// <summary>What the character is about to do; each has its own food and potion (roadmap 7.11).</summary>
public enum MaintenanceActivity
{
    Crafting,
    Gathering,
}

public sealed class MaintenanceService
{
    private static readonly uint[] DarkMatter = [33916, 10386]; // grade 8, grade 7
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1.5);
    private const float FoodRefreshBelowSeconds = 300f;

    private enum Phase
    {
        Idle,
        OpeningRepair,
        RepairingAll,
        Confirming,
        WaitingForRepair,
        ClosingRepair,
        EatingFood,
    }

    private readonly IGameBridge gameBridge;
    private readonly AutomationSettings configuration;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly Throttle attempts;

    private Phase phase = Phase.Idle;
    private DateTime phaseStartedAt;
    private DateTime lastIdleCheckAt;
    private bool foodFailedThisSession;

    public string StatusText { get; private set; } = "";

    /// <summary>Set when maintenance is required but impossible; callers should pause with this reason.</summary>
    public string? BlockedReason { get; private set; }

    public MaintenanceService(IGameBridge gameBridge, AutomationSettings configuration, ILog log, IClock clock)
    {
        this.gameBridge = gameBridge;
        this.configuration = configuration;
        this.log = log;
        this.clock = clock;
        attempts = new Throttle(clock, RetryInterval);
    }

    private bool NeedsRepair =>
        configuration.AutoRepair
        && gameBridge.GetLowestEquipmentConditionPercent() < Math.Clamp(configuration.RepairThresholdPercent, 1, 99);

    private bool NeedsFood =>
        configuration.FoodItemId != 0
        && !foodFailedThisSession
        && gameBridge.GetFoodBuffRemainingSeconds() < FoodRefreshBelowSeconds;

    /// <summary>The kind of work about to start; selects the consumable set (roadmap 7.11).</summary>
    public MaintenanceActivity Activity { get; private set; } = MaintenanceActivity.Crafting;

    /// <summary>
    /// Called by the production runner before a craft step or a gather task
    /// (roadmap 7.11): the next Tick keeps up the food and potion of that
    /// activity's set. Implemented by the consumables package; today it only
    /// records the activity.
    /// </summary>
    public void PrepareFor(MaintenanceActivity activity) => Activity = activity;

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
                // Gear condition and food buffs change on a minutes timescale;
                // polling the native containers every frame is pure waste.
                if (clock.UtcNow - lastIdleCheckAt < TimeSpan.FromSeconds(1))
                    return false;

                lastIdleCheckAt = clock.UtcNow;
                if (NeedsRepair)
                    return StartRepair();
                if (NeedsFood)
                    return StartFood();
                return false;

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
                    return NeedsFood && StartFood();
                }

                attempts.Try(CloseRepairUi);
                if (TimedOut("the repair window did not close"))
                    return false;

                return true;

            case Phase.EatingFood:
                if (gameBridge.GetFoodBuffRemainingSeconds() >= FoodRefreshBelowSeconds)
                {
                    log.Information("[Maintenance] Food refreshed.");
                    phase = Phase.Idle;
                    return false;
                }

                if (clock.UtcNow - phaseStartedAt > PhaseTimeout)
                {
                    // Non-fatal: keep running without food rather than stalling.
                    log.Warning("[Maintenance] Could not eat the configured food; continuing without it.");
                    foodFailedThisSession = true;
                    phase = Phase.Idle;
                    return false;
                }

                attempts.Try(() =>
                {
                    var id = configuration.FoodItemId;
                    // HQ consumables are addressed as item id + 1,000,000.
                    if (!configuration.FoodIsHq || !gameBridge.UseItem(id + 1_000_000))
                        gameBridge.UseItem(id);
                });
                return true;

            default:
                return false;
        }
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

    private bool StartFood()
    {
        log.Information($"[Maintenance] Eating food (item {configuration.FoodItemId}).");
        EnterPhase(Phase.EatingFood, "Eating food...");
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

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"Phase {phase} since {phaseStartedAt:HH:mm:ss}Z; last attempt {attempts.LastAttempt:HH:mm:ss}Z; last idle check {lastIdleCheckAt:HH:mm:ss}Z; status: {StatusText}; blocked: {BlockedReason ?? "-"}; foodFailedThisSession {foodFailedThisSession}";
        yield return $"Gear condition {gameBridge.GetLowestEquipmentConditionPercent():F0}% (auto-repair {configuration.AutoRepair}, threshold {configuration.RepairThresholdPercent}%); food item {configuration.FoodItemId} (HQ {configuration.FoodIsHq}), remaining {gameBridge.GetFoodBuffRemainingSeconds():F0}s";
    }
}
