using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Translates the action ids Raphael emits (CRP-flavored) into ids usable by
/// the active job. Craft actions (ids ≥ 100000) go through the CraftAction
/// sheet's per-job cross-reference columns. Buff actions below 100000 (Waste
/// Not, Manipulation, Veneration, Innovation, Great Strides, Final Appraisal)
/// are per-job too — the Action sheet has no cross-reference columns, so they
/// are matched by name among the target job's actions. (Observed: Waste Not II
/// as 4639 — the CRP id — never became usable on BSM and stalled the craft.)
/// </summary>
public static class CraftActionResolver
{
    private static readonly Dictionary<(uint ActionId, uint JobId), uint?> Cache = new();
    private static Dictionary<(uint JobId, string Name), uint>? actionsByJobAndName;

    public static uint? ResolveForJob(uint raphaelActionId, uint classJobId)
    {
        var key = (raphaelActionId, classJobId);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var resolved = raphaelActionId < 100000
            ? ResolveBuffAction(raphaelActionId, classJobId)
            : ResolveCraftAction(raphaelActionId, classJobId);
        Cache[key] = resolved;
        return resolved;
    }

    private static uint? ResolveCraftAction(uint actionId, uint classJobId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<CraftAction>();
        if (!sheet.TryGetRow(actionId, out var row))
            return null;

        if (row.ClassJob.RowId == classJobId)
            return actionId;

        var sibling = classJobId switch
        {
            8 => row.CRP.RowId,
            9 => row.BSM.RowId,
            10 => row.ARM.RowId,
            11 => row.GSM.RowId,
            12 => row.LTW.RowId,
            13 => row.WVR.RowId,
            14 => row.ALC.RowId,
            15 => row.CUL.RowId,
            _ => 0u,
        };

        return sibling != 0 ? sibling : null;
    }

    private static uint? ResolveBuffAction(uint actionId, uint classJobId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (!sheet.TryGetRow(actionId, out var row))
            return actionId; // unknown to the sheet: let the ready-guard decide

        var ownerJob = row.ClassJob.RowId;
        if (ownerJob == classJobId || !IsCrafterJob(ownerJob))
            return actionId; // already right, or genuinely shared

        actionsByJobAndName ??= IndexCrafterActions(sheet);
        if (actionsByJobAndName.TryGetValue((classJobId, row.Name.ExtractText()), out var sibling))
            return sibling;

        // The per-job copies of every crafter buff sit at consecutive ids
        // (CRP..CUL); fall back to that layout if the name lookup misses.
        return IsCrafterJob(classJobId) ? actionId + (classJobId - ownerJob) : null;
    }

    private static Dictionary<(uint, string), uint> IndexCrafterActions(Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Action> sheet)
    {
        var index = new Dictionary<(uint, string), uint>();
        foreach (var action in sheet)
        {
            var job = action.ClassJob.RowId;
            if (!IsCrafterJob(job))
                continue;

            var name = action.Name.ExtractText();
            if (name.Length > 0)
                index.TryAdd((job, name), action.RowId);
        }

        return index;
    }

    private static bool IsCrafterJob(uint classJobId) => classJobId is >= 8 and <= 15;
}
