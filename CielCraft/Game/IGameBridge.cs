using CielCraft.Core;

namespace CielCraft.Game;

/// <summary>
/// Provider boundary between CielCraft's logic and the game (spec §6/§8).
/// The only Dalamud-backed implementation is <see cref="DalamudGameBridge"/>;
/// tests use mocks.
/// </summary>
public interface IGameBridge
{
    bool IsLoggedIn { get; }

    /// <summary>True while a synthesis is in progress.</summary>
    bool IsCrafting { get; }

    /// <summary>True while the crafting log is open / a recipe is selected but no synthesis is running.</summary>
    bool IsPreparingToCraft { get; }

    bool IsGathering { get; }

    /// <summary>Null when no character is logged in.</summary>
    PlayerSnapshot? GetPlayerState();

    /// <summary>Null when no craft is active.</summary>
    CraftSnapshot? GetCraftState();

    /// <summary>True when the game reports the craft action as currently usable (CP, state, availability).</summary>
    bool IsCraftActionReady(uint craftActionId);

    /// <summary>Requests execution of a craft action. True if the game accepted the request.</summary>
    bool ExecuteCraftAction(uint craftActionId);
}
