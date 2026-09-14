using System;

namespace CielCraft.Core;

/// <summary>
/// Minimum settle times between server-visible actions. Firing them back to
/// back (teleport → gearset → synthesize → first action within two seconds)
/// got the client disconnected in testing; these keep the cadence within what
/// a person at the keyboard would produce. Tune here, not at the call sites.
/// </summary>
public static class Pacing
{
    /// <summary>After a loading screen, before any action in the new zone.</summary>
    public static readonly TimeSpan AfterZoneChange = TimeSpan.FromSeconds(4);

    /// <summary>After a gearset/job change, before opening the crafting log or synthesizing.</summary>
    public static readonly TimeSpan AfterJobChange = TimeSpan.FromSeconds(3);

    /// <summary>After a synthesis begins, before the first craft action.</summary>
    public static readonly TimeSpan AfterCraftStart = TimeSpan.FromSeconds(3);

    /// <summary>After a craft ends, before pressing Synthesize again.</summary>
    public static readonly TimeSpan BetweenCrafts = TimeSpan.FromSeconds(4);

    /// <summary>After the gather target is reached and the node closed, before teleporting.</summary>
    public static readonly TimeSpan AfterGatherComplete = TimeSpan.FromSeconds(2.5);

    /// <summary>After arriving at a node, before interacting with it.</summary>
    public static readonly TimeSpan BeforeInteract = TimeSpan.FromSeconds(1);

    /// <summary>After the node window opens, before choosing a slot.</summary>
    public static readonly TimeSpan AfterNodeOpen = TimeSpan.FromSeconds(1);
}
