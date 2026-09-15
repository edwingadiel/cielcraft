using System.Collections.Generic;
using CielCraft.Core;

namespace CielCraft.Game;

/// <summary>
/// Provider boundary between CielCraft's logic and the game (spec §6/§8).
/// The only Dalamud-backed implementation is <see cref="DalamudGameBridge"/>;
/// tests use mocks.
/// </summary>
public interface IGameBridge : ITravelBridge
{
    bool IsLoggedIn { get; }

    /// <summary>True while a synthesis is in progress.</summary>
    bool IsCrafting { get; }

    /// <summary>True while the crafting log is open / a recipe is selected but no synthesis is running.</summary>
    bool IsPreparingToCraft { get; }

    bool IsGathering { get; }

    /// <summary>Null when no character is logged in.</summary>
    PlayerSnapshot? GetPlayerState();

    /// <summary>Null when no craft is active.</summary>
    CraftSnapshot? GetCraftState();

    /// <summary>Null when no gathering node is open.</summary>
    GatheringSnapshot? GetGatheringState();

    /// <summary>True while a gather swing/action is animating.</summary>
    bool IsGatheringActionInProgress { get; }

    /// <summary>
    /// Nearest targetable gathering point not in the excluded set, or null.
    /// Ranked by distance from <paramref name="origin"/> when given (so a node
    /// group can be preferred over whatever happens to be next to the player);
    /// the snapshot's Distance is always the player's distance.
    /// </summary>
    GatheringNodeSnapshot? FindNearestGatheringNode(
        IReadOnlyCollection<ulong>? excludedObjectIds = null, System.Numerics.Vector3? origin = null);

    /// <summary>Targets and interacts with the object. False when it is gone.</summary>
    bool InteractWithObject(ulong objectId);

    /// <summary>Clicks an item slot in the open gathering window. False when not clickable.</summary>
    bool GatherSlot(int slotIndex);

    /// <summary>Closes the gathering node window if it is open; the character cannot move while it is up.</summary>
    void CloseGatheringWindow();

    /// <summary>True when the game reports the craft action as currently usable (CP, state, availability).</summary>
    bool IsCraftActionReady(uint craftActionId);

    /// <summary>Requests execution of a craft action. True if the game accepted the request.</summary>
    bool ExecuteCraftAction(uint craftActionId);

    /// <summary>True when the crafting log is open with a recipe selected and Synthesize is pressable.</summary>
    bool IsReadyToStartCraft { get; }

    /// <summary>Recipe currently selected in the crafting log; 0 when none.</summary>
    ushort SelectedRecipeId { get; }

    /// <summary>Raw crafting-log selection fields, for the diagnostic report.</summary>
    string DescribeRecipeSelection();

    /// <summary>Presses Synthesize on the open crafting log. True if the request was issued.</summary>
    bool StartSynthesis();

    /// <summary>Result item id and per-craft yield of the active craft; null when not crafting.</summary>
    (uint ItemId, int Amount)? CurrentCraftResult { get; }

    /// <summary>Total NQ+HQ count of the item in the player inventory.</summary>
    int GetItemCount(uint itemId);

    /// <summary>HQ-only count of the item in the player inventory.</summary>
    int GetHqItemCount(uint itemId);

    /// <summary>
    /// Ingredient lines of a recipe with live inventory counts; empty when the
    /// recipe id is unknown.
    /// </summary>
    IReadOnlyList<IngredientRequirement> GetRecipeRequirements(ushort recipeId);

    /// <summary>ClassJob row id of the current job; 0 when not logged in.</summary>
    uint CurrentClassJobId { get; }

    /// <summary>Opens the crafting log on the given recipe.</summary>
    void OpenRecipe(uint recipeId);

    /// <summary>Closes the crafting log if it is open.</summary>
    void CloseRecipeNote();

    /// <summary>Equips the best gearset for the job. False when none exists.</summary>
    bool EquipGearsetForJob(uint classJobId);

    /// <summary>Current territory row id.</summary>
    uint CurrentTerritoryId { get; }

    /// <summary>True during zone transitions/loading screens.</summary>
    bool IsBetweenAreas { get; }

    /// <summary>Teleports to an attuned aetheryte in the territory. False when none is attuned.</summary>
    bool TeleportToTerritory(uint territoryId);

    /// <summary>
    /// Teleports to the home point for crafting (roadmap 7.6): the estate hall,
    /// the apartment, or — inn rooms being out of Telepo's reach — the nearest
    /// inn city's aetheryte. territoryId is where the teleport lands, 0 when
    /// no such aetheryte is in the teleport list. False when not issued.
    /// </summary>
    bool TeleportHome(CraftingLocation location, out uint territoryId);

    // IsMounted / TryMount / TryDismount / PlayerPosition come from ITravelBridge.

    /// <summary>Free bag slots in the main inventory.</summary>
    int GetFreeInventorySlots();

    /// <summary>Quick Synthesis is offered for the selected recipe.</summary>
    bool IsQuickSynthAvailable { get; }

    /// <summary>Opens the quick-synthesis dialog from the crafting log.</summary>
    bool OpenQuickSynthesisDialog();

    /// <summary>Confirms the quick-synthesis dialog for the given count (uses HQ materials).</summary>
    bool ConfirmQuickSynthesisDialog(int count);

    /// <summary>The quick-synthesis progress window is up.</summary>
    bool IsQuickSynthesisActive { get; }

    /// <summary>Cancels a quick synthesis in progress.</summary>
    void CancelQuickSynthesis();

    /// <summary>Uses an inventory item (e.g. a cordial). False when unusable or absent.</summary>
    bool UseItem(uint itemId);

    /// <summary>The open gathering node has quick gathering toggled on.</summary>
    bool IsQuickGatheringEnabled { get; }

    /// <summary>Toggles quick gathering off on the open node.</summary>
    void DisableQuickGathering();

    /// <summary>Live state of the collectable gathering window; null when closed.</summary>
    CollectableGatheringSnapshot? GetCollectableGatheringState();

    /// <summary>A gearset exists for the job.</summary>
    bool HasGearsetForJob(uint classJobId);

    /// <summary>Item count across saddlebags and cached retainer pages (read-only awareness).</summary>
    int GetStoredItemCount(uint itemId);

    /// <summary>Lowest condition of equipped gear, 0-100.</summary>
    float GetLowestEquipmentConditionPercent();

    /// <summary>Opens the self-repair window (general action).</summary>
    void OpenRepairWindow();

    /// <summary>Plays an in-game system sound effect (&lt;se.N&gt;, 1..16). False when nothing could play it.</summary>
    bool PlaySoundEffect(int soundEffectNumber);

    /// <summary>Runs a text command ("/shutdown") as if typed into the chat box.</summary>
    void ExecuteChatCommand(string command);

    bool IsAddonVisible(string addonName);

    /// <summary>Fires an integer callback on a visible addon. False when it is not open.</summary>
    bool FireAddonCallbackInt(string addonName, int value);

    /// <summary>Every non-empty string AtkValue of an open addon, in index order; empty when it is not open.</summary>
    IReadOnlyList<string> ReadAddonStrings(string addonName);

    /// <summary>True when the named player is in the party/alliance or wears the local player's free company tag.</summary>
    bool IsPartyOrFreeCompanyMember(string playerName);

    /// <summary>Seconds left on the Well Fed buff; 0 when not fed.</summary>
    float GetFoodBuffRemainingSeconds();

    /// <summary>Seconds left on the Medicated buff (potions, roadmap 7.11); 0 when none.</summary>
    float GetMedicatedRemainingSeconds();

    /// <summary>Every meal and medicine stack in the player inventory, one entry per NQ/HQ stack (roadmap 7.11).</summary>
    IReadOnlyList<ConsumableItem> ListConsumables();

    /// <summary>
    /// Fills the selected recipe's ingredient slots with as many HQ materials
    /// as owned (NQ for the remainder). False when no recipe is selected.
    /// </summary>
    bool FillHqIngredients();

    /// <summary>Presses the crafting log's NQ or HQ fill button; true when every ingredient is then assigned.</summary>
    bool FillIngredients(bool preferHq);

    /// <summary>Every ingredient of the selected recipe has NQ+HQ assigned up to its required amount.</summary>
    bool AreIngredientsAssigned();

    // ---- Materia extraction (roadmap 7.2) ----

    /// <summary>Spiritbond of every equipped piece (0..10000 = 0..100%), by equipment slot; empty slots omitted.</summary>
    IReadOnlyList<EquippedSpiritbond> GetEquipmentSpiritbond();

    /// <summary>Equipment slots whose piece is at 100% spiritbond, in slot order; empty when none.</summary>
    IReadOnlyList<int> GetSpiritbondReadySlots();

    /// <summary>Opens the Materialize window (general action "Materia Extraction"). False when the action is unknown to the sheets.</summary>
    bool OpenMaterialize();

    /// <summary>Selects the equipment slot's row in the open Materialize window, which raises MaterializeDialog. False when the window is not open.</summary>
    bool ExtractMateria(int slot);

    /// <summary>Presses Yes on the open MaterializeDialog. False when it is not open.</summary>
    bool ConfirmMaterializeDialog();

    /// <summary>The extraction is playing out (the character is occupied by it).</summary>
    bool IsMaterializing { get; }

    /// <summary>Dismisses MaterializeDialog (No) and the Materialize window, whichever are open.</summary>
    void CloseMaterialize();

    /// <summary>
    /// Facts about the open node for the rotation engine (roadmap 7.14): the
    /// chosen slot's Gatherer's Boon chance (-1 when the window does not show
    /// it), the point's bonus conditions and whether the character meets
    /// them, timed-ness, and the gatherer statuses on the player.
    /// <see cref="GatheringNodeFacts.Unknown"/> when the node cannot be read.
    /// </summary>
    GatheringNodeFacts GetGatheringNodeFacts(ulong nodeObjectId, int slotIndex);

    /// <summary>Seconds left on a status of the local player; 0 when absent.</summary>
    float GetStatusRemainingSeconds(uint statusId);

    /// <summary>The item's recast timer is running (cordials share one).</summary>
    bool IsItemOnCooldown(uint itemId);

    // ---- Shops (7.3b) ----

    /// <summary>Gil the character carries; 0 when not logged in.</summary>
    long Gil { get; }

    /// <summary>
    /// Buys <paramref name="count"/> of the item from the open Shop window
    /// (roadmap 7.3b). False when the window is shut or the shop does not
    /// list the item. The purchase is verified by the caller's inventory
    /// delta, never by this return value (spec §34).
    /// </summary>
    bool BuyFromShop(uint itemId, int count);

    /// <summary>Closes the Shop window if it is open.</summary>
    void CloseShop();
    // ---- NPC (7.3) ----

    /// <summary>An aetheryte of that territory is in the teleport list, so a trip there is possible.</summary>
    bool CanTeleportTo(uint territoryId);

    /// <summary>
    /// The nearest loaded event NPC (or event object — summoning bells and the
    /// like) with that data id, and where it stands; null when none is in the
    /// object table. The sheets say where an NPC belongs, the object table says
    /// where it actually is.
    /// </summary>
    (ulong ObjectId, System.Numerics.Vector3 Position)? FindNpcObject(uint dataId);

    /// <summary>Option labels of the open SelectString / SelectIconString, in the order the menu lists them; empty when neither is open.</summary>
    IReadOnlyList<string> ReadDialogOptions();

    /// <summary>
    /// Picks the option of the open SelectString / SelectIconString whose label
    /// contains the text (case-insensitive). False when no menu is open or no
    /// option matches.
    /// </summary>
    bool SelectDialogOption(string textContains);

    /// <summary>Advances the open Talk box one step. False when none is open.</summary>
    bool AdvanceTalk();
    // ---- Retainers / desynth (7.17) ----

    /// <summary>Every retainer of the character, in the retainer list's sorted order; empty when the list is not loaded.</summary>
    IReadOnlyList<RetainerSnapshot> GetRetainers();

    /// <summary>
    /// The item count on one retainer's cached pages. A retainer the client
    /// has not loaded this session reports zero; the count is awareness, and
    /// the bag delta after a withdrawal stays the truth.
    /// </summary>
    int GetRetainerItemCount(int retainerIndex, uint itemId);

    /// <summary>The nearest targetable summoning bell, or null when none is in the object table.</summary>
    SummoningBellSnapshot? FindSummoningBell();

    /// <summary>A summoning bell is close enough to ring without moving.</summary>
    bool IsNearSummoningBell { get; }

    /// <summary>Rings the nearest summoning bell, which raises RetainerList. False when none is in reach.</summary>
    bool OpenRetainerList();

    /// <summary>Summons the retainer at the sorted index from the open RetainerList. False when it is not open.</summary>
    bool SelectRetainer(int retainerIndex);

    /// <summary>A retainer is summoned (its menu, inventory or venture windows can be driven).</summary>
    bool IsRetainerSummoned { get; }

    /// <summary>Picks the summoned retainer's menu entry whose text contains this (the retainer SelectString). False when it is not open or has no such entry.</summary>
    bool SelectRetainerMenuOption(string textContains);

    /// <summary>The summoned retainer's inventory window is open and its pages are readable.</summary>
    bool IsRetainerInventoryOpen { get; }

    /// <summary>
    /// Moves up to <paramref name="count"/> of the item from the summoned
    /// retainer's pages into the bag. Returns how many the client was asked
    /// to move (whole stacks; the caller verifies by the bag delta).
    /// </summary>
    int WithdrawFromRetainer(uint itemId, int count);

    /// <summary>The deposit direction of <see cref="WithdrawFromRetainer"/>: bag → the summoned retainer.</summary>
    int DepositToRetainer(uint itemId, int count);

    /// <summary>Sends the summoned retainer on the venture (a RetainerTask row id). False when the venture window would not take it.</summary>
    bool AssignVenture(uint ventureTaskId);

    /// <summary>Collects the summoned retainer's finished venture. False when nothing is waiting.</summary>
    bool CollectVenture();

    /// <summary>Sends the summoned retainer away and closes its windows.</summary>
    void DismissRetainer();

    /// <summary>Closes RetainerList if it is open.</summary>
    void CloseRetainerList();

    /// <summary>Opens the desynthesis dialog on one of the item's stacks (Salvage agent). False when it cannot be desynthesized.</summary>
    bool Desynthesize(uint itemId);

    /// <summary>Confirms an open SalvageDialog and dismisses the result window. False when neither is open.</summary>
    bool ConfirmDesynthesis();

    /// <summary>Discards one stack of the item (inventory context menu + SelectYesno). False when it is not in the bag.</summary>
    bool DiscardItem(uint itemId);
    // ---- Exchanges (7.17) ----

    /// <summary>
    /// How much of a currency the character holds: scrips and tomestones live
    /// in the Currency container, Grand Company seals in the company purse
    /// (the seal items 20/21/22 map to the character's own company).
    /// </summary>
    long GetCurrencyCount(uint currencyItemId);

    /// <summary>GrandCompany row id the character belongs to; 0 when none.</summary>
    uint GrandCompanyId { get; }

    /// <summary>Grand Company rank (GrandCompanyRank row id); 0 when unranked or unaffiliated.</summary>
    int GrandCompanyRank { get; }

    /// <summary>
    /// Buys from the currency-exchange window that is open
    /// (ShopExchangeCurrency / InclusionShop / GrandCompanyExchange). False
    /// when no exchange window is up or the item is not on its list; the
    /// caller verifies by inventory delta either way.
    /// </summary>
    bool ExchangeBuy(uint shopId, uint itemId, int count);

    /// <summary>Closes whichever currency-exchange window is open.</summary>
    void CloseExchangeShop();

    /// <summary>Items in the bag that are collectables at or above a collectability (quality / 10, roadmap 7.23).</summary>
    int GetCollectableCount(uint itemId, int minCollectability);

    /// <summary>Selects the collectable's row in the open CollectablesShop window. False when it is not listed.</summary>
    bool TurnInCollectable(uint itemId);

    /// <summary>Confirms the selected turn-in in the open CollectablesShop window.</summary>
    bool HandInCollectable();

    /// <summary>Closes the CollectablesShop window if it is open.</summary>
    void CloseCollectablesShop();
    // ---- Combat (7.5) ----

    /// <summary>
    /// Every loaded, hostile battle NPC of that BNpcName that is not in the
    /// excluded set, nearest first. Ranked by distance from
    /// <paramref name="origin"/> when one is given (so a known spot can be
    /// preferred over whatever wandered past); each snapshot's Distance is
    /// always the player's. Empty when nothing is in the object table.
    /// </summary>
    IReadOnlyList<HuntTargetSnapshot> FindHuntTargets(
        uint bnpcNameId,
        IReadOnlyCollection<ulong>? excludedObjectIds = null,
        System.Numerics.Vector3? origin = null);

    /// <summary>Makes the object the current target. False when it is gone or not targetable.</summary>
    bool TargetObject(ulong objectId);

    /// <summary>Object id of the current target; 0 when nothing is targeted.</summary>
    ulong CurrentTargetId { get; }

    /// <summary>
    /// The BNpcName row id and display name of the current target when it is a
    /// monster; null when nothing is targeted or the target is not one. The
    /// panel's "remember this spot" and the hunt run's drop verification both
    /// need the row id, which the object id alone does not give.
    /// </summary>
    (uint BNpcNameId, string Name)? CurrentTargetMob { get; }

    /// <summary>The player's HP as a percentage, 0..100; 0 when not logged in.</summary>
    float PlayerHpPercent { get; }

    /// <summary>The character is in combat.</summary>
    bool IsInCombat { get; }

    /// <summary>The character is knocked out (dead or waiting for a raise).</summary>
    bool IsDead { get; }

    /// <summary>
    /// Answers the post-death prompt: confirms an open SelectYesno, or asks to
    /// return to the home point when the client says the character is revivable
    /// and no prompt is up. False when there was nothing to answer.
    /// </summary>
    bool AnswerReturnPrompt();

    /// <summary>How many battle NPCs currently have the player as their target.</summary>
    int EnemiesTargetingMe();
}

/// <summary>One equipped piece's spiritbond (roadmap 7.2): the equipment slot, the item and 0..10000.</summary>
public sealed record EquippedSpiritbond(int Slot, uint ItemId, int Spiritbond)
{
    public const int Full = 10000;

    public bool IsFull => Spiritbond >= Full;

    public float Percent => Spiritbond / 100f;
}
