namespace CielCraft.Core;

/// <summary>
/// Static knowledge about the actions Raphael emits, keyed by Raphael's ids.
/// Costs and modifiers mirror raphael-sim. Progress estimates include only
/// the buffs known to be active for the next action, so they never exceed
/// the real gain.
/// </summary>
public static class CraftActionData
{
    public const uint HeartAndSoul = 100419;
    public const uint QuickInnovation = 100459;
    public const uint TrainedPerfection = 100475;

    /// <summary>Specialist one-shots; they do not advance the step counter.</summary>
    public static bool IsSpecialist(uint actionId) => actionId is HeartAndSoul or QuickInnovation;

    /// <summary>Actions that only serve quality — safe to skip once the quality target is met.</summary>
    private static readonly HashSet<uint> QualityOnly =
    [
        100002, // Basic Touch
        100004, // Standard Touch
        100411, // Advanced Touch
        100355, // Hasty Touch
        100451, // Daring Touch
        100128, // Precise Touch
        100227, // Prudent Touch
        100299, // Preparatory Touch
        100435, // Trained Finesse
        100443, // Refined Touch
        100339, // Byregot's Blessing
        100283, // Trained Eye
        100387, // Reflect
        19004,  // Innovation
        260,    // Great Strides
        100459, // Quick Innovation
        100419, // Heart and Soul (only serves condition-gated quality plays)
        100010, // Observe (only feeds touch combos)
        100371, // Tricks of the Trade (condition-gated utility)
    ];

    public static bool IsQualityOnly(uint actionId) => QualityOnly.Contains(actionId);

    public const uint Observe = 100010;
    private const uint DelicateSynthesis = 100323;

    /// <summary>Actions whose quality gain a Poor condition would halve.</summary>
    public static bool AffectsQuality(uint actionId) =>
        actionId == DelicateSynthesis || (QualityOnly.Contains(actionId) && actionId is not (Observe or 100371 or 19004 or 260 or HeartAndSoul or QuickInnovation));

    /// <summary>CP cost by Raphael (CRP) id; unknown ids get a conservative 50.</summary>
    public static int CpCost(uint actionId) => actionId switch
    {
        100001 => 0,   // Basic Synthesis
        100002 => 18,  // Basic Touch
        100003 => 88,  // Master's Mend
        100355 => 0,   // Hasty Touch
        100363 => 0,   // Rapid Synthesis
        100010 => 7,   // Observe
        100371 => 0,   // Tricks of the Trade
        4631 => 56,    // Waste Not
        19297 => 18,   // Veneration
        100004 => 32,  // Standard Touch
        260 => 32,     // Great Strides
        19004 => 18,   // Innovation
        19012 => 1,    // Final Appraisal
        4639 => 98,    // Waste Not II
        100339 => 24,  // Byregot's Blessing
        100128 => 18,  // Precise Touch
        100379 => 6,   // Muscle Memory
        100203 => 7,   // Careful Synthesis
        4574 => 96,    // Manipulation
        100227 => 25,  // Prudent Touch
        100411 => 46,  // Advanced Touch
        100387 => 6,   // Reflect
        100299 => 40,  // Preparatory Touch
        100403 => 18,  // Groundwork
        100323 => 32,  // Delicate Synthesis
        100315 => 6,   // Intensive Synthesis
        100283 => 250, // Trained Eye
        100427 => 18,  // Prudent Synthesis
        100435 => 32,  // Trained Finesse
        100443 => 24,  // Refined Touch
        100467 => 112, // Immaculate Mend
        100475 => 0,   // Trained Perfection
        100451 => 0,   // Daring Touch
        100459 => 0,   // Quick Innovation
        100419 => 0,   // Heart and Soul
        100395 => 0,   // Careful Observation
        _ => 50,
    };

    /// <summary>Durability cost by Raphael (CRP) id before Waste Not; unknown ids get 10.</summary>
    public static int DurabilityCost(uint actionId) => actionId switch
    {
        100299 or 100403 => 20,                                    // Preparatory Touch, Groundwork
        100227 or 100427 => 5,                                     // Prudent Touch, Prudent Synthesis
        100003 or 100010 or 100371 or 4631 or 19297 or 260 or 19004 or 19012 or 4639
            or 4574 or 100435 or 100467 or 100475 or 100459 or 100419 or 100395 => 0,
        _ => 10,
    };

    public sealed record SynthesisAction(
        uint ActionId,
        string Name,
        byte LevelRequirement,
        ushort CpCost,
        ushort DurabilityCost,
        bool RequiresGoodCondition)
    {
        public int ProgressModifier(byte level) => ActionId switch
        {
            100001 => level < 31 ? 100 : 120,  // Basic Synthesis
            100203 => level < 82 ? 150 : 180,  // Careful Synthesis
            100403 => level < 86 ? 300 : 360,  // Groundwork
            100315 => 400,                     // Intensive Synthesis
            _ => 0,
        };

        /// <summary>
        /// Progress gain for the next action: base × action efficiency ×
        /// active progress buffs (Veneration +50%, Muscle Memory +100%),
        /// floored as the game does. Groundwork is halved below its
        /// durability cost as actually charged (halved under Waste Not, free
        /// under Trained Perfection).
        /// </summary>
        public int ProgressGain(
            int baseProgress, byte level, int currentDurability,
            bool veneration = false, bool muscleMemory = false, bool wasteNot = false, bool trainedPerfection = false)
        {
            var modifier = ProgressModifier(level);
            var effectiveCost = trainedPerfection ? 0 : wasteNot ? DurabilityCost / 2 : DurabilityCost;
            if (ActionId == 100403 && currentDurability < effectiveCost)
                modifier /= 2;

            var buffModifier = 10 + (muscleMemory ? 10 : 0) + (veneration ? 5 : 0);
            return baseProgress * modifier * buffModifier / 1000;
        }
    }

    /// <summary>Progress finishers, cheapest CP first.</summary>
    public static readonly IReadOnlyList<SynthesisAction> Finishers =
    [
        new(100001, "Basic Synthesis", 1, 0, 10, RequiresGoodCondition: false),
        new(100315, "Intensive Synthesis", 78, 6, 10, RequiresGoodCondition: true),
        new(100203, "Careful Synthesis", 62, 7, 10, RequiresGoodCondition: false),
        new(100403, "Groundwork", 72, 18, 20, RequiresGoodCondition: false),
    ];
}
