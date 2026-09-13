using System;
using System.Numerics;
using CielCraft.Core;
using Dalamud.Plugin.Ipc;

namespace CielCraft.Navigation;

/// <summary>
/// INavigationProvider over vnavmesh's Dalamud IPC (spec §29). Every call is
/// guarded: when vnavmesh is missing or not responding the provider reports
/// unavailable instead of throwing, and crafting remains fully usable (§31).
/// </summary>
public sealed class VNavmeshProvider : INavigationProvider
{
    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;

    public VNavmeshProvider()
    {
        var ipc = Plugin.PluginInterface;
        navIsReady = ipc.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathfindAndMoveTo = ipc.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathfindAndMoveCloseTo = ipc.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathfindInProgress = ipc.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathIsRunning = ipc.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = ipc.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        nearestPoint = ipc.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
    }

    public bool IsAvailable => Plugin.IsVNavmeshAvailable && Try(() => { navIsReady.InvokeFunc(); return true; });

    public bool IsReady => Try(() => navIsReady.InvokeFunc());

    public bool IsMoving => Try(() => pathIsRunning.InvokeFunc()) || Try(() => pathfindInProgress.InvokeFunc());

    public bool MoveTo(Vector3 destination, bool fly)
    {
        Plugin.Log.Information($"[Navigation] MoveTo {destination.X:F1}, {destination.Y:F1}, {destination.Z:F1} (fly: {fly}).");
        return Try(() => pathfindAndMoveTo.InvokeFunc(destination, fly));
    }

    public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly)
    {
        Plugin.Log.Information(
            $"[Navigation] MoveCloseTo {destination.X:F1}, {destination.Y:F1}, {destination.Z:F1} " +
            $"(tolerance: {tolerance:F1}, fly: {fly}).");
        return Try(() => pathfindAndMoveCloseTo.InvokeFunc(destination, fly, tolerance));
    }

    public void Stop()
    {
        Try(() =>
        {
            pathStop.InvokeAction();
            return true;
        });
        Plugin.Log.Information("[Navigation] Stop requested.");
    }

    public Vector3? FindNearestMeshPoint(Vector3 approximate, float halfExtentXZ, float halfExtentY)
    {
        try
        {
            return nearestPoint.InvokeFunc(approximate, halfExtentXZ, halfExtentY);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Try(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            // vnavmesh missing, unloaded, or its IPC not ready.
            return false;
        }
    }
}
