using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CielCraft.Game;

internal static unsafe class AtkTextParser
{
    /// <summary>First integer in the node's text; 0 when absent. Allocation-free (runs per frame).</summary>
    public static int ParseInt(AtkTextNode* node)
    {
        if (node == null)
            return 0;

        var text = node->NodeText.AsSpan();
        var value = 0;
        var seenDigit = false;

        foreach (var b in text)
        {
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                value = value * 10 + (b - (byte)'0');
                seenDigit = true;
            }
            else if (seenDigit)
            {
                break;
            }
        }

        return value;
    }
}
