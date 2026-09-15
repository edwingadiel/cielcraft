using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Sourcing;
using Lumina.Excel.Sheets;

namespace CielCraft.Game;

/// <summary>
/// Currency shops and collectable turn-ins, read from the game data (spec
/// §19, roadmap 7.17).
///
/// <para>Three sheet families feed it. <c>SpecialShop</c> rows hold "N of an
/// item for M of a currency"; a cost line's <c>CostType</c> says how to read
/// its <c>ItemCost</c> — 0/1 a plain item, 2 gil, and 3 a *currency index*
/// rather than an item row (verified against the game data: the master-book
/// shop 1770873 costs "100 of currency 2"). <c>GCShop</c> +
/// <c>GCScripShopCategory</c> + <c>GCScripShopItem</c> hold the Grand Company
/// quartermasters' seal lists. <c>CollectablesShop</c> rows with reward type 1
/// hold the appraiser's scrip turn-ins, through <c>CollectablesShopItem</c>
/// (level range), <c>CollectablesShopRefine</c> (the three collectability
/// thresholds) and <c>CollectablesShopRewardScrip</c> (currency index and the
/// reward per tier).</para>
///
/// <para>The scrip currency indices are resolved from the Item sheet rather
/// than hard-coded: crafters' and gatherers' scrips are consecutive currency
/// items (category 100, stack 4000) whose icons run downwards, so the pairs
/// sort by age and indices 6/7 name the newest pair while 2/4 name the one
/// before it. A new expansion's scrips therefore need no code change.</para>
/// </summary>
public sealed class ExchangeDatabase : IExchangeCatalog
{
    /// <summary>ItemUICategory of the currency items (scrips, seals, tomestones, tribal coin).</summary>
    private const uint CurrencyCategory = 100;

    /// <summary>Every crafters'/gatherers' scrip stacks to this; the tribal currencies do not.</summary>
    private const uint ScripStackSize = 4000;

    /// <summary>ItemUICategory of the seals and Allagan tomestones.</summary>
    private const uint TomestoneCategory = 63;

    /// <summary>Storm / Serpent / Flame Seal, in GrandCompany row order (1..3).</summary>
    private const uint FirstSealItemId = 20;

    /// <summary>Currency index of the newest crafters' / gatherers' scrip in SpecialShop and CollectablesShopRewardScrip.</summary>
    private const uint CurrentCraftersScripIndex = 6;
    private const uint CurrentGatherersScripIndex = 7;

    /// <summary>Currency index of the previous expansion's pair (everything below the level cap pays these).</summary>
    private const uint PreviousCraftersScripIndex = 2;
    private const uint PreviousGatherersScripIndex = 4;

    // The high 16 bits of an ENpcData entry name the sheet it points at.
    private const uint SpecialShopTag = 0x001B;
    private const uint GrandCompanyShopTag = 0x0016;
    private const uint InclusionShopTag = 0x003A;
    private const uint PreHandlerTag = 0x0036;

    // SpecialShop ItemCosts.CostType.
    private const byte CostTypeGil = 2;
    private const byte CostTypeCurrencyIndex = 3;

    /// <summary>CollectablesShop.RewardType 1 = scrips (2 = a reward item, which the planner cannot use).</summary>
    private const byte ScripRewardType = 1;

    /// <summary>The appraiser's ENpcResident name; the scrip CollectablesShops hang off a CustomTalk, not off ENpcData.</summary>
    private const string AppraiserName = "collectable appraiser";

    /// <summary>Rough per-unit durations for the scrip planner's "fastest" comparison; only the ratio matters.</summary>
    private const int SecondsPerCraft = 75;
    private const int SecondsPerGather = 45;

    private readonly INpcLocator? locator;
    private readonly Func<uint> currentTerritory;
    private readonly Func<uint, uint?> gatheringJob;
    private readonly ILog log;

    private Dictionary<uint, ExchangeCurrency>? scripByIndex;
    private Dictionary<uint, List<ExchangeOffer>>? itemToOffers;
    private Dictionary<uint, List<ScripTurnIn>>? currencyToTurnIns;
    private List<uint>? appraisers;
    private readonly Dictionary<uint, string> itemNames = new();

    /// <param name="gatheringJob">
    /// GatheringDatabase.GetGatheringJob, so a gathered collectable knows which
    /// job (and level) the planner must gate on; without it only crafted
    /// turn-ins are offered.
    /// </param>
    public ExchangeDatabase(
        ILog log,
        INpcLocator? locator = null,
        Func<uint>? currentTerritory = null,
        Func<uint, uint?>? gatheringJob = null)
    {
        this.log = log;
        this.locator = locator;
        this.currentTerritory = currentTerritory ?? (() => 0);
        this.gatheringJob = gatheringJob ?? (_ => null);
    }

    public IReadOnlyList<ExchangeOffer> FindExchanges(uint itemId)
    {
        EnsureShops();
        if (!itemToOffers!.TryGetValue(itemId, out var offers))
            return [];

        // Best first: an NPC standing in this zone, then the cheapest line,
        // then the lowest shop id so the choice is stable across runs.
        var here = currentTerritory();
        return offers
            .OrderBy(o => here != 0 && locator?.Locate(o.NpcId)?.TerritoryId == here ? 0 : 1)
            .ThenBy(o => o.Cost / Math.Max(1, o.ReceiveCount))
            .ThenBy(o => o.ShopId)
            .ToList();
    }

    public IReadOnlyList<ScripTurnIn> FindTurnIns(uint currencyItemId)
    {
        EnsureTurnIns();
        return currencyToTurnIns!.TryGetValue(currencyItemId, out var list) ? list : [];
    }

    /// <summary>Every collectable appraiser, so the turn-in run can take the first one it can reach.</summary>
    public IReadOnlyList<uint> TurnInNpcIds
    {
        get
        {
            EnsureTurnIns();
            return appraisers!;
        }
    }

    /// <summary>The scrip currencies the sheets resolved to, for the diagnostic report.</summary>
    public IReadOnlyDictionary<uint, ExchangeCurrency> ScripCurrencies
    {
        get
        {
            EnsureCurrencies();
            return scripByIndex!;
        }
    }

    public string GetItemName(uint itemId)
    {
        if (itemNames.TryGetValue(itemId, out var cached))
            return cached;

        var name = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)
            ? item.Name.ExtractText()
            : $"Item {itemId}";
        return itemNames[itemId] = name.Length > 0 ? name : $"Item {itemId}";
    }

    public string GetZoneName(uint territoryId) => GatheringDatabase.GetTerritoryName(territoryId);

    public IEnumerable<string> Describe()
    {
        EnsureShops();
        EnsureTurnIns();
        var offers = itemToOffers!;
        var turnIns = currencyToTurnIns!;
        yield return $"Exchange lines: {offers.Sum(p => p.Value.Count)} for {offers.Count} items";
        yield return "Scrip currencies: " + string.Join(
            ", ", ScripCurrencies.OrderBy(p => p.Key).Select(p => $"{p.Key} → {p.Value.Name} ({p.Value.ItemId})"));
        yield return $"Scrip turn-ins: {turnIns.Sum(p => p.Value.Count)} across {turnIns.Count} currencies; " +
                     $"{appraisers!.Count} appraiser(s)";
    }

    // ------------------------------------------------------------ currencies

    private void EnsureCurrencies()
    {
        if (scripByIndex != null)
            return;

        var items = Plugin.DataManager.GetExcelSheet<Item>();
        var pairs = new List<(uint Crafters, uint Gatherers)>();
        foreach (var item in items)
        {
            if (item.ItemUICategory.RowId != CurrencyCategory || item.StackSize != ScripStackSize)
                continue;

            // A scrip pair is two consecutive currency items whose icons run
            // downwards (crafters first, gatherers one row later, one icon
            // earlier); nothing else in the category is laid out that way.
            if (!items.TryGetRow(item.RowId + 1, out var next))
                continue;

            if (next.ItemUICategory.RowId != CurrencyCategory || next.StackSize != ScripStackSize
                || next.Icon != item.Icon - 1)
                continue;

            pairs.Add((item.RowId, next.RowId));
        }

        pairs.Sort((a, b) => a.Crafters.CompareTo(b.Crafters));
        scripByIndex = new Dictionary<uint, ExchangeCurrency>();
        if (pairs.Count == 0)
        {
            log.Warning("[Exchange] No crafters'/gatherers' scrip pair found in the Item sheet; scrip shops are off.");
            return;
        }

        Add(CurrentCraftersScripIndex, pairs[^1].Crafters);
        Add(CurrentGatherersScripIndex, pairs[^1].Gatherers);
        if (pairs.Count > 1)
        {
            Add(PreviousCraftersScripIndex, pairs[^2].Crafters);
            Add(PreviousGatherersScripIndex, pairs[^2].Gatherers);
        }

        log.Information("[Exchange] Scrip currencies: " + string.Join(
            ", ", scripByIndex.OrderBy(p => p.Key).Select(p => $"{p.Key} → {p.Value.Name}")));

        void Add(uint index, uint itemId) =>
            scripByIndex[index] = new ExchangeCurrency(itemId, GetItemName(itemId), ExchangeCurrencyKind.Scrip);
    }

    private ExchangeCurrency? CurrencyOfItem(uint itemId)
    {
        if (itemId == 0)
            return null;

        EnsureCurrencies();
        if (itemId is >= FirstSealItemId and <= FirstSealItemId + 2)
            return new ExchangeCurrency(itemId, GetItemName(itemId), ExchangeCurrencyKind.GrandCompanySeal);

        var known = scripByIndex!.Values.FirstOrDefault(c => c.ItemId == itemId);
        if (known != null)
            return known;

        var kind = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)
                   && item.ItemUICategory.RowId == TomestoneCategory
            ? ExchangeCurrencyKind.Tomestone
            : ExchangeCurrencyKind.Item;
        return new ExchangeCurrency(itemId, GetItemName(itemId), kind);
    }

    // ------------------------------------------------------------ shop index

    private void EnsureShops()
    {
        if (itemToOffers != null)
            return;

        EnsureCurrencies();
        itemToOffers = new Dictionary<uint, List<ExchangeOffer>>();
        var shopToNpcs = BuildNpcIndex();
        IndexSpecialShops(shopToNpcs);
        IndexGrandCompanyShops(shopToNpcs);
    }

    /// <summary>
    /// Event-handler id → the NPCs that raise it. ENpcData entries point
    /// straight at a shop for most vendors; the scrip exchange goes through a
    /// PreHandler (an unlock check) whose Target is the InclusionShop, and an
    /// InclusionShop is then expanded to the SpecialShops of its categories.
    /// </summary>
    private Dictionary<uint, List<(uint NpcId, string Name, ExchangeShopKind Kind)>> BuildNpcIndex()
    {
        var map = new Dictionary<uint, List<(uint, string, ExchangeShopKind)>>();
        var residents = Plugin.DataManager.GetExcelSheet<ENpcResident>();
        var preHandlers = Plugin.DataManager.GetExcelSheet<PreHandler>();

        foreach (var npc in Plugin.DataManager.GetExcelSheet<ENpcBase>())
        {
            string? name = null;
            foreach (var entry in npc.ENpcData)
            {
                var id = entry.RowId;
                if (id == 0)
                    continue;

                if (id >> 16 == PreHandlerTag && preHandlers.TryGetRow(id, out var pre) && pre.Target.RowId != 0)
                    id = pre.Target.RowId;

                var tag = id >> 16;
                if (tag != SpecialShopTag && tag != GrandCompanyShopTag && tag != InclusionShopTag)
                    continue;

                name ??= residents.TryGetRow(npc.RowId, out var resident) ? resident.Singular.ExtractText() : "";
                if (name.Length == 0)
                    continue;

                var kind = tag == GrandCompanyShopTag ? ExchangeShopKind.GrandCompanyShop
                    : tag == InclusionShopTag ? ExchangeShopKind.InclusionShop
                    : ExchangeShopKind.SpecialShop;
                Add(id, (npc.RowId, name, kind));
            }
        }

        // An InclusionShop is a list of categories, each a series of
        // SpecialShop rows; its NPC sells every one of them.
        var categories = Plugin.DataManager.GetExcelSheet<InclusionShopCategory>();
        var series = Plugin.DataManager.GetSubrowExcelSheet<InclusionShopSeries>();
        foreach (var inclusion in Plugin.DataManager.GetExcelSheet<InclusionShop>())
        {
            if (!map.TryGetValue(inclusion.RowId, out var npcs))
                continue;

            foreach (var categoryRef in inclusion.Category)
            {
                if (categoryRef.RowId == 0 || !categories.TryGetRow(categoryRef.RowId, out var category))
                    continue;

                if (!series.TryGetRow(category.InclusionShopSeries.RowId, out var subrows))
                    continue;

                foreach (var row in subrows)
                {
                    if (row.SpecialShop.RowId == 0)
                        continue;

                    foreach (var (npcId, npcName, _) in npcs)
                        Add(row.SpecialShop.RowId, (npcId, npcName, ExchangeShopKind.InclusionShop));
                }
            }
        }

        return map;

        void Add(uint id, (uint NpcId, string Name, ExchangeShopKind Kind) npc)
        {
            if (!map.TryGetValue(id, out var list))
                map[id] = list = [];

            if (!list.Any(e => e.Item1 == npc.NpcId))
                list.Add((npc.NpcId, npc.Name, npc.Kind));
        }
    }

    private void IndexSpecialShops(Dictionary<uint, List<(uint NpcId, string Name, ExchangeShopKind Kind)>> shopToNpcs)
    {
        foreach (var shop in Plugin.DataManager.GetExcelSheet<SpecialShop>())
        {
            if (!shopToNpcs.TryGetValue(shop.RowId, out var npcs))
                continue;

            foreach (var entry in shop.Item)
            {
                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.CurrencyCost == 0 || cost.CostType == CostTypeGil)
                        continue;

                    // CostType 3 is the odd one: ItemCost holds a currency
                    // index (2/4/6/7 = the scrip pairs), not an item row.
                    var currency = cost.CostType == CostTypeCurrencyIndex
                        ? scripByIndex!.GetValueOrDefault(cost.ItemCost.RowId)
                        : CurrencyOfItem(cost.ItemCost.RowId);
                    if (currency == null || currency.ItemId == ExchangeSource.GilItemId)
                        continue;

                    foreach (var receive in entry.ReceiveItems)
                    {
                        if (receive.Item.RowId == 0 || receive.ReceiveCount == 0)
                            continue;

                        foreach (var (npcId, npcName, kind) in npcs)
                        {
                            AddOffer(new ExchangeOffer(
                                receive.Item.RowId,
                                GetItemName(receive.Item.RowId),
                                (int)receive.ReceiveCount,
                                currency,
                                (int)cost.CurrencyCost,
                                shop.RowId,
                                kind,
                                npcId,
                                npcName));
                        }
                    }
                }
            }
        }
    }

    private void IndexGrandCompanyShops(Dictionary<uint, List<(uint NpcId, string Name, ExchangeShopKind Kind)>> shopToNpcs)
    {
        var categories = Plugin.DataManager.GetExcelSheet<GCScripShopCategory>();
        var items = Plugin.DataManager.GetSubrowExcelSheet<GCScripShopItem>();
        foreach (var shop in Plugin.DataManager.GetExcelSheet<GCShop>())
        {
            var company = shop.GrandCompany.RowId;
            if (company is 0 or > 3 || !shopToNpcs.TryGetValue(shop.RowId, out var npcs))
                continue;

            var currency = new ExchangeCurrency(
                FirstSealItemId + company - 1,
                GetItemName(FirstSealItemId + company - 1),
                ExchangeCurrencyKind.GrandCompanySeal);

            foreach (var category in categories)
            {
                if (category.GrandCompany.RowId != company || !items.TryGetRow(category.RowId, out var subrows))
                    continue;

                foreach (var row in subrows)
                {
                    if (row.Item.RowId == 0 || row.CostGCSeals == 0)
                        continue;

                    foreach (var (npcId, npcName, _) in npcs)
                    {
                        AddOffer(new ExchangeOffer(
                            row.Item.RowId,
                            GetItemName(row.Item.RowId),
                            1,
                            currency,
                            (int)row.CostGCSeals,
                            shop.RowId,
                            ExchangeShopKind.GrandCompanyShop,
                            npcId,
                            npcName,
                            (int)row.RequiredGrandCompanyRank.RowId));
                    }
                }
            }
        }
    }

    private void AddOffer(ExchangeOffer offer)
    {
        if (!itemToOffers!.TryGetValue(offer.ItemId, out var list))
            itemToOffers[offer.ItemId] = list = [];

        if (!list.Any(o => o.ShopId == offer.ShopId && o.NpcId == offer.NpcId && o.Cost == offer.Cost))
            list.Add(offer);
    }

    // -------------------------------------------------------- scrip turn-ins

    private void EnsureTurnIns()
    {
        if (currencyToTurnIns != null)
            return;

        EnsureCurrencies();
        currencyToTurnIns = new Dictionary<uint, List<ScripTurnIn>>();
        appraisers = FindAppraisers();
        var appraiserId = appraisers.Count > 0 ? appraisers[0] : 0u;
        var appraiserName = appraiserId == 0 ? "" : NameOfNpc(appraiserId);

        var shopItems = Plugin.DataManager.GetSubrowExcelSheet<CollectablesShopItem>();
        var refines = Plugin.DataManager.GetExcelSheet<CollectablesShopRefine>();
        var rewards = Plugin.DataManager.GetExcelSheet<CollectablesShopRewardScrip>();
        var seen = new HashSet<uint>();

        foreach (var shop in Plugin.DataManager.GetExcelSheet<CollectablesShop>())
        {
            if (shop.RewardType != ScripRewardType)
                continue;

            foreach (var group in shop.ShopItems)
            {
                if (group.RowId == 0 || !shopItems.TryGetRow(group.RowId, out var subrows))
                    continue;

                foreach (var row in subrows)
                {
                    if (row.Item.RowId == 0 || !seen.Add(row.Item.RowId))
                        continue;

                    if (!rewards.TryGetRow(row.CollectablesShopRewardScrip.RowId, out var reward)
                        || !scripByIndex!.TryGetValue(reward.Currency, out var currency))
                        continue;

                    if (reward.LowReward == 0 && reward.MidReward == 0 && reward.HighReward == 0)
                        continue;

                    refines.TryGetRow(row.CollectablesShopRefine.RowId, out var refine);
                    var turnIn = BuildTurnIn(row, refine, reward, currency, appraiserId, appraiserName);
                    if (turnIn == null)
                        continue;

                    if (!currencyToTurnIns.TryGetValue(currency.ItemId, out var list))
                        currencyToTurnIns[currency.ItemId] = list = [];

                    list.Add(turnIn);
                }
            }
        }
    }

    private ScripTurnIn? BuildTurnIn(
        CollectablesShopItem row,
        CollectablesShopRefine refine,
        CollectablesShopRewardScrip reward,
        ExchangeCurrency currency,
        uint appraiserId,
        string appraiserName)
    {
        var itemId = row.Item.RowId;
        uint jobId;
        bool gathered;
        var materialCost = 1;

        var recipe = FindRecipe(itemId);
        if (recipe != null)
        {
            // CraftType 0..7 are CRP..CUL, whose ClassJob rows start at 8.
            jobId = recipe.Value.Job;
            gathered = false;
            materialCost = recipe.Value.Materials;
        }
        else if (gatheringJob(itemId) is { } gatherJob)
        {
            jobId = gatherJob;
            gathered = true;
        }
        else
        {
            // Spearfishing and line fishing land here until P4's database is
            // wired in (roadmap 7.4): no job, so nothing to gate or plan on.
            return null;
        }

        return new ScripTurnIn(
            itemId,
            GetItemName(itemId),
            currency.ItemId,
            jobId,
            row.LevelMin,
            gathered,
            reward.LowReward,
            reward.MidReward,
            reward.HighReward,
            refine.LowCollectability,
            refine.MidCollectability,
            refine.HighCollectability,
            materialCost,
            gathered ? SecondsPerGather : SecondsPerCraft,
            appraiserId,
            appraiserName);
    }

    private Dictionary<uint, (uint Job, int Materials)>? recipesByResult;

    /// <summary>
    /// The DoH job and total ingredient units of a collectable recipe; null
    /// when the item is not crafted. Indexed once — the turn-in tables run to
    /// a thousand rows and the Recipe sheet to forty thousand.
    /// </summary>
    private (uint Job, int Materials)? FindRecipe(uint itemId)
    {
        if (recipesByResult == null)
        {
            recipesByResult = new Dictionary<uint, (uint, int)>();
            foreach (var recipe in Plugin.DataManager.GetExcelSheet<Recipe>())
            {
                var result = recipe.ItemResult.RowId;
                if (result == 0 || recipesByResult.ContainsKey(result))
                    continue;

                var materials = 0;
                for (var i = 0; i < recipe.Ingredient.Count; i++)
                {
                    if (recipe.Ingredient[i].RowId != 0)
                        materials += recipe.AmountIngredient[i];
                }

                recipesByResult[result] = (recipe.CraftType.RowId + CraftJobOffset, Math.Max(1, materials));
            }
        }

        return recipesByResult.TryGetValue(itemId, out var found) ? found : null;
    }

    /// <summary>ClassJob row of CraftType 0 (Carpenter); the eight DoH jobs run 8..15.</summary>
    private const uint CraftJobOffset = 8;

    private List<uint> FindAppraisers()
    {
        var found = new List<uint>();
        foreach (var resident in Plugin.DataManager.GetExcelSheet<ENpcResident>())
        {
            if (string.Equals(resident.Singular.ExtractText(), AppraiserName, StringComparison.OrdinalIgnoreCase))
                found.Add(resident.RowId);
        }

        if (found.Count == 0)
            log.Warning($"[Exchange] No '{AppraiserName}' NPC found; collectable turn-ins are off.");

        return found;
    }

    private string NameOfNpc(uint npcId) =>
        Plugin.DataManager.GetExcelSheet<ENpcResident>().TryGetRow(npcId, out var resident)
            ? resident.Singular.ExtractText()
            : $"NPC {npcId}";
}
