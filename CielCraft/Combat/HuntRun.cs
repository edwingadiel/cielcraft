using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Combat;

/// <summary>
/// One monster the object table is showing right now (roadmap 7.5). Package B
/// reads these off <c>ObjectTable</c> as <c>HuntTargetSnapshot</c>; the
/// coordinator's adapter maps them onto this record, so the hunt run and its
/// tests never depend on Dalamud types.
/// </summary>
public sealed record HuntTarget(
    ulong ObjectId,
    uint BNpcNameId,
    string Name,
    int Level,
    Vector3 Position,
    float Distance,
    float HpPercent,
    bool TargetedByOthers,
    bool IsAlive);

/// <summary>
/// The slice of the game a hunt needs (roadmap 7.5). Package B adds the
/// combat members to <c>IGameBridge</c>; the coordinator satisfies this
/// interface with a thin adapter over the real bridge (see the package A
/// report), which keeps the run testable against one small fake.
/// </summary>
public interface IHuntBridge : ITravelBridge
{
    uint CurrentTerritoryId { get; }

    bool IsBetweenAreas { get; }

    bool CanTeleportTo(uint territoryId);

    bool TeleportToTerritory(uint territoryId);

    bool EquipGearsetForJob(uint classJobId);

    bool HasGearsetForJob(uint classJobId);

    uint CurrentClassJobId { get; }

    int GetItemCount(uint itemId);

    /// <summary>Living monsters of that BNpcName near the player, nearest to <paramref name="origin"/> first.</summary>
    IReadOnlyList<HuntTarget> FindHuntTargets(uint bnpcNameId, IReadOnlyCollection<ulong>? excluded = null, Vector3? origin = null);

    /// <summary>Makes the object the current target. False when it is gone.</summary>
    bool TargetObject(ulong objectId);

    ulong CurrentTargetId { get; }

    /// <summary>The player's HP as a percentage (0..100); 100 when unknown.</summary>
    float PlayerHpPercent { get; }

    bool IsInCombat { get; }

    bool IsDead { get; }

    /// <summary>Answers the death prompt (Return to the aetheryte). False when no prompt is up.</summary>
    bool AnswerReturnPrompt();

    /// <summary>How many enemies have the player targeted; more than the one being fought means adds.</summary>
    int EnemiesTargetingMe();
}

/// <summary>
/// Which ClassJob rows can fight and how close they need to be (roadmap 7.5).
/// The ranges are the game's own melee / ranged attack ranges, not a guess at
/// a rotation: the hunt only has to be close enough for the combat plugin to
/// start, and the plugin does its own positioning from there.
/// </summary>
public static class CombatJobs
{
    /// <summary>Melee and tank jobs plus their base classes (ClassJob rows).</summary>
    private static readonly HashSet<uint> Melee =
        [1, 2, 3, 4, 19, 20, 21, 22, 29, 30, 32, 34, 37, 39, 41];

    /// <summary>Physical ranged and casters (ClassJob rows).</summary>
    private static readonly HashSet<uint> Ranged =
        [5, 6, 7, 23, 24, 25, 26, 27, 28, 31, 33, 35, 36, 38, 40, 42];

    /// <summary>Close enough for a melee opener.</summary>
    public const float MeleeRange = 3f;

    /// <summary>Close enough for a ranged opener; the game's own cast range is 25y.</summary>
    public const float RangedRange = 20f;

    public static bool IsCombatJob(uint classJobId) => Melee.Contains(classJobId) || Ranged.Contains(classJobId);

    /// <summary>Every combat ClassJob row, lowest first; used to pick the best job with a gearset.</summary>
    public static IEnumerable<uint> All => Melee.Concat(Ranged).OrderBy(id => id);

    public static float AttackRange(uint classJobId) => Melee.Contains(classJobId) ? MeleeRange : RangedRange;
}

/// <summary>
/// The coarse grid a hunt walks when no spot is remembered (roadmap 7.5).
/// Pure, so the ordering is testable: points inside the zone's map box,
/// spaced by <paramref name="step"/>, nearest to the starting position first.
/// </summary>
public static class HuntSweep
{
    public static IReadOnlyList<Vector3> Points(MapBounds bounds, Vector3 origin, float step, int max)
    {
        if (step <= 0 || max <= 0)
            return [];

        var points = new List<Vector3>();
        for (var x = bounds.MinX + (step / 2f); x < bounds.MaxX; x += step)
        {
            for (var z = bounds.MinZ + (step / 2f); z < bounds.MaxZ; z += step)
                points.Add(new Vector3(x, origin.Y, z));
        }

        return points
            .OrderBy(p => Vector3.DistanceSquared(p, origin))
            .Take(max)
            .ToList();
    }
}

/// <summary>The phases of one hunt (roadmap 7.5).</summary>
public enum HuntRunState
{
    Idle,

    /// <summary>Switching to the combat job's gearset.</summary>
    Equipping,

    Teleporting,

    /// <summary>Riding to a remembered spot.</summary>
    TravelingToSpot,

    /// <summary>Walking the zone's grid until the object table shows the monster.</summary>
    Sweeping,

    /// <summary>Choosing which monster to fight.</summary>
    Targeting,

    /// <summary>Closing to the job's attack range.</summary>
    Approaching,

    /// <summary>The combat driver is engaged; waiting for the kill.</summary>
    Fighting,

    /// <summary>Settling after a kill before the next target.</summary>
    AfterKill,

    /// <summary>Below the HP threshold: disengaged and heading back to the last safe point.</summary>
    Retreating,

    /// <summary>Dead: answering the Return prompt and waiting for the revive.</summary>
    Recovering,

    Completed,
    Failed,
    Paused,
}

/// <summary>
/// Fights monsters for a material no other source supplies (roadmap 7.5).
/// CielCraft never casts a combat action: it travels, picks the target, hands
/// the fight to the combat plugin through <see cref="ICombatDriver"/> and
/// takes it back when the monster is down. The bag count is the truth — a
/// kill only counts when the item actually arrives (spec §34).
///
/// Safety rules, in the order they are checked every tick: death, then the
/// retreat threshold, then the fight itself. Pause, Resume and Stop always
/// disengage first, so a stopped run never leaves the rotation running.
/// </summary>
public sealed class HuntRun : AutomationMachine<HuntRunState>, ISourceRun
{
    /// <summary>One fight may take this long before the target is written off.</summary>
    public static readonly TimeSpan FightTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Gearset, teleport and dialog phases.</summary>
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A leg of travel — a ride across a zone or one sweep hop.</summary>
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Waiting for a revive after a death.</summary>
    private static readonly TimeSpan ReviveTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>Breathing room after a kill before the next pull (pacing; never remove).</summary>
    private static readonly TimeSpan AfterKillSettle = TimeSpan.FromSeconds(2.5);

    /// <summary>How far apart the sweep's grid points are, and how many of them a run will try.</summary>
    public const float SweepStep = 120f;
    public const int SweepPoints = 20;

    /// <summary>A retreat waits until HP is back to this before hunting again.</summary>
    public const int RecoveredHpPercent = 85;

    /// <summary>Consecutive targets that went wrong before the run gives up.</summary>
    public const int MaxTargetFailures = 5;

    private readonly IHuntBridge bridge;
    private readonly ICombatDriver driver;
    private readonly CombatDatabase? database;
    private readonly INavigationProvider navigation;
    private readonly AutomationSettings settings;
    private readonly SourceOffer offer;
    private readonly MobDrop? drop;
    private readonly uint jobId;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly Func<uint, string> itemName;
    private readonly TravelDriver travel;
    private readonly Throttle attempts;

    private readonly int startCount;
    private readonly HashSet<ulong> skipped = [];

    private DateTime phaseStartedAt;
    private DateTime settleUntil;
    private DateTime fightStartedAt;
    private Vector3 safePoint;
    private ulong targetId;
    private string targetName = "";
    private int killCount;
    private int targetFailures;
    private int deaths;
    private bool spotRemembered;
    private int teleportAttempts;
    private int sweepIndex;
    private IReadOnlyList<Vector3> sweep = [];
    private HuntRunState resumeTo = HuntRunState.Targeting;
    private string failureReason = "";

    public HuntRun(
        IHuntBridge bridge,
        ICombatDriver driver,
        INavigationProvider navigation,
        AutomationSettings settings,
        SourceOffer offer,
        MobDrop? drop,
        uint jobId,
        ILog log,
        IClock clock,
        CombatDatabase? database = null,
        Func<CharacterCapabilities>? capabilities = null,
        Func<uint, string>? itemName = null)
        : base(log, clock, "[Hunt]", HuntRunState.Idle, "Idle.")
    {
        this.bridge = bridge;
        this.driver = driver;
        this.navigation = navigation;
        this.settings = settings;
        this.offer = offer;
        this.drop = drop;
        this.jobId = jobId;
        this.database = database;
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
        this.itemName = itemName ?? (id => $"item {id}");
        travel = new TravelDriver(navigation, bridge, clock, log, "[Hunt]");
        attempts = new Throttle(clock, RetryInterval);
        startCount = bridge.GetItemCount(offer.ItemId);
        phaseStartedAt = clock.UtcNow;
        safePoint = bridge.PlayerPosition ?? Vector3.Zero;
        Begin();
    }

    /// <summary>Drops in the bag since the run began (spec §34: the bag count is the truth).</summary>
    public int Obtained => Math.Max(0, bridge.GetItemCount(offer.ItemId) - startCount);

    /// <summary>Monsters killed this run, whether or not they dropped anything.</summary>
    public int Kills => killCount;

    SourceRunState ISourceRun.State => State switch
    {
        HuntRunState.Completed => SourceRunState.Completed,
        HuntRunState.Failed => SourceRunState.Failed,
        HuntRunState.Paused => SourceRunState.Paused,
        HuntRunState.Idle => SourceRunState.Idle,
        _ => SourceRunState.Running,
    };

    public void Pause(string reason)
    {
        if (State is HuntRunState.Paused or HuntRunState.Completed or HuntRunState.Failed)
            return;

        // A paused run must never leave the rotation swinging.
        Disengage();
        travel.Stop();
        resumeTo = State is HuntRunState.Fighting or HuntRunState.Approaching or HuntRunState.AfterKill
            ? HuntRunState.Targeting
            : State;
        Transition(HuntRunState.Paused, $"Paused: {reason}");
    }

    public void Resume()
    {
        if (State != HuntRunState.Paused)
            return;

        // Whatever was engaged while the run was parked is not this run's
        // fight: start the next target from a clean rotation.
        Disengage();
        attempts.Reset();
        Enter(resumeTo, $"Resumed: {PhaseText(resumeTo)}.");
    }

    public void Stop()
    {
        if (State is HuntRunState.Completed or HuntRunState.Failed)
            return;

        Disengage();
        travel.Stop();
        navigation.Stop();
        Transition(HuntRunState.Idle, $"Stopped after {Obtained}/{offer.Amount} {itemName(offer.ItemId)} ({killCount} kills).");
    }

    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} - {StatusText}";
        yield return drop == null
            ? $"Monster: none known for {itemName(offer.ItemId)}"
            : $"Monster: {drop.MobName} (BNpcName {drop.BNpcNameId}, Lv {drop.Level}) in {drop.ZoneName} " +
              $"[territory {drop.TerritoryId}]{(drop.Position is { } p ? $", remembered spot {p.X:F0}, {p.Y:F0}, {p.Z:F0}" : ", no remembered spot")}";
        yield return $"Wanted {offer.Amount}, obtained {Obtained}, kills {killCount}, target failures {targetFailures}, deaths {deaths}";
        yield return $"Job {jobId} (range {CombatJobs.AttackRange(jobId):F0}y), driver {driver.Name} " +
                     $"(available {driver.IsAvailable}, engaged {driver.IsEngaged}); HP {bridge.PlayerHpPercent:F0}%, " +
                     $"retreat below {settings.HuntRetreatHpPercent}%";
        yield return $"Target {targetId} \"{targetName}\"; skipped {skipped.Count}; sweep {sweepIndex}/{sweep.Count}; " +
                     $"failure: {(failureReason.Length == 0 ? "-" : failureReason)}";
        foreach (var line in travel.Describe())
            yield return "  " + line;
        foreach (var line in driver.Describe())
            yield return "  driver: " + line;
    }

    protected override void OnTick()
    {
        if (State is HuntRunState.Idle or HuntRunState.Paused or HuntRunState.Completed or HuntRunState.Failed)
            return;

        if (Clock.UtcNow < settleUntil)
            return;

        // Death first: nothing else matters while the character is on the floor.
        if (bridge.IsDead && State != HuntRunState.Recovering)
        {
            BeginRecovery();
            return;
        }

        // Then the retreat threshold, wherever the run is — a sweep can be
        // jumped by something that was not on the menu.
        if (State is not (HuntRunState.Retreating or HuntRunState.Recovering
            or HuntRunState.Equipping or HuntRunState.Teleporting) && Retreating())
            return;

        switch (State)
        {
            case HuntRunState.Equipping:
                TickEquipping();
                break;

            case HuntRunState.Teleporting:
                TickTeleporting();
                break;

            case HuntRunState.TravelingToSpot:
                TickTravelingToSpot();
                break;

            case HuntRunState.Sweeping:
                TickSweeping();
                break;

            case HuntRunState.Targeting:
                TickTargeting();
                break;

            case HuntRunState.Approaching:
                TickApproaching();
                break;

            case HuntRunState.Fighting:
                TickFighting();
                break;

            case HuntRunState.AfterKill:
                TickAfterKill();
                break;

            case HuntRunState.Retreating:
                TickRetreating();
                break;

            case HuntRunState.Recovering:
                TickRecovering();
                break;
        }
    }

    // ------------------------------------------------------------- the start

    private void Begin()
    {
        if (drop == null)
        {
            Fail($"no known monster drops {itemName(offer.ItemId)}");
            return;
        }

        if (!driver.IsAvailable)
        {
            Fail("no combat plugin is loaded to drive the fight");
            return;
        }

        if (jobId == 0 || !bridge.HasGearsetForJob(jobId))
        {
            Fail($"no gearset for the combat job (ClassJob {jobId})");
            return;
        }

        Enter(HuntRunState.Equipping, $"Hunting {drop.MobName} in {drop.ZoneName} for {offer.Amount}× {itemName(offer.ItemId)}.");
    }

    private void TickEquipping()
    {
        if (bridge.CurrentClassJobId == jobId)
        {
            Settle(Pacing.AfterJobChange);
            BeginTravel();
            return;
        }

        if (TimedOut(PhaseTimeout, $"could not switch to the combat job (ClassJob {jobId})"))
            return;

        attempts.Try(() => bridge.EquipGearsetForJob(jobId));
        StatusText = $"Equipping the ClassJob {jobId} gearset...";
    }

    /// <summary>Teleport when the monster lives elsewhere, then ride to the spot or sweep.</summary>
    private void BeginTravel()
    {
        if (bridge.CurrentTerritoryId != drop!.TerritoryId)
        {
            teleportAttempts = 0;
            Enter(HuntRunState.Teleporting, $"Teleporting to {drop.ZoneName}.");
            return;
        }

        if (drop.Position is { } spot)
        {
            travel.Start(spot, 15f, Fly, preciseArrival: false, TravelTimeout, $"the {drop.MobName} spot");
            Enter(HuntRunState.TravelingToSpot, $"Riding to the remembered {drop.MobName} spot in {drop.ZoneName}.");
            return;
        }

        BeginSweep();
    }

    private void TickTeleporting()
    {
        if (bridge.IsBetweenAreas)
        {
            StatusText = $"Loading {drop!.ZoneName}...";
            phaseStartedAt = Clock.UtcNow;
            return;
        }

        if (bridge.CurrentTerritoryId == drop!.TerritoryId)
        {
            Settle(Pacing.AfterZoneChange);
            // The travel decision is taken again in the new zone.
            attempts.Reset();
            phaseStartedAt = Clock.UtcNow;
            if (drop.Position is { } spot)
            {
                travel.Start(spot, 15f, Fly, preciseArrival: false, TravelTimeout, $"the {drop.MobName} spot");
                Enter(HuntRunState.TravelingToSpot, $"Arrived in {drop.ZoneName}; riding to the remembered spot.");
            }
            else
            {
                BeginSweep();
            }

            return;
        }

        if (teleportAttempts >= 3)
        {
            Fail($"could not teleport to {drop.ZoneName}");
            return;
        }

        if (TimedOut(PhaseTimeout, $"the teleport to {drop.ZoneName} never landed"))
            return;

        attempts.Try(() =>
        {
            teleportAttempts++;
            if (!bridge.TeleportToTerritory(drop.TerritoryId))
                Log.Warning($"[Hunt] The teleport to {drop.ZoneName} was refused (attempt {teleportAttempts}).");
        });
        StatusText = $"Teleporting to {drop.ZoneName} ({teleportAttempts}/3)...";
    }

    private void TickTravelingToSpot()
    {
        // The monster may already be in range: no need to finish the ride.
        if (VisibleTargets().Count > 0)
        {
            travel.Stop();
            BeginTargeting($"{drop!.MobName} in sight.");
            return;
        }

        travel.Tick();
        switch (travel.State)
        {
            case TravelState.Arrived:
                BeginTargeting($"At the remembered {drop!.MobName} spot.");
                break;

            case TravelState.Failed:
                Log.Warning($"[Hunt] Could not reach the remembered spot ({travel.FailureReason}); sweeping {drop!.ZoneName} instead.");
                BeginSweep();
                break;

            default:
                StatusText = travel.StatusText;
                break;
        }
    }

    // ------------------------------------------------------------- the sweep

    private void BeginSweep()
    {
        var bounds = database?.BoundsOf(drop!.TerritoryId);
        var origin = bridge.PlayerPosition ?? Vector3.Zero;
        sweep = bounds == null ? [] : HuntSweep.Points(bounds, origin, SweepStep, SweepPoints);
        sweepIndex = 0;
        travel.Stop();
        Enter(HuntRunState.Sweeping, sweep.Count == 0
            ? $"Looking for {drop!.MobName} where the character stands ({drop.ZoneName} has no map bounds)."
            : $"Sweeping {drop!.ZoneName} for {drop.MobName} ({sweep.Count} points).");
    }

    private void TickSweeping()
    {
        if (VisibleTargets().Count > 0)
        {
            travel.Stop();
            BeginTargeting($"Found {drop!.MobName}.");
            return;
        }

        if (travel.IsActive)
        {
            travel.Tick();
            if (travel.State == TravelState.Failed)
            {
                Log.Information($"[Hunt] Sweep point {sweepIndex} unreachable ({travel.FailureReason}); trying the next one.");
                travel.Stop();
            }
            else if (travel.State != TravelState.Arrived)
            {
                StatusText = $"Sweeping {drop!.ZoneName} ({sweepIndex}/{sweep.Count}): {travel.StatusText}";
                return;
            }
        }

        if (sweepIndex >= sweep.Count)
        {
            // Nothing left to walk to. Give respawns a moment before giving
            // up — and give the zones with no map bounds, where the sweep is
            // empty from the start, the same grace.
            if (Clock.UtcNow - phaseStartedAt <= PhaseTimeout)
            {
                StatusText = $"Waiting for {drop!.MobName} to appear in {drop.ZoneName}...";
                return;
            }

            Fail($"no {drop!.MobName} found in {drop.ZoneName} after {sweep.Count} sweep points");
            return;
        }

        var point = sweep[sweepIndex++];
        // The map box is the whole image, most of it off the mesh: snap each
        // point to walkable ground and skip the ones with none nearby.
        var landing = navigation.FindNearestMeshPoint(point, SweepStep / 2f, 200f);
        if (landing == null)
            return;

        travel.Start(landing.Value, 10f, Fly, preciseArrival: false, TravelTimeout, $"sweep point {sweepIndex}");
        StatusText = $"Sweeping {drop!.ZoneName} for {drop.MobName} ({sweepIndex}/{sweep.Count}).";
    }

    // ------------------------------------------------------------ the target

    private void BeginTargeting(string statusText)
    {
        travel.Stop();
        targetId = 0;
        targetName = "";
        if (bridge.PlayerPosition is { } here && bridge.PlayerHpPercent >= settings.HuntRetreatHpPercent)
            safePoint = here;

        Enter(HuntRunState.Targeting, statusText);
    }

    private void TickTargeting()
    {
        if (Obtained >= offer.Amount)
        {
            Complete();
            return;
        }

        var candidates = VisibleTargets();
        if (candidates.Count == 0)
        {
            // Nothing left here: either the camp is empty (respawn) or the
            // sweep has to carry on.
            if (Clock.UtcNow - phaseStartedAt > PhaseTimeout)
            {
                Log.Information($"[Hunt] No {drop!.MobName} in reach; sweeping on.");
                BeginSweep();
            }
            else
            {
                StatusText = $"Waiting for {drop!.MobName} to respawn ({Obtained}/{offer.Amount})...";
            }

            return;
        }

        var target = candidates[0];
        targetId = target.ObjectId;
        targetName = target.Name;
        if (!bridge.TargetObject(target.ObjectId))
        {
            skipped.Add(target.ObjectId);
            StatusText = $"{target.Name} could not be targeted; trying another.";
            return;
        }

        var range = CombatJobs.AttackRange(jobId);
        travel.Start(target.Position, range, fly: false, preciseArrival: true, TravelTimeout, target.Name);
        Enter(HuntRunState.Approaching, $"Closing on {target.Name} (Lv {target.Level}, {target.Distance:F0}y).");
    }

    private void TickApproaching()
    {
        var target = Current();
        if (target == null || !target.IsAlive)
        {
            // Killed by someone else, or it despawned.
            skipped.Add(targetId);
            BeginTargeting($"{targetName} is gone; picking another target.");
            return;
        }

        // The monster does not wait to be walked to: once it is in range —
        // because it came to us, or because it wandered — start the fight
        // instead of finishing the leg to where it used to stand.
        if (target.Distance <= CombatJobs.AttackRange(jobId))
        {
            Engage(target);
            return;
        }

        travel.Tick();
        switch (travel.State)
        {
            case TravelState.Arrived:
                Engage(target);
                break;

            case TravelState.Failed:
                targetFailures++;
                skipped.Add(targetId);
                Log.Warning($"[Hunt] Could not reach {targetName} ({travel.FailureReason}).");
                if (targetFailures >= MaxTargetFailures)
                {
                    Fail($"{targetFailures} targets in a row could not be reached ({travel.FailureReason})");
                    return;
                }

                BeginTargeting("Picking another target.");
                break;

            default:
                StatusText = travel.StatusText;
                break;
        }
    }

    private void Engage(HuntTarget target)
    {
        travel.Stop();
        // Re-assert the target: the approach may have lost it to an auto-target.
        if (bridge.CurrentTargetId != target.ObjectId)
            bridge.TargetObject(target.ObjectId);

        fightStartedAt = Clock.UtcNow;
        driver.Engage();
        Enter(HuntRunState.Fighting, $"Fighting {target.Name} (Lv {target.Level}) with {driver.Name}.");
    }

    private void TickFighting()
    {
        var target = Current();
        if (target == null || !target.IsAlive)
        {
            Disengage();
            killCount++;
            targetFailures = 0;

            // The spot is worth remembering now that a kill happened here —
            // once per run, so a long hunt does not rewrite the configuration
            // after every monster.
            if (!spotRemembered && bridge.PlayerPosition is { } here && drop != null)
            {
                spotRemembered = true;
                database?.RememberSpot(drop.BNpcNameId, bridge.CurrentTerritoryId, here);
            }

            Log.Information($"[Hunt] {targetName} down; {Obtained}/{offer.Amount} {itemName(offer.ItemId)} after {killCount} kills.");
            Settle(AfterKillSettle);
            Enter(HuntRunState.AfterKill, $"{targetName} down ({Obtained}/{offer.Amount}).");
            return;
        }

        if (Clock.UtcNow - fightStartedAt > FightTimeout)
        {
            Disengage();
            targetFailures++;
            skipped.Add(targetId);
            Log.Warning($"[Hunt] {targetName} was still up after {FightTimeout.TotalSeconds:F0}s; leaving it.");
            if (targetFailures >= MaxTargetFailures)
            {
                Fail($"{targetFailures} fights in a row did not finish");
                return;
            }

            BeginTargeting("Picking another target.");
            return;
        }

        // Keep the combat plugin pointed at the monster this run chose.
        if (bridge.CurrentTargetId != targetId)
            attempts.Try(() => bridge.TargetObject(targetId));

        StatusText = $"Fighting {targetName} ({target.HpPercent:F0}% left, {Obtained}/{offer.Amount} obtained, " +
                     $"{bridge.EnemiesTargetingMe()} on me).";
    }

    private void TickAfterKill()
    {
        if (Obtained >= offer.Amount)
        {
            Complete();
            return;
        }

        BeginTargeting($"Looking for the next {drop!.MobName} ({Obtained}/{offer.Amount}).");
    }

    // ------------------------------------------------------------- the rules

    /// <summary>
    /// The retreat rule (roadmap 7.5): below the configured HP the run
    /// disengages and walks back to the last safe point instead of dying.
    /// True when the run has taken over this tick.
    /// </summary>
    private bool Retreating()
    {
        if (State == HuntRunState.Retreating || bridge.PlayerHpPercent >= settings.HuntRetreatHpPercent)
            return false;

        Disengage();
        travel.Start(safePoint, 8f, fly: false, preciseArrival: false, TravelTimeout, "the last safe point");
        Enter(HuntRunState.Retreating, $"HP {bridge.PlayerHpPercent:F0}% is below {settings.HuntRetreatHpPercent}%; retreating.");
        return true;
    }

    private void TickRetreating()
    {
        if (travel.IsActive)
        {
            travel.Tick();
            if (travel.State == TravelState.Moving)
            {
                StatusText = $"Retreating at {bridge.PlayerHpPercent:F0}% HP: {travel.StatusText}";
                return;
            }

            travel.Stop();
        }

        if (bridge.IsInCombat)
        {
            StatusText = $"Still in combat at {bridge.PlayerHpPercent:F0}% HP; waiting it out.";
            return;
        }

        if (bridge.PlayerHpPercent < RecoveredHpPercent)
        {
            StatusText = $"Resting: {bridge.PlayerHpPercent:F0}% of {RecoveredHpPercent}%.";
            return;
        }

        skipped.Clear();
        BeginTargeting($"Recovered to {bridge.PlayerHpPercent:F0}% HP; back to the hunt.");
    }

    /// <summary>Death handling (roadmap 7.5): answer the Return prompt, wait for the revive, resume once.</summary>
    private void BeginRecovery()
    {
        Disengage();
        travel.Stop();
        deaths++;
        Enter(HuntRunState.Recovering, $"Died hunting {drop?.MobName ?? "the monster"} (death {deaths}).");
    }

    private void TickRecovering()
    {
        if (bridge.IsDead)
        {
            if (TimedOut(ReviveTimeout, "the character never revived after dying"))
                return;

            attempts.Try(() => bridge.AnswerReturnPrompt());
            StatusText = "Dead; answering the Return prompt.";
            return;
        }

        if (bridge.IsBetweenAreas)
        {
            StatusText = "Returning to the aetheryte...";
            phaseStartedAt = Clock.UtcNow;
            return;
        }

        // One death is an accident, two is the wrong monster for this job.
        if (deaths > 1)
        {
            Fail($"died twice hunting {drop?.MobName ?? "the monster"}");
            return;
        }

        Settle(Pacing.AfterZoneChange);
        skipped.Clear();
        teleportAttempts = 0;
        Log.Information($"[Hunt] Revived; resuming the hunt for {drop?.MobName}.");
        BeginTravel();
    }

    // ------------------------------------------------------------- the tools

    /// <summary>
    /// The monsters this run may fight, nearest first: alive, not already
    /// written off, within the level allowance, and — when the etiquette
    /// setting says so — not already someone else's fight.
    /// </summary>
    private IReadOnlyList<HuntTarget> VisibleTargets()
    {
        if (drop == null)
            return [];

        var maxLevel = MaxTargetLevel();
        var candidates = bridge.FindHuntTargets(drop.BNpcNameId, skipped, bridge.PlayerPosition);
        var allowed = new List<HuntTarget>();
        foreach (var candidate in candidates)
        {
            if (!candidate.IsAlive || skipped.Contains(candidate.ObjectId))
                continue;

            if (settings.HuntSkipMobsTargetedByOthers && candidate.TargetedByOthers)
                continue;

            if (maxLevel > 0 && candidate.Level > maxLevel)
                continue;

            allowed.Add(candidate);
        }

        return allowed;
    }

    /// <summary>The highest monster level this job may pull; 0 when the job level is unknown (allow everything).</summary>
    private int MaxTargetLevel()
    {
        var level = capabilities().LevelOf(jobId);
        return level <= 0 ? 0 : level + Math.Max(0, settings.HuntMaxLevelAbove);
    }

    private HuntTarget? Current() =>
        targetId == 0 ? null : VisibleTargetsIncludingSkipped().FirstOrDefault(t => t.ObjectId == targetId);

    /// <summary>The target in flight is looked up without the etiquette filters: it is already ours.</summary>
    private IReadOnlyList<HuntTarget> VisibleTargetsIncludingSkipped() =>
        drop == null ? [] : bridge.FindHuntTargets(drop.BNpcNameId, null, bridge.PlayerPosition);

    private bool Fly => capabilities().CanFlyIn(bridge.CurrentTerritoryId);

    private void Disengage()
    {
        if (driver.IsEngaged)
            driver.Disengage();
    }

    private void Complete()
    {
        Disengage();
        travel.Stop();
        Transition(HuntRunState.Completed,
            $"Hunted {Obtained}× {itemName(offer.ItemId)} from {drop?.MobName ?? "monsters"} in {killCount} kills.");
    }

    private void Fail(string reason)
    {
        failureReason = reason;
        Disengage();
        travel.Stop();
        Transition(HuntRunState.Failed, reason);
    }

    private void Enter(HuntRunState next, string statusText)
    {
        phaseStartedAt = Clock.UtcNow;
        attempts.Reset();
        Transition(next, statusText);
    }

    private void Settle(TimeSpan delay) => settleUntil = Clock.UtcNow + delay;

    private bool TimedOut(TimeSpan limit, string reason)
    {
        if (Clock.UtcNow - phaseStartedAt <= limit)
            return false;

        Fail(reason);
        return true;
    }

    private string PhaseText(HuntRunState state) => state switch
    {
        HuntRunState.Equipping => "equipping the combat gearset",
        HuntRunState.Teleporting => $"teleporting to {drop?.ZoneName}",
        HuntRunState.TravelingToSpot => "riding to the spot",
        HuntRunState.Sweeping => $"sweeping {drop?.ZoneName}",
        HuntRunState.Targeting => $"looking for {drop?.MobName}",
        HuntRunState.Retreating => "retreating",
        HuntRunState.Recovering => "recovering from a death",
        _ => state.ToString(),
    };
}
