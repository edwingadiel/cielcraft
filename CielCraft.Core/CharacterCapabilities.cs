namespace CielCraft.Core;

/// <summary>A GP-regeneration trait of a gathering job and whether the character has it (roadmap 7.16).</summary>
public sealed record GpRegenTrait(uint JobId, string Name, int Level, bool Unlocked);

/// <summary>
/// What the character can actually do (roadmap 7.16): flight per territory,
/// unlocked master recipe books, tribe reputation ranks, GP-regen traits and
/// DoH/DoL job levels. Read from the game on login / on demand and cached.
/// IsKnown is false until the first successful read; every query is then
/// permissive so nothing gets planned away on missing data.
/// </summary>
public sealed record CharacterCapabilities(
    bool IsKnown,
    IReadOnlySet<uint> FlightTerritories,
    IReadOnlySet<uint> UnlockedRecipeBooks,
    IReadOnlyDictionary<uint, int> TribeRanks,
    IReadOnlyList<GpRegenTrait> GpRegenTraits,
    IReadOnlyDictionary<uint, int> JobLevels,
    DateTime ReadAt)
{
    public static readonly CharacterCapabilities Unknown = new(
        false, new HashSet<uint>(), new HashSet<uint>(), new Dictionary<uint, int>(), [],
        new Dictionary<uint, int>(), DateTime.MinValue);

    /// <summary>Flight is unlocked in the territory; true while unknown (the runners keep their trial-and-error fallback).</summary>
    public bool CanFlyIn(uint territoryId) => !IsKnown || FlightTerritories.Contains(territoryId);

    /// <summary>The master book is unlocked; book 0 means the recipe needs none.</summary>
    public bool HasRecipeBook(uint bookId) => bookId == 0 || !IsKnown || UnlockedRecipeBooks.Contains(bookId);

    public bool IsRecipeUsable(RecipeInfo recipe) => HasRecipeBook(recipe.SecretRecipeBookId);

    /// <summary>Level of a DoH/DoL job; 0 when unknown.</summary>
    public int LevelOf(uint jobId) => JobLevels.GetValueOrDefault(jobId);

    /// <summary>Reputation rank with a tribe (BeastTribe row id); 0 when unknown.</summary>
    public int TribeRank(uint tribeId) => TribeRanks.GetValueOrDefault(tribeId);

    /// <summary>ClassJob rows 8..15 are the eight crafters, 16..18 the three gatherers; everything else fights (roadmap 7.5).</summary>
    public static bool IsCombatJob(uint jobId) => jobId != 0 && jobId is < 8 or > 18;

    /// <summary>
    /// The combat job to hunt on (roadmap 7.5): the highest-level ClassJob that
    /// is not a crafter or gatherer and has a gearset saved, preferring the
    /// higher row id on a tie so a job wins over the class it grew out of (they
    /// share an experience bar, so they always tie). 0 when nothing qualifies —
    /// no gearsets, or the capabilities have never been read.
    /// </summary>
    public uint BestCombatJob(Func<uint, bool> hasGearset)
    {
        uint best = 0;
        var bestLevel = 0;
        foreach (var (jobId, level) in JobLevels)
        {
            if (level <= 0 || !IsCombatJob(jobId) || !hasGearset(jobId))
                continue;

            if (level > bestLevel || (level == bestLevel && jobId > best))
            {
                best = jobId;
                bestLevel = level;
            }
        }

        return best;
    }
}

/// <summary>Planning decisions that depend on capabilities, kept in Core so they are testable with fake data.</summary>
public static class CapabilityRules
{
    /// <summary>
    /// Picks the recipe to plan for a multi-recipe item (roadmap 4.5 + 7.16):
    /// a recipe whose master book is unlocked always beats a locked one; among
    /// those, the current job, then a job with a gearset, then the lowest
    /// recipe id (candidates are expected in id order). A locked recipe is
    /// still returned when it is the only one so the runner can name the book.
    /// </summary>
    public static RecipeInfo? ChooseRecipe(
        IEnumerable<RecipeInfo> candidates,
        uint currentJob,
        Func<uint, bool> hasGearsetForJob,
        CharacterCapabilities capabilities)
    {
        RecipeInfo? best = null;
        var bestRank = int.MaxValue;
        foreach (var info in candidates)
        {
            var rank = info.ClassJobId == currentJob ? 0 : hasGearsetForJob(info.ClassJobId) ? 1 : 2;
            if (!capabilities.IsRecipeUsable(info))
                rank += 10;

            if (rank < bestRank)
            {
                best = info;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a node area beats the current pick as the source of an item
    /// (spec §33/§35, roadmap 7.16): untimed beats timed; then a territory
    /// where flight is unlocked beats one where it is not; then the lower
    /// gathering level. Flight only reorders — the only source is never dropped.
    /// </summary>
    public static bool IsBetterSource(GatheringLocation candidate, GatheringLocation existing, CharacterCapabilities capabilities)
    {
        if (candidate.IsTimed != existing.IsTimed)
            return !candidate.IsTimed;

        var candidateFlies = capabilities.CanFlyIn(candidate.TerritoryId);
        var existingFlies = capabilities.CanFlyIn(existing.TerritoryId);
        if (candidateFlies != existingFlies)
            return candidateFlies;

        return candidate.GatheringLevel < existing.GatheringLevel;
    }

    /// <summary>Best source by <see cref="IsBetterSource"/>; null when there is none.</summary>
    public static GatheringLocation? ChooseSource(IEnumerable<GatheringLocation> candidates, CharacterCapabilities capabilities)
    {
        GatheringLocation? best = null;
        foreach (var candidate in candidates)
        {
            if (best == null || IsBetterSource(candidate, best, capabilities))
                best = candidate;
        }

        return best;
    }
}
