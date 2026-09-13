using System.Numerics;

namespace CielCraft.Core;

/// <summary>
/// Navigation boundary (spec §28/§30): CielCraft decides where and why to go;
/// the provider owns pathfinding and movement execution. Poll-based to match
/// the framework-tick architecture.
/// </summary>
public interface INavigationProvider
{
    /// <summary>The navigation backend is installed and responding.</summary>
    bool IsAvailable { get; }

    /// <summary>A navmesh for the current zone is loaded and ready.</summary>
    bool IsReady { get; }

    /// <summary>A pathfind or path traversal is in progress.</summary>
    bool IsMoving { get; }

    /// <summary>Starts pathfinding and moving to the destination. True if accepted.</summary>
    bool MoveTo(Vector3 destination, bool fly);

    /// <summary>Like MoveTo but stops within the given range of the destination.</summary>
    bool MoveCloseTo(Vector3 destination, float tolerance, bool fly);

    void Stop();
}
