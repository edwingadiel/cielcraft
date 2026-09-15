using System;
using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>What happens to the surplus of an item a <see cref="StorageRule"/> names (roadmap 7.17).</summary>
public enum StorageAction
{
    /// <summary>Nothing; the rule only records how much to keep (a reserve the other rules must not touch).</summary>
    Keep,

    /// <summary>Put the surplus in a retainer at the next summoning-bell visit.</summary>
    Deposit,

    /// <summary>Desynthesize the surplus (needs <see cref="AutomationSettings.DesynthUnusedByproducts"/>).</summary>
    Desynth,

    /// <summary>Throw the surplus away (needs <see cref="AutomationSettings.TrashCleanup"/>).</summary>
    Discard,
}

/// <summary>
/// One line of the storage policy (roadmap 7.17): how much of an item stays
/// in the bag, how much a retainer may hold, and what happens to whatever is
/// left after a run. Only items a rule names are ever deposited, desynthesized
/// or discarded — the keeper never touches anything else.
/// </summary>
public sealed class StorageRule
{
    public uint ItemId { get; set; }

    /// <summary>Always keep this many in the bag; the plan's own needs are reserved on top of it.</summary>
    public int KeepInBag { get; set; }

    /// <summary>Cap for <see cref="StorageAction.Deposit"/>: stop depositing once the retainers hold this many. 0 = no cap.</summary>
    public int KeepInRetainer { get; set; }

    public StorageAction Action { get; set; } = StorageAction.Deposit;

    public StorageRule Clone() => new()
    {
        ItemId = ItemId,
        KeepInBag = KeepInBag,
        KeepInRetainer = KeepInRetainer,
        Action = Action,
    };
}

/// <summary>
/// One thing the inventory keeper decided to do after a run (roadmap 7.17):
/// pure output of the decision pass, executed later by the keeper's machine.
/// </summary>
public sealed record KeeperAction(uint ItemId, int Amount, StorageAction Action, string Reason);

/// <summary>What the bag and the retainers hold, for the keeper's pure decision pass.</summary>
public interface IStorageView
{
    /// <summary>Total NQ+HQ count of the item in the player inventory.</summary>
    int InBag(uint itemId);

    /// <summary>Count across the cached retainer pages and saddlebags.</summary>
    int InStorage(uint itemId);

    /// <summary>Free bag slots; a deposit needs none, a withdrawal does.</summary>
    int FreeBagSlots { get; }
}

/// <summary>Convenience view over two dictionaries, for tests and the panel preview.</summary>
public sealed class DictionaryStorageView : IStorageView
{
    public Dictionary<uint, int> Bag { get; } = new();

    public Dictionary<uint, int> Storage { get; } = new();

    public int FreeBagSlots { get; set; } = 20;

    public int InBag(uint itemId) => Bag.TryGetValue(itemId, out var count) ? count : 0;

    public int InStorage(uint itemId) => Storage.TryGetValue(itemId, out var count) ? count : 0;
}

/// <summary>
/// The storage policy's decision pass (roadmap 7.17), kept pure so it can be
/// tested and previewed in the panel without touching the game: given the
/// rules, the settings, what the plan still needs and what is in the bag, say
/// what should be deposited, desynthesized or discarded.
/// </summary>
public static class StorageKeeper
{
    /// <summary>
    /// Everything the rules call for, in rule order. Only items a rule names
    /// appear; a rule whose action the settings switch off is skipped with no
    /// action at all, never downgraded to another one.
    /// </summary>
    /// <param name="stillNeeded">
    /// What the finished plan still reserves per item (raw materials and
    /// ingredients); reserved on top of <see cref="StorageRule.KeepInBag"/> so
    /// a rule can never eat a material the next group needs.
    /// </param>
    public static IReadOnlyList<KeeperAction> Decide(
        IReadOnlyList<StorageRule> rules,
        AutomationSettings settings,
        IStorageView storage,
        IReadOnlyDictionary<uint, int>? stillNeeded = null,
        Func<uint, string>? itemName = null)
    {
        var name = itemName ?? (id => $"item {id}");
        var actions = new List<KeeperAction>();
        var seen = new HashSet<uint>();
        foreach (var rule in rules)
        {
            if (rule.ItemId == 0 || rule.Action == StorageAction.Keep || !seen.Add(rule.ItemId))
                continue;

            var owned = storage.InBag(rule.ItemId);
            var needed = stillNeeded != null && stillNeeded.TryGetValue(rule.ItemId, out var n) ? n : 0;
            var reserved = Math.Max(rule.KeepInBag, 0) + Math.Max(needed, 0);
            var surplus = owned - reserved;
            if (surplus <= 0)
                continue;

            switch (rule.Action)
            {
                case StorageAction.Deposit:
                    // KeepInRetainer is a cap, not a target: once the
                    // retainers hold that many the surplus stays in the bag
                    // rather than being quietly destroyed.
                    var room = rule.KeepInRetainer > 0 ? rule.KeepInRetainer - storage.InStorage(rule.ItemId) : surplus;
                    var deposit = Math.Min(surplus, room);
                    if (deposit > 0)
                    {
                        actions.Add(new KeeperAction(
                            rule.ItemId, deposit, StorageAction.Deposit,
                            $"{name(rule.ItemId)} ×{deposit} over the {reserved} kept in the bag"));
                    }

                    break;

                case StorageAction.Desynth:
                    if (settings.DesynthUnusedByproducts)
                    {
                        actions.Add(new KeeperAction(
                            rule.ItemId, surplus, StorageAction.Desynth,
                            $"{name(rule.ItemId)} ×{surplus} no order needs"));
                    }

                    break;

                case StorageAction.Discard:
                    if (settings.TrashCleanup)
                    {
                        actions.Add(new KeeperAction(
                            rule.ItemId, surplus, StorageAction.Discard,
                            $"{name(rule.ItemId)} ×{surplus} no order needs and nothing desynthesizes"));
                    }

                    break;
            }
        }

        return actions;
    }

    /// <summary>What a finished plan still reserves per item, for <see cref="Decide"/>.</summary>
    public static Dictionary<uint, int> Reserved(ProductionPlan? plan)
    {
        var reserved = new Dictionary<uint, int>();
        if (plan == null)
            return reserved;

        foreach (var material in plan.RawMaterials)
            reserved[material.ItemId] = reserved.GetValueOrDefault(material.ItemId) + material.Amount;

        foreach (var target in plan.Targets)
            reserved[target.ItemId] = reserved.GetValueOrDefault(target.ItemId) + target.Quantity;

        return reserved;
    }
}
