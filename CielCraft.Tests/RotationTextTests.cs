using CielCraft.Core;
using CielCraft.Core.Rotations;
using Xunit;

namespace CielCraft.Tests;

public class RotationTextTests
{
    private const uint BasicSynthesis = 100001, BasicTouch = 100002, MastersMend = 100003, Innovation = 19004,
        WasteNot2 = 4639, ByregotsBlessing = 100339, MuscleMemory = 100379, Groundwork = 100403;

    [Fact]
    public void ParsesNamesOnePerLine()
    {
        var parsed = RotationText.Parse("Muscle Memory\nBasic Touch\n\nGroundwork\n");

        Assert.True(parsed.Success, string.Join("; ", parsed.Errors));
        Assert.Equal([MuscleMemory, BasicTouch, Groundwork], parsed.ActionIds);
    }

    [Fact]
    public void ParsesCommaSeparatedNames()
    {
        var parsed = RotationText.Parse("Muscle Memory, Basic Touch,Groundwork; Basic Synthesis");

        Assert.True(parsed.Success);
        Assert.Equal([MuscleMemory, BasicTouch, Groundwork, BasicSynthesis], parsed.ActionIds);
    }

    [Fact]
    public void ParsesTeamcraftMacroLines()
    {
        const string macro = "/ac \"Muscle Memory\" <wait.3>\n" +
                             "/ac Innovation <wait.2>\n" +
                             "/action \"Basic Touch\" <wait.3>\n" +
                             "/ac \"Waste Not II\" <wait.2>\n" +
                             "/echo Craft finished <se.1>\n";

        var parsed = RotationText.Parse(macro);

        Assert.True(parsed.Success, string.Join("; ", parsed.Errors));
        Assert.Equal([MuscleMemory, Innovation, BasicTouch, WasteNot2], parsed.ActionIds);
    }

    [Fact]
    public void MatchesNamesCaseInsensitivelyAndToleratesApostrophes()
    {
        var parsed = RotationText.Parse("MASTER'S MEND\nbyregot’s blessing\nmasters mend\nwaste not 2");

        Assert.True(parsed.Success, string.Join("; ", parsed.Errors));
        Assert.Equal([MastersMend, ByregotsBlessing, MastersMend, WasteNot2], parsed.ActionIds);
    }

    [Fact]
    public void StripsListNumberingFromPastedRotations()
    {
        var parsed = RotationText.Parse(" 1. Muscle Memory\n 2. Basic Touch\n10) Groundwork");

        Assert.True(parsed.Success, string.Join("; ", parsed.Errors));
        Assert.Equal([MuscleMemory, BasicTouch, Groundwork], parsed.ActionIds);
    }

    [Fact]
    public void ReportsUnknownNamesWithLineNumbers()
    {
        var parsed = RotationText.Parse("Muscle Memory\nBasic Tuch\n/ac \"Groundwerk\" <wait.3>\nGroundwork");

        Assert.False(parsed.Success);
        Assert.Equal(2, parsed.Errors.Count);
        Assert.Contains("line 2", parsed.Errors[0]);
        Assert.Contains("Basic Tuch", parsed.Errors[0]);
        Assert.Contains("line 3", parsed.Errors[1]);
        Assert.Contains("Groundwerk", parsed.Errors[1]);
        // The valid actions are still returned so the editor can show what parsed.
        Assert.Equal([MuscleMemory, Groundwork], parsed.ActionIds);
    }

    [Fact]
    public void RejectsUnsupportedCommandsAndEmptyText()
    {
        var command = RotationText.Parse("/wait 3\n/tell Someone hi\nBasic Touch");
        Assert.Single(command.Errors);
        Assert.Contains("/tell", command.Errors[0]);
        Assert.Equal([BasicTouch], command.ActionIds);

        Assert.False(RotationText.Parse("").Success);
        Assert.Contains("no actions", RotationText.Parse("   \n").Errors[0]);
        Assert.False(RotationText.Parse(null).Success);
    }

    [Fact]
    public void FormatRoundTrips()
    {
        uint[] ids = [MuscleMemory, Innovation, BasicTouch, WasteNot2, ByregotsBlessing];

        var text = RotationText.Format(ids);
        Assert.Equal("Muscle Memory\nInnovation\nBasic Touch\nWaste Not II\nByregot's Blessing", text);
        Assert.Equal(ids, RotationText.Parse(text).ActionIds);

        var macro = RotationText.FormatMacro(ids);
        Assert.Contains("/ac \"Muscle Memory\" <wait.3>", macro);
        Assert.Contains("/ac \"Innovation\" <wait.2>", macro);
        Assert.Equal(ids, RotationText.Parse(macro).ActionIds);
    }

    [Fact]
    public void ReverseLookupCoversEveryKnownAction()
    {
        foreach (var (id, name) in RaphaelActionNames.ById)
        {
            Assert.True(RaphaelActionNames.TryGetId(name, out var found), name);
            Assert.Equal(id, found);
        }

        Assert.False(RaphaelActionNames.TryGetId("Careful Observation", out _));
    }
}
