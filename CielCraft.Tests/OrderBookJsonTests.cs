using System.Text.Json;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class OrderBookJsonTests
{
    private static string NameOf(uint itemId) => $"Item{itemId}";

    private static OrderBook SampleBook() => new()
    {
        Perpetual = true,
        Groups =
        [
            new OrderGroup
            {
                Name = "Consumables",
                Orders =
                [
                    new Order { ItemId = 5, Amount = 20, AmountMode = AmountMode.Restock, Mode = ProductionMode.ForceHq, Note = "raid food" },
                    new Order { ItemId = 6, Amount = 3, Mode = ProductionMode.QuickSynth, MaterialsOnly = true, Enabled = false },
                ],
            },
            new OrderGroup
            {
                Name = "Gear",
                Enabled = false,
                Orders = [new Order { ItemId = 7, Amount = 1, Mode = ProductionMode.Collectable }],
            },
        ],
    };

    [Fact]
    public void RoundTripKeepsEveryFieldAndRegeneratesIds()
    {
        var original = SampleBook();

        var json = OrderBookJson.Export(original, NameOf);
        var imported = OrderBookJson.Import(json, out var error);

        Assert.Equal("", error);
        Assert.NotNull(imported);
        Assert.True(imported.Perpetual);
        Assert.Equal(2, imported.Groups.Count);

        for (var g = 0; g < original.Groups.Count; g++)
        {
            var expected = original.Groups[g];
            var actual = imported.Groups[g];
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Enabled, actual.Enabled);
            Assert.NotEqual(expected.Id, actual.Id);
            Assert.Equal(expected.Orders.Count, actual.Orders.Count);

            for (var o = 0; o < expected.Orders.Count; o++)
            {
                var e = expected.Orders[o];
                var a = actual.Orders[o];
                Assert.NotEqual(e.Id, a.Id);
                Assert.Equal(e.ItemId, a.ItemId);
                Assert.Equal(e.Amount, a.Amount);
                Assert.Equal(e.AmountMode, a.AmountMode);
                Assert.Equal(e.Mode, a.Mode);
                Assert.Equal(e.MaterialsOnly, a.MaterialsOnly);
                Assert.Equal(e.Enabled, a.Enabled);
                Assert.Equal(e.Note, a.Note);
            }
        }
    }

    [Fact]
    public void ExportUsesTheDocumentedShape()
    {
        using var document = JsonDocument.Parse(OrderBookJson.Export(SampleBook(), NameOf));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.True(root.GetProperty("perpetual").GetBoolean());

        var group = root.GetProperty("groups")[0];
        Assert.Equal("Consumables", group.GetProperty("name").GetString());
        Assert.True(group.GetProperty("enabled").GetBoolean());

        // camelCase keys, enums as strings, the item name next to its id, no ids.
        var order = group.GetProperty("orders")[0];
        Assert.Equal(5, order.GetProperty("itemId").GetInt32());
        Assert.Equal("Item5", order.GetProperty("name").GetString());
        Assert.Equal(20, order.GetProperty("amount").GetInt32());
        Assert.Equal("Restock", order.GetProperty("amountMode").GetString());
        Assert.Equal("ForceHq", order.GetProperty("mode").GetString());
        Assert.False(order.GetProperty("materialsOnly").GetBoolean());
        Assert.True(order.GetProperty("enabled").GetBoolean());
        Assert.Equal("raid food", order.GetProperty("note").GetString());
        Assert.False(order.TryGetProperty("id", out _));
        Assert.False(group.TryGetProperty("id", out _));
    }

    [Fact]
    public void MinimalHandWrittenDocumentGetsDefaults()
    {
        const string json = """{"version":1,"groups":[{"orders":[{"itemId":5,"amount":2},{"itemId":6,"amount":1,"amountMode":"restock","name":"ignored"}]}]}""";

        var book = OrderBookJson.Import(json, out var error);

        Assert.Equal("", error);
        Assert.NotNull(book);
        Assert.False(book.Perpetual);
        var group = Assert.Single(book.Groups);
        Assert.Equal("Orders", group.Name);
        Assert.True(group.Enabled);
        Assert.Equal(2, group.Orders.Count);

        var first = group.Orders[0];
        Assert.Equal(5u, first.ItemId);
        Assert.Equal(2, first.Amount);
        Assert.Equal(AmountMode.Absolute, first.AmountMode);
        Assert.Equal(ProductionMode.Any, first.Mode);
        Assert.False(first.MaterialsOnly);
        Assert.True(first.Enabled);
        Assert.Equal("", first.Note);

        // Enum names are read case-insensitively; the name is only decoration.
        Assert.Equal(AmountMode.Restock, group.Orders[1].AmountMode);
        Assert.Equal(6u, group.Orders[1].ItemId);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        const string json = """{"version":1,"exportedBy":"someone","groups":[{"orders":[{"itemId":1,"amount":1,"colour":"red"}]}]}""";

        var book = OrderBookJson.Import(json, out var error);

        Assert.Equal("", error);
        Assert.NotNull(book);
        Assert.Equal(1u, Assert.Single(Assert.Single(book.Groups).Orders).ItemId);
    }

    [Fact]
    public void WrongVersionIsRefused()
    {
        Assert.Null(OrderBookJson.Import("""{"version":2,"groups":[]}""", out var error));
        Assert.Contains("version 2", error);

        Assert.Null(OrderBookJson.Import("""{"groups":[]}""", out error));
        Assert.Contains("version", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"version":1,"groups":[{"orders":[{"itemId":1,"mode":"Sideways"}]}]}""")]
    public void MalformedDocumentsReturnNullWithAnError(string json)
    {
        Assert.Null(OrderBookJson.Import(json, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void EmptyBookRoundTrips()
    {
        var book = OrderBookJson.Import(OrderBookJson.Export(new OrderBook(), NameOf), out var error);

        Assert.Equal("", error);
        Assert.NotNull(book);
        Assert.Empty(book.Groups);
        Assert.False(book.Perpetual);
    }
}
