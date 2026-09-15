using System;
using System.Collections.Generic;

namespace CielCraft.Fishing;

/// <summary>The fishing actions the controller can fire (roadmap 7.4).</summary>
public enum FishAction
{
    /// <summary>Opens the bait window; the controller applies bait through the bridge instead.</summary>
    Bait,
    Cast,
    Hook,

    /// <summary>Precision Hookset: the "!" (light) tug. Level 15, 50 GP.</summary>
    PrecisionHookset,

    /// <summary>Powerful Hookset: the "!!" / "!!!" tug. Level 15, 50 GP.</summary>
    PowerfulHookset,

    /// <summary>Mooch: re-cast the catch still on the line. Level 25.</summary>
    Mooch,

    /// <summary>Quit: put the rod away.</summary>
    Quit,
}

/// <summary>
/// Fishing action ids per the Action sheet (ClassJob 18, read 2026-09-15).
/// The catalogue resolves them by English name at load like the gathering one
/// (roadmap 7.14) so a patch that renumbers an action is picked up; these are
/// the fallbacks when the sheet cannot be read.
/// </summary>
public sealed class FishingActionCatalog
{
    private static readonly (FishAction Action, string Name, uint Id, int Level, int Gp)[] Known =
    [
        (FishAction.Bait, "Bait", 288, 1, 0),
        (FishAction.Cast, "Cast", 289, 1, 0),
        (FishAction.Hook, "Hook", 296, 1, 0),
        (FishAction.PrecisionHookset, "Precision Hookset", 4179, 15, 50),
        (FishAction.PowerfulHookset, "Powerful Hookset", 4103, 15, 50),
        (FishAction.Mooch, "Mooch", 297, 25, 0),
        (FishAction.Quit, "Quit", 299, 1, 0),
    ];

    private readonly Dictionary<FishAction, uint> ids = new();
    private readonly Dictionary<FishAction, int> levels = new();
    private readonly Dictionary<FishAction, int> gp = new();

    /// <summary>
    /// resolveByName maps an English Action-sheet name of ClassJob 18 to its
    /// row id (and the level it unlocks at); null = keep the bundled id.
    /// </summary>
    public FishingActionCatalog(Func<string, (uint Id, int Level, int Gp)?>? resolveByName = null)
    {
        foreach (var (action, name, id, level, cost) in Known)
        {
            var resolved = resolveByName?.Invoke(name);
            ids[action] = resolved?.Id ?? id;
            levels[action] = resolved?.Level ?? level;
            gp[action] = resolved?.Gp ?? cost;

            // Only a resolver that was asked and came up empty is a miss; with
            // no resolver at all (the tests) the bundled ids are the answer.
            if (resolveByName != null && resolved == null)
                Unresolved.Add(name);
        }
    }

    /// <summary>Names that did not come back from the Action sheet and kept their bundled id.</summary>
    public List<string> Unresolved { get; } = [];

    public uint Id(FishAction action) => ids[action];

    /// <summary>Job level the action unlocks at; the controller never fires one the character cannot have.</summary>
    public int Level(FishAction action) => levels[action];

    public int GpCost(FishAction action) => gp[action];

    /// <summary>One line per action for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var (action, name, _, _, _) in Known)
            yield return $"{name}: action {Id(action)}, level {Level(action)}, {GpCost(action)} GP";
    }
}
