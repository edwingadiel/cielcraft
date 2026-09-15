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

    /// <summary><paramref name="nameOf"/> supplies the display name written next to each item id.</summary>
    public static string Export(OrderBook book, System.Func<uint, string> nameOf)
    {
        // Implemented by the Core orders agent; see docs/design/orders.md.
        throw new System.NotImplementedException();
    }

    /// <summary>Null with an error message when the text is not an order book export.</summary>
    public static OrderBook? Import(string json, out string error)
    {
        throw new System.NotImplementedException();
    }
}
