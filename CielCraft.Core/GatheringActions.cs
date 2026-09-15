using System;
using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>
/// The gathering actions the rotation engine can choose (roadmap 7.14). The
/// plugin's catalogue resolves each (action, job) to Action-sheet ids by
/// English name at load; the Core only knows names, GP costs and roles.
/// </summary>
public enum GatherAction
{
    /// <summary>King's Yield / Blessed Harvest: +1 yield per swing for the node (400 GP).</summary>
    YieldI,

    /// <summary>King's Yield II / Blessed Harvest II: +2 yield per swing (500 GP).</summary>
    YieldII,

    /// <summary>Solid Reason / Ageless Words: +1 gathering attempt (300 GP).</summary>
    RestoreIntegrity,

    /// <summary>Bountiful Yield / Bountiful Harvest (II): +1..3 items on the next swing (100 GP).</summary>
    BountifulYield,

    /// <summary>Mountaineer's / Pioneer's Gift I: Gatherer's Boon chance +10% (50 GP).</summary>
    GiftI,

    /// <summary>Mountaineer's / Pioneer's Gift II: Gatherer's Boon chance +30% (100 GP).</summary>
    GiftII,

    /// <summary>Nald'thal's / Nophica's Tidings: Gatherer's Boon yields an extra item (200 GP).</summary>
    Tidings,

    /// <summary>The Twelve's Bounty: +3 shards/crystals per swing (150 GP).</summary>
    TwelvesBounty,

    /// <summary>The Giving Land: random extra shards/crystals/clusters (200 GP, 3-minute recast).</summary>
    GivingLand,

    /// <summary>Wise to the World: +1 attempt, only under Eureka Moment (free).</summary>
    WiseToTheWorld,

    /// <summary>Luck of the Mountaineer / Pioneer: reveals hidden items (200 GP).</summary>
    Luck,

    /// <summary>Scour: fixed collectability gain, consumes an attempt (free).</summary>
    Scour,

    /// <summary>Brazen Prospector / Woodsman: random collectability gain, consumes an attempt (free).</summary>
    Brazen,

    /// <summary>Meticulous Prospector / Woodsman: collectability gain with a chance to keep the attempt (free).</summary>
    Meticulous,

    /// <summary>Scrutiny: the next appraisal is stronger (200 GP).</summary>
    Scrutiny,

    /// <summary>Collector's Focus: better Collector's Intuition odds for the node (100 GP).</summary>
    CollectorsFocus,

    /// <summary>Priming Touch: the next Meticulous is more likely to keep its attempt (100 GP).</summary>
    PrimingTouch,

    /// <summary>Collect: takes the collectable at its current rating.</summary>
    Collect,
}

/// <summary>Gatherer statuses the rotation conditions can test (`status:...`).</summary>
public enum GatherStatus
{
    EurekaMoment,
    Scrutiny,
    CollectorsFocus,
    PrimingTouch,

    /// <summary>Gathering Yield Up (Limited): a Bountiful Yield waiting for the next swing.</summary>
    BountifulYield,

    /// <summary>Gathering Yield Up (II): a King's Yield / Blessed Harvest for the node.</summary>
    YieldUp,
    GiftI,
    GiftII,
    Tidings,
    TwelvesBounty,
    GivingLand,
}

/// <summary>A cordial: GP restored NQ / HQ and its recast, strongest first in <see cref="GatheringActions.Cordials"/>.</summary>
public sealed record CordialInfo(uint ItemId, string Name, int GpNq, int GpHq, int CooldownSeconds, bool CanBeHq)
{
    public int Gp(bool hq) => hq && CanBeHq ? GpHq : GpNq;
}

/// <summary>
/// Gathering action vocabulary (spec §37, roadmap 7.14): English names per
/// job for the plugin's catalogue, GP costs, roles, and the cordials. The
/// costs and status ids were read from the Action / Status sheets on
/// 2026-09-15; the catalogue replaces the costs with the live sheet values.
/// </summary>
public static class GatheringActions
{
    public const uint MinerJobId = 16;
    public const uint BotanistJobId = 17;

    /// <summary>Fisher (roadmap 7.4). Fishing has its own action vocabulary; only the job id is shared.</summary>
    public const uint FisherJobId = 18;

    /// <summary>
    /// Cordials strongest first: Hi-Cordial (NQ only), Cordial, Watered
    /// Cordial. GP values from the ItemAction sheet (Data / DataHQ), recasts
    /// from the Item sheet; the three share one recast group in game.
    /// </summary>
    public static readonly CordialInfo[] Cordials =
    [
        new(12669, "Hi-Cordial", 400, 400, 180, CanBeHq: false),
        new(6141, "Cordial", 300, 350, 240, CanBeHq: true),
        new(16911, "Watered Cordial", 150, 200, 140, CanBeHq: true),
    ];

    /// <summary>Elemental shards (2–7), crystals (8–13) and clusters (14–19).</summary>
    public static bool IsCrystal(uint itemId) => itemId is >= 2 and <= 19;

    /// <summary>
    /// English Action-sheet names for the action on the job, preferred
    /// first (an upgraded action before the one it replaces). Empty for a
    /// job that is not MIN/BTN.
    /// </summary>
    public static IReadOnlyList<string> Names(GatherAction action, uint jobId)
    {
        var miner = jobId == MinerJobId;
        if (!miner && jobId != BotanistJobId)
            return [];

        return action switch
        {
            GatherAction.YieldI => [miner ? "King's Yield" : "Blessed Harvest"],
            GatherAction.YieldII => [miner ? "King's Yield II" : "Blessed Harvest II"],
            GatherAction.RestoreIntegrity => [miner ? "Solid Reason" : "Ageless Words"],
            GatherAction.BountifulYield => miner
                ? ["Bountiful Yield II", "Bountiful Yield"]
                : ["Bountiful Harvest II", "Bountiful Harvest"],
            GatherAction.GiftI => [miner ? "Mountaineer's Gift I" : "Pioneer's Gift I"],
            GatherAction.GiftII => [miner ? "Mountaineer's Gift II" : "Pioneer's Gift II"],
            GatherAction.Tidings => [miner ? "Nald'thal's Tidings" : "Nophica's Tidings"],
            GatherAction.TwelvesBounty => ["The Twelve's Bounty"],
            GatherAction.GivingLand => ["The Giving Land"],
            GatherAction.WiseToTheWorld => ["Wise to the World"],
            GatherAction.Luck => [miner ? "Luck of the Mountaineer" : "Luck of the Pioneer"],
            GatherAction.Scour => ["Scour"],
            GatherAction.Brazen => [miner ? "Brazen Prospector" : "Brazen Woodsman"],
            GatherAction.Meticulous => [miner ? "Meticulous Prospector" : "Meticulous Woodsman"],
            GatherAction.Scrutiny => ["Scrutiny"],
            GatherAction.CollectorsFocus => ["Collector's Focus"],
            GatherAction.PrimingTouch => ["Priming Touch"],
            GatherAction.Collect => ["Collect"],
            _ => [],
        };
    }

    /// <summary>GP cost per the Action sheet (PrimaryCostValue, 2026-09-15).</summary>
    public static int DefaultGpCost(GatherAction action) => action switch
    {
        GatherAction.YieldI => 400,
        GatherAction.YieldII => 500,
        GatherAction.RestoreIntegrity => 300,
        GatherAction.BountifulYield => 100,
        GatherAction.GiftI => 50,
        GatherAction.GiftII => 100,
        GatherAction.Tidings => 200,
        GatherAction.TwelvesBounty => 150,
        GatherAction.GivingLand => 200,
        GatherAction.Luck => 200,
        GatherAction.Scrutiny => 200,
        GatherAction.CollectorsFocus => 100,
        GatherAction.PrimingTouch => 100,
        _ => 0,
    };

    /// <summary>A buff that lasts the whole node, so firing it twice is a waste.</summary>
    public static bool IsOncePerNode(GatherAction action) => action is GatherAction.YieldI or GatherAction.YieldII
        or GatherAction.GiftI or GatherAction.GiftII or GatherAction.Tidings or GatherAction.TwelvesBounty
        or GatherAction.GivingLand or GatherAction.Luck or GatherAction.CollectorsFocus;

    /// <summary>Appraisals and Collect consume a gathering attempt (the collectable window's swing).</summary>
    public static bool ConsumesAttempt(GatherAction action) => action is GatherAction.Scour or GatherAction.Brazen
        or GatherAction.Meticulous or GatherAction.Collect;

    /// <summary>The status an action leaves on the player, if any (for the cost simulation and tests).</summary>
    public static GatherStatus? StatusOf(GatherAction action) => action switch
    {
        GatherAction.YieldI or GatherAction.YieldII => GatherStatus.YieldUp,
        GatherAction.BountifulYield => GatherStatus.BountifulYield,
        GatherAction.GiftI => GatherStatus.GiftI,
        GatherAction.GiftII => GatherStatus.GiftII,
        GatherAction.Tidings => GatherStatus.Tidings,
        GatherAction.TwelvesBounty => GatherStatus.TwelvesBounty,
        GatherAction.GivingLand => GatherStatus.GivingLand,
        GatherAction.Scrutiny => GatherStatus.Scrutiny,
        GatherAction.CollectorsFocus => GatherStatus.CollectorsFocus,
        GatherAction.PrimingTouch => GatherStatus.PrimingTouch,
        _ => null,
    };

    /// <summary>Statuses consumed by the next swing / appraisal.</summary>
    public static bool IsPerSwingStatus(GatherStatus status) =>
        status is GatherStatus.Scrutiny or GatherStatus.BountifulYield or GatherStatus.PrimingTouch;

    /// <summary>Status-sheet ids per status (2026-09-15); Yield Up has two rows (I and II).</summary>
    public static IReadOnlyList<uint> StatusIds(GatherStatus status) => status switch
    {
        GatherStatus.EurekaMoment => [2765],
        GatherStatus.Scrutiny => [757],
        GatherStatus.CollectorsFocus => [2668, 2418, 3911], // Collector's Focus / Standard / High Standard
        GatherStatus.PrimingTouch => [3910],
        GatherStatus.BountifulYield => [756],
        GatherStatus.YieldUp => [219, 1286],
        GatherStatus.GiftI => [2666],
        GatherStatus.GiftII => [759],
        GatherStatus.Tidings => [2667],
        GatherStatus.TwelvesBounty => [825],
        GatherStatus.GivingLand => [1802],
        _ => [],
    };

    /// <summary>Short display name used in logs and the settings page.</summary>
    public static string DisplayName(GatherAction action) => action switch
    {
        GatherAction.YieldI => "Yield I",
        GatherAction.YieldII => "Yield II",
        GatherAction.RestoreIntegrity => "Restore Integrity",
        GatherAction.BountifulYield => "Bountiful Yield",
        GatherAction.GiftI => "Gift I",
        GatherAction.GiftII => "Gift II",
        GatherAction.TwelvesBounty => "Twelve's Bounty",
        GatherAction.GivingLand => "Giving Land",
        GatherAction.WiseToTheWorld => "Wise to the World",
        GatherAction.CollectorsFocus => "Collector's Focus",
        GatherAction.PrimingTouch => "Priming Touch",
        _ => action.ToString(),
    };

    /// <summary>
    /// Parses an action from rotation text: the enum name or either job's
    /// in-game name, ignoring case, spaces and punctuation ("Yield II",
    /// "King's Yield II", "blessed harvest ii", "yield2" all match).
    /// </summary>
    public static bool TryParse(string text, out GatherAction action)
    {
        var key = Normalize(text);
        if (Aliases.TryGetValue(key, out action))
            return true;

        action = default;
        return false;
    }

    private static readonly Dictionary<string, GatherAction> Aliases = BuildAliases();

    private static Dictionary<string, GatherAction> BuildAliases()
    {
        var map = new Dictionary<string, GatherAction>(StringComparer.Ordinal);
        foreach (var action in Enum.GetValues<GatherAction>())
        {
            map[Normalize(action.ToString())] = action;
            map[Normalize(DisplayName(action))] = action;
            foreach (var job in new[] { MinerJobId, BotanistJobId })
            {
                foreach (var name in Names(action, job))
                    map[Normalize(name)] = action;
            }
        }

        map["yield1"] = GatherAction.YieldI;
        map["yield2"] = GatherAction.YieldII;
        map["gift1"] = GatherAction.GiftI;
        map["gift2"] = GatherAction.GiftII;
        map["wise"] = GatherAction.WiseToTheWorld;
        map["focus"] = GatherAction.CollectorsFocus;
        map["priming"] = GatherAction.PrimingTouch;
        map["bountiful"] = GatherAction.BountifulYield;
        map["bountifulharvest"] = GatherAction.BountifulYield;
        map["twelvesbounty"] = GatherAction.TwelvesBounty;
        map["givingland"] = GatherAction.GivingLand;
        map["mountaineersgift"] = GatherAction.GiftI;
        map["pioneersgift"] = GatherAction.GiftI;
        return map;
    }

    private static string Normalize(string text)
    {
        var chars = new char[text.Length];
        var n = 0;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                chars[n++] = char.ToLowerInvariant(c);
        }

        return new string(chars, 0, n);
    }
}
