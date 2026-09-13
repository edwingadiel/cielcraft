using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Translates the action ids Raphael emits into ids usable by the active job.
/// Ids below 100000 are shared buff actions and pass through; craft actions
/// (CRP-flavored from Raphael) are translated via the CraftAction sheet's
/// per-job cross-reference columns.
/// </summary>
public static class CraftActionResolver
{
    private static readonly Dictionary<(uint ActionId, uint JobId), uint?> Cache = new();

    public static uint? ResolveForJob(uint raphaelActionId, uint classJobId)
    {
        if (raphaelActionId < 100000)
            return raphaelActionId;

        var key = (raphaelActionId, classJobId);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var resolved = Resolve(raphaelActionId, classJobId);
        Cache[key] = resolved;
        return resolved;
    }

    private static uint? Resolve(uint actionId, uint classJobId)
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
}
