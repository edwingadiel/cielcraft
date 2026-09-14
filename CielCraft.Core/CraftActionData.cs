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
        /// durability cost.
        /// </summary>
        public int ProgressGain(int baseProgress, byte level, int currentDurability, bool veneration = false, bool muscleMemory = false)
        {
            var modifier = ProgressModifier(level);
            if (ActionId == 100403 && currentDurability < DurabilityCost)
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
