namespace CielCraft.Core;

/// <summary>
/// Gathering action and consumable ids (spec §37). Per-job pairs are
/// (Miner, Botanist); usability (GP, level, unlock) is checked through the
/// game's own action status at runtime, so no costs are duplicated here.
/// </summary>
public static class GatheringActions
{
    public const uint MinerJobId = 16;
    public const uint BotanistJobId = 17;

    /// <summary>King's Yield II / Blessed Harvest II (+2 yield per swing, 500 GP).</summary>
    public static uint YieldII(uint jobId) => jobId == MinerJobId ? 241u : 224u;

    /// <summary>King's Yield / Blessed Harvest (+1 yield per swing, 400 GP).</summary>
    public static uint YieldI(uint jobId) => jobId == MinerJobId ? 239u : 222u;

    /// <summary>Solid Reason / Ageless Words (+1 gathering attempt, 300 GP).</summary>
    public static uint RestoreIntegrity(uint jobId) => jobId == MinerJobId ? 232u : 215u;

    /// <summary>Cordial item ids, strongest first (Hi-Cordial, Cordial, Watered Cordial).</summary>
    public static readonly uint[] Cordials = [12669, 6141, 16911];
}
