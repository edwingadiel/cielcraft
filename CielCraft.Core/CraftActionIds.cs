namespace CielCraft.Core;

/// <summary>
/// CraftAction ids, which differ per crafting job. If an id here is ever wrong
/// for the current job, the action-ready guard rejects it before execution.
/// </summary>
public static class CraftActionIds
{
    /// <summary>Basic Synthesis for the given ClassJob row id; null for non-crafting jobs.</summary>
    public static uint? BasicSynthesis(uint classJobId) => classJobId switch
    {
        8 => 100001,  // CRP
        9 => 100015,  // BSM
        10 => 100030, // ARM
        11 => 100075, // GSM
        12 => 100045, // LTW
        13 => 100060, // WVR
        14 => 100090, // ALC
        15 => 100105, // CUL
        _ => null,
    };
}
