using System.Text;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class DigitParserTests
{
    private static int Parse(string text) => DigitParser.ParseFirstInt(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("12345", 12345)]
    [InlineData("12,345", 12345)]
    [InlineData("12.345", 12345)]
    [InlineData("12 345", 12345)]
    [InlineData("12\u00A0345", 12345)]
    [InlineData("12\u202F345", 12345)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("  12345  ", 12345)]
    [InlineData("12345 / 15000", 12345)]
    [InlineData("Durability: 40", 40)]
    [InlineData("0", 0)]
    public void ParsesFormattedIntegers(string text, int expected) => Assert.Equal(expected, Parse(text));

    [Theory]
    [InlineData("", 0)]
    [InlineData("n/a", 0)]
    [InlineData("12.34", 12)]       // a decimal, not a thousands group
    [InlineData("12 3456", 12)]     // four digits after the separator: two numbers
    [InlineData("12, 345", 12)]     // separator not directly followed by digits
    public void StopsAtNonGroupSeparators(string text, int expected) => Assert.Equal(expected, Parse(text));

    [Fact]
    public void SaturatesOnOverflow() => Assert.Equal(int.MaxValue, Parse("99999999999999"));
}
