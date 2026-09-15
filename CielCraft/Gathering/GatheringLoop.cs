using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Gathering;

public enum GatheringLoopState
{
    Idle,
    Running,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// "Gather item X ×N" (spec §66): runs the single-node controller node after
/// node until the inventory holds the requested amount. Progress is measured
/// against live inventory, never assumed from swing counts. Nodes that fail
/// (wrong contents, unreachable, despawned) are blacklisted for this run; too
/// many consecutive failures stop the loop.
/// </summary>
public sealed class GatheringLoop : AutomationMachine<GatheringLoopState>
{
    private const int MaxConsecutiveFailures = 5;

    private readonly IGameBridge gameBridge;
    private readonly GatheringController controller;
    private readonly Core.INavigationProvider navigation;
    private readonly AutomationSettings configuration;
    private readonly Game.MaintenanceService maintenance;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly IReadOnlyList<CordialInfo> cordials;
    private readonly HashSet<ulong> blacklistedNodes = [];

    private static readonly TimeSpan NoNodeTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NavmeshTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartRetryInterval = TimeSpan.FromSeconds(2);
    // Cordial maths (roadmap 7.14): GP regenerates 5 per 3-second tick plus one
    // per unlocked GP-regen trait; a walk to the next node is about 20 s.
    private const int BaseGpRegenPerTick = 5;
    private const int GpTickSeconds = 3;
    private const int EstimatedWalkSeconds = 20;

    private uint itemId;
    private int targetQuantity;
    private int baselineCount;
    private int consecutiveFailures;
    private bool controllerActive;
    private CollectableTier? collectableTier;
    private NodeKind nodeKind;
    private int collectablesTaken;   // summed over finished node runs (7.1)
    private DateTime noNodeSince = DateTime.MaxValue;
    private DateTime navmeshWaitSince = DateTime.MaxValue;
    private DateTime lastStartAttempt = DateTime.MinValue;
    private System.Numerics.Vector3? areaCenter;
    private DateTime lastCordialAt = DateTime.MinValue;
    private DateTime lastHousekeepingAt = DateTime.MinValue;

    /// <summary>
    /// Progress toward the amount: the inventory gain, and for a collectable
    /// order (7.1) at least the collectables the controller took — the
    /// appraisal window is the observed event there, the bag count the check.
    /// </summary>
    public int Gathered => itemId == 0 ? 0
        : Math.Max(
            Math.Max(0, gameBridge.GetItemCount(itemId) - baselineCount),
            collectableTier != null ? collectablesTaken + (controllerActive ? controller.CollectablesTaken : 0) : 0);

    public int TargetQuantity => targetQuantity;

    public GatheringLoop(
        IGameBridge gameBridge,
        GatheringController controller,
        Core.INavigationProvider navigation,
        AutomationSettings configuration,
        Game.MaintenanceService maintenance,
        ILog log,
        IClock clock,
        Func<CharacterCapabilities>? capabilities = null,
        GatheringActionCatalog? catalog = null)
        : base(log, clock, "[Gather]", GatheringLoopState.Idle, "Idle.")
    {
        this.maintenance = maintenance;
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
        cordials = (catalog ?? controller.Catalog).Cordials;
        this.gameBridge = gameBridge;
        this.controller = controller;
        this.navigation = navigation;
        this.configuration = configuration;
    }

    /// <summary>
    /// Starts the loop; a tier means the item is gathered as a collectable at
    /// that tier's collectability (7.1); kind picks the rotation table and
    /// the GP a node wants for the cordial decision (7.14).
    /// </summary>
    public bool Start(
        uint gatherItemId,
        int quantity,
        System.Numerics.Vector3? nodeAreaCenter = null,
        CollectableTier? tier = null,
        NodeKind kind = NodeKind.Normal)
    {
        if (State is GatheringLoopState.Running or GatheringLoopState.Paused)
            return false;

        if (gatherItemId == 0 || quantity < 1)
        {
            Transition(GatheringLoopState.Idle, "A specific item id and quantity are required.");
            return false;
        }

        itemId = gatherItemId;
        targetQuantity = quantity;
        areaCenter = nodeAreaCenter;
        baselineCount = gameBridge.GetItemCount(itemId);
        consecutiveFailures = 0;
        controllerActive = false;
        collectableTier = tier;
        nodeKind = kind;
        collectablesTaken = 0;
        noNodeSince = DateTime.MaxValue;
        navmeshWaitSince = DateTime.MaxValue;
        blacklistedNodes.Clear();

        Transition(GatheringLoopState.Running, $"Gathering item {itemId} ×{quantity}{TierText()}.");
        return true;
    }

    private string TierText() => collectableTier is { } tier ? $" as {tier} collectables" : "";

    public void Pause(string reason)
    {
        if (State != GatheringLoopState.Running)
            return;

        controller.Pause("loop paused");
        // The between-node area drift runs while the controller is idle, so
        // its Pause cannot stop that movement — stop it here.
        navigation.Stop();
        Transition(GatheringLoopState.Paused, $"Paused: {reason}.");
    }

    public void Resume()
    {
        if (State != GatheringLoopState.Paused)
            return;

        if (controller.State == GatheringState.Paused)
            controller.Resume();

        Transition(GatheringLoopState.Running, ProgressText());
    }

    public void Stop()
    {
        controller.Stop();
        maintenance.Abort();
        navigation.Stop();
        if (State is GatheringLoopState.Running or GatheringLoopState.Paused)
            Transition(GatheringLoopState.Idle, $"Stopped by user at {Gathered}/{targetQuantity}.");
    }

    protected override void OnTick()
    {
        if (State != GatheringLoopState.Running)
            return;

        // Completion, inventory-space and cordial checks all poll native
        // inventory sweeps; once a second is plenty (node runs start at most
        // every two seconds anyway).
        if (Clock.UtcNow - lastHousekeepingAt > TimeSpan.FromSeconds(1))
        {
            lastHousekeepingAt = Clock.UtcNow;
            var gathered = Gathered;
            if (gathered >= targetQuantity)
            {
                controller.Stop();

                // The target is usually reached mid-node. The close request can
                // be ignored while a swing animates, so keep asking (once a
                // second) and only report completion once the character is free.
                if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
                {
                    gameBridge.CloseGatheringWindow();
                    StatusText = $"Gathered {gathered}/{targetQuantity}; leaving the node...";
                    return;
                }

                Transition(GatheringLoopState.Completed, $"Completed: {gathered}/{targetQuantity} gathered.");
                return;
            }

            if (gameBridge.GetFreeInventorySlots() < 1)
            {
                Pause("inventory is full");
                return;
            }

            // Cordials only between nodes: item use while the node window is
            // open is refused and would just burn the retry back-off.
            if (controller.State is not (GatheringState.MovingToNode or GatheringState.Interacting
                or GatheringState.GatheringNode or GatheringState.CollectableNode))
                TryCordial();
        }

        // Between-node maintenance (repair/food) — never while a node run is live.
        var nodeRunActive = controller.State is GatheringState.MovingToNode
            or GatheringState.Interacting or GatheringState.GatheringNode or GatheringState.CollectableNode;
        if (!nodeRunActive)
        {
            if (maintenance.Tick())
            {
                StatusText = maintenance.StatusText;
                return;
            }

            if (maintenance.BlockedReason != null)
            {
                Pause(maintenance.BlockedReason);
                return;
            }
        }

        switch (controller.State)
        {
            case GatheringState.MovingToNode:
            case GatheringState.Interacting:
            case GatheringState.GatheringNode:
            case GatheringState.CollectableNode:
                return; // a node run is in progress

            case GatheringState.Paused:
                Transition(GatheringLoopState.Paused, $"Paused: {controller.StatusText}");
                return;

            case GatheringState.Completed when controllerActive:
                consecutiveFailures = 0;
                controllerActive = false;
                collectablesTaken += controller.CollectablesTaken;
                StatusText = ProgressText();
                break;

            case GatheringState.Failed when controllerActive:
                controllerActive = false;
                collectablesTaken += controller.CollectablesTaken; // a node can fail after handing over collectables
                consecutiveFailures++;
                if (controller.LastNodeId != 0)
                    blacklistedNodes.Add(controller.LastNodeId);

                Log.Warning(
                    $"[Gather] Node run failed ({controller.StatusText}); " +
                    $"blacklisting node, failure {consecutiveFailures}/{MaxConsecutiveFailures}.");

                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    Transition(GatheringLoopState.Failed, $"Failed: {consecutiveFailures} node runs in a row failed.");
                    return;
                }

                break;
        }

        // Start the next node run. Node groups respawn on a delay after being
        // exhausted, so an empty object table is retried before giving up.
        if (Clock.UtcNow - lastStartAttempt < StartRetryInterval)
            return;

        lastStartAttempt = Clock.UtcNow;

        // Still at the previous node (window up, or the gathering condition
        // not yet cleared after closing it): the character is pinned until it
        // is gone, so nothing can start.
        if (gameBridge.GetGatheringState() != null || gameBridge.IsGathering)
        {
            gameBridge.CloseGatheringWindow();
            StatusText = $"{ProgressText()} Leaving the previous node...";
            return;
        }

        // vnavmesh rebuilds its mesh after every zone change (the first visit
        // to a zone can take a minute or more). The no-node clock must not run
        // while nodes are merely out of walking reach.
        if (!navigation.IsReady && !NodeInReach())
        {
            if (navmeshWaitSince == DateTime.MaxValue)
            {
                navmeshWaitSince = Clock.UtcNow;
                Log.Information("[Gather] Waiting for the navmesh to build before approaching a node.");
            }

            noNodeSince = DateTime.MaxValue;
            if (Clock.UtcNow - navmeshWaitSince > NavmeshTimeout)
                Transition(
                    GatheringLoopState.Failed,
                    $"Failed: the navmesh did not become ready within {NavmeshTimeout.TotalMinutes:F0} minutes.");
            else
                StatusText = $"{ProgressText()} Waiting for the navmesh to build...";
            return;
        }

        navmeshWaitSince = DateTime.MaxValue;

        if (controller.Start(itemId, blacklistedNodes, targetQuantity - Gathered, areaCenter, collectableTier, nodeKind))
        {
            controllerActive = true;
            noNodeSince = DateTime.MaxValue;
        }
        else
        {
            if (noNodeSince == DateTime.MaxValue)
                noNodeSince = Clock.UtcNow;

            // Drift back toward the node-area center while waiting: fresh
            // spawns may be outside object-table range (roadmap 2.4).
            var player = gameBridge.GetPlayerState();
            if (areaCenter is { } center && player != null && navigation.IsReady
                && System.Numerics.Vector3.Distance(player.Position, center) > 60f
                && !navigation.IsMoving)
            {
                navigation.MoveCloseTo(center, 15f, fly: false);
            }

            if (Clock.UtcNow - noNodeSince > NoNodeTimeout)
                Transition(
                    GatheringLoopState.Failed,
                    $"Failed: no usable gathering node appeared within {NoNodeTimeout.TotalSeconds:F0}s ({controller.StatusText}).");
            else
                StatusText = $"{ProgressText()} Waiting for a node to appear...";
        }
    }

    /// <summary>
    /// Drinks a cordial between nodes (roadmap 2.2 / 7.14) when the GP the
    /// next node wants (<see cref="GatheringRotationCost"/>) is not covered by
    /// the current GP plus the regen of the walk there: the strongest cordial
    /// held whose GP fits into the pool, HQ before NQ, never while its recast
    /// runs. Never at a node — the caller checks the controller's state.
    /// </summary>
    private void TryCordial()
    {
        if (!configuration.UseCordials || Clock.UtcNow - lastCordialAt < TimeSpan.FromSeconds(5))
            return;

        var player = gameBridge.GetPlayerState();
        if (player == null || player.MaxGp == 0)
            return;

        var wanted = GatheringRotationCost.GpPerNode(
            nodeKind, collectableTier != null, (int)player.MaxGp, configuration, GatheringActions.IsCrystal(itemId));
        var regenDuringWalk = GpRegenPerTick(player.ClassJobId) * (EstimatedWalkSeconds / GpTickSeconds);
        if (player.CurrentGp + regenDuringWalk >= wanted)
            return;

        foreach (var cordial in cordials)
        {
            var total = gameBridge.GetItemCount(cordial.ItemId);
            if (total == 0)
                continue;

            if (gameBridge.IsItemOnCooldown(cordial.ItemId))
                continue;

            // HQ consumables are addressed as item id + 1,000,000; the count
            // covers both qualities, so try the HQ form first when it fits.
            var hqCount = cordial.CanBeHq ? gameBridge.GetHqItemCount(cordial.ItemId) : 0;
            var fitsHq = hqCount > 0 && player.CurrentGp + cordial.Gp(true) <= player.MaxGp;
            var fitsNq = total - hqCount > 0 && player.CurrentGp + cordial.Gp(false) <= player.MaxGp;
            if (fitsHq && gameBridge.UseItem(cordial.ItemId + 1_000_000))
            {
                LogCordial(cordial, true, player, wanted);
                return;
            }

            if (fitsNq && gameBridge.UseItem(cordial.ItemId))
            {
                LogCordial(cordial, false, player, wanted);
                return;
            }
        }

        // None usable (cooldown, none held, or none fits): back off before checking again.
        lastCordialAt = Clock.UtcNow;
    }

    private void LogCordial(CordialInfo cordial, bool hq, PlayerSnapshot player, int wanted)
    {
        lastCordialAt = Clock.UtcNow;
        Log.Information(
            $"[Gather] Drinking {cordial.Name}{(hq ? " HQ" : "")} (+{cordial.Gp(hq)} GP, recast {cordial.CooldownSeconds}s); " +
            $"GP {player.CurrentGp}/{player.MaxGp}, the next node wants {wanted}.");
    }

    /// <summary>GP per 3-second tick: the base regen plus one per unlocked GP-regen trait of the job (7.16).</summary>
    private int GpRegenPerTick(uint jobId)
    {
        var traits = 0;
        foreach (var trait in capabilities().GpRegenTraits)
        {
            if (trait.JobId == jobId && trait.Unlocked)
                traits++;
        }

        return BaseGpRegenPerTick + traits;
    }

    private string ProgressText() => $"Gathered {Gathered}/{targetQuantity} of item {itemId}{TierText()}.";

    /// <summary>A usable node is close enough to interact with without navigation.</summary>
    private bool NodeInReach() =>
        gameBridge.FindNearestGatheringNode(blacklistedNodes, areaCenter) is { } nearest
        && nearest.Distance <= GatheringController.InteractRange;

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"Item {itemId} ×{targetQuantity}: gathered {Gathered} (baseline {baselineCount}; kind {nodeKind}; tier {collectableTier?.ToString() ?? "-"}, collectables taken {collectablesTaken}); consecutive failures {consecutiveFailures}; controllerActive {controllerActive}; blacklisted nodes {blacklistedNodes.Count}";
        yield return $"noNodeSince {(noNodeSince == DateTime.MaxValue ? "-" : noNodeSince.ToString("HH:mm:ss") + "Z")}; navmeshWaitSince {(navmeshWaitSince == DateTime.MaxValue ? "-" : navmeshWaitSince.ToString("HH:mm:ss") + "Z")}; last start attempt {lastStartAttempt:HH:mm:ss}Z; area center {areaCenter?.ToString() ?? "-"}; last cordial {lastCordialAt:HH:mm:ss}Z";
    }
}
