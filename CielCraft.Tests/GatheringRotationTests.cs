using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class GatheringRotationTests
{
    private static GatheringRotationTable Table(string text, NodeClass nodeClass = NodeClass.Normal)
    {
        var parsed = GatheringRotationTable.Parse(text, nodeClass);
        Assert.True(parsed.Success, string.Join("; ", parsed.Errors));
        return parsed.Table;
    }

    private static GatheringRotationContext Context(
        int gp = 1000,
        int integrity = 6,
        int integrityMax = 6,
        int remaining = int.MaxValue,
        int yield = 1,
        int boon = -1,
        IReadOnlyList<GatheringBonusCondition>? bonuses = null,
        IEnumerable<GatherStatus>? statuses = null,
        IEnumerable<GatherAction>? used = null,
        IEnumerable<GatherAction>? unusable = null,
        NodeClass nodeClass = NodeClass.Normal,
        int collectability = 0,
        int goal = 1000,
        int minimum = 400) => new()
    {
        Class = nodeClass,
        Gp = gp,
        MaxGp = 1000,
        Integrity = integrity,
        IntegrityMax = integrityMax,
        Remaining = remaining,
        YieldPerSwing = yield,
        BoonChance = boon,
        Bonuses = bonuses ?? [],
        Statuses = new HashSet<GatherStatus>(statuses ?? []),
        Used = new HashSet<GatherAction>(used ?? []),
        Unusable = new HashSet<GatherAction>(unusable ?? []),
        Collectability = collectability,
        CollectabilityMax = 1000,
        CollectabilityGoal = goal,
        CollectabilityMinimum = minimum,
    };

    // ------------------------------------------------------------- parser

    [Fact]
    public void ParsesRulesCommentsAndBlankLines()
    {
        var table = Table(
            "# a comment\n" +
            "\n" +
            "when gp >= 500, remaining > integrity*yield, !used:YieldII: YieldII   # trailing comment\n" +
            "always: Meticulous\r\n");

        Assert.Equal(2, table.Rules.Count);
        Assert.Equal(3, table.Rules[0].Line);
        Assert.Equal(GatherAction.YieldII, table.Rules[0].Action);
        Assert.Equal(GatherAction.Meticulous, table.Rules[1].Action);
    }

    [Fact]
    public void ActionNamesAcceptGameNamesAndAliases()
    {
        var table = Table(
            "always: King's Yield II\n" +
            "always: blessed harvest\n" +
            "always: Solid Reason\n" +
            "always: Nophica's Tidings\n" +
            "always: the Twelve's Bounty\n" +
            "always: Meticulous Woodsman\n" +
            "always: gift2\n" +
            "always: Collector's Focus\n");

        Assert.Equal(
            [GatherAction.YieldII, GatherAction.YieldI, GatherAction.RestoreIntegrity, GatherAction.Tidings,
             GatherAction.TwelvesBounty, GatherAction.Meticulous, GatherAction.GiftII, GatherAction.CollectorsFocus],
            table.Rules.Select(r => r.Action));
    }

    [Fact]
    public void ReportsErrorsWithLineNumbersAndKeepsGoodRules()
    {
        var parsed = GatheringRotationTable.Parse(
            "when gp >= 500: YieldII\n" +
            "when gp >= 500: Fireball\n" +
            "gp >= 500: YieldI\n" +
            "when mana > 3: YieldI\n" +
            "when bonus:luck: YieldI\n" +
            "when integrity > goal: YieldI\n" +
            "no colon here\n");

        Assert.False(parsed.Success);
        Assert.Equal(6, parsed.Errors.Count);
        Assert.Contains(parsed.Errors, e => e.StartsWith("line 2:") && e.Contains("Fireball"));
        Assert.Contains(parsed.Errors, e => e.StartsWith("line 3:") && e.Contains("when"));
        Assert.Contains(parsed.Errors, e => e.StartsWith("line 4:") && e.Contains("mana"));
        Assert.Contains(parsed.Errors, e => e.StartsWith("line 5:") && e.Contains("luck"));
        Assert.Contains(parsed.Errors, e => e.StartsWith("line 6:") && e.Contains("goal"));
        Assert.Contains(parsed.Errors, e => e.StartsWith("line 7:"));
        Assert.Single(parsed.Table.Rules);
    }

    [Fact]
    public void BuiltInTablesParseForEveryClass()
    {
        foreach (var nodeClass in new[] { NodeClass.Normal, NodeClass.Unspoiled, NodeClass.Crystal, NodeClass.Collectable })
        {
            var table = GatheringRotationTable.BuiltIn(nodeClass);
            Assert.Equal(nodeClass, table.Class);
            Assert.NotEmpty(table.Rules);
        }
    }

    // ---------------------------------------------------------- evaluator

    [Fact]
    public void FirstMatchingAffordableRuleWins()
    {
        var table = Table(
            "when gp >= 500: YieldII\n" +
            "when gp >= 400: YieldI\n");

        Assert.Equal(GatherAction.YieldII, table.Next(Context(gp: 600)));
        Assert.Equal(GatherAction.YieldI, table.Next(Context(gp: 450)));
        Assert.Null(table.Next(Context(gp: 300)));
    }

    [Fact]
    public void UnaffordableAndRefusedActionsFallThrough()
    {
        var table = Table(
            "when gp >= 200: Scrutiny\n" +
            "always: Meticulous\n");

        // Scrutiny costs 200 GP: an "always" style rule still needs the GP.
        Assert.Equal(GatherAction.Scrutiny, table.Next(Context(gp: 200)));
        Assert.Equal(GatherAction.Meticulous, table.Next(Context(gp: 199)));
        Assert.Equal(GatherAction.Meticulous, table.Next(Context(gp: 500, unusable: [GatherAction.Scrutiny])));
    }

    [Fact]
    public void OncePerNodeBuffsNeverFireTwice()
    {
        var table = Table("when gp >= 500: YieldII\nalways: RestoreIntegrity\n");

        Assert.Equal(GatherAction.YieldII, table.Next(Context()));
        Assert.Equal(GatherAction.RestoreIntegrity, table.Next(Context(used: [GatherAction.YieldII])));
        // Per-swing actions may repeat unless the rule says !used.
        Assert.Equal(GatherAction.RestoreIntegrity, table.Next(Context(used: [GatherAction.YieldII, GatherAction.RestoreIntegrity])));
    }

    [Fact]
    public void RemainingComparesAgainstIntegrityTimesYield()
    {
        var table = Table("when remaining > integrity*yield: YieldII\nwhen remaining > yield: BountifulYield\n");

        Assert.Equal(GatherAction.YieldII, table.Next(Context(integrity: 4, yield: 2, remaining: 9)));
        Assert.Equal(GatherAction.BountifulYield, table.Next(Context(integrity: 4, yield: 2, remaining: 8)));
        Assert.Null(table.Next(Context(integrity: 4, yield: 2, remaining: 2)));
        Assert.Equal(GatherAction.YieldII, table.Next(Context(integrity: 4, yield: 2)));  // unlimited need
    }

    [Fact]
    public void IntegrityMaxAndLastAttempt()
    {
        var table = Table("when lastAttempt: Collect\nwhen integrity < max: RestoreIntegrity\n");

        Assert.Null(table.Next(Context(integrity: 6, integrityMax: 6)));
        Assert.Equal(GatherAction.RestoreIntegrity, table.Next(Context(integrity: 3, integrityMax: 6)));
        Assert.Equal(GatherAction.Collect, table.Next(Context(integrity: 1, integrityMax: 6)));
    }

    [Fact]
    public void UnknownBoonChanceSkipsBoonRulesButNegationStillFires()
    {
        var table = Table("when boon < 100: GiftII\n");
        var negated = Table("when !boon >= 100: GiftII\n");

        Assert.Null(table.Next(Context(boon: -1)));
        Assert.Equal(GatherAction.GiftII, table.Next(Context(boon: 60)));
        Assert.Null(table.Next(Context(boon: 100)));

        Assert.Equal(GatherAction.GiftII, negated.Next(Context(boon: -1)));
        Assert.Equal(GatherAction.GiftII, negated.Next(Context(boon: 60)));
        Assert.Null(negated.Next(Context(boon: 100)));
    }

    [Fact]
    public void BonusConditionsCountOnlyWhenUnmet()
    {
        var table = Table("when bonus:boon: GiftI\nwhen bonus:any: Luck\n");
        var unmetBoon = new List<GatheringBonusCondition> { new(GatheringBonusKind.Boon, "Perception ≥ 900", Met: false) };
        var metBoon = new List<GatheringBonusCondition> { new(GatheringBonusKind.Boon, "Perception ≥ 42", Met: true) };
        var unmetYield = new List<GatheringBonusCondition> { new(GatheringBonusKind.Yield, "Gathering ≥ 900", Met: false) };

        Assert.Equal(GatherAction.GiftI, table.Next(Context(bonuses: unmetBoon)));
        Assert.Null(table.Next(Context(bonuses: metBoon)));
        Assert.Equal(GatherAction.Luck, table.Next(Context(bonuses: unmetYield)));
        Assert.Null(table.Next(Context()));
    }

    [Fact]
    public void StatusesUsedAndKindConditions()
    {
        var table = Table(
            "when status:eureka, integrity < max: WiseToTheWorld\n" +
            "when kind:crystal, !used:TwelvesBounty: TwelvesBounty\n" +
            "when !status:bountiful: BountifulYield\n");

        Assert.Equal(GatherAction.WiseToTheWorld, table.Next(Context(integrity: 3, statuses: [GatherStatus.EurekaMoment])));
        Assert.Equal(GatherAction.TwelvesBounty, table.Next(Context(nodeClass: NodeClass.Crystal)));
        Assert.Equal(GatherAction.BountifulYield, table.Next(Context(nodeClass: NodeClass.Crystal, used: [GatherAction.TwelvesBounty])));
        Assert.Null(table.Next(Context(statuses: [GatherStatus.BountifulYield])));
    }

    [Fact]
    public void CollectableConditions()
    {
        var table = GatheringRotationTable.BuiltIn(NodeClass.Collectable);

        // Under the goal with GP: Scrutiny first, then Meticulous under its status.
        Assert.Equal(GatherAction.CollectorsFocus, table.Next(Context(nodeClass: NodeClass.Collectable, gp: 800, collectability: 100, goal: 800)));
        Assert.Equal(GatherAction.Scrutiny, table.Next(Context(nodeClass: NodeClass.Collectable, gp: 700, collectability: 100, goal: 800, used: [GatherAction.CollectorsFocus])));
        Assert.Equal(GatherAction.Meticulous, table.Next(Context(nodeClass: NodeClass.Collectable, gp: 500, collectability: 100, goal: 800, used: [GatherAction.CollectorsFocus], statuses: [GatherStatus.Scrutiny])));
        // No GP for Scrutiny: Scour.
        Assert.Equal(GatherAction.Scour, table.Next(Context(nodeClass: NodeClass.Collectable, gp: 150, collectability: 100, goal: 800, used: [GatherAction.CollectorsFocus])));
        // At the goal: Collect; on the last attempt at the minimum: Collect.
        Assert.Equal(GatherAction.Collect, table.Next(Context(nodeClass: NodeClass.Collectable, gp: 800, collectability: 800, goal: 800)));
        Assert.Equal(GatherAction.Collect, table.Next(Context(nodeClass: NodeClass.Collectable, gp: 800, integrity: 1, collectability: 450, goal: 800, minimum: 400)));
        Assert.NotEqual(GatherAction.Collect, table.Next(Context(nodeClass: NodeClass.Collectable, gp: 800, integrity: 1, collectability: 350, goal: 800, minimum: 400)));
    }

    [Fact]
    public void BuiltInNormalTableSpendsAsSpecified()
    {
        var table = GatheringRotationTable.BuiltIn(NodeClass.Normal);

        Assert.Equal(GatherAction.YieldII, table.Next(Context(gp: 500)));
        Assert.Equal(GatherAction.YieldI, table.Next(Context(gp: 450)));
        Assert.Equal(GatherAction.YieldI, table.Next(Context(gp: 900, unusable: [GatherAction.YieldII])));
        // A node that alone covers the need gets no yield buff; only the per-swing Bountiful Yield when GP is plentiful.
        Assert.Equal(GatherAction.BountifulYield, table.Next(Context(gp: 900, integrity: 6, yield: 2, remaining: 10)));
        Assert.Null(table.Next(Context(gp: 500, integrity: 6, yield: 2, remaining: 10)));
        // After the buff: restore integrity while it pays, Eureka Moment first.
        Assert.Equal(GatherAction.RestoreIntegrity, table.Next(Context(gp: 400, integrity: 3, used: [GatherAction.YieldII])));
        Assert.Equal(GatherAction.WiseToTheWorld, table.Next(Context(gp: 100, integrity: 3, used: [GatherAction.YieldII], statuses: [GatherStatus.EurekaMoment])));
        Assert.Null(table.Next(Context(gp: 200, integrity: 3, used: [GatherAction.YieldII])));
    }

    [Fact]
    public void BuiltInUnspoiledAndCrystalTablesUseTheirBuffs()
    {
        var unspoiled = GatheringRotationTable.BuiltIn(NodeClass.Unspoiled);
        Assert.Equal(GatherAction.YieldII, unspoiled.Next(Context(gp: 1000, boon: 60)));
        Assert.Equal(GatherAction.GiftII, unspoiled.Next(Context(gp: 500, boon: 60, used: [GatherAction.YieldII])));
        Assert.Equal(GatherAction.GiftI, unspoiled.Next(Context(gp: 400, boon: 60, used: [GatherAction.YieldII, GatherAction.GiftII])));
        Assert.Equal(GatherAction.Tidings, unspoiled.Next(Context(gp: 350, boon: 60, used: [GatherAction.YieldII, GatherAction.GiftII, GatherAction.GiftI])));
        // Boon already capped: no Gifts, still Tidings.
        Assert.Equal(GatherAction.Tidings, unspoiled.Next(Context(gp: 500, boon: 100, used: [GatherAction.YieldII])));
        // Boon capped but a boon bonus unmet: Gifts still fire.
        var unmet = new List<GatheringBonusCondition> { new(GatheringBonusKind.Boon, "Perception ≥ 900", false) };
        Assert.Equal(GatherAction.GiftII, unspoiled.Next(Context(gp: 500, boon: 100, bonuses: unmet, used: [GatherAction.YieldII])));

        var crystal = GatheringRotationTable.BuiltIn(NodeClass.Crystal);
        Assert.Equal(GatherAction.GivingLand, crystal.Next(Context(nodeClass: NodeClass.Crystal)));
        Assert.Equal(GatherAction.TwelvesBounty, crystal.Next(Context(nodeClass: NodeClass.Crystal, used: [GatherAction.GivingLand])));
        Assert.Equal(GatherAction.YieldII, crystal.Next(Context(nodeClass: NodeClass.Crystal, used: [GatherAction.GivingLand, GatherAction.TwelvesBounty])));
    }

    // ---------------------------------------------------------------- cost

    [Fact]
    public void CostSumsTheBuffsTheTopRulesFireAtFullGp()
    {
        Assert.Equal(500, GatheringRotationTable.BuiltIn(NodeClass.Normal).EstimateGpPerNode(1000));
        Assert.Equal(850, GatheringRotationTable.BuiltIn(NodeClass.Unspoiled).EstimateGpPerNode(1000));
        Assert.Equal(850, GatheringRotationTable.BuiltIn(NodeClass.Crystal).EstimateGpPerNode(1000));
        // Collector's Focus once + Scrutiny for two swings.
        Assert.Equal(500, GatheringRotationTable.BuiltIn(NodeClass.Collectable).EstimateGpPerNode(1000));
    }

    [Fact]
    public void CostIsCappedByMaxGpAndFollowsWhatFitsThere()
    {
        // 600 GP: Yield II fires (500), nothing else fits.
        Assert.Equal(500, GatheringRotationTable.BuiltIn(NodeClass.Normal).EstimateGpPerNode(600));
        // 450 GP: only Yield I fits.
        Assert.Equal(400, GatheringRotationTable.BuiltIn(NodeClass.Normal).EstimateGpPerNode(450));
        Assert.Equal(300, Table("always: RestoreIntegrity\nalways: Scrutiny\n").EstimateGpPerNode(300));
    }

    [Fact]
    public void GpPerNodeMapsKindsToClassesAndHonoursOverrides()
    {
        Assert.Equal(500, GatheringRotationCost.GpPerNode(NodeKind.Normal, collectable: false, maxGp: 1000));
        Assert.Equal(850, GatheringRotationCost.GpPerNode(NodeKind.Unspoiled, collectable: false, maxGp: 1000));
        Assert.Equal(850, GatheringRotationCost.GpPerNode(NodeKind.Legendary, collectable: false, maxGp: 1000));
        Assert.Equal(500, GatheringRotationCost.GpPerNode(NodeKind.Ephemeral, collectable: false, maxGp: 1000));
        Assert.Equal(500, GatheringRotationCost.GpPerNode(NodeKind.Normal, collectable: true, maxGp: 1000));
        Assert.Equal(850, GatheringRotationCost.GpPerNode(NodeKind.Normal, collectable: false, maxGp: 1000, null, crystal: true));
        Assert.Equal(400, GatheringRotationCost.GpPerNode(NodeKind.Normal, collectable: false, maxGp: 400));

        var settings = new AutomationSettings();
        settings.GatheringRotationOverrides["Normal"] = "when gp >= 400: YieldI\n";
        Assert.Equal(400, GatheringRotationCost.GpPerNode(NodeKind.Normal, collectable: false, maxGp: 1000, settings));
    }

    [Fact]
    public void OverrideReplacesTheBuiltInWhenItParsesAndIsLoggedOnceWhenNot()
    {
        var log = new ListLog();
        var settings = new AutomationSettings();
        var set = new GatheringRotationSet(settings, log);

        Assert.Same(GatheringRotationTable.BuiltIn(NodeClass.Normal), set.For(NodeClass.Normal));
        Assert.False(set.IsOverridden(NodeClass.Normal));

        settings.GatheringRotationOverrides["Normal"] = "when gp >= 400: YieldI\n";
        Assert.True(set.IsOverridden(NodeClass.Normal));
        Assert.Equal(GatherAction.YieldI, set.For(NodeClass.Normal).Next(Context(gp: 1000)));

        settings.GatheringRotationOverrides["Normal"] = "when gp >= 400: Fireball\n";
        Assert.Same(GatheringRotationTable.BuiltIn(NodeClass.Normal), set.For(NodeClass.Normal));
        set.For(NodeClass.Normal);
        set.For(NodeClass.Normal);
        Assert.Single(log.Lines, line => line.StartsWith("WRN") && line.Contains("Fireball"));

        settings.GatheringRotationOverrides["Normal"] = "";
        Assert.Same(GatheringRotationTable.BuiltIn(NodeClass.Normal), set.For(NodeClass.Normal));
    }

    [Fact]
    public void ClassForMapsKinds()
    {
        Assert.Equal(NodeClass.Normal, GatheringRotationTable.ClassFor(NodeKind.Normal, false, false));
        Assert.Equal(NodeClass.Unspoiled, GatheringRotationTable.ClassFor(NodeKind.Unspoiled, false, false));
        Assert.Equal(NodeClass.Unspoiled, GatheringRotationTable.ClassFor(NodeKind.Legendary, false, false));
        Assert.Equal(NodeClass.Crystal, GatheringRotationTable.ClassFor(NodeKind.Normal, false, true));
        Assert.Equal(NodeClass.Collectable, GatheringRotationTable.ClassFor(NodeKind.Unspoiled, true, false));
        Assert.Equal(NodeClass.Collectable, GatheringRotationTable.ClassFor(NodeKind.Ephemeral, false, false));
    }

    [Fact]
    public void ActionVocabulary()
    {
        Assert.True(GatheringActions.TryParse("Bountiful Harvest II", out var bountiful));
        Assert.Equal(GatherAction.BountifulYield, bountiful);
        Assert.False(GatheringActions.TryParse("Basic Synthesis", out _));
        Assert.Equal(["Bountiful Yield II", "Bountiful Yield"], GatheringActions.Names(GatherAction.BountifulYield, GatheringActions.MinerJobId));
        Assert.Empty(GatheringActions.Names(GatherAction.Collect, 18));
        Assert.True(GatheringActions.IsCrystal(2));
        Assert.True(GatheringActions.IsCrystal(19));
        Assert.False(GatheringActions.IsCrystal(20));
        Assert.Equal(400, GatheringActions.Cordials[0].Gp(true));
        Assert.Equal(350, GatheringActions.Cordials[1].Gp(true));
        Assert.Equal(300, GatheringActions.Cordials[1].Gp(false));
    }
}
