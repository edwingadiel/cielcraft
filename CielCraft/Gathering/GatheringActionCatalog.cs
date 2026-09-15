using System;
using System.Collections.Generic;
using CielCraft.Core;
using Lumina.Excel.Sheets;

namespace CielCraft.Gathering;

/// <summary>
/// Resolves the Core's <see cref="GatherAction"/> vocabulary to Action-sheet
/// ids by English name per job at load (roadmap 7.14), so no action id is
/// hard-coded: an upgraded action (Bountiful Yield II) comes before the one
/// it replaces, and the game's own action status decides which is usable.
/// Names that do not resolve are logged once and never chosen. Also lists
/// the cordials with the GP they restore (ItemAction Data / DataHQ) and
/// their recast (Item.Cooldowns).
/// </summary>
public sealed class GatheringActionCatalog
{
    private static readonly uint[] Jobs = [GatheringActions.MinerJobId, GatheringActions.BotanistJobId];

    private readonly Dictionary<(GatherAction Action, uint Job), IReadOnlyList<uint>> ids = new();
    private readonly Dictionary<GatherAction, int> costs = new();
    private readonly List<string> unresolved = [];

    public GatheringActionCatalog(ILog log)
    {
        var cordials = new List<CordialInfo>();
        try
        {
            Resolve(log);
            cordials.AddRange(ReadCordials());
        }
        catch (Exception e)
        {
            log.Error($"[Gather] Action catalogue could not read the game sheets ({e.Message}); gathering actions are unavailable.");
        }

        Cordials = cordials.Count > 0 ? cordials : GatheringActions.Cordials;
    }

    /// <summary>Cordials strongest first with sheet GP values and recasts (the Core defaults when the sheets failed).</summary>
    public IReadOnlyList<CordialInfo> Cordials { get; }

    /// <summary>"Yield II (MIN): 'King's Yield II'" entries that did not resolve; empty when everything did.</summary>
    public IReadOnlyList<string> Unresolved => unresolved;

    /// <summary>Number of (action, job) pairs that resolved to at least one id.</summary>
    public int ResolvedCount => ids.Count;

    /// <summary>Action ids to try in order for the action on the job; empty when unresolved or not MIN/BTN.</summary>
    public IReadOnlyList<uint> ActionIds(GatherAction action, uint jobId) =>
        ids.TryGetValue((action, jobId), out var list) ? list : [];

    /// <summary>GP cost from the sheet; the Core default when the action did not resolve.</summary>
    public int GpCost(GatherAction action) =>
        costs.TryGetValue(action, out var cost) ? cost : GatheringActions.DefaultGpCost(action);

    private void Resolve(ILog log)
    {
        // (job, lower-case name) → (id, GP cost) over the player actions of MIN/BTN.
        var byName = new Dictionary<(uint Job, string Name), (uint Id, int Cost)>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            var job = row.ClassJob.RowId;
            if (!row.IsPlayerAction || (job != GatheringActions.MinerJobId && job != GatheringActions.BotanistJobId))
                continue;

            var name = row.Name.ExtractText().Trim().ToLowerInvariant();
            if (name.Length == 0)
                continue;

            // PrimaryCostType 7 is GP on the gathering actions.
            var cost = row.PrimaryCostType == 7 ? row.PrimaryCostValue : 0;
            byName.TryAdd((job, name), (row.RowId, cost));
        }

        foreach (var action in Enum.GetValues<GatherAction>())
        {
            foreach (var job in Jobs)
            {
                var resolved = new List<uint>();
                foreach (var name in GatheringActions.Names(action, job))
                {
                    if (byName.TryGetValue((job, name.ToLowerInvariant()), out var entry))
                    {
                        resolved.Add(entry.Id);
                        costs.TryAdd(action, entry.Cost);
                    }
                    else
                    {
                        unresolved.Add($"{GatheringActions.DisplayName(action)} ({JobName(job)}): '{name}'");
                    }
                }

                if (resolved.Count > 0)
                    ids[(action, job)] = resolved;
            }
        }

        if (unresolved.Count > 0)
            log.Warning($"[Gather] Action catalogue: {unresolved.Count} name(s) not found in the Action sheet and never used: {string.Join(", ", unresolved)}.");
        else
            log.Information($"[Gather] Action catalogue: {ids.Count} gathering actions resolved from the Action sheet.");
    }

    private static IEnumerable<CordialInfo> ReadCordials()
    {
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var known in GatheringActions.Cordials)
        {
            if (!items.TryGetRow(known.ItemId, out var item))
            {
                yield return known;
                continue;
            }

            var gpNq = known.GpNq;
            var gpHq = known.GpHq;
            if (item.ItemAction.IsValid && item.ItemAction.RowId != 0)
            {
                var action = item.ItemAction.Value;
                if (action.Data.Count > 0 && action.Data[0] > 0)
                    gpNq = action.Data[0];
                if (action.DataHQ.Count > 0 && action.DataHQ[0] > 0)
                    gpHq = action.DataHQ[0];
            }

            var name = item.Name.ExtractText();
            yield return new CordialInfo(
                known.ItemId,
                name.Length > 0 ? name : known.Name,
                gpNq,
                gpHq,
                item.Cooldowns > 0 ? item.Cooldowns : known.CooldownSeconds,
                item.CanBeHq);
        }
    }

    private static string JobName(uint job) => job == GatheringActions.MinerJobId ? "MIN" : "BTN";

    /// <summary>One line per action for the diagnostic report / settings page.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var action in Enum.GetValues<GatherAction>())
        {
            var min = string.Join("/", ActionIds(action, GatheringActions.MinerJobId));
            var btn = string.Join("/", ActionIds(action, GatheringActions.BotanistJobId));
            yield return $"{GatheringActions.DisplayName(action)}: MIN {(min.Length > 0 ? min : "-")}, BTN {(btn.Length > 0 ? btn : "-")}, {GpCost(action)} GP";
        }

        foreach (var cordial in Cordials)
            yield return $"{cordial.Name} ({cordial.ItemId}): {cordial.GpNq}/{cordial.GpHq} GP, recast {cordial.CooldownSeconds}s";
    }
}
