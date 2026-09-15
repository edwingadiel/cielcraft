namespace CielCraft.Core;

/// <summary>Which kind of consumable an inventory stack is (roadmap 7.11): meals feed the Food slot, medicine the Potion slot.</summary>
public enum ConsumableKind
{
    Food,
    Medicine,
}

/// <summary>
/// One food or medicine stack of the player inventory as the consumable
/// pickers list it (roadmap 7.11): NQ and HQ stacks are separate entries so
/// a pick also settles the HQ preference. BuffSummary is the ItemFood bonus
/// text ("Craftsmanship +8% (max 118), ...") or "n/a" when the sheet has none.
/// </summary>
public sealed record ConsumableItem(uint ItemId, string Name, bool IsHq, int Count, string BuffSummary, ConsumableKind Kind);
