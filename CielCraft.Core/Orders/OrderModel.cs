using System;
using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>How an order's amount is read (roadmap 7.13, borrowed from Lisbeth).</summary>
public enum AmountMode
{
    /// <summary>Produce exactly Amount, regardless of what the bag holds.</summary>
    Absolute,

    /// <summary>Top the bag up to Amount: produce Amount − owned, nothing when already there.</summary>
    Restock,
}

/// <summary>How the order's final craft is produced.</summary>
public enum ProductionMode
{
    /// <summary>Solve for the configured target quality; NQ or HQ as it lands.</summary>
    Any,

    /// <summary>Solve for full quality with HQ materials and keep crafting until the HQ count is met.</summary>
    ForceHq,

    /// <summary>Quick synthesis for the final craft too (NQ, fast) when the game offers it.</summary>
    QuickSynth,

    /// <summary>Craft as a collectable (roadmap 7.23; refused by the planner until then).</summary>
    Collectable,
}

/// <summary>What an order asks for: a craft (with its sub-crafts and gathering) or gathering alone (roadmap 7.1).</summary>
public enum OrderKind
{
    Craft,

    /// <summary>Gather the item itself: teleport, travel, timed windows, the node loop; no crafting.</summary>
    Gather,
}

/// <summary>Collectability tier to hit for collectable crafts and gathers (roadmap 7.23 / 7.1); thresholds come from the game data.</summary>
public enum CollectableTier
{
    Low,
    Mid,
    High,
}

/// <summary>
/// One thing to produce. Plain mutable class so the plugin configuration
/// serializes it as-is; Core never mutates orders behind the UI's back.
/// </summary>
public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public OrderKind Kind { get; set; } = OrderKind.Craft;

    public uint ItemId { get; set; }

    /// <summary>Only read when Mode is Collectable.</summary>
    public CollectableTier CollectableTier { get; set; } = CollectableTier.High;

    public int Amount { get; set; } = 1;

    public AmountMode AmountMode { get; set; } = AmountMode.Absolute;

    public ProductionMode Mode { get; set; } = ProductionMode.Any;

    /// <summary>Gather and craft everything the item needs, but skip its own final craft.</summary>
    public bool MaterialsOnly { get; set; }

    /// <summary>Disabled orders stay in the list but are not planned.</summary>
    public bool Enabled { get; set; } = true;

    public string Note { get; set; } = "";
}

/// <summary>Orders planned together (shared sub-crafts, one gather trip per material); groups run in sequence.</summary>
public sealed class OrderGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Orders";

    public bool Enabled { get; set; } = true;

    public List<Order> Orders { get; set; } = [];
}

/// <summary>Everything the user has asked for, in run order.</summary>
public sealed class OrderBook
{
    public List<OrderGroup> Groups { get; set; } = [];

    /// <summary>Restart from the first group when the last one completes (Lisbeth's perpetual mode).</summary>
    public bool Perpetual { get; set; }
}

/// <summary>One target handed to the dependency resolver; several make a group plan.</summary>
public sealed record PlanTarget(
    uint ItemId,
    int Quantity,
    ProductionMode Mode = ProductionMode.Any,
    bool MaterialsOnly = false,
    OrderKind Kind = OrderKind.Craft,
    CollectableTier CollectableTier = CollectableTier.High);

/// <summary>What the planner decided for one order of a group.</summary>
public sealed record OrderOutcome(Order Order, int PlannedQuantity, string? SkipReason)
{
    public bool Planned => PlannedQuantity > 0 && SkipReason == null;
}

/// <summary>A group's merged plan plus the per-order decisions (restock satisfied, disabled, unsupported mode).</summary>
public sealed record GroupPlan(OrderGroup Group, ProductionPlan? Plan, IReadOnlyList<OrderOutcome> Orders)
{
    /// <summary>Nothing left to do for this group (every order skipped or already satisfied).</summary>
    public bool IsEmpty => Plan == null || (Plan.CraftSteps.Count == 0 && Plan.RawMaterials.Count == 0);
}
