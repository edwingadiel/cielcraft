using System;
using CielCraft.Core;

namespace CielCraft.Game;

/// <summary>
/// Keeps long unattended runs healthy (roadmap 6.1/6.2): self-repairs with
/// Dark Matter when gear condition drops below the configured threshold, and
/// re-eats the configured food before the buff runs out. Driven by callers
/// between crafts/nodes via <see cref="Tick"/>; never acts mid-craft.
/// </summary>
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
    private readonly Configuration configuration;

    private Phase phase = Phase.Idle;
    private DateTime phaseStartedAt;
    private DateTime lastAttemptAt;
    private bool foodFailedThisSession;

    public string StatusText { get; private set; } = "";

    /// <summary>Set when maintenance is required but impossible; callers should pause with this reason.</summary>
    public string? BlockedReason { get; private set; }

    public MaintenanceService(IGameBridge gameBridge, Configuration configuration)
    {
        this.gameBridge = gameBridge;
        this.configuration = configuration;
    }

    private bool NeedsRepair =>
        configuration.AutoRepair
        && gameBridge.GetLowestEquipmentConditionPercent() < Math.Clamp(configuration.RepairThresholdPercent, 1, 99);

    private bool NeedsFood =>
        configuration.FoodItemId != 0
        && !foodFailedThisSession
        && gameBridge.GetFoodBuffRemainingSeconds() < FoodRefreshBelowSeconds;

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

                Throttled(() =>
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

                Throttled(() => gameBridge.FireAddonCallbackInt("Repair", 0));
                return true;

            case Phase.Confirming:
                if (!gameBridge.IsAddonVisible("SelectYesno"))
                {
                    EnterPhase(Phase.WaitingForRepair, "Waiting for repairs...");
                    return true;
                }

                Throttled(() => gameBridge.FireAddonCallbackInt("SelectYesno", 0));
                if (TimedOut("the repair confirmation did not respond"))
                    return false;

                return true;

            case Phase.WaitingForRepair:
                if (!NeedsRepair)
                {
                    Plugin.Log.Information("[Maintenance] Equipment repaired.");
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

                Throttled(() => gameBridge.FireAddonCallbackInt("Repair", -1));
                if (TimedOut("the repair window did not close"))
                    return false;

                return true;

            case Phase.EatingFood:
                if (gameBridge.GetFoodBuffRemainingSeconds() >= FoodRefreshBelowSeconds)
                {
                    Plugin.Log.Information("[Maintenance] Food refreshed.");
                    phase = Phase.Idle;
                    return false;
                }

                if (DateTime.UtcNow - phaseStartedAt > PhaseTimeout)
                {
                    // Non-fatal: keep running without food rather than stalling.
                    Plugin.Log.Warning("[Maintenance] Could not eat the configured food; continuing without it.");
                    foodFailedThisSession = true;
                    phase = Phase.Idle;
                    return false;
                }

                Throttled(() =>
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

        Plugin.Log.Information(
            $"[Maintenance] Gear at {gameBridge.GetLowestEquipmentConditionPercent():F0}%; self-repairing.");
        EnterPhase(Phase.OpeningRepair, "Opening the repair window...");
        return true;
    }

    private bool StartFood()
    {
        Plugin.Log.Information($"[Maintenance] Eating food (item {configuration.FoodItemId}).");
        EnterPhase(Phase.EatingFood, "Eating food...");
        return true;
    }

    private void EnterPhase(Phase next, string statusText)
    {
        phase = next;
        phaseStartedAt = DateTime.UtcNow;
        lastAttemptAt = DateTime.MinValue;
        StatusText = statusText;
    }

    private bool TimedOut(string reason)
    {
        if (DateTime.UtcNow - phaseStartedAt <= PhaseTimeout)
            return false;

        Plugin.Log.Warning($"[Maintenance] {reason}.");
        BlockedReason = reason;
        phase = Phase.Idle;
        return true;
    }

    private void Throttled(Action action)
    {
        if (DateTime.UtcNow - lastAttemptAt < RetryInterval)
            return;

        lastAttemptAt = DateTime.UtcNow;
        action();
    }
}
