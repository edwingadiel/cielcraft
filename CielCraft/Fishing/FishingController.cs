using System;
using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Fishing;

public enum FishingRunState
{
    Idle,

    /// <summary>Switching to the FSH gearset.</summary>
    PreparingJob,

    /// <summary>Teleporting to the fishing hole's territory.</summary>
    Teleporting,

    /// <summary>Walking or flying to the hole.</summary>
    Traveling,

    /// <summary>Applying the bait the fish wants.</summary>
    ApplyingBait,

    /// <summary>Rod out: cast, watch the bite, hook, mooch, repeat.</summary>
    Fishing,

    /// <summary>Putting the rod away before finishing.</summary>
    Quitting,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// Lets the AutoHook plugin decide the bite timing when it is installed
/// (setting <c>FishingPreferAutoHook</c>, roadmap 7.4). Optional everywhere:
/// CielCraft's own hooking is the default and the fallback.
/// </summary>
public interface IAutoHookControl
{
    /// <summary>AutoHook is installed, loaded and answering its IPC.</summary>
    bool IsAvailable { get; }

    /// <summary>Turns AutoHook's automation on or off for the run. False when the call did not get through.</summary>
    bool SetEnabled(bool enabled);

    IEnumerable<string> Describe();
}

/// <summary>
/// Fishes for one item (spec §32 lifted, roadmap 7.4): equip the FSH gearset,
/// travel to the hole the database picked, apply its bait, then cast — one
/// action per decision, each confirmed by an observed change of the game's
/// fishing state, with settle delays in between the way a person at the
/// keyboard produces them. Hooking is the plain Hook unless the tug is known
/// (the installed ClientStructs does not expose it — see
/// <see cref="FishingTug"/>), mooching is driven by the game's own
/// "this catch can be mooched" flag, and progress is the bag count, never the
/// number of casts. Dalamud-free: the plugin ticks it from the framework
/// driver, the tests from a fake bridge.
/// </summary>
public sealed class FishingController : AutomationMachine<FishingRunState>
{
    /// <summary>A wrong bundled bait (<see cref="FishingBaitTable"/>) costs this many casts, then the run pauses.</summary>
    public const int MaxCastsWithoutTarget = 30;

    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BiteTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CastBlockedTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BlindTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>A bite is over in a few seconds; hooking cannot wait on the general retry gate.</summary>
    private static readonly TimeSpan HookRetryInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromSeconds(1);

    // Settle delays (the Pacing rule, roadmap 7.20): a person looks at the
    // catch before casting again and does not cast the instant they arrive.
    private static readonly TimeSpan BeforeFirstCast = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AfterCatch = TimeSpan.FromSeconds(1.5);

    /// <summary>How close the travel leg has to get; the last stretch to the water is the navmesh's job.</summary>
    private const float ArriveWithin = 12f;

    /// <summary>AutoHook gets this long to hook a bite before the controller hooks it itself.</summary>
    private static readonly TimeSpan AutoHookGrace = TimeSpan.FromSeconds(3);

    private readonly IGameBridge bridge;
    private readonly INavigationProvider navigation;
    private readonly AutomationSettings configuration;
    private readonly FishingDatabase database;
    private readonly FishingActionCatalog catalog;
    private readonly IAutoHookControl? autoHook;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly Func<uint, string> zoneName;
    private readonly IReadOnlyList<CordialInfo> cordials;
    private readonly TravelDriver travel;
    private readonly Throttle retry;
    private readonly Throttle hookGate;

    private FishingPlan? plan;
    private uint targetItemId;
    private int targetAmount;
    private int baselineCount;
    private int casts;
    private int castsWithoutTarget;
    private int caughtAtLastCheck;
    private bool autoHookEngaged;
    private bool gearsetRequested;
    private bool baitRequested;
    private bool sawLoadingScreen;
    private DateTime phaseStartedAt;
    private DateTime phaseChangedAt;          // when the *game's* fishing phase last changed
    private DateTime zoneArrivedAt = DateTime.MinValue;
    private DateTime lastCatchAt = DateTime.MinValue;
    private DateTime lineOutSince = DateTime.MaxValue;
    private DateTime castBlockedSince = DateTime.MaxValue;
    private DateTime blindSince = DateTime.MaxValue;   // rod out but no fishing state to read
    private DateTime lastHousekeepingAt = DateTime.MinValue;
    private DateTime lastCordialAt = DateTime.MinValue;
    private int waterProbe;                             // ring points tried around the marker when no water is in range
    private FishingPhase lastPhase = FishingPhase.None;
    private (FishAction Action, uint ActionId, FishingPhase PhaseBefore, DateTime At)? pending;
    private string lastDecision = "-";

    public FishingController(
        IGameBridge bridge,
        INavigationProvider navigation,
        AutomationSettings configuration,
        FishingDatabase database,
        ILog log,
        IClock clock,
        FishingActionCatalog? catalog = null,
        IAutoHookControl? autoHook = null,
        Func<CharacterCapabilities>? capabilities = null,
        Func<uint, string>? zoneName = null,
        IReadOnlyList<CordialInfo>? cordials = null)
        : base(log, clock, "[Fishing]", FishingRunState.Idle, "Idle.")
    {
        this.bridge = bridge;
        this.navigation = navigation;
        this.configuration = configuration;
        this.database = database;
        this.catalog = catalog ?? new FishingActionCatalog();
        this.autoHook = autoHook;
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
        // The plugin passes zoneName; the tests a stub
        // (this class stays Dalamud-free so it compiles into CielCraft.Tests).
        this.zoneName = zoneName ?? (id => $"zone {id}");
        this.cordials = cordials ?? GatheringActions.Cordials;
        travel = new TravelDriver(navigation, bridge, clock, log, "[Fishing]");
        retry = new Throttle(clock, RetryInterval);
        hookGate = new Throttle(clock, HookRetryInterval);
    }

    /// <summary>Fish in the bag since the run started; the only measure of progress (spec §34).</summary>
    public int Caught => targetItemId == 0 ? 0 : Math.Max(0, bridge.GetItemCount(targetItemId) - baselineCount);

    public int TargetAmount => targetAmount;

    /// <summary>The hole and bait this run is using; null before the first Start.</summary>
    public FishingPlan? Plan => plan;

    /// <summary>Why the last run failed or paused in one sentence; empty when it did not.</summary>
    public string FailureReason { get; private set; } = "";

    /// <summary>
    /// Starts fishing for the item. False (with the reason in StatusText) when
    /// no hole / bait is known, the bait is not in the bag, or the character's
    /// Fisher level is below the hole.
    /// </summary>
    public bool Start(uint itemId, int amount)
    {
        if (IsBusy)
            return false;

        if (itemId == 0 || amount < 1)
        {
            Transition(FishingRunState.Idle, "A specific fish and amount are required.");
            return false;
        }

        if (bridge.IsCrafting)
        {
            Transition(FishingRunState.Idle, "Cannot start fishing while crafting.");
            return false;
        }

        var level = database.FisherLevel();
        var cap = level > 0 ? level : int.MaxValue;
        var chosen = database.FindSpot(itemId, bridge.CurrentTerritoryId, cap);
        if (chosen == null)
        {
            var reason = database.RefusalReason(itemId, bridge.CurrentTerritoryId, cap)
                         ?? $"no fishing hole is known for item {itemId}";
            Transition(FishingRunState.Idle, $"Cannot fish: {reason}.");
            return false;
        }

        if (!bridge.HasGearsetForJob(GatheringActions.FisherJobId))
        {
            Transition(FishingRunState.Idle, "Cannot fish: there is no FSH gearset.");
            return false;
        }

        if (bridge.GetItemCount(chosen.BaitItemId) < 1)
        {
            Transition(FishingRunState.Idle, $"Cannot fish: no {chosen.BaitName} in the bag.");
            return false;
        }

        plan = chosen;
        targetItemId = itemId;
        targetAmount = amount;
        baselineCount = bridge.GetItemCount(itemId);
        casts = 0;
        waterProbe = 0;
        rodAwayTries = 0;
        castsWithoutTarget = 0;
        caughtAtLastCheck = 0;
        autoHookEngaged = false;
        gearsetRequested = false;
        baitRequested = false;
        sawLoadingScreen = false;
        zoneArrivedAt = DateTime.MinValue;
        lastCatchAt = DateTime.MinValue;
        lineOutSince = DateTime.MaxValue;
        castBlockedSince = DateTime.MaxValue;
        blindSince = DateTime.MaxValue;
        lastPhase = FishingPhase.None;
        pending = null;
        lastDecision = "-";
        FailureReason = "";

        if (chosen.TimeRestricted || chosen.WeatherRestricted)
        {
            // FishingNoteInfo only flags *that* a fish is restricted; the sheets
            // carry no window, so the run cannot be scheduled around it (7.15).
            Log.Warning(
                $"[Fishing] {chosen.FishName} is {(chosen.TimeRestricted ? "time" : "")}" +
                $"{(chosen.TimeRestricted && chosen.WeatherRestricted ? "- and " : "")}" +
                $"{(chosen.WeatherRestricted ? "weather" : "")}-restricted; the game sheets do not say when, " +
                "so the run just fishes and may come up empty outside the window.");
        }

        EnterPhase(FishingRunState.PreparingJob, $"Fishing for {chosen.Describe(zoneName)} ×{amount}.");
        return true;
    }

    public bool IsBusy => State is FishingRunState.PreparingJob or FishingRunState.Teleporting
        or FishingRunState.Traveling or FishingRunState.ApplyingBait or FishingRunState.Fishing
        or FishingRunState.Quitting;

    public void Pause(string reason)
    {
        if (!IsBusy)
            return;

        travel.Stop();
        navigation.Stop();
        DisengageAutoHook();
        pending = null;
        FailureReason = reason;
        Transition(FishingRunState.Paused, $"Paused: {reason}.");
    }

    /// <summary>
    /// Put a rod left out by an earlier run away (a pause, then a reload,
    /// 2026-09-15): while the character stands in the fishing stance every
    /// teleport and gearset command is "unable to execute". True when the
    /// rod is away; false while Quit is still being asked for.
    /// </summary>
    public bool PutRodAway()
    {
        // The stance itself only raises Gathering (Fishing is the line in the
        // water, 2026-09-15), so both count; a bounded number of Quits, in
        // case Gathering is something else the character stands in.
        if (!(bridge.IsFishing || bridge.IsGathering) || rodAwayTries >= RodAwayTries)
            return true;

        if (retry.Try(() => bridge.ExecuteCraftAction(catalog.Id(FishAction.Quit))))
        {
            rodAwayTries++;
            Log.Information($"[Fishing] A rod is still out; putting it away ({rodAwayTries}/{RodAwayTries}).");
        }

        return false;
    }

    private const int RodAwayTries = 4;
    private int rodAwayTries;

    public void Resume()
    {
        if (State != FishingRunState.Paused || plan == null)
            return;

        // A pause freezes nothing in the game, so every deadline and counter
        // the pause outlived has to start over — otherwise whatever caused the
        // pause fires again on the first tick and the run cannot be resumed at
        // all. The fruitless-cast count goes too: "set the bait by hand and
        // resume" has to mean something.
        pending = null;
        lineOutSince = DateTime.MaxValue;
        castBlockedSince = DateTime.MaxValue;
        blindSince = DateTime.MaxValue;
        lastCatchAt = DateTime.MinValue;
        lastHousekeepingAt = DateTime.MinValue;
        castsWithoutTarget = 0;

        // Off the job (the user swapped gearsets while paused): start again at
        // the gearset rather than walking to the water as a crafter.
        if (bridge.CurrentClassJobId != GatheringActions.FisherJobId)
        {
            gearsetRequested = false;
            EnterPhase(FishingRunState.PreparingJob, "Resuming: back onto FSH first.");
        }
        else if (bridge.IsFishing)
        {
            EnterPhase(FishingRunState.Fishing, "Resuming at the rod.");
        }
        else if (bridge.CurrentTerritoryId != plan.Spot.TerritoryId)
        {
            EnterPhase(FishingRunState.Teleporting, "Resuming: back to the fishing hole's zone.");
        }
        else
        {
            StartTravel("Resuming the approach.");
        }
    }

    public void Stop()
    {
        travel.Stop();
        navigation.Stop();
        DisengageAutoHook();
        if (bridge.IsFishing)
            bridge.ExecuteCraftAction(catalog.Id(FishAction.Quit));

        pending = null;
        if (State is not (FishingRunState.Idle or FishingRunState.Completed or FishingRunState.Failed))
            Transition(FishingRunState.Idle, $"Stopped by user at {Caught}/{targetAmount}.");
    }

    protected override void OnTick()
    {
        switch (State)
        {
            case FishingRunState.PreparingJob:
                TickPreparingJob();
                break;
            case FishingRunState.Teleporting:
                TickTeleporting();
                break;
            case FishingRunState.Traveling:
                TickTraveling();
                break;
            case FishingRunState.ApplyingBait:
                TickApplyingBait();
                break;
            case FishingRunState.Fishing:
                TickFishing();
                break;
            case FishingRunState.Quitting:
                TickQuitting();
                break;
        }
    }

    // ------------------------------------------------------------ preparation

    private void TickPreparingJob()
    {
        if (Clock.UtcNow - phaseStartedAt > JobTimeout)
        {
            Fail("could not switch to the FSH gearset");
            return;
        }

        if (bridge.IsCrafting)
            return;

        // A rod still out from an earlier run blocks the gearset and teleport.
        if (!PutRodAway())
        {
            StatusText = "Putting the rod away first.";
            return;
        }

        // A shop window left up (the bait purchase just before, 2026-09-15)
        // makes every gearset command "unable to execute while occupied".
        if (bridge.IsAddonVisible("Shop"))
        {
            retry.Try(bridge.CloseShop);
            StatusText = "Closing the shop before switching to FSH.";
            phaseStartedAt = Clock.UtcNow; // the job budget starts once the window is gone
            return;
        }

        if (bridge.CurrentClassJobId == GatheringActions.FisherJobId)
        {
            // Settle after a job change before the next server-visible action.
            if (gearsetRequested && Clock.UtcNow - phaseStartedAt < Pacing.AfterJobChange)
                return;

            GoToSpot();
            return;
        }

        retry.Try(() =>
        {
            gearsetRequested = true;
            if (!bridge.EquipGearsetForJob(GatheringActions.FisherJobId))
                Fail("there is no FSH gearset to equip");
        });
    }

    private void GoToSpot()
    {
        if (plan == null)
        {
            Fail("the fishing plan vanished");
            return;
        }

        if (bridge.CurrentTerritoryId != plan.Spot.TerritoryId)
        {
            sawLoadingScreen = false;
            zoneArrivedAt = DateTime.MinValue;
            EnterPhase(
                FishingRunState.Teleporting,
                $"Teleporting to {zoneName(plan.Spot.TerritoryId)} for {plan.Spot.Name}.");
            return;
        }

        StartTravel($"Heading for {plan.Spot.Name}.");
    }

    /// <summary>Same shape as the production runner's teleport phase: request, loading screen, settle.</summary>
    private void TickTeleporting()
    {
        if (plan == null)
        {
            Fail("the fishing plan vanished");
            return;
        }

        if (Clock.UtcNow - phaseStartedAt > TeleportTimeout)
        {
            Fail("teleport did not complete (cast interrupted or loading took too long)");
            return;
        }

        if (bridge.IsBetweenAreas)
        {
            sawLoadingScreen = true;
            zoneArrivedAt = DateTime.MinValue;
            return;
        }

        if (sawLoadingScreen && bridge.CurrentTerritoryId == plan.Spot.TerritoryId && bridge.GetPlayerState() != null)
        {
            if (zoneArrivedAt == DateTime.MinValue)
                zoneArrivedAt = Clock.UtcNow;
            if (Clock.UtcNow - zoneArrivedAt < Pacing.AfterZoneChange)
                return;

            StartTravel($"Arrived; heading for {plan.Spot.Name}.");
            return;
        }

        retry.Try(() =>
        {
            if (!bridge.TeleportToTerritory(plan.Spot.TerritoryId))
            {
                Fail($"no attuned aetheryte in {zoneName(plan.Spot.TerritoryId)}");
            }
        });
    }

    private void StartTravel(string statusText)
    {
        var fly = capabilities().CanFlyIn(bridge.CurrentTerritoryId);
        // The sheet gives the hole's X/Z only (Y comes out 0): a point off the
        // mesh never gets a path (Central Shroud, 2026-09-15), so ask vnavmesh
        // for the floor under it first; the raw point stays the fallback.
        var destination = navigation.FindPointOnFloor(plan!.Spot.Position, 25f) ?? plan.Spot.Position;
        travel.Start(destination, ArriveWithin, fly, preciseArrival: false, TravelTimeout, plan.Spot.Name);
        EnterPhase(FishingRunState.Traveling, statusText);
    }

    private const int WaterProbeCount = 16; // two rings of eight points, 12 y and 24 y out

    /// <summary>Walk to the next point of the ring around the marker; the arrival re-checks the game's CanFish.</summary>
    private void StartWaterProbe()
    {
        var index = waterProbe++;
        var radius = index < 8 ? 12f : 24f;
        var angle = index % 8 * (MathF.PI / 4f);
        var center = plan!.Spot.Position;
        var raw = center + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
        var point = navigation.FindPointOnFloor(raw, 6f) ?? raw;
        castBlockedSince = DateTime.MaxValue;
        Log.Information($"[Fishing] No water in casting range here; trying {radius:F0}y at {angle * 180f / MathF.PI:F0}° from the marker ({waterProbe}/{WaterProbeCount}).");
        travel.Start(point, 2f, fly: false, preciseArrival: false, TravelTimeout, "the water's edge");
        EnterPhase(FishingRunState.Traveling, "Looking for the water's edge.");
    }

    private void TickTraveling()
    {
        travel.Tick();
        switch (travel.State)
        {
            case TravelState.Arrived:
                EnterPhase(FishingRunState.ApplyingBait, $"At {plan!.Spot.Name}; putting on {plan.BaitName}.");
                break;
            case TravelState.Failed:
                Fail(travel.FailureReason);
                break;
            default:
                StatusText = travel.StatusText;
                break;
        }
    }

    private void TickApplyingBait()
    {
        if (plan == null)
        {
            Fail("the fishing plan vanished");
            return;
        }

        // Mooching starts from a base fish, so the bait to apply is always the
        // plan's bait; the mooch itself replaces a cast later on.
        var snapshot = bridge.GetFishingState();
        var applied = snapshot?.BaitItemId == plan.BaitItemId;

        // The game only builds the fishing event handler once the character has
        // fished this session, so before the first cast there is nothing to read
        // the applied bait back from. The bait request went through the
        // inventory instead; after the timeout, cast and find out rather than
        // refusing.
        var unverifiable = snapshot == null && baitRequested && Clock.UtcNow - phaseStartedAt > BaitTimeout;
        if (unverifiable)
        {
            Log.Warning(
                $"[Fishing] The game is not reporting a fishing state yet, so {plan.BaitName} could not be " +
                "confirmed; casting anyway and watching the catch.");
        }

        if (applied || unverifiable)
        {
            // A mount blocks the rod. Every phase here is on a timeout, so a
            // character that cannot dismount fails instead of waiting forever.
            if (bridge.IsMounted)
            {
                if (Clock.UtcNow - phaseStartedAt > BaitTimeout + Pacing.AfterZoneChange)
                    Fail("could not dismount to fish");
                else
                    retry.Try(bridge.TryDismount);

                return;
            }

            StartFishing();
            return;
        }

        if (Clock.UtcNow - phaseStartedAt > BaitTimeout)
        {
            Fail($"{plan.BaitName} could not be applied (is any left in the bag?)");
            return;
        }

        retry.Try(() =>
        {
            baitRequested = true;
            if (!bridge.SelectBait(plan.BaitItemId))
                Log.Information($"[Fishing] The bait request for {plan.BaitName} was refused; trying again.");
        });
    }

    private void StartFishing()
    {
        EngageAutoHook();
        EnterPhase(FishingRunState.Fishing, $"{plan!.BaitName} on; fishing for {plan.FishName}.");
    }

    // --------------------------------------------------------------- fishing

    private void TickFishing()
    {
        if (plan == null)
        {
            Fail("the fishing plan vanished");
            return;
        }

        if (!Housekeeping())
            return;

        var snapshot = bridge.GetFishingState();
        var phase = snapshot?.Phase ?? FishingPhase.None;

        // With the rod out the game must be reporting a fishing state; if it
        // never does, every decision here would be blind guessing — stop rather
        // than cast into the dark forever.
        if (snapshot == null && bridge.IsFishing)
        {
            if (blindSince == DateTime.MaxValue)
                blindSince = Clock.UtcNow;
            else if (Clock.UtcNow - blindSince > BlindTimeout)
            {
                Pause("the game is not reporting a fishing state (the fishing event handler never came up)");
                return;
            }

            StatusText = ProgressText("Waiting for the game's fishing state");
            return;
        }

        blindSince = DateTime.MaxValue;

        if (ResolvePending(phase))
            return;

        // A catch (or a fish that slipped) shows as the line coming back in and
        // the pole returning to ready. Count what actually landed in the bag.
        if (phase != lastPhase)
        {
            // Anything that ends with the pole ready again closes a cast —
            // including the collectable confirmation and a tick that skipped
            // over the reeling-in phases. Missing one would leave the
            // line-out clock running across casts and trip a bogus "no bite".
            if (IsLineOut(lastPhase) && phase is FishingPhase.PoleReady or FishingPhase.None)
            {
                lastCatchAt = Clock.UtcNow;
                lineOutSince = DateTime.MaxValue;
            }

            lastPhase = phase;
            phaseChangedAt = Clock.UtcNow;
        }

        switch (phase)
        {
            case FishingPhase.Bite:
                TickBite(snapshot!);
                return;

            case FishingPhase.CastingOut:
            case FishingPhase.LineInWater:
                if (lineOutSince == DateTime.MaxValue)
                {
                    lineOutSince = Clock.UtcNow;
                }
                else if (Clock.UtcNow - lineOutSince > BiteTimeout)
                {
                    Pause($"no bite in {BiteTimeout.TotalSeconds:F0}s at {plan.Spot.Name}");
                    return;
                }

                StatusText = ProgressText("Line in the water");
                return;

            case FishingPhase.Hooking:
            case FishingPhase.PullingPoleIn:
            case FishingPhase.ReleasingCatch:
            case FishingPhase.ConfirmingCollectable:
            case FishingPhase.Lure:
            case FishingPhase.Quitting:
                StatusText = ProgressText("Reeling in");
                return;

            case FishingPhase.PoleReady:
            case FishingPhase.None:
            case FishingPhase.Unknown:
            default:
                TickReadyToCast(snapshot, phase);
                return;
        }
    }

    /// <summary>A phase with the line (or the rod) out: everything between a cast and the pole being ready again.</summary>
    private static bool IsLineOut(FishingPhase phase) => phase is FishingPhase.CastingOut or FishingPhase.LineInWater
        or FishingPhase.Bite or FishingPhase.Hooking or FishingPhase.PullingPoleIn
        or FishingPhase.ReleasingCatch or FishingPhase.ConfirmingCollectable or FishingPhase.Lure;

    /// <summary>
    /// Bag, rod, target and GP checks, once a second (each polls the game).
    /// False when the caller should stop this tick.
    /// </summary>
    private bool Housekeeping()
    {
        if (Clock.UtcNow - lastHousekeepingAt < HousekeepingInterval)
            return true;

        lastHousekeepingAt = Clock.UtcNow;

        var caught = Caught;
        if (caught > caughtAtLastCheck)
        {
            var gained = caught - caughtAtLastCheck;
            caughtAtLastCheck = caught;
            castsWithoutTarget = 0;
            Log.Information($"[Fishing] Caught {gained}× {plan!.FishName} ({caught}/{targetAmount}, {casts} casts).");
        }

        if (caught >= targetAmount)
        {
            EnterPhase(FishingRunState.Quitting, $"{caught}/{targetAmount} caught; putting the rod away.");
            return false;
        }

        if (bridge.CurrentClassJobId != GatheringActions.FisherJobId)
        {
            Pause("the character is no longer on FSH");
            return false;
        }

        if (bridge.GetFreeInventorySlots() < 1)
        {
            Pause("inventory is full");
            return false;
        }

        if (bridge.GetMainHandConditionPercent() <= 0f)
        {
            Pause("the fishing rod is broken");
            return false;
        }

        if (castsWithoutTarget >= MaxCastsWithoutTarget)
        {
            Pause(
                $"{castsWithoutTarget} casts with {plan!.BaitName} at {plan.Spot.Name} brought no {plan.FishName} — " +
                "the bundled bait table may be wrong for this fish; set the bait by hand and resume");
            return false;
        }

        TryCordial();
        return true;
    }

    private void TickBite(FishingSnapshot snapshot)
    {
        // AutoHook, when the user prefers it and it is there, owns the bite: it
        // knows the tug, which no struct in the installed ClientStructs carries.
        // If it has not hooked within the grace window, hook it ourselves.
        if (autoHookEngaged && Clock.UtcNow - phaseChangedAt < AutoHookGrace)
        {
            StatusText = ProgressText("Bite — AutoHook is on it");
            return;
        }

        // A bite lasts a few seconds, so the two-second retry gate the other
        // phases use would throw the fish back: hooking gets its own short one,
        // and a hookset the game refuses falls straight through to plain Hook
        // instead of waiting for the next slot.
        if (!hookGate.IsReady)
            return;

        hookGate.Touch();
        var action = HooksetFor(snapshot.Tug);
        if (!Fire(action, FishingPhase.Bite) && action != FishAction.Hook)
            Fire(FishAction.Hook, FishingPhase.Bite);
    }

    /// <summary>
    /// Which hookset the tug asks for (roadmap 7.4): Precision for the light
    /// "!" tug, Powerful for "!!"/"!!!", plain Hook when the tug is unknown —
    /// which it is unless a tug source supplies one. Plain Hook lands the fish
    /// without Patience, so an unknown tug costs nothing but the GP saving.
    /// </summary>
    private FishAction HooksetFor(FishingTug tug)
    {
        var level = capabilities().LevelOf(GatheringActions.FisherJobId);
        var action = tug switch
        {
            FishingTug.Light => FishAction.PrecisionHookset,
            FishingTug.Strong or FishingTug.Legendary => FishAction.PowerfulHookset,
            _ => FishAction.Hook,
        };

        if (action == FishAction.Hook)
            return action;

        // Never fire an action the character cannot have, and never spend GP
        // that is not there.
        var gp = bridge.GetPlayerState()?.CurrentGp ?? 0;
        return level > 0 && level < catalog.Level(action) ? FishAction.Hook
            : gp < catalog.GpCost(action) ? FishAction.Hook
            : action;
    }

    private void TickReadyToCast(FishingSnapshot? snapshot, FishingPhase phase)
    {
        if (plan == null)
            return;

        // Settle after a catch before the next cast (pacing), and after arriving
        // before the first one.
        var since = lastCatchAt == DateTime.MinValue ? phaseStartedAt : lastCatchAt;
        var settle = lastCatchAt == DateTime.MinValue ? BeforeFirstCast : AfterCatch;
        if (Clock.UtcNow - since < settle)
        {
            StatusText = ProgressText("Settling before the next cast");
            return;
        }

        // The game's own "you are at a fishing hole": when it says no, the cast
        // will be refused however many times we try.
        if (snapshot is { CanFish: false })
        {
            if (castBlockedSince == DateTime.MaxValue)
            {
                castBlockedSince = Clock.UtcNow;
            }
            else if (Clock.UtcNow - castBlockedSince > CastBlockedTimeout)
            {
                // The sheet's marker sits near the hole, not on its bank (The
                // Vein, 2026-09-15): try a ring of points around it before
                // asking the user to walk.
                if (waterProbe < WaterProbeCount)
                {
                    StartWaterProbe();
                    return;
                }

                Pause($"there is no water in casting range at {plan.Spot.Name} — walk to the water's edge and resume");
                return;
            }

            StatusText = ProgressText("Looking for water");
            return;
        }

        // A mooch is a free cast with the catch still on the line; take it when
        // the target needs one and the game says the last catch can be mooched.
        if (plan.NeedsMooch && snapshot is { CanMooch: true }
            && capabilities().LevelOf(GatheringActions.FisherJobId) is 0 or >= 25)
        {
            // A mooch is an attempt at the target like any cast, so it counts
            // toward the fruitless-cast safety net.
            retry.Try(() =>
            {
                if (Fire(FishAction.Mooch, phase))
                {
                    casts++;
                    castsWithoutTarget++;
                }
            });
            return;
        }

        var castId = catalog.Id(FishAction.Cast);
        if (!bridge.IsCraftActionReady(castId))
        {
            if (castBlockedSince == DateTime.MaxValue)
            {
                castBlockedSince = Clock.UtcNow;
            }
            else if (Clock.UtcNow - castBlockedSince > CastBlockedTimeout)
            {
                Pause($"Cast stayed unavailable at {plan.Spot.Name} — walk to the water's edge and resume");
                return;
            }

            StatusText = ProgressText("Waiting for Cast to become available");
            return;
        }

        castBlockedSince = DateTime.MaxValue;
        retry.Try(() =>
        {
            if (Fire(FishAction.Cast, phase))
            {
                casts++;
                castsWithoutTarget++;
            }
        });
    }

    /// <summary>
    /// One action per decision (roadmap 7.4): fire it and remember what the
    /// game state looked like, so the next ticks can confirm it landed instead
    /// of firing again.
    /// </summary>
    private bool Fire(FishAction action, FishingPhase phaseBefore)
    {
        var id = catalog.Id(action);
        if (!bridge.IsCraftActionReady(id) || !bridge.ExecuteCraftAction(id))
            return false;

        pending = (action, id, phaseBefore, Clock.UtcNow);
        lastDecision = $"{action} (action {id})";
        Log.Information($"[Fishing] {action} — action {id} ({Caught}/{targetAmount} {plan?.FishName} so far, {casts} casts).");
        return true;
    }

    /// <summary>
    /// True while an action is still in flight. An action the game swallowed
    /// (no state change within the timeout) is retried, not repeated blindly:
    /// the retry throttle still gates how often that can happen.
    /// </summary>
    private bool ResolvePending(FishingPhase phase)
    {
        if (pending is not { } inFlight)
            return false;

        if (phase != inFlight.PhaseBefore)
        {
            pending = null;
            return false;
        }

        if (Clock.UtcNow - inFlight.At > ActionTimeout)
        {
            pending = null;
            Log.Warning($"[Fishing] {inFlight.Action} (action {inFlight.ActionId}) did not change the fishing state; trying again.");
        }

        return pending != null;
    }

    private void TickQuitting()
    {
        DisengageAutoHook();

        if (!bridge.IsFishing)
        {
            Transition(FishingRunState.Completed, $"Completed: {Caught}/{targetAmount} {plan?.FishName} in {casts} casts.");
            return;
        }

        if (Clock.UtcNow - phaseStartedAt > QuitTimeout)
        {
            // The catch is in the bag either way; a rod left out is not a failure.
            Log.Warning("[Fishing] Quit did not take; finishing with the rod still out.");
            Transition(FishingRunState.Completed, $"Completed: {Caught}/{targetAmount} {plan?.FishName} in {casts} casts.");
            return;
        }

        retry.Try(() => bridge.ExecuteCraftAction(catalog.Id(FishAction.Quit)));
        StatusText = ProgressText("Putting the rod away");
    }

    // ------------------------------------------------------------ GP, AutoHook

    /// <summary>
    /// Cordials between casts (roadmap 7.14 rules, 7.4 use): only when a
    /// hookset the character can use would otherwise be unaffordable. Fishing
    /// spends far less GP than gathering, so this rarely fires.
    /// </summary>
    private void TryCordial()
    {
        if (!configuration.UseCordials || Clock.UtcNow - lastCordialAt < TimeSpan.FromSeconds(10))
            return;

        var player = bridge.GetPlayerState();
        if (player == null || player.MaxGp == 0)
            return;

        var level = capabilities().LevelOf(GatheringActions.FisherJobId);
        if (level > 0 && level < catalog.Level(FishAction.PowerfulHookset))
            return;

        var wanted = catalog.GpCost(FishAction.PowerfulHookset) * 4;
        if (player.CurrentGp >= wanted)
            return;

        foreach (var cordial in cordials)
        {
            if (bridge.GetItemCount(cordial.ItemId) == 0 || bridge.IsItemOnCooldown(cordial.ItemId))
                continue;

            // HQ consumables are addressed as item id + 1,000,000.
            var hq = cordial.CanBeHq && bridge.GetHqItemCount(cordial.ItemId) > 0;
            if (player.CurrentGp + cordial.Gp(hq) > player.MaxGp)
                continue;

            if (bridge.UseItem(hq ? cordial.ItemId + 1_000_000 : cordial.ItemId))
            {
                lastCordialAt = Clock.UtcNow;
                Log.Information(
                    $"[Fishing] Drinking {cordial.Name}{(hq ? " HQ" : "")} (+{cordial.Gp(hq)} GP); GP {player.CurrentGp}/{player.MaxGp}.");
                return;
            }
        }

        lastCordialAt = Clock.UtcNow;
    }

    private void EngageAutoHook()
    {
        if (autoHookEngaged || autoHook == null || !configuration.FishingPreferAutoHook)
            return;

        // Checked now, not at construction: the user can enable or disable
        // AutoHook while CielCraft is loaded.
        if (!autoHook.IsAvailable)
            return;

        autoHookEngaged = autoHook.SetEnabled(true);
        Log.Information(autoHookEngaged
            ? "[Fishing] AutoHook is installed and FishingPreferAutoHook is on; it decides the bite timing this run."
            : "[Fishing] AutoHook is installed but did not answer its IPC; hooking here instead.");
    }

    private void DisengageAutoHook()
    {
        if (!autoHookEngaged || autoHook == null)
            return;

        autoHookEngaged = false;
        autoHook.SetEnabled(false);
        Log.Information("[Fishing] AutoHook handed back.");
    }

    // ---------------------------------------------------------------- plumbing

    private string ProgressText(string what) =>
        $"{what}: {Caught}/{targetAmount} {plan?.FishName ?? "fish"} after {casts} casts.";

    private void EnterPhase(FishingRunState state, string statusText)
    {
        phaseStartedAt = Clock.UtcNow;
        retry.Reset();
        Transition(state, statusText);
    }

    private void Fail(string reason)
    {
        travel.Stop();
        navigation.Stop();
        DisengageAutoHook();
        pending = null;
        FailureReason = reason;
        Transition(FishingRunState.Failed, $"Failed: {reason}.");
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        foreach (var line in base.Describe())
            yield return line;

        yield return plan == null
            ? "Plan: none"
            : $"Plan: {plan.Describe(zoneName)}; spot #{plan.Spot.SpotId} at " +
              $"{plan.Spot.Position.X:F0}, {plan.Spot.Position.Z:F0}; time-restricted {plan.TimeRestricted}; weather-restricted {plan.WeatherRestricted}";
        yield return $"Target item {targetItemId} ×{targetAmount}: caught {Caught} (baseline {baselineCount}); casts {casts}; " +
                     $"casts without the target {castsWithoutTarget}/{MaxCastsWithoutTarget}";
        yield return $"Phase since {phaseStartedAt:HH:mm:ss}Z; last phase {lastPhase}; last decision {lastDecision}; " +
                     $"pending {(pending is { } p ? $"{p.Action} #{p.ActionId} since {p.At:HH:mm:ss}Z" : "-")}";
        yield return $"Line out since {(lineOutSince == DateTime.MaxValue ? "-" : lineOutSince.ToString("HH:mm:ss") + "Z")}; " +
                     $"cast blocked since {(castBlockedSince == DateTime.MaxValue ? "-" : castBlockedSince.ToString("HH:mm:ss") + "Z")}; " +
                     $"AutoHook engaged {autoHookEngaged}";
        foreach (var line in catalog.Describe())
            yield return line;
        foreach (var line in travel.Describe())
            yield return line;
        if (autoHook != null)
        {
            foreach (var line in autoHook.Describe())
                yield return line;
        }
    }
}
