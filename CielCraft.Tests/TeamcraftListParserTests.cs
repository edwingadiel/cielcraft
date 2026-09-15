using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class TeamcraftListParserTests
{
    /// <summary>What Teamcraft's "copy list as text" pastes: three sections, HQ markers, blank lines.</summary>
    private const string Paste = """
        Final items :
        2x Grade 8 Tincture of Strength (HQ)
        1x Rarefied Titanoboa Leather
        3x Ironwood Lumber HQ

        Items :
        4x Titanoboa Leather
        2x Ironwood Lumber HQ

        Crystals :
        16x Fire Crystal
        8x Earth Cluster

        Gathering :
        10x Titanoboa Skin
        6x Ironwood Log

        """;

    [Fact]
    public void ParsesEverySectionInOrder()
    {
        var lines = TeamcraftListParser.Parse(Paste);

        Assert.Equal(9, lines.Count);
        Assert.Equal(new ImportedLine("Grade 8 Tincture of Strength", 2, "Final items"), lines[0]);
        Assert.Equal(new ImportedLine("Rarefied Titanoboa Leather", 1, "Final items"), lines[1]);
        Assert.Equal(new ImportedLine("Ironwood Lumber", 3, "Final items"), lines[2]);
        Assert.Equal(new ImportedLine("Titanoboa Leather", 4, "Items"), lines[3]);
        Assert.Equal(new ImportedLine("Ironwood Lumber", 2, "Items"), lines[4]);
        Assert.Equal(new ImportedLine("Fire Crystal", 16, "Crystals"), lines[5]);
        Assert.Equal(new ImportedLine("Earth Cluster", 8, "Crystals"), lines[6]);
        Assert.Equal(new ImportedLine("Titanoboa Skin", 10, "Gathering"), lines[7]);
        Assert.Equal(new ImportedLine("Ironwood Log", 6, "Gathering"), lines[8]);
    }

    [Fact]
    public void FinalItemsReturnsOnlyTheFinalSection()
    {
        var final = TeamcraftListParser.FinalItems(TeamcraftListParser.Parse(Paste));

        Assert.Equal(["Grade 8 Tincture of Strength", "Rarefied Titanoboa Leather", "Ironwood Lumber"], final.Select(l => l.Name));
    }

    [Fact]
    public void FinalItemsFallsBackToEveryLineWithoutAFinalSection()
    {
        var lines = TeamcraftListParser.Parse("Items :\n2x Iron Ingot\nCrystals :\n4x Fire Shard");

        Assert.Same(lines, TeamcraftListParser.FinalItems(lines));
        Assert.Equal(2, lines.Count);
    }

    [Theory]
    [InlineData("3x Iron Ingot", "Iron Ingot", 3)]
    [InlineData("3 x Iron Ingot", "Iron Ingot", 3)]
    [InlineData("3 Iron Ingot", "Iron Ingot", 3)]
    [InlineData("Iron Ingot x3", "Iron Ingot", 3)]
    [InlineData("Iron Ingot x 3", "Iron Ingot", 3)]
    [InlineData("Iron Ingot ×3", "Iron Ingot", 3)]
    [InlineData("3× Iron Ingot", "Iron Ingot", 3)]
    [InlineData("Iron Ingot HQ x2", "Iron Ingot", 2)]
    [InlineData("2x Iron Ingot (HQ)", "Iron Ingot", 2)]
    [InlineData("2x Iron Ingot [HQ]", "Iron Ingot", 2)]
    [InlineData("  - 12x Iron Ingot  ", "Iron Ingot", 12)]
    [InlineData("3 Xelphatol Apple", "Xelphatol Apple", 3)]
    [InlineData("1x X-Potion", "X-Potion", 1)]
    public void ParsesTolerantLineForms(string line, string name, int amount)
    {
        var parsed = Assert.Single(TeamcraftListParser.Parse(line));

        Assert.Equal(name, parsed.Name);
        Assert.Equal(amount, parsed.Amount);
        Assert.Equal("", parsed.Section);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("Iron Ingot")]
    [InlineData("Lv. 80")]
    [InlineData("https://ffxivteamcraft.com/list/abc123")]
    [InlineData("0x Iron Ingot")]
    [InlineData("3x")]
    [InlineData("99999999999x Iron Ingot")]
    public void IgnoresLinesThatAreNotACountAndAName(string text)
    {
        Assert.Empty(TeamcraftListParser.Parse(text));
    }

    [Fact]
    public void HeadingsAreCaseInsensitiveAndTrimmed()
    {
        var lines = TeamcraftListParser.Parse("  FINAL Items:  \r\n2x Iron Ingot\r\nother :\r\n1x Fire Shard");

        Assert.Equal("FINAL Items", lines[0].Section);
        Assert.Equal("other", lines[1].Section);
        Assert.Equal("Iron Ingot", Assert.Single(TeamcraftListParser.FinalItems(lines)).Name);
    }

    [Fact]
    public void LinesBeforeAnyHeadingHaveAnEmptySection()
    {
        var lines = TeamcraftListParser.Parse("2x Iron Ingot\nItems :\n1x Ore");

        Assert.Equal("", lines[0].Section);
        Assert.Equal("Items", lines[1].Section);
    }
}
