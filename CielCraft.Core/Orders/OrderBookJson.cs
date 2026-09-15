using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CielCraft.Core;

/// <summary>
/// Import/export of an order book as JSON (roadmap 7.13): a stable, versioned
/// document (`{"version":1,"groups":[...],"perpetual":false}`) so lists can be
/// shared between characters and installs. Item ids are kept as-is; names are
/// written alongside for readability and ignored on import.
/// </summary>
public static class OrderBookJson
{
    public const int Version = 1;

    // The document is a separate DTO shape rather than the configuration
    // classes themselves so the export stays stable when the model grows
    // (ids, runtime state) and so hand-edited documents may omit fields.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Document
    {
        public int Version { get; set; }
        public bool Perpetual { get; set; }
        public List<GroupDto>? Groups { get; set; }
    }

    private sealed class GroupDto
    {
        public string? Name { get; set; }
        public bool Enabled { get; set; } = true;
        public List<OrderDto>? Orders { get; set; }
    }

    private sealed class OrderDto
    {
        public uint ItemId { get; set; }
        public string? Name { get; set; }
        public int Amount { get; set; } = 1;
        public AmountMode AmountMode { get; set; } = AmountMode.Absolute;
        public ProductionMode Mode { get; set; } = ProductionMode.Any;
        public bool MaterialsOnly { get; set; }
        public bool Enabled { get; set; } = true;
        public string? Note { get; set; }
    }

    /// <summary><paramref name="nameOf"/> supplies the display name written next to each item id.</summary>
    public static string Export(OrderBook book, Func<uint, string> nameOf)
    {
        var document = new Document
        {
            Version = Version,
            Perpetual = book.Perpetual,
            Groups = book.Groups.Select(g => new GroupDto
            {
                Name = g.Name,
                Enabled = g.Enabled,
                Orders = g.Orders.Select(o => new OrderDto
                {
                    ItemId = o.ItemId,
                    Name = nameOf(o.ItemId),
                    Amount = o.Amount,
                    AmountMode = o.AmountMode,
                    Mode = o.Mode,
                    MaterialsOnly = o.MaterialsOnly,
                    Enabled = o.Enabled,
                    Note = o.Note,
                }).ToList(),
            }).ToList(),
        };

        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>Null with an error message when the text is not an order book export.</summary>
    public static OrderBook? Import(string json, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Nothing to import: the text is empty.";
            return null;
        }

        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, Options);
        }
        catch (JsonException ex)
        {
            error = $"Not valid order book JSON: {ex.Message}";
            return null;
        }

        if (document == null)
        {
            error = "Not valid order book JSON: the document is null.";
            return null;
        }

        if (document.Version != Version)
        {
            error = document.Version == 0
                ? "Not an order book export: missing \"version\"."
                : $"Unsupported order book version {document.Version} (this build reads version {Version}).";
            return null;
        }

        // Ids are regenerated (fresh Guid defaults) so an import never
        // collides with groups already in the book.
        var book = new OrderBook { Perpetual = document.Perpetual };
        foreach (var group in document.Groups ?? [])
        {
            var imported = new OrderGroup
            {
                Name = string.IsNullOrWhiteSpace(group.Name) ? "Orders" : group.Name,
                Enabled = group.Enabled,
            };
            foreach (var order in group.Orders ?? [])
            {
                imported.Orders.Add(new Order
                {
                    ItemId = order.ItemId,
                    Amount = order.Amount,
                    AmountMode = order.AmountMode,
                    Mode = order.Mode,
                    MaterialsOnly = order.MaterialsOnly,
                    Enabled = order.Enabled,
                    Note = order.Note ?? "",
                });
            }

            book.Groups.Add(imported);
        }

        return book;
    }
}
