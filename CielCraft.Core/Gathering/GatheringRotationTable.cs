using System;
using System.Collections.Generic;
using System.Text;

namespace CielCraft.Core;

/// <summary>Which built-in rotation table a node run uses (roadmap 7.14).</summary>
public enum NodeClass
{
    Normal,

    /// <summary>Unspoiled and legendary nodes: boon buffs pay because every item is rare.</summary>
    Unspoiled,

    /// <summary>Shards, crystals and clusters: Twelve's Bounty and The Giving Land.</summary>
    Crystal,

    /// <summary>Collectable appraisal (also every ephemeral node).</summary>
    Collectable,
}

/// <summary>The bonus a gathering point offers when a condition is met (GatheringPointBonus sheet).</summary>
public enum GatheringBonusKind
{
    Other,
    Boon,
    Yield,
    Attempts,
    GatheringRate,
    Collectability,
}

/// <summary>One bonus condition of the open node; Met = the character satisfies it (so the bonus is active).</summary>
public sealed record GatheringBonusCondition(GatheringBonusKind Kind, string Text, bool Met);

/// <summary>
/// Everything a rotation rule may test, gathered by the controller before
/// each decision. Unknown facts use their neutral value (boon chance -1 =
/// unknown, no bonuses, no statuses), which makes the rules that need them
/// false rather than wrong.
/// </summary>
public sealed record GatheringRotationContext
{
    private static readonly IReadOnlySet<GatherStatus> NoStatuses = new HashSet<GatherStatus>();
    private static readonly IReadOnlySet<GatherAction> NoActions = new HashSet<GatherAction>();

    public NodeClass Class { get; init; }

    public int Gp { get; init; }

    public int MaxGp { get; init; }

    public int Integrity { get; init; }

    public int IntegrityMax { get; init; }

    /// <summary>Items still needed; int.MaxValue when unlimited.</summary>
    public int Remaining { get; init; } = int.MaxValue;

    /// <summary>Items one swing yields on this node so far (at least 1).</summary>
    public int YieldPerSwing { get; init; } = 1;

    /// <summary>Gatherer's Boon chance in percent; -1 when it could not be read.</summary>
    public int BoonChance { get; init; } = -1;

    public IReadOnlyList<GatheringBonusCondition> Bonuses { get; init; } = [];

    public IReadOnlySet<GatherStatus> Statuses { get; init; } = NoStatuses;

    /// <summary>Actions already fired on this node.</summary>
    public IReadOnlySet<GatherAction> Used { get; init; } = NoActions;

    /// <summary>Actions the game refused on this node (level, unlock); never chosen again here.</summary>
    public IReadOnlySet<GatherAction> Unusable { get; init; } = NoActions;

    public int Collectability { get; init; }

    public int CollectabilityMax { get; init; }

    /// <summary>The collectability the run aims for (the ordered tier, or the highest threshold).</summary>
    public int CollectabilityGoal { get; init; }

    /// <summary>The lowest collectability that still counts (the last attempt settles for it).</summary>
    public int CollectabilityMinimum { get; init; }

    /// <summary>GP cost per action; the plugin passes the catalogue's sheet values, tests the defaults.</summary>
    public Func<GatherAction, int>? GpCost { get; init; }

    public bool LastAttempt => Integrity <= 1;

    public int CostOf(GatherAction action) => GpCost?.Invoke(action) ?? GatheringActions.DefaultGpCost(action);
}

/// <summary>One `when ...: action` line of a table.</summary>
public sealed class GatheringRotationRule
{
    internal GatheringRotationRule(int line, string text, IReadOnlyList<RotationCondition> conditions, GatherAction action)
    {
        Line = line;
        Text = text;
        Conditions = conditions;
        Action = action;
    }

    public int Line { get; }

    /// <summary>The source line, for logs and the settings page.</summary>
    public string Text { get; }

    internal IReadOnlyList<RotationCondition> Conditions { get; }

    public GatherAction Action { get; }

    public bool Matches(GatheringRotationContext context)
    {
        foreach (var condition in Conditions)
        {
            if (!condition.Evaluate(context))
                return false;
        }

        return true;
    }
}

/// <summary>Result of <see cref="GatheringRotationTable.Parse"/>: the rules that parsed plus every error with its line.</summary>
public sealed record GatheringRotationParse(GatheringRotationTable Table, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// A conditional gathering rotation (roadmap 7.14): rules evaluated top-down
/// each time the controller may act at a node; the first rule whose
/// conditions hold and whose action is affordable and not refused wins.
/// No rule = gather (or, on a collectable node, the controller's fallback).
///
/// Text format, one rule per line, `#` comments:
/// <code>
/// when gp >= 500, remaining > integrity*yield, !used:YieldII: YieldII
/// </code>
/// Conditions: `gp OP N`, `integrity OP N|max`, `remaining OP N|yield|integrity*yield`,
/// `boon OP N` (false while unknown), `collectability OP N|goal|min|max`,
/// `bonus:boon|yield|attempts|rate|any` (the node offers it and the character
/// does not meet its condition), `status:eureka|scrutiny|focus|priming|bountiful|yield|gift|gift2|tidings|bounty|givingland`,
/// `used:ACTION`, `kind:crystal|unspoiled|normal|collectable`, `lastAttempt`, `always`;
/// any condition may be negated with `!`. OP is one of `>= > <= < == !=`.
/// </summary>
public sealed class GatheringRotationTable
{
    /// <summary>Integrity of the notional node the GP-cost simulation runs on.</summary>
    public const int NominalIntegrity = 6;

    /// <summary>Per-swing GP actions the cost counts (two appraisal swings' worth of Scrutiny etc.).</summary>
    public const int CostSwings = 2;

    private static readonly Dictionary<NodeClass, GatheringRotationTable> BuiltIns = new();

    private GatheringRotationTable(NodeClass nodeClass, string source, IReadOnlyList<GatheringRotationRule> rules)
    {
        Class = nodeClass;
        Source = source;
        Rules = rules;
    }

    public NodeClass Class { get; }

    public string Source { get; }

    public IReadOnlyList<GatheringRotationRule> Rules { get; }

    /// <summary>The class name used as the overrides key ("Normal", "Unspoiled", "Crystal", "Collectable").</summary>
    public static string ClassName(NodeClass nodeClass) => nodeClass.ToString();

    /// <summary>The class a node run falls in: collectables (and ephemerals) first, then crystals, then timed-ness.</summary>
    public static NodeClass ClassFor(NodeKind kind, bool collectable, bool crystal)
    {
        if (collectable || kind == NodeKind.Ephemeral)
            return NodeClass.Collectable;
        if (crystal)
            return NodeClass.Crystal;
        return kind is NodeKind.Unspoiled or NodeKind.Legendary ? NodeClass.Unspoiled : NodeClass.Normal;
    }

    // ------------------------------------------------------------ built-ins

    /// <summary>The built-in table text for a class; the settings page shows it read-only.</summary>
    public static string BuiltInText(NodeClass nodeClass) => nodeClass switch
    {
        NodeClass.Unspoiled =>
            "# Unspoiled / legendary nodes: yield buffs by GP, then boon buffs (every item is rare).\n" +
            "when gp >= 500, remaining > integrity*yield, !used:YieldII, !used:YieldI: YieldII\n" +
            "when gp >= 400, remaining > integrity*yield, !used:YieldII, !used:YieldI: YieldI\n" +
            "# Gifts raise the boon chance: when it is under 100% (or could not be read), or a boon bonus is unmet.\n" +
            "when !boon >= 100, !used:GiftII: GiftII\n" +
            "when !boon >= 100, !used:GiftI: GiftI\n" +
            "when bonus:boon, !used:GiftII: GiftII\n" +
            "when bonus:boon, !used:GiftI: GiftI\n" +
            "when !used:Tidings: Tidings\n" +
            "when status:eureka, integrity < max: WiseToTheWorld\n" +
            "when integrity < max, remaining > integrity*yield: RestoreIntegrity\n" +
            "when gp >= 600, remaining > yield, !status:bountiful: BountifulYield\n",
        NodeClass.Crystal =>
            "# Shards, crystals and clusters: the crystal buffs first, then yield buffs.\n" +
            "when remaining > integrity*yield, !used:GivingLand: GivingLand\n" +
            "when remaining > integrity*yield, !used:TwelvesBounty: TwelvesBounty\n" +
            "when gp >= 500, remaining > integrity*yield, !used:YieldII, !used:YieldI: YieldII\n" +
            "when gp >= 400, remaining > integrity*yield, !used:YieldII, !used:YieldI: YieldI\n" +
            "when status:eureka, integrity < max: WiseToTheWorld\n" +
            "when integrity < max, remaining > integrity*yield: RestoreIntegrity\n" +
            "when gp >= 600, remaining > yield, !status:bountiful: BountifulYield\n",
        NodeClass.Collectable =>
            "# Collectables: Collect at the goal (or on the last attempt at the minimum that counts),\n" +
            "# otherwise Scrutiny then Meticulous; Scour when Scrutiny is unaffordable.\n" +
            "when collectability >= goal: Collect\n" +
            "when lastAttempt, collectability >= min: Collect\n" +
            "when gp >= 300, !used:CollectorsFocus: CollectorsFocus\n" +
            "when status:eureka, integrity < max: WiseToTheWorld\n" +
            "when gp >= 200, !status:scrutiny: Scrutiny\n" +
            "when status:scrutiny: Meticulous\n" +
            "when gp < 200: Scour\n" +
            "always: Meticulous\n",
        _ =>
            "# Normal nodes: a yield buff by GP threshold, then extra attempts while they pay.\n" +
            "when gp >= 500, remaining > integrity*yield, !used:YieldII, !used:YieldI: YieldII\n" +
            "when gp >= 400, remaining > integrity*yield, !used:YieldII, !used:YieldI: YieldI\n" +
            "when status:eureka, integrity < max: WiseToTheWorld\n" +
            "when integrity < max, remaining > integrity*yield: RestoreIntegrity\n" +
            "when gp >= 600, remaining > yield, !status:bountiful: BountifulYield\n",
    };

    public static GatheringRotationTable BuiltIn(NodeClass nodeClass)
    {
        lock (BuiltIns)
        {
            if (!BuiltIns.TryGetValue(nodeClass, out var table))
            {
                var parsed = Parse(BuiltInText(nodeClass), nodeClass);
                if (!parsed.Success)
                    throw new InvalidOperationException($"Built-in {nodeClass} table: {string.Join("; ", parsed.Errors)}");

                BuiltIns[nodeClass] = table = parsed.Table;
            }

            return table;
        }
    }

    // --------------------------------------------------------------- parse

    /// <summary>Parses table text; every bad line is an error and the good lines still form the table.</summary>
    public static GatheringRotationParse Parse(string text, NodeClass nodeClass = NodeClass.Normal)
    {
        var rules = new List<GatheringRotationRule>();
        var errors = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var comment = raw.IndexOf('#');
            var line = (comment >= 0 ? raw[..comment] : raw).Trim();
            if (line.Length == 0)
                continue;

            var lineNumber = i + 1;
            var separator = line.LastIndexOf(':');
            if (separator < 0)
            {
                errors.Add($"line {lineNumber}: expected 'when <conditions>: <action>'");
                continue;
            }

            var head = line[..separator].Trim();
            var actionText = line[(separator + 1)..].Trim();
            if (!GatheringActions.TryParse(actionText, out var action))
            {
                errors.Add($"line {lineNumber}: unknown action '{actionText}'");
                continue;
            }

            if (head.StartsWith("when ", StringComparison.OrdinalIgnoreCase))
                head = head[5..].Trim();
            else if (!head.Equals("always", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"line {lineNumber}: expected 'when' or 'always' before the conditions");
                continue;
            }

            var conditions = new List<RotationCondition>();
            var ok = true;
            foreach (var part in head.Split(','))
            {
                var conditionText = part.Trim();
                if (conditionText.Length == 0)
                    continue;

                if (RotationCondition.TryParse(conditionText, out var condition, out var error))
                    conditions.Add(condition);
                else
                {
                    errors.Add($"line {lineNumber}: {error}");
                    ok = false;
                }
            }

            if (ok)
                rules.Add(new GatheringRotationRule(lineNumber, line, conditions, action));
        }

        return new GatheringRotationParse(new GatheringRotationTable(nodeClass, text, rules), errors);
    }

    // ------------------------------------------------------------ evaluate

    /// <summary>
    /// The first rule (top-down) whose conditions hold, whose action is
    /// affordable at the context's GP and was not refused on this node.
    /// Null when nothing applies (gather / the controller's fallback).
    /// </summary>
    public GatherAction? Next(GatheringRotationContext context, out GatheringRotationRule? rule)
    {
        foreach (var candidate in Rules)
        {
            // A whole-node buff never fires twice, whatever the rule says:
            // the game would accept it and only burn the GP.
            if (context.Unusable.Contains(candidate.Action) || context.CostOf(candidate.Action) > context.Gp
                || (GatheringActions.IsOncePerNode(candidate.Action) && context.Used.Contains(candidate.Action)))
                continue;

            if (candidate.Matches(context))
            {
                rule = candidate;
                return candidate.Action;
            }
        }

        rule = null;
        return null;
    }

    public GatherAction? Next(GatheringRotationContext context) => Next(context, out _);

    // ---------------------------------------------------------------- cost

    /// <summary>
    /// GP a full run of this table wants (roadmap 7.15 slot maths): the
    /// once-per-node buffs the top rules fire at full GP on a notional
    /// node (integrity <see cref="NominalIntegrity"/>, unlimited need, boon
    /// chance below 100, no bonuses), plus <see cref="CostSwings"/> swings'
    /// worth of the per-swing GP actions that chain fires (Scrutiny,
    /// Bountiful Yield). Capped at maxGp when positive.
    /// </summary>
    public int EstimateGpPerNode(int maxGp, Func<GatherAction, int>? gpCost = null)
    {
        var gp = maxGp > 0 ? maxGp : 1000;
        var used = new HashSet<GatherAction>();
        var statuses = new HashSet<GatherStatus>();
        var oncePerNode = 0;
        var perSwing = 0;
        for (var guard = 0; guard < 32; guard++)
        {
            var context = new GatheringRotationContext
            {
                Class = Class,
                Gp = gp,
                MaxGp = gp,
                Integrity = NominalIntegrity,
                IntegrityMax = NominalIntegrity,
                BoonChance = 50,
                Statuses = statuses,
                Used = used,
                CollectabilityGoal = 1000,
                CollectabilityMinimum = 1000,
                CollectabilityMax = 1000,
                GpCost = gpCost,
            };
            var action = Next(context);
            if (action == null || GatheringActions.ConsumesAttempt(action.Value) || !used.Add(action.Value))
                break;

            var cost = context.CostOf(action.Value);
            gp -= cost;
            if (GatheringActions.IsOncePerNode(action.Value))
                oncePerNode += cost;
            else
                perSwing += cost;

            if (GatheringActions.StatusOf(action.Value) is { } status)
                statuses.Add(status);
        }

        var wanted = oncePerNode + perSwing * CostSwings;
        return maxGp > 0 ? Math.Min(wanted, maxGp) : wanted;
    }

    /// <summary>The rules as text for logs; the built-in text is kept verbatim in <see cref="Source"/>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var rule in Rules)
            text.AppendLine(rule.Text);
        return text.ToString();
    }
}

/// <summary>
/// Picks the table for a node class: the user's override from
/// <see cref="AutomationSettings.GatheringRotationOverrides"/> when it
/// parses, else the built-in. A parse error is logged once per distinct
/// override text.
/// </summary>
public sealed class GatheringRotationSet
{
    private readonly AutomationSettings settings;
    private readonly ILog? log;
    private readonly Dictionary<NodeClass, (string Text, GatheringRotationTable Table)> cache = new();

    public GatheringRotationSet(AutomationSettings settings, ILog? log = null)
    {
        this.settings = settings;
        this.log = log;
    }

    /// <summary>True when the class runs on a user override rather than the built-in table.</summary>
    public bool IsOverridden(NodeClass nodeClass) => !ReferenceEquals(For(nodeClass), GatheringRotationTable.BuiltIn(nodeClass));

    public GatheringRotationTable For(NodeClass nodeClass)
    {
        var text = settings.GatheringRotationOverrides.TryGetValue(GatheringRotationTable.ClassName(nodeClass), out var value)
            ? value.Trim()
            : "";

        if (cache.TryGetValue(nodeClass, out var cached) && cached.Text == text)
            return cached.Table;

        var table = GatheringRotationTable.BuiltIn(nodeClass);
        if (text.Length > 0)
        {
            var parsed = GatheringRotationTable.Parse(text, nodeClass);
            if (parsed.Success && parsed.Table.Rules.Count > 0)
            {
                table = parsed.Table;
                log?.Information($"[Gather] Using the {nodeClass} rotation override ({table.Rules.Count} rules).");
            }
            else
            {
                var reason = parsed.Success ? "it has no rules" : string.Join("; ", parsed.Errors);
                log?.Warning($"[Gather] Ignoring the {nodeClass} rotation override ({reason}); using the built-in table.");
            }
        }

        cache[nodeClass] = (text, table);
        return table;
    }
}

/// <summary>One parsed condition of a rule.</summary>
internal sealed class RotationCondition
{
    private enum Kind
    {
        Always,
        LastAttempt,
        Gp,
        Integrity,
        Remaining,
        Boon,
        Collectability,
        Bonus,
        Status,
        Used,
        NodeClass,
    }

    private enum Operand
    {
        Number,
        Max,
        Goal,
        Min,
        Yield,
        IntegrityTimesYield,
    }

    private readonly Kind kind;
    private readonly bool negated;
    private readonly string op;
    private readonly Operand operand;
    private readonly int number;
    private readonly GatheringBonusKind bonus;
    private readonly bool anyBonus;
    private readonly GatherStatus status;
    private readonly GatherAction action;
    private readonly NodeClass nodeClass;

    private RotationCondition(
        Kind kind, bool negated, string op = "", Operand operand = Operand.Number, int number = 0,
        GatheringBonusKind bonus = GatheringBonusKind.Other, bool anyBonus = false, GatherStatus status = default,
        GatherAction action = default, NodeClass nodeClass = default)
    {
        this.kind = kind;
        this.negated = negated;
        this.op = op;
        this.operand = operand;
        this.number = number;
        this.bonus = bonus;
        this.anyBonus = anyBonus;
        this.status = status;
        this.action = action;
        this.nodeClass = nodeClass;
    }

    public bool Evaluate(GatheringRotationContext context)
    {
        var result = kind switch
        {
            Kind.Always => true,
            Kind.LastAttempt => context.LastAttempt,
            Kind.Gp => Compare(context.Gp, number),
            Kind.Integrity => Compare(context.Integrity, operand == Operand.Max ? context.IntegrityMax : number),
            Kind.Remaining => Compare(context.Remaining, operand switch
            {
                Operand.Yield => context.YieldPerSwing,
                Operand.IntegrityTimesYield => context.Integrity * context.YieldPerSwing,
                _ => number,
            }),
            // An unknown boon chance makes every comparison false: the rule
            // that needs it is skipped rather than fired on a guess.
            Kind.Boon => context.BoonChance >= 0 && Compare(context.BoonChance, number),
            Kind.Collectability => Compare(context.Collectability, operand switch
            {
                Operand.Goal => context.CollectabilityGoal,
                Operand.Min => context.CollectabilityMinimum,
                Operand.Max => context.CollectabilityMax,
                _ => number,
            }),
            Kind.Bonus => HasUnmetBonus(context),
            Kind.Status => context.Statuses.Contains(status),
            Kind.Used => context.Used.Contains(action),
            Kind.NodeClass => context.Class == nodeClass,
            _ => false,
        };
        return negated ? !result : result;
    }

    private bool HasUnmetBonus(GatheringRotationContext context)
    {
        foreach (var condition in context.Bonuses)
        {
            if (!condition.Met && (anyBonus || condition.Kind == bonus))
                return true;
        }

        return false;
    }

    private bool Compare(long left, long right) => op switch
    {
        ">=" => left >= right,
        ">" => left > right,
        "<=" => left <= right,
        "<" => left < right,
        "==" => left == right,
        "!=" => left != right,
        _ => false,
    };

    public static bool TryParse(string text, out RotationCondition condition, out string error)
    {
        condition = null!;
        error = "";
        var negated = false;
        var body = text.Trim();
        while (body.StartsWith('!'))
        {
            negated = !negated;
            body = body[1..].TrimStart();
        }

        if (body.Length == 0)
        {
            error = "empty condition";
            return false;
        }

        if (body.Equals("always", StringComparison.OrdinalIgnoreCase))
        {
            condition = new RotationCondition(Kind.Always, negated);
            return true;
        }

        if (body.Equals("lastAttempt", StringComparison.OrdinalIgnoreCase) || body.Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            condition = new RotationCondition(Kind.LastAttempt, negated);
            return true;
        }

        var colon = body.IndexOf(':');
        if (colon > 0)
        {
            var key = body[..colon].Trim().ToLowerInvariant();
            var value = body[(colon + 1)..].Trim();
            switch (key)
            {
                case "bonus":
                    return ParseBonus(value, negated, out condition, out error);
                case "status":
                    return ParseStatus(value, negated, out condition, out error);
                case "used":
                    if (!GatheringActions.TryParse(value, out var action))
                    {
                        error = $"unknown action '{value}' in used:";
                        return false;
                    }

                    condition = new RotationCondition(Kind.Used, negated, action: action);
                    return true;
                case "kind":
                    return ParseKind(value, negated, out condition, out error);
                default:
                    error = $"unknown condition '{body}'";
                    return false;
            }
        }

        return ParseComparison(body, negated, out condition, out error);
    }

    private static bool ParseBonus(string value, bool negated, out RotationCondition condition, out string error)
    {
        condition = null!;
        error = "";
        switch (value.ToLowerInvariant())
        {
            case "boon":
                condition = new RotationCondition(Kind.Bonus, negated, bonus: GatheringBonusKind.Boon);
                return true;
            case "yield":
                condition = new RotationCondition(Kind.Bonus, negated, bonus: GatheringBonusKind.Yield);
                return true;
            case "attempts":
            case "integrity":
                condition = new RotationCondition(Kind.Bonus, negated, bonus: GatheringBonusKind.Attempts);
                return true;
            case "rate":
                condition = new RotationCondition(Kind.Bonus, negated, bonus: GatheringBonusKind.GatheringRate);
                return true;
            case "collectability":
                condition = new RotationCondition(Kind.Bonus, negated, bonus: GatheringBonusKind.Collectability);
                return true;
            case "any":
                condition = new RotationCondition(Kind.Bonus, negated, anyBonus: true);
                return true;
            default:
                error = $"unknown bonus '{value}' (boon, yield, attempts, rate, collectability, any)";
                return false;
        }
    }

    private static bool ParseStatus(string value, bool negated, out RotationCondition condition, out string error)
    {
        condition = null!;
        error = "";
        GatherStatus? status = value.ToLowerInvariant() switch
        {
            "eureka" or "eurekamoment" => GatherStatus.EurekaMoment,
            "scrutiny" => GatherStatus.Scrutiny,
            "focus" or "collectorsfocus" => GatherStatus.CollectorsFocus,
            "priming" or "primingtouch" => GatherStatus.PrimingTouch,
            "bountiful" or "bountifulyield" => GatherStatus.BountifulYield,
            "yield" or "yieldup" => GatherStatus.YieldUp,
            "gift" or "gift1" or "gifti" => GatherStatus.GiftI,
            "gift2" or "giftii" => GatherStatus.GiftII,
            "tidings" => GatherStatus.Tidings,
            "bounty" or "twelvesbounty" => GatherStatus.TwelvesBounty,
            "givingland" => GatherStatus.GivingLand,
            _ => null,
        };
        if (status == null)
        {
            error = $"unknown status '{value}' (eureka, scrutiny, focus, priming, bountiful, yield, gift, gift2, tidings, bounty, givingland)";
            return false;
        }

        condition = new RotationCondition(Kind.Status, negated, status: status.Value);
        return true;
    }

    private static bool ParseKind(string value, bool negated, out RotationCondition condition, out string error)
    {
        condition = null!;
        error = "";
        NodeClass? nodeClass = value.ToLowerInvariant() switch
        {
            "crystal" => NodeClass.Crystal,
            "unspoiled" or "legendary" => NodeClass.Unspoiled,
            "normal" => NodeClass.Normal,
            "collectable" or "ephemeral" => NodeClass.Collectable,
            _ => null,
        };
        if (nodeClass == null)
        {
            error = $"unknown node kind '{value}' (crystal, unspoiled, normal, collectable)";
            return false;
        }

        condition = new RotationCondition(Kind.NodeClass, negated, nodeClass: nodeClass.Value);
        return true;
    }

    private static readonly string[] Operators = [">=", "<=", "==", "!=", ">", "<"];

    private static bool ParseComparison(string body, bool negated, out RotationCondition condition, out string error)
    {
        condition = null!;
        error = "";
        var opIndex = -1;
        var op = "";
        foreach (var candidate in Operators)
        {
            var index = body.IndexOf(candidate, StringComparison.Ordinal);
            if (index > 0 && (opIndex < 0 || index < opIndex || (index == opIndex && candidate.Length > op.Length)))
            {
                opIndex = index;
                op = candidate;
            }
        }

        if (opIndex < 0)
        {
            error = $"unknown condition '{body}'";
            return false;
        }

        var name = body[..opIndex].Trim().ToLowerInvariant();
        var value = body[(opIndex + op.Length)..].Trim().Replace(" ", "").ToLowerInvariant();
        var kind = name switch
        {
            "gp" => Kind.Gp,
            "integrity" => Kind.Integrity,
            "remaining" => Kind.Remaining,
            "boon" => Kind.Boon,
            "collectability" => Kind.Collectability,
            _ => (Kind?)null,
        };
        if (kind == null)
        {
            error = $"unknown value '{name}' (gp, integrity, remaining, boon, collectability)";
            return false;
        }

        if (int.TryParse(value, out var number))
        {
            condition = new RotationCondition(kind.Value, negated, op, Operand.Number, number);
            return true;
        }

        var operand = (kind.Value, value) switch
        {
            (Kind.Integrity, "max") => Operand.Max,
            (Kind.Remaining, "yield") => Operand.Yield,
            (Kind.Remaining, "integrity*yield") => Operand.IntegrityTimesYield,
            (Kind.Collectability, "goal") => Operand.Goal,
            (Kind.Collectability, "min") => Operand.Min,
            (Kind.Collectability, "max") => Operand.Max,
            _ => (Operand?)null,
        };
        if (operand == null)
        {
            error = $"'{name}' cannot be compared with '{value}'";
            return false;
        }

        condition = new RotationCondition(kind.Value, negated, op, operand.Value);
        return true;
    }
}
