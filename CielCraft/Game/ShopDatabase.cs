using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Sourcing;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Which NPCs sell which items for gil (roadmap 7.3b). <c>GilShopItem</c> is
/// a subrow sheet keyed by the <c>GilShop</c> row, and an NPC opens a shop
/// when its <c>ENpcBase.ENpcData</c> lists that shop's row id; the position
/// comes from the NPC locator (the <c>Level</c> sheet, P1). Special and
/// currency shops (<c>SpecialShop</c> 0x1B0000+, <c>GCShop</c> 0x160000+) are
/// deliberately not indexed here — they are P3a's (roadmap 7.17).
/// Indexed once per session, like <see cref="GatheringDatabase"/>.
/// </summary>
public sealed class ShopDatabase : IVendorDirectory
{
    /// <summary>GilShop row ids occupy 0x40000–0x4FFFF; an ENpcData entry in that range opens a gil shop.</summary>
    private const uint GilShopFirst = 0x40000;
    private const uint GilShopLast = 0x50000;

    /// <summary>CustomTalk (0xB0000+) and TopicSelect (0x320000+) handlers put an NPC's shop behind a menu.</summary>
    private const uint CustomTalkFirst = 0xB0000;
    private const uint CustomTalkLast = 0xC0000;
    private const uint TopicSelectFirst = 0x320000;
    private const uint TopicSelectLast = 0x330000;

    private readonly IGameBridge gameBridge;
    private readonly INpcLocator npcs;

    private Dictionary<uint, List<ShopEntry>>? itemToShops;
    private Dictionary<uint, List<uint>>? shopToNpcs;
    private Dictionary<uint, NpcInfo>? npcInfo;

    /// <summary>An item on a shop's list: the shop row and the item's sheet data.</summary>
    private readonly record struct ShopEntry(uint ShopId, int Price, int StackSize);

    /// <summary>What the index knows about a selling NPC before the locator is asked.</summary>
    private readonly record struct NpcInfo(string Name, bool OpensMenu);

    public ShopDatabase(IGameBridge gameBridge, INpcLocator npcs)
    {
        this.gameBridge = gameBridge;
        this.npcs = npcs;
    }

    public IReadOnlyList<ShopVendor> FindVendors(uint itemId)
    {
        EnsureIndex();
        if (!itemToShops!.TryGetValue(itemId, out var shops))
            return [];

        var candidates = new List<ShopVendor>();
        var seenNpcs = new HashSet<uint>();
        foreach (var shop in shops)
        {
            if (!shopToNpcs!.TryGetValue(shop.ShopId, out var sellers))
                continue;

            foreach (var npcId in sellers)
            {
                // One entry per NPC: a merchant that lists the item on two of
                // its shops is still one trip, and the first shop is the one
                // the "Purchase" option opens.
                if (!seenNpcs.Add(npcId))
                    continue;

                var target = npcs.Locate(npcId);
                if (target == null || target.TerritoryId == 0)
                    continue;

                var info = npcInfo!.GetValueOrDefault(npcId);
                candidates.Add(new ShopVendor(
                    itemId,
                    shop.Price,
                    shop.StackSize,
                    shop.ShopId,
                    ShopName(shop.ShopId),
                    npcId,
                    string.IsNullOrEmpty(target.Name) ? info.Name : target.Name,
                    target.TerritoryId,
                    GatheringDatabase.GetTerritoryName(target.TerritoryId),
                    info.OpensMenu));
            }
        }

        return VendorOrdering.Order(candidates, gameBridge.CurrentTerritoryId, gameBridge.CanTeleportTo);
    }

    /// <summary>Every gil-shop item known, for the diagnostic report.</summary>
    public int IndexedItems
    {
        get
        {
            EnsureIndex();
            return itemToShops!.Count;
        }
    }

    public IEnumerable<string> Describe()
    {
        EnsureIndex();
        yield return $"Gil shops indexed: {shopToNpcs!.Count} shop(s) with a seller, {itemToShops!.Count} item(s), {npcInfo!.Count} vendor NPC(s).";
    }

    private static string ShopName(uint shopId) =>
        Plugin.DataManager.GetExcelSheet<GilShop>().TryGetRow(shopId, out var shop)
            ? shop.Name.ExtractText()
            : "";

    private void EnsureIndex()
    {
        if (itemToShops != null)
            return;

        // GilShopItem: one subrow group per shop, each subrow one item on the
        // list. The price the shop charges is the item's PriceMid.
        itemToShops = new Dictionary<uint, List<ShopEntry>>();
        foreach (var group in Plugin.DataManager.GetSubrowExcelSheet<GilShopItem>())
        {
            foreach (var line in group)
            {
                var item = line.Item.ValueNullable;
                if (item == null || line.Item.RowId == 0)
                    continue;

                if (!itemToShops.TryGetValue(line.Item.RowId, out var list))
                    itemToShops[line.Item.RowId] = list = [];

                list.Add(new ShopEntry(group.RowId, (int)item.Value.PriceMid, Math.Max(1, (int)item.Value.StackSize)));
            }
        }

        // ENpcBase.ENpcData holds the event handlers an NPC offers; the ones
        // in the GilShop range are its shops. More than one shop, or a
        // CustomTalk / TopicSelect alongside it, means interacting raises a
        // menu instead of the Shop window (heuristic — the run falls back to
        // the other script when the first attempt fails).
        shopToNpcs = new Dictionary<uint, List<uint>>();
        npcInfo = new Dictionary<uint, NpcInfo>();
        var residents = Plugin.DataManager.GetExcelSheet<ENpcResident>();
        var shops = Plugin.DataManager.GetExcelSheet<GilShop>();
        foreach (var npc in Plugin.DataManager.GetExcelSheet<ENpcBase>())
        {
            var owned = new List<uint>();
            var menu = false;
            foreach (var handler in npc.ENpcData)
            {
                var id = handler.RowId;
                if (id >= GilShopFirst && id < GilShopLast)
                {
                    if (shops.HasRow(id))
                        owned.Add(id);
                }
                else if ((id >= CustomTalkFirst && id < CustomTalkLast) || (id >= TopicSelectFirst && id < TopicSelectLast))
                {
                    menu = true;
                }
            }

            if (owned.Count == 0)
                continue;

            foreach (var shopId in owned)
            {
                if (!shopToNpcs.TryGetValue(shopId, out var sellers))
                    shopToNpcs[shopId] = sellers = [];

                sellers.Add(npc.RowId);
            }

            var name = residents.TryGetRow(npc.RowId, out var resident) ? resident.Singular.ExtractText() : "";
            npcInfo[npc.RowId] = new NpcInfo(name, menu || owned.Count > 1);
        }
    }
}
