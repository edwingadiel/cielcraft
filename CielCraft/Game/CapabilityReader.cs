using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Reads <see cref="CharacterCapabilities"/> from the game (roadmap 7.16) and
/// caches the last snapshot. Refreshed on login, on demand (debug window) and
/// by the production runner's pre-flight; must run on the framework thread.
/// </summary>
public sealed class CapabilityReader
{
    public CharacterCapabilities Current { get; private set; } = CharacterCapabilities.Unknown;

    /// <summary>Re-reads everything; keeps the previous snapshot if the read throws. Unknown while logged out.</summary>
    public CharacterCapabilities Refresh()
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            Current = CharacterCapabilities.Unknown;
            return Current;
        }

        try
        {
            Current = Read();
            Plugin.Log.Information(
                $"[Capabilities] Read: flight in {Current.FlightTerritories.Count} zones, {Current.UnlockedRecipeBooks.Count} master books, " +
                $"{Current.GpRegenTraits.Count(t => t.Unlocked)}/{Current.GpRegenTraits.Count} GP-regen traits.");
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "[Capabilities] Reading character capabilities failed.");
        }

        return Current;
    }

    private static unsafe CharacterCapabilities Read()
    {
        var playerState = PlayerState.Instance();
        if (playerState == null)
            return CharacterCapabilities.Unknown;

        var data = Plugin.DataManager;

        // Flight: a zone is flyable once every aether current of its
        // AetherCurrentCompFlgSet is attuned. ARR zones got sets in 6.0, so
        // the one rule covers every flyable territory; zones without a set
        // (cities, instances) are not flyable.
        var flight = new HashSet<uint>();
        foreach (var territory in data.GetExcelSheet<TerritoryType>())
        {
            var set = territory.AetherCurrentCompFlgSet.RowId;
            if (set != 0 && playerState->IsAetherCurrentZoneComplete(set))
                flight.Add(territory.RowId);
        }

        // Master recipe books (Recipe.SecretRecipeBook points into this sheet).
        var books = new HashSet<uint>();
        foreach (var book in data.GetExcelSheet<SecretRecipeBook>())
        {
            if (book.RowId != 0 && playerState->IsSecretRecipeBookUnlocked(book.RowId))
                books.Add(book.RowId);
        }

        // Tribe reputation ranks, keyed by BeastTribe row id. The rank
        // accessor is a raw game call whose index semantics are undocumented
        // in ClientStructs; the row id is what the game's own UI passes.
        var tribes = new Dictionary<uint, int>();
        foreach (var tribe in data.GetExcelSheet<BeastTribe>())
        {
            if (tribe.RowId != 0 && tribe.RowId <= byte.MaxValue)
                tribes[tribe.RowId] = playerState->GetBeastTribeRank((byte)tribe.RowId);
        }

        // Every job's level, DoH (ClassJob 8..15), DoL (16..18) and the combat
        // jobs alike (roadmap 7.5 needs the latter to pick a hunting job); the
        // level array is indexed by ClassJob.ExpArrayIndex, which a class and
        // the job it grows into share, so both rows report the same number.
        // Row 0 (adventurer) and rows with no experience bar are skipped.
        var levels = new Dictionary<uint, int>();
        var jobLevels = playerState->ClassJobLevels;
        foreach (var job in data.GetExcelSheet<ClassJob>())
        {
            // Row 0 is "adventurer" and rows 44/45 are unnamed placeholders
            // that share pugilist's experience index; an abbreviation is what
            // separates a real job from them.
            if (job.RowId == 0 || job.Abbreviation.ExtractText().Length == 0)
                continue;

            var index = job.ExpArrayIndex;
            if (index >= 0 && index < jobLevels.Length)
                levels[job.RowId] = jobLevels[index];
        }

        // GP-regen traits (MIN/BTN; fishing is out of scope, spec §32). The
        // level-70 ones are granted by a job quest (Trait.Quest); II and III
        // carry no quest and are plain level gates. Quest ids in the game's
        // completion bitfield are the low 16 bits of the Quest row id.
        var traits = new List<GpRegenTrait>();
        foreach (var trait in data.GetExcelSheet<Trait>())
        {
            var jobId = trait.ClassJob.RowId;
            if (jobId is not (GatheringActions.MinerJobId or GatheringActions.BotanistJobId))
                continue;

            var name = trait.Name.ExtractText();
            if (!name.StartsWith("Enhanced GP Regeneration", StringComparison.Ordinal))
                continue;

            var questId = trait.Quest.RowId;
            var unlocked = levels.GetValueOrDefault(jobId) >= trait.Level
                && (questId == 0 || QuestManager.IsQuestComplete((ushort)(questId & 0xFFFF)));
            traits.Add(new GpRegenTrait(jobId, name, trait.Level, unlocked));
        }

        traits.Sort((a, b) => a.JobId != b.JobId ? a.JobId.CompareTo(b.JobId) : a.Level.CompareTo(b.Level));

        return new CharacterCapabilities(true, flight, books, tribes, traits, levels, DateTime.UtcNow);
    }

    /// <summary>Human-readable summary for the debug window and the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        var caps = Current;
        if (!caps.IsKnown)
        {
            yield return "Not read yet (log in, or press Refresh).";
            yield break;
        }

        var data = Plugin.DataManager;
        var territoryId = Plugin.ClientState.TerritoryType;
        var zoneName = data.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory)
            ? territory.PlaceName.Value.Name.ExtractText()
            : "?";
        var totalBooks = data.GetExcelSheet<SecretRecipeBook>().Count - 1;

        yield return $"Read at {caps.ReadAt:HH:mm:ss}Z";
        yield return $"Flight unlocked in {caps.FlightTerritories.Count} zones; current zone {zoneName} ({territoryId}): {(caps.CanFlyIn(territoryId) ? "flyable" : "no flight")}";
        yield return $"Master books unlocked: {caps.UnlockedRecipeBooks.Count}/{totalBooks}";
        yield return "GP-regen traits: " + (caps.GpRegenTraits.Count == 0
            ? "none found"
            : string.Join(", ", caps.GpRegenTraits.Select(t => $"{JobName(t.JobId)} {t.Name} (Lv{t.Level}) {(t.Unlocked ? "yes" : "no")}")));
        // Every ClassJob is read now (roadmap 7.5), so the line would otherwise
        // be forty "Lv0" entries: only levelled jobs are listed.
        var levelled = caps.JobLevels.Where(p => p.Value > 0).OrderBy(p => p.Key).ToList();
        yield return "Job levels: " + (levelled.Count == 0
            ? "none read"
            : string.Join(", ", levelled.Select(p => $"{JobName(p.Key)} {p.Value}")));
        var combatJob = caps.BestCombatJob(HasGearset);
        yield return "Best combat job with a gearset (roadmap 7.5): "
            + (combatJob == 0 ? "none" : $"{JobName(combatJob)} (level {caps.LevelOf(combatJob)})");
        var ranked = caps.TribeRanks
            .Where(p => p.Value > 0)
            .OrderBy(p => p.Key)
            .Select(p => $"{TribeName(p.Key)} {p.Value}")
            .ToList();
        yield return "Tribe ranks: " + (ranked.Count == 0 ? "none" : string.Join(", ", ranked));
    }

    /// <summary>
    /// A gearset exists for the job. The same question the bridge answers, but
    /// the reader has no bridge and the diagnostic line above needs it; the
    /// gearset module is the single source either way.
    /// </summary>
    private static bool HasGearset(uint classJobId)
    {
        unsafe
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (module == null)
                return false;

            for (var i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var gearset = module->GetGearset(i);
                if (gearset != null && gearset->ClassJob == classJobId)
                    return true;
            }

            return false;
        }
    }

    private static string JobName(uint jobId) =>
        Plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(jobId, out var row)
            ? row.Abbreviation.ExtractText()
            : $"job {jobId}";

    private static string TribeName(uint tribeId) =>
        Plugin.DataManager.GetExcelSheet<BeastTribe>().TryGetRow(tribeId, out var row)
            ? row.Name.ExtractText()
            : $"tribe {tribeId}";
}
