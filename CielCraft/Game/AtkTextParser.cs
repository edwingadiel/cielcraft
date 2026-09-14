using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CielCraft.Game;

internal static unsafe class AtkTextParser
{
    /// <summary>First integer in the node's text; 0 when absent. Allocation-free (runs per frame).</summary>
    public static int ParseInt(AtkTextNode* node) =>
        node == null ? 0 : Core.DigitParser.ParseFirstInt(node->NodeText.AsSpan());
}
