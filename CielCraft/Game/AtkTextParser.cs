using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CielCraft.Game;

internal static unsafe class AtkTextParser
{
    /// <summary>First integer in the node's text; 0 when absent.</summary>
    public static int ParseInt(AtkTextNode* node)
    {
        if (node == null)
            return 0;

        var text = node->NodeText.ToString();
        var value = 0;
        var seenDigit = false;

        foreach (var c in text)
        {
            if (c is >= '0' and <= '9')
            {
                value = value * 10 + (c - '0');
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
