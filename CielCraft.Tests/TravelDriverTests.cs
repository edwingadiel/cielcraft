using System;
using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class TravelDriverTests
{
    private sealed class FakeNavigation : INavigationProvider
    {
        public List<(Vector3 Destination, float Tolerance, bool Fly)> Moves { get; } = [];
        public bool IsMoving { get; set; }
        public bool IsAvailable => true;
        public bool IsReady => true;

        public bool MoveTo(Vector3 destination, bool fly)
        {
            Moves.Add((destination, 0f, fly));
            IsMoving = true;
            return true;
        }

        public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly)
        {
            Moves.Add((destination, tolerance, fly));
            IsMoving = true;
            return true;
        }

        public void Stop() => IsMoving = false;

        public Vector3? FindNearestMeshPoint(Vector3 approximate, float halfExtentXZ, float halfExtentY) => approximate;

        public Vector3? FindPointOnFloor(Vector3 near, float halfExtentXZ) => near with { Y = near.Y - 1 };
    }

    private sealed class FakeTravelBridge : ITravelBridge
    {
        public Vector3? PlayerPosition { get; set; } = Vector3.Zero;
        public bool IsMounted { get; set; }
        public int MountRequests;
        public int DismountRequests;

        public void TryMount()
        {
            MountRequests++;
            IsMounted = true;
        }

        public void TryDismount()
        {
            DismountRequests++;
            IsMounted = false;
        }
    }

    private static (TravelDriver Driver, FakeNavigation Nav, FakeTravelBridge Bridge, FakeClock Clock) Make()
    {
        var nav = new FakeNavigation();
        var bridge = new FakeTravelBridge();
        var clock = new FakeClock();
        var driver = new TravelDriver(nav, bridge, clock, new ListLog(), "[Test]");
        return (driver, nav, bridge, clock);
    }

    [Fact]
    public void LongPreciseLegMountsFliesToLandingPointDismountsAndWalks()
    {
        var (driver, nav, bridge, clock) = Make();
        var node = new Vector3(200, 0, 0);
        driver.Start(node, 2f, fly: true, preciseArrival: true, TimeSpan.FromSeconds(60), "node");

        driver.Tick(); // far and unmounted: mount
        Assert.Equal(1, bridge.MountRequests);
        Assert.True(bridge.IsMounted);

        clock.Advance(2.1);
        driver.Tick(); // mounted: fly to a floor point near the node with a loose tolerance
        Assert.Single(nav.Moves);
        Assert.True(nav.Moves[0].Fly);
        Assert.Equal(3f, nav.Moves[0].Tolerance);
        Assert.Equal(-1f, nav.Moves[0].Destination.Y);

        // Flight ends 20y out, still mounted: dismount and walk.
        nav.IsMoving = false;
        bridge.PlayerPosition = new Vector3(180, 0, 0);
        clock.Advance(2.1);
        driver.Tick();
        Assert.Equal(1, bridge.DismountRequests);
        Assert.False(bridge.IsMounted);

        clock.Advance(2.1);
        driver.Tick();
        Assert.Equal(2, nav.Moves.Count);
        Assert.False(nav.Moves[1].Fly);
        Assert.Equal(1.5f, nav.Moves[1].Tolerance);

        bridge.PlayerPosition = new Vector3(198.5f, 0, 0);
        driver.Tick();
        Assert.Equal(TravelState.Arrived, driver.State);
        Assert.False(nav.IsMoving);
    }

    [Fact]
    public void AreaLegFliesStraightAndArrivesWithinRangeWhileMounted()
    {
        var (driver, nav, bridge, clock) = Make();
        bridge.IsMounted = true;
        driver.Start(new Vector3(300, 0, 0), 10f, fly: true, preciseArrival: true == false, TimeSpan.FromSeconds(60), "area");

        driver.Tick();
        Assert.Single(nav.Moves);
        Assert.True(nav.Moves[0].Fly);
        Assert.Equal(10f, nav.Moves[0].Tolerance);

        bridge.PlayerPosition = new Vector3(292, 0, 0);
        driver.Tick();
        Assert.Equal(TravelState.Arrived, driver.State);
        Assert.Equal(0, bridge.DismountRequests);
    }

    [Fact]
    public void FlightThatNeverStartsFallsBackToWalking()
    {
        var (driver, nav, bridge, clock) = Make();
        bridge.IsMounted = true;
        driver.Start(new Vector3(50, 0, 0), 2f, fly: true, preciseArrival: false, TimeSpan.FromSeconds(60), "spot");

        driver.Tick();
        Assert.True(nav.Moves[0].Fly);
        nav.IsMoving = false; // the fly request did not start moving
        clock.Advance(2.1);
        driver.Tick();
        Assert.Equal(2, nav.Moves.Count);
        Assert.False(nav.Moves[1].Fly);
    }

    [Fact]
    public void TimesOutWhenTheTargetIsNeverReached()
    {
        var (driver, nav, _, clock) = Make();
        driver.Start(new Vector3(50, 0, 0), 2f, fly: false, preciseArrival: true, TimeSpan.FromSeconds(30), "spot");
        driver.Tick();
        clock.Advance(31);
        driver.Tick();
        Assert.Equal(TravelState.Failed, driver.State);
        Assert.False(nav.IsMoving);
    }
}
