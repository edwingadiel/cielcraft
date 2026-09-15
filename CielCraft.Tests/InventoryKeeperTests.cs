using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// The two decisions P3b makes before it ever opens a window (roadmap 7.17):
/// what the storage rules do with a run's leftovers
/// (<see cref="StorageKeeper.Decide"/>) and which retainer, if any, can supply
/// a material (<see cref="RetainerOffers.Choose"/>). Both are pure, so the
/// whole policy is exercised here without a game.
/// </summary>
public class InventoryKeeperTests
{
    private const uint Ore = 5111;
    private const uint Hide = 5291;
    private const uint Junk = 4551;

    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static AutomationSettings Settings(bool desynth = true, bool trash = true) =>
        new() { DesynthUnusedByproducts = desynth, TrashCleanup = trash };

    private static DictionaryStorageView Bag(params (uint ItemId, int Count)[] items)
    {
        var view = new DictionaryStorageView();
        foreach (var (itemId, count) in items)
            view.Bag[itemId] = count;
        return view;
    }

    // ------------------------------------------------- the storage policy

    [Fact]
    public void ItemsWithoutARuleAreNeverTouched()
    {
        var actions = StorageKeeper.Decide([], Settings(), Bag((Ore, 500), (Junk, 99)));

        Assert.Empty(actions);
    }

    [Fact]
    public void DepositsOnlyTheSurplusOverTheReserve()
    {
        var rules = new List<StorageRule> { new() { ItemId = Ore, KeepInBag = 30, Action = StorageAction.Deposit } };

        var actions = StorageKeeper.Decide(rules, Settings(), Bag((Ore, 100)));

        var action = Assert.Single(actions);
        Assert.Equal(StorageAction.Deposit, action.Action);
        Assert.Equal(70, action.Amount);
    }

    [Fact]
    public void WhatThePlanStillNeedsIsReservedOnTopOfKeepInBag()
    {
        var rules = new List<StorageRule> { new() { ItemId = Ore, KeepInBag = 30, Action = StorageAction.Deposit } };
        var plan = new ProductionPlan(Hide, 1, [], [new MissingMaterial(Ore, 50)]);

        var actions = StorageKeeper.Decide(rules, Settings(), Bag((Ore, 100)), StorageKeeper.Reserved(plan));

        var action = Assert.Single(actions);
        Assert.Equal(20, action.Amount); // 100 − (30 kept + 50 the plan needs)
    }

    [Fact]
    public void NothingHappensWhenTheBagIsAtOrUnderTheReserve()
    {
        var rules = new List<StorageRule> { new() { ItemId = Ore, KeepInBag = 100, Action = StorageAction.Deposit } };

        Assert.Empty(StorageKeeper.Decide(rules, Settings(), Bag((Ore, 100))));
        Assert.Empty(StorageKeeper.Decide(rules, Settings(), Bag((Ore, 40))));
    }

    [Fact]
    public void TheRetainerCapStopsTheDeposit()
    {
        var rules = new List<StorageRule>
        {
            new() { ItemId = Ore, KeepInBag = 0, KeepInRetainer = 200, Action = StorageAction.Deposit },
        };
        var bag = Bag((Ore, 100));
        bag.Storage[Ore] = 180;

        var action = Assert.Single(StorageKeeper.Decide(rules, Settings(), bag));
        Assert.Equal(20, action.Amount);

        // Already at the cap: the surplus stays in the bag rather than going anywhere else.
        bag.Storage[Ore] = 200;
        Assert.Empty(StorageKeeper.Decide(rules, Settings(), bag));
    }

    [Fact]
    public void DesynthAndDiscardNeedTheirSwitchAndAreNeverSubstituted()
    {
        var rules = new List<StorageRule>
        {
            new() { ItemId = Hide, KeepInBag = 0, Action = StorageAction.Desynth },
            new() { ItemId = Junk, KeepInBag = 0, Action = StorageAction.Discard },
        };
        var bag = Bag((Hide, 7), (Junk, 3));

        var both = StorageKeeper.Decide(rules, Settings(), bag);
        Assert.Equal([StorageAction.Desynth, StorageAction.Discard], both.Select(a => a.Action));
        Assert.Equal([7, 3], both.Select(a => a.Amount));

        // A switch that is off removes that action entirely; it never falls
        // back to depositing or discarding something else.
        var desynthOff = StorageKeeper.Decide(rules, Settings(desynth: false), bag);
        Assert.Equal([StorageAction.Discard], desynthOff.Select(a => a.Action));

        Assert.Empty(StorageKeeper.Decide(rules, Settings(desynth: false, trash: false), bag));
    }

    [Fact]
    public void AKeepRuleReservesWithoutActing()
    {
        var rules = new List<StorageRule> { new() { ItemId = Ore, KeepInBag = 10, Action = StorageAction.Keep } };

        Assert.Empty(StorageKeeper.Decide(rules, Settings(), Bag((Ore, 100))));
    }

    [Fact]
    public void ASecondRuleForTheSameItemIsIgnored()
    {
        var rules = new List<StorageRule>
        {
            new() { ItemId = Ore, KeepInBag = 30, Action = StorageAction.Deposit },
            new() { ItemId = Ore, KeepInBag = 0, Action = StorageAction.Discard },
        };

        var action = Assert.Single(StorageKeeper.Decide(rules, Settings(), Bag((Ore, 100))));
        Assert.Equal(StorageAction.Deposit, action.Action);
    }

    [Fact]
    public void TheReasonNamesTheItemAndTheAmount()
    {
        var rules = new List<StorageRule> { new() { ItemId = Ore, Action = StorageAction.Deposit } };

        var action = Assert.Single(
            StorageKeeper.Decide(rules, Settings(), Bag((Ore, 12)), null, id => id == Ore ? "Iron Ore" : "?"));
        Assert.Contains("Iron Ore", action.Reason);
        Assert.Contains("12", action.Reason);
    }

    // --------------------------------------------- the retainer's offer

    private static RetainerSnapshot Retainer(
        int index, string name = "Kaede", uint ventureId = 0, DateTime? due = null, bool available = true) =>
        new(index, name, 16, 90, ventureId, due, 30, available);

    private static VentureOption Venture(uint itemId, int guaranteed, uint taskId = 89) =>
        new(taskId, itemId, "Iron Ore", [guaranteed, guaranteed + 5, guaranteed + 10, guaranteed + 15, guaranteed + 20],
            RetainerLevel: 14, RequiredGathering: 44, RequiredItemLevel: 0, Minutes: 60, ClassJobCategoryId: 17);

    private static RetainerSupply? Choose(
        IReadOnlyList<RetainerSnapshot> retainers,
        int amount,
        Func<RetainerSnapshot, int> held,
        Func<RetainerSnapshot, VentureOption?>? venture = null,
        bool ventures = false) =>
        RetainerOffers.Choose(retainers, Ore, amount, held, venture ?? (_ => null), ventures, Now);

    [Fact]
    public void NoOfferWhenNoRetainerHoldsTheWholeAmount()
    {
        // 30 + 30 across two retainers is not 50 in one trip, and the runner
        // marks a source task done when its run completes.
        var list = new[] { Retainer(0), Retainer(1, "Mira") };

        Assert.Null(Choose(list, 50, _ => 30));
    }

    [Fact]
    public void OffersTheRetainerWithTheSmallestSufficientStock()
    {
        var big = Retainer(0, "Kaede");
        var small = Retainer(1, "Mira");
        var list = new[] { big, small };

        var supply = Choose(list, 10, r => r.Index == 0 ? 400 : 12);

        Assert.NotNull(supply);
        Assert.Equal(RetainerSupplyKind.Withdraw, supply.Kind);
        Assert.Equal("Mira", supply.Retainer.Name);
        Assert.Equal(RetainerOffers.BellVisitSeconds, supply.EstimatedSeconds);
    }

    [Fact]
    public void AnUnavailableRetainerIsNotOffered()
    {
        var list = new[] { Retainer(0, available: false) };

        Assert.Null(Choose(list, 5, _ => 999));
    }

    [Fact]
    public void AVentureIsOnlyOfferedWhenTheSettingIsOn()
    {
        var list = new[] { Retainer(0, ventureId: 89, due: Now.AddMinutes(-5)) };

        Assert.Null(Choose(list, 10, _ => 0, _ => Venture(Ore, 15)));

        var supply = Choose(list, 10, _ => 0, _ => Venture(Ore, 15), ventures: true);
        Assert.NotNull(supply);
        Assert.Equal(RetainerSupplyKind.CollectVenture, supply.Kind);
        Assert.Equal(89u, supply.Venture!.TaskId);
    }

    [Fact]
    public void AVentureStillRunningIsNotWaitedFor()
    {
        var running = new[] { Retainer(0, ventureId: 89, due: Now.AddMinutes(45)) };
        Assert.Null(Choose(running, 10, _ => 0, _ => Venture(Ore, 15), ventures: true));

        // About to land: worth the trip, and the estimate carries the wait.
        var nearly = new[] { Retainer(0, ventureId: 89, due: Now.AddSeconds(60)) };
        var supply = Choose(nearly, 10, _ => 0, _ => Venture(Ore, 15), ventures: true);
        Assert.NotNull(supply);
        Assert.Equal(RetainerOffers.BellVisitSeconds + 60, supply.EstimatedSeconds);
    }

    [Fact]
    public void AVentureThatBringsTooLittleOrTheWrongItemIsNotOffered()
    {
        var list = new[] { Retainer(0, ventureId: 89, due: Now.AddMinutes(-1)) };

        Assert.Null(Choose(list, 20, _ => 0, _ => Venture(Ore, 15), ventures: true));
        Assert.Null(Choose(list, 10, _ => 0, _ => Venture(Hide, 40), ventures: true));
    }

    [Fact]
    public void StockBeatsAVentureOnTheSameTrip()
    {
        var list = new[] { Retainer(0, "Kaede"), Retainer(1, "Mira", ventureId: 89, due: Now.AddMinutes(-1)) };

        var supply = Choose(list, 10, r => r.Index == 0 ? 50 : 0, _ => Venture(Ore, 40), ventures: true);

        Assert.NotNull(supply);
        Assert.Equal(RetainerSupplyKind.Withdraw, supply.Kind);
        Assert.Equal("Kaede", supply.Retainer.Name);
    }

    [Fact]
    public void NothingIsOfferedForAnEmptyRequestOrAnEmptyRetainerList()
    {
        Assert.Null(Choose([Retainer(0)], 0, _ => 999));
        Assert.Null(Choose([], 5, _ => 999));
    }
}
