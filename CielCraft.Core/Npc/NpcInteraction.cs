using System.Collections.Generic;
using System.Numerics;

namespace CielCraft.Core;

/// <summary>
/// An NPC to go to (roadmap 7.3): the ENpcResident row, its name, where it
/// stands (territory + world position from the Level sheet) and the object
/// data id the object table shows for it.
/// </summary>
public sealed record NpcTarget(uint NpcId, string Name, uint TerritoryId, Vector3 Position, uint DataId);

/// <summary>Who can answer "where is NPC N?" — the NPC database (P1) implements it; sources (7.3b, 7.17, 7.4) consume it.</summary>
public interface INpcLocator
{
    NpcTarget? Locate(uint npcId);
}

/// <summary>One step of an NPC dialog, driven by the interactor after the interaction starts.</summary>
public abstract record DialogStep;

/// <summary>Pick the SelectString / SelectIconString option whose text contains this (case-insensitive).</summary>
public sealed record SelectOption(string TextContains) : DialogStep;

/// <summary>Advance a Talk box.</summary>
public sealed record AdvanceTalk : DialogStep;

/// <summary>Answer a SelectYesno with yes (or no).</summary>
public sealed record Confirm(bool Yes = true) : DialogStep;

/// <summary>Wait until the named addon is visible; the script ends there and the caller drives that window.</summary>
public sealed record WaitForAddon(string AddonName) : DialogStep;

public enum NpcInteractionState
{
    Idle,
    Teleporting,
    Traveling,
    Interacting,
    InDialog,
    Completed,
    Failed,
    Paused,
}

/// <summary>
/// Goes to an NPC and drives its dialog (roadmap 7.3): teleport to the
/// nearest attuned aetheryte of its territory, travel (TravelDriver), target
/// and interact, then run the dialog script step by step with the same
/// timeouts and interference rules as travel. Completed once the script's
/// last step is done (typically <see cref="WaitForAddon"/> — the caller then
/// operates that window and calls <see cref="Stop"/> when finished).
/// </summary>
public interface INpcInteractor
{
    NpcInteractionState State { get; }

    string StatusText { get; }

    /// <summary>Why the last run failed; empty otherwise.</summary>
    string FailureReason { get; }

    /// <summary>False when another interaction is in flight.</summary>
    bool Start(NpcTarget target, IReadOnlyList<DialogStep> script, string description);

    void Tick();

    void Pause(string reason);

    void Resume();

    /// <summary>Abandons the interaction, closing any dialog it left open.</summary>
    void Stop();

    IEnumerable<string> Describe();
}
