using System.Collections.Generic;
using CielCraft.Core;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CielCraft.Game;

/// <summary>Reads the open gathering node from the Gathering addon (spec §64).</summary>
internal static unsafe class GatheringStateReader
{
    public static GatheringSnapshot? Read()
    {
        var addonPtr = Plugin.GameGui.GetAddonByName("Gathering");
        if (addonPtr.IsNull || !addonPtr.IsVisible)
            return null;

        var addon = (AddonGathering*)addonPtr.Address;

        var items = new List<GatheringItemSlot>();
        for (var i = 0; i < addon->ItemIds.Length; i++)
        {
            var itemId = addon->ItemIds[i];
            if (itemId == 0)
                continue;

            var checkbox = addon->GatheredItemComponentCheckbox[i].Value;
            items.Add(new GatheringItemSlot(i, itemId, checkbox != null && checkbox->IsEnabled));
        }

        var player = Plugin.ObjectTable.LocalPlayer;

        return new GatheringSnapshot(
            IntegrityRemaining: AtkTextParser.ParseInt(addon->IntegrityLeftover),
            IntegrityTotal: AtkTextParser.ParseInt(addon->IntegrityTotal),
            CurrentGp: player?.CurrentGp ?? 0,
            MaxGp: player?.MaxGp ?? 0,
            Items: items);
    }

    /// <summary>
    /// The visible text nodes of one item row (the slot's checkbox
    /// component), in node order: the item name and the row's percentages
    /// (gathering chance, Gatherer's Boon chance). Empty when the window or
    /// the slot is not there.
    /// </summary>
    public static IReadOnlyList<string> ReadSlotTexts(int slotIndex)
    {
        var addonPtr = Plugin.GameGui.GetAddonByName("Gathering");
        if (addonPtr.IsNull || !addonPtr.IsVisible)
            return [];

        var addon = (AddonGathering*)addonPtr.Address;
        if (slotIndex < 0 || slotIndex >= addon->GatheredItemComponentCheckbox.Length)
            return [];

        var checkbox = addon->GatheredItemComponentCheckbox[slotIndex].Value;
        if (checkbox == null)
            return [];

        var texts = new List<string>();
        var manager = &checkbox->UldManager;
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
                continue;

            var text = Dalamud.Utility.Utf8StringExtensions.ExtractText(((AtkTextNode*)node)->NodeText).Trim();
            if (text.Length > 0)
                texts.Add(text);
        }

        return texts;
    }

    /// <summary>
    /// Gatherer's Boon chance of the slot from the row's text: the last
    /// "NN%" of the row (the row shows the gathering chance first, then the
    /// boon chance). -1 when the row shows fewer than two percentages, so a
    /// rule that needs the boon chance is skipped rather than fed the
    /// gathering chance.
    /// </summary>
    public static int ReadBoonChance(int slotIndex)
    {
        // The row shows the chance and the boon as "NN%" — or, as observed
        // on 2026-09-15, as separate "%" and "NN" text nodes; a "%" pairs
        // with the number next to it.
        var texts = ReadSlotTexts(slotIndex);
        var percentages = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (TryParsePercent(texts[i], out var value))
            {
                percentages.Add(value);
            }
            else if (texts[i].Trim() == "%")
            {
                if (i + 1 < texts.Count && int.TryParse(texts[i + 1].Trim(), out var next) && next is >= 0 and <= 100)
                {
                    percentages.Add(next);
                    i++;
                }
                else if (i > 0 && int.TryParse(texts[i - 1].Trim(), out var previous) && previous is >= 0 and <= 100)
                {
                    percentages.Add(previous);
                }
            }
        }

        // Node order is the reverse of the display (observed: [%, 60, %, 100] for
        // a row showing 100% chance and 60% boon), so the boon comes first.
        return percentages.Count >= 2 ? percentages[0] : -1;
    }

    private static bool TryParsePercent(string text, out int value)
    {
        value = 0;
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[^1] != '%')
            return false;

        return int.TryParse(trimmed[..^1].Trim(), out value) && value is >= 0 and <= 100;
    }
}
