using System.Collections.Generic;
using CielCraft.Core;
using FFXIVClientStructs.FFXIV.Client.UI;

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
}
