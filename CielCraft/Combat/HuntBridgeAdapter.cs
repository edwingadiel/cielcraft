using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Combat;

/// <summary>
/// The hunt run's view of the game (roadmap 7.5) over the real bridge: the
/// travel members, the combat members package B added, and B's target
/// snapshots mapped onto A's <see cref="HuntTarget"/> (the BNpcName id is the
/// query argument, so it is filled in here).
/// </summary>
internal sealed class HuntBridgeAdapter(IGameBridge bridge) : IHuntBridge
{
    public Vector3? PlayerPosition => bridge.PlayerPosition;

    public bool IsMounted => bridge.IsMounted;

    public void TryMount() => bridge.TryMount();

    public void TryDismount() => bridge.TryDismount();

    public uint CurrentTerritoryId => bridge.CurrentTerritoryId;

    public bool IsBetweenAreas => bridge.IsBetweenAreas;

    public bool CanTeleportTo(uint territoryId) => bridge.CanTeleportTo(territoryId);

    public bool TeleportToTerritory(uint territoryId) => bridge.TeleportToTerritory(territoryId);

    public bool EquipGearsetForJob(uint classJobId) => bridge.EquipGearsetForJob(classJobId);

    public bool HasGearsetForJob(uint classJobId) => bridge.HasGearsetForJob(classJobId);

    public uint CurrentClassJobId => bridge.CurrentClassJobId;

    public int GetItemCount(uint itemId) => bridge.GetItemCount(itemId);

    public IReadOnlyList<HuntTarget> FindHuntTargets(
        uint bnpcNameId, IReadOnlyCollection<ulong>? excluded = null, Vector3? origin = null) =>
        bridge.FindHuntTargets(bnpcNameId, excluded, origin)
            .Select(s => new HuntTarget(
                s.ObjectId, bnpcNameId, s.Name, s.Level, s.Position, s.Distance, s.HpPercent, s.TargetedByOthers, s.IsAlive))
            .ToList();

    public bool TargetObject(ulong objectId) => bridge.TargetObject(objectId);

    public ulong CurrentTargetId => bridge.CurrentTargetId;

    public float PlayerHpPercent => bridge.PlayerHpPercent;

    public bool IsInCombat => bridge.IsInCombat;

    public bool IsDead => bridge.IsDead;

    public bool AnswerReturnPrompt() => bridge.AnswerReturnPrompt();

    public int EnemiesTargetingMe() => bridge.EnemiesTargetingMe();
}
