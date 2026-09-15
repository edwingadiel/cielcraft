using System;
using System.Collections.Generic;
using System.Numerics;

namespace CielCraft.Core;

/// <summary>The part of the game the travel driver needs: where the player is and whether they are mounted.</summary>
public interface ITravelBridge
{
    Vector3? PlayerPosition { get; }

    bool IsMounted { get; }

    /// <summary>Requests mount roulette; the game may refuse (indoors, combat).</summary>
    void TryMount();

    void TryDismount();
}

public enum TravelState
{
    Idle,
    Moving,
    Arrived,
    Failed,
}

/// <summary>
/// One place for "get the character to a point": mount for long legs, fly
/// when allowed, fall back to walking when a flight never starts, and — for
/// precise targets such as gathering nodes — fly to a landable spot nearby,
/// dismount, and walk the last stretch, because a flight cannot settle on an
/// exact coordinate. Replaces the duplicated logic in the production runner
/// and the gathering controller (review follow-up under roadmap 5.1).
/// </summary>
public sealed class TravelDriver
{
    /// <summary>Mount when the leg is longer than this.</summary>
    public const float MountBeyond = 80f;

    /// <summary>Precise legs: flights aim for a landable floor point within this radius of the target.</summary>
    public const float LandingRange = 8f;

    /// <summary>Precise legs: once a flight ends anywhere within this distance, dismount and walk.</summary>
    public const float DismountRange = 30f;

    private const int MaxMountAttempts = 3;

    private readonly INavigationProvider navigation;
    private readonly ITravelBridge bridge;
    private readonly IClock clock;
    private readonly ILog log;
    private readonly string logPrefix;
    private readonly Throttle attempts;
    private readonly Random random;

    private Vector3 destination;
    private float arriveWithin;
    private bool allowFly;
    private bool precise;
    private TimeSpan timeout;
    private DateTime startedAt;
    private int mountAttempts;
    private bool flyAttempted;
    private bool flyBlocked;
    private string what = "";

    public TravelDriver(INavigationProvider navigation, ITravelBridge bridge, IClock clock, ILog log, string logPrefix, Random? random = null)
    {
        this.random = random ?? Random.Shared;
        this.navigation = navigation;
        this.bridge = bridge;
        this.clock = clock;
        this.log = log;
        this.logPrefix = logPrefix;
        attempts = new Throttle(clock, TimeSpan.FromSeconds(2));
    }

    public TravelState State { get; private set; } = TravelState.Idle;

    public string StatusText { get; private set; } = "Idle.";

    public bool IsActive => State == TravelState.Moving;

    /// <summary>Why the last leg failed, for the owner's own failure message; empty otherwise.</summary>
    public string FailureReason { get; private set; } = "";

    /// <summary>
    /// Begin a leg. <paramref name="precise"/> = the target must be reached on
    /// foot within <paramref name="arriveWithin"/> (a node); otherwise the leg
    /// ends when within range by any means (an area).
    /// </summary>
    public void Start(Vector3 target, float arriveWithinRange, bool fly, bool preciseArrival, TimeSpan legTimeout, string description)
    {
        FailureReason = "";
        destination = target;
        arriveWithin = arriveWithinRange;
        allowFly = fly;
        precise = preciseArrival;
        timeout = legTimeout;
        what = description;
        startedAt = clock.UtcNow;
        mountAttempts = 0;
        flyAttempted = false;
        flyBlocked = false;
        attempts.Reset();
        State = TravelState.Moving;
        StatusText = $"Moving to {what}.";
    }

    public void Stop()
    {
        if (State == TravelState.Moving)
            navigation.Stop();
        State = TravelState.Idle;
        StatusText = "Idle.";
    }

    /// <summary>Distance from the player to the target; null when the player is unknown.</summary>
    public float? Distance =>
        bridge.PlayerPosition is { } position ? Vector3.Distance(position, destination) : null;

    public void Tick()
    {
        if (State != TravelState.Moving)
            return;

        var position = bridge.PlayerPosition;
        if (position == null)
        {
            Fail("player position unavailable");
            return;
        }

        var distance = Vector3.Distance(position.Value, destination);
        if (distance <= arriveWithin)
        {
            navigation.Stop();
            State = TravelState.Arrived;
            StatusText = $"Arrived at {what}.";
            return;
        }

        if (clock.UtcNow - startedAt > timeout)
        {
            navigation.Stop();
            Fail($"could not reach {what} within {timeout.TotalSeconds:F0}s");
            return;
        }

        if (navigation.IsMoving)
        {
            flyAttempted = false;
            return;
        }

        // A flight cannot settle on the exact coordinate (the mount hovers and
        // vnavmesh keeps nudging): once the flight leg is over near a precise
        // target, get off and walk the rest.
        if (precise && bridge.IsMounted && distance <= DismountRange)
        {
            attempts.Try(bridge.TryDismount);
            StatusText = $"Landing near {what} ({distance:F0}y away).";
            return;
        }

        attempts.Try(() =>
        {
            if (!bridge.IsMounted && mountAttempts < MaxMountAttempts && distance > MountBeyond)
            {
                mountAttempts++;
                bridge.TryMount();
                return;
            }

            // A fly request that never started moving means no flying here.
            if (flyAttempted)
                flyBlocked = true;

            var fly = allowFly && bridge.IsMounted && !flyBlocked;
            flyAttempted = fly;
            if (fly && precise)
            {
                var landing = navigation.FindPointOnFloor(RandomLandingSpot(), LandingRange) ?? destination;
                navigation.MoveCloseTo(landing, 3f, fly: true);
            }
            else if (fly)
            {
                navigation.MoveCloseTo(destination, arriveWithin, fly: true);
            }
            else
            {
                navigation.MoveCloseTo(destination, Math.Max(0.5f, arriveWithin - 0.5f), fly: false);
            }

            StatusText = $"Moving to {what} ({distance:F0}y away{(fly ? ", flying" : "")}).";
        });
    }

    /// <summary>
    /// A different spot around the target each time (roadmap 7.20): several
    /// characters landing on the exact node coordinate look like bots, and
    /// the walk-up leg makes the last stretch precise anyway.
    /// </summary>
    public Vector3 RandomLandingSpot()
    {
        var angle = random.NextDouble() * Math.PI * 2;
        var radius = LandingRange * (0.4 + 0.6 * random.NextDouble());
        return destination + new Vector3((float)(Math.Cos(angle) * radius), 0f, (float)(Math.Sin(angle) * radius));
    }

    private void Fail(string reason)
    {
        FailureReason = reason;
        State = TravelState.Failed;
        StatusText = $"Failed: {reason}.";
        log.Information($"{logPrefix} Travel failed: {reason}.");
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Travel {State} — {StatusText}";
        yield return $"target {destination.X:F1}, {destination.Y:F1}, {destination.Z:F1} within {arriveWithin:F1}y; fly {allowFly}; precise {precise}; started {startedAt:HH:mm:ss}Z; mountAttempts {mountAttempts}; flyBlocked {flyBlocked}; flyAttempted {flyAttempted}";
    }
}
