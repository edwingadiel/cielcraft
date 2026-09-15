using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CielCraft.Combat;
using CielCraft.Core;
using CielCraft.Game;
using CielCraft.Sourcing;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// The hunt (roadmap 7.5) against fakes: the combat database's ranking and
/// remembered spots, the source's gating, and one hunt driven tick by tick —
/// gearset, travel, target, engage, kill counted by inventory delta, retreat
/// below the HP threshold, death handling, and the rule that pause and stop
/// always disengage the combat plugin.
/// </summary>
public class HuntRunTests
{
    private const uint RaptorSkin = 5310;
    private const uint BoarHide = 5330;
    private const uint SouthShroud = 153;
    private const uint CoerthasCentral = 155;
    private const uint Thanalan = 140;
    private const uint WildBoarNpc = 100;
    private const uint RaptorNpc = 200;
    private const uint AntelopeNpc = 300;

    /// <summary>Lancer: a melee job, so the approach range is 3y.</summary>
    private const uint Lancer = 4;

    // ------------------------------------------------------------- the fakes

    private sealed class FakeZones : IZoneDirectory
    {
        public readonly Dictionary<string, uint> Territories = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<uint, MapBounds> Bounds = new();

        public uint TerritoryOf(string placeName) => Territories.GetValueOrDefault(placeName ?? "");

        public string NameOf(uint territoryId)
        {
            foreach (var pair in Territories)
            {
                if (pair.Value == territoryId)
                    return pair.Key;
            }

            return $"zone {territoryId}";
        }

        public MapBounds? BoundsOf(uint territoryId) => Bounds.GetValueOrDefault(territoryId);
    }

    private sealed class FakeCombatDriver : ICombatDriver
    {
        public int Engages;
        public int Disengages;
        public bool Available = true;

        public CombatDriverKind Kind => CombatDriverKind.RotationSolverReborn;

        public string Name => "fake rotation";

        public bool IsAvailable => Available;

        public bool IsEngaged { get; private set; }

        public void Engage()
        {
            Engages++;
            IsEngaged = true;
        }

        public void Disengage()
        {
            Disengages++;
            IsEngaged = false;
        }

        public IEnumerable<string> Describe()
        {
            yield return $"fake driver, engaged {IsEngaged}";
        }
    }

    /// <summary>Navigation that simply puts the character where it was asked to go.</summary>
    private sealed class FakeNavigation : INavigationProvider
    {
        private readonly FakeHuntBridge bridge;

        public FakeNavigation(FakeHuntBridge bridge) => this.bridge = bridge;

        public readonly List<Vector3> Moves = [];

        public bool IsAvailable => true;

        public bool IsReady => true;

        public bool IsMoving { get; set; }

        public bool MoveTo(Vector3 destination, bool fly) => MoveCloseTo(destination, 0f, fly);

        public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly)
        {
            Moves.Add(destination);
            // The leg lands at once: the travel driver's own retries are the
            // subject of TravelDriverTests, not of the hunt's state flow.
            bridge.PlayerPosition = destination;
            return true;
        }

        public void Stop() => IsMoving = false;

        public Vector3? FindNearestMeshPoint(Vector3 approximate, float halfExtentXZ, float halfExtentY) => approximate;

        public Vector3? FindPointOnFloor(Vector3 near, float halfExtentXZ) => near;
    }

    private sealed class FakeHuntBridge : IHuntBridge
    {
        public readonly List<string> Calls = [];
        public readonly List<HuntTarget> Targets = [];
        public readonly Dictionary<uint, int> ItemCounts = new();
        public readonly HashSet<uint> Attuned = [];
        public readonly HashSet<uint> Gearsets = [Lancer];

        public uint Territory = SouthShroud;
        public uint JobId;
        public float Hp = 100f;
        public bool InCombat;
        public bool Dead;
        public bool Loading;
        public int ReturnPrompts;

        public Vector3? PlayerPosition { get; set; } = Vector3.Zero;

        public bool IsMounted { get; set; }

        public void TryMount() => IsMounted = true;

        public void TryDismount() => IsMounted = false;

        public uint CurrentTerritoryId => Territory;

        public bool IsBetweenAreas => Loading;

        public bool CanTeleportTo(uint territoryId) => Attuned.Contains(territoryId);

        public bool TeleportToTerritory(uint territoryId)
        {
            Calls.Add($"Teleport({territoryId})");
            Territory = territoryId;
            return true;
        }

        public bool EquipGearsetForJob(uint classJobId)
        {
            Calls.Add($"Gearset({classJobId})");
            JobId = classJobId;
            return true;
        }

        public bool HasGearsetForJob(uint classJobId) => Gearsets.Contains(classJobId);

        public uint CurrentClassJobId => JobId;

        public int GetItemCount(uint itemId) => ItemCounts.GetValueOrDefault(itemId);

        public IReadOnlyList<HuntTarget> FindHuntTargets(
            uint bnpcNameId, IReadOnlyCollection<ulong>? excluded = null, Vector3? origin = null)
        {
            var from = origin ?? PlayerPosition ?? Vector3.Zero;
            return Targets
                .Where(t => t.BNpcNameId == bnpcNameId)
                .Where(t => excluded == null || !excluded.Contains(t.ObjectId))
                .Select(t => t with { Distance = Vector3.Distance(from, t.Position) })
                .OrderBy(t => t.Distance)
                .ToList();
        }

        public bool TargetObject(ulong objectId)
        {
            Calls.Add($"Target({objectId})");
            if (Targets.All(t => t.ObjectId != objectId))
                return false;

            CurrentTargetId = objectId;
            return true;
        }

        public ulong CurrentTargetId { get; private set; }

        public float PlayerHpPercent => Hp;

        public bool IsInCombat => InCombat;

        public bool IsDead => Dead;

        public bool AnswerReturnPrompt()
        {
            ReturnPrompts++;
            Calls.Add("Return");
            Dead = false;
            return true;
        }

        public int EnemiesTargetingMe() => InCombat ? 1 : 0;

        /// <summary>Kills the monster and drops the item, the way the server would.</summary>
        public void Kill(ulong objectId, uint itemId, int amount = 1)
        {
            var index = Targets.FindIndex(t => t.ObjectId == objectId);
            if (index < 0)
                return;

            Targets[index] = Targets[index] with { IsAlive = false, HpPercent = 0f };
            ItemCounts[itemId] = ItemCounts.GetValueOrDefault(itemId) + amount;
            InCombat = false;
        }
    }

    // ------------------------------------------------------------ the set-up

    private static HuntTarget Target(ulong id, uint bnpc, string name, int level, Vector3 position, bool others = false) =>
        new(id, bnpc, name, level, position, 0f, 100f, others, true);

    private sealed record Harness(
        FakeHuntBridge Bridge,
        FakeCombatDriver Driver,
        FakeNavigation Navigation,
        FakeZones Zones,
        CombatDatabase Database,
        CombatSource Source,
        AutomationSettings Settings,
        FakeClock Clock,
        ListLog Log);

    private static Harness Build(
        int jobLevel = 50,
        bool hunting = true,
        IReadOnlyDictionary<uint, IReadOnlyList<DropTableMob>>? table = null)
    {
        var clock = new FakeClock();
        var log = new ListLog();
        var bridge = new FakeHuntBridge();
        bridge.Attuned.Add(SouthShroud);
        bridge.Attuned.Add(CoerthasCentral);

        var navigation = new FakeNavigation(bridge);
        var driver = new FakeCombatDriver();
        var zones = new FakeZones();
        zones.Territories["South Shroud"] = SouthShroud;
        zones.Territories["Coerthas Central Highlands"] = CoerthasCentral;
        zones.Territories["Western Thanalan"] = Thanalan;
        zones.Bounds[SouthShroud] = new MapBounds(-300, -300, 300, 300);

        var settings = new AutomationSettings
        {
            HuntingEnabled = hunting,
            CombatJobId = Lancer,
            HuntRetreatHpPercent = 30,
            HuntMaxLevelAbove = 0,
        };

        table ??= new Dictionary<uint, IReadOnlyList<DropTableMob>>
        {
            [RaptorSkin] = [new DropTableMob(WildBoarNpc, "Wild Boar", 15, "South Shroud")],
        };

        var database = new CombatDatabase(
            table, zones, settings, log, clock, bridge.CanTeleportTo, () => bridge.CurrentTerritoryId);

        Func<CharacterCapabilities> capabilities = () => CharacterCapabilities.Unknown with
        {
            IsKnown = true,
            JobLevels = new Dictionary<uint, int> { [Lancer] = jobLevel },
        };

        var source = new CombatSource(
            database, driver, bridge, navigation, settings, log, clock, capabilities, id => $"item {id}");

        return new Harness(bridge, driver, navigation, zones, database, source, settings, clock, log);
    }

    /// <summary>Ticks until the run leaves the phase it is in, or the budget runs out.</summary>
    private static void TickUntil(HuntRun run, FakeClock clock, Func<bool> done, int maxTicks = 200)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            run.Tick();
            clock.Advance(0.5);
        }
    }

    // --------------------------------------------------------- the database

    [Fact]
    public void RanksARememberedSpotFirstThenAReachableZoneThenTheRest()
    {
        var table = new Dictionary<uint, IReadOnlyList<DropTableMob>>
        {
            [RaptorSkin] =
            [
                // Unreachable zone (not attuned), lowest level of the three.
                new DropTableMob(AntelopeNpc, "Antelope Doe", 10, "Western Thanalan"),
                // Reachable, higher level.
                new DropTableMob(RaptorNpc, "Raptor", 30, "Coerthas Central Highlands"),
                // Reachable and remembered: the winner despite the level.
                new DropTableMob(WildBoarNpc, "Wild Boar", 20, "South Shroud"),
                // A zone the sheets do not know: dropped entirely.
                new DropTableMob(999, "Phantom", 1, "Nowhere At All"),
            ],
        };

        var h = Build(table: table);
        h.Bridge.Territory = CoerthasCentral;
        h.Database.RememberSpot(WildBoarNpc, SouthShroud, new Vector3(12, 0, 34));

        var drops = h.Database.DropsOf(RaptorSkin);

        Assert.Equal(3, drops.Count);
        Assert.Equal(WildBoarNpc, drops[0].BNpcNameId);
        Assert.Equal(new Vector3(12, 0, 34), drops[0].Position);
        Assert.Equal(RaptorNpc, drops[1].BNpcNameId);
        Assert.Null(drops[1].Position);
        Assert.Equal(AntelopeNpc, drops[2].BNpcNameId);
        Assert.Equal("Western Thanalan", drops[2].ZoneName);
    }

    [Fact]
    public void ReachableZonesOutrankUnreachableOnesAndTheLowestLevelWinsWithin()
    {
        var table = new Dictionary<uint, IReadOnlyList<DropTableMob>>
        {
            [RaptorSkin] =
            [
                new DropTableMob(AntelopeNpc, "Antelope Doe", 5, "Western Thanalan"),
                new DropTableMob(RaptorNpc, "Raptor", 30, "Coerthas Central Highlands"),
                new DropTableMob(WildBoarNpc, "Wild Boar", 20, "South Shroud"),
            ],
        };

        var h = Build(table: table);

        var drops = h.Database.DropsOf(RaptorSkin);

        Assert.Equal(WildBoarNpc, drops[0].BNpcNameId);
        Assert.Equal(RaptorNpc, drops[1].BNpcNameId);
        Assert.Equal(AntelopeNpc, drops[2].BNpcNameId);
    }

    [Fact]
    public void RememberingTheSameCampMergesAndRemembersAFarOneSeparately()
    {
        var h = Build();
        var saves = 0;
        var database = new CombatDatabase(
            new Dictionary<uint, IReadOnlyList<DropTableMob>>(), h.Zones, h.Settings, h.Log, h.Clock,
            h.Bridge.CanTeleportTo, () => h.Bridge.CurrentTerritoryId, () => saves++);

        database.RememberSpot(WildBoarNpc, SouthShroud, new Vector3(0, 0, 0));
        database.RememberSpot(WildBoarNpc, SouthShroud, new Vector3(10, 0, 10));   // same camp
        database.RememberSpot(WildBoarNpc, SouthShroud, new Vector3(400, 0, 400)); // another camp

        Assert.Equal(2, h.Settings.HuntSpots.Count);
        var merged = h.Settings.HuntSpots.First(s => s.X < 100);
        Assert.Equal(2, merged.Hits);
        Assert.Equal(10f, merged.X);
        Assert.Equal(3, saves);

        // The busier camp is the one a hunt is sent to.
        Assert.Equal(new Vector3(10, 0, 10), database.BestSpotFor(WildBoarNpc, SouthShroud)!.Position);
    }

    [Fact]
    public void ReadsTheBundledDropTableTheRefreshScriptWrites()
    {
        // Exactly the shape tools/refresh-drops.ps1 emits, including the
        // entries the loader has to throw away (no monster, no zone).
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"cielcraft-drops-{Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(path, """
            [
              { "item": 5310, "mobs": [{ "bnpc": 2, "name": "Ruins Runner", "level": 50, "zone": "Snowcloak" }] },
              { "item": 5319, "mobs": [{ "bnpc": 3, "name": "Antelope Doe", "level": 20, "zone": "South Shroud" },
                                       { "bnpc": 4, "name": "Antelope Stag", "level": 25, "zone": "South Shroud" }] },
              { "item": 0, "mobs": [{ "bnpc": 9, "name": "Nobody", "level": 1, "zone": "South Shroud" }] },
              { "item": 7, "mobs": [{ "bnpc": 0, "name": "Nameless", "level": 1, "zone": "" }] }
            ]
            """);

        try
        {
            var log = new ListLog();
            var (table, source) = CombatDatabase.LoadTable(path, log);

            Assert.Equal(2, table.Count);
            Assert.Equal(2u, table[5310][0].BNpcNameId);
            Assert.Equal("Snowcloak", table[5310][0].Zone);
            Assert.Equal(2, table[5319].Count);
            Assert.Equal(25, table[5319][1].Level);
            Assert.Contains("2 items", source);

            // A file that is not there is a warning, not a crash.
            var (missing, why) = CombatDatabase.LoadTable(path + ".gone", log);
            Assert.Empty(missing);
            Assert.Contains("missing", why);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void SweepPointsStayInTheMapBoxAndStartNearest()
    {
        var bounds = new MapBounds(-100, -100, 100, 100);

        var points = HuntSweep.Points(bounds, new Vector3(-90, 7, -90), 50f, 6);

        Assert.Equal(6, points.Count);
        Assert.All(points, p => Assert.True(bounds.Contains(p)));
        Assert.All(points, p => Assert.Equal(7f, p.Y));
        var distances = points.Select(p => Vector3.Distance(p, new Vector3(-90, 7, -90))).ToList();
        Assert.Equal(distances.OrderBy(d => d), distances);
    }

    // ----------------------------------------------------------- the source

    [Fact]
    public void OffersAHuntWithTheMonsterTheZoneAndTheLevel()
    {
        var h = Build();

        var offer = h.Source.Offer(RaptorSkin, 5);

        Assert.NotNull(offer);
        Assert.Equal(MaterialSourceKind.Hunt, offer.Kind);
        Assert.Equal(5, offer.Amount);
        Assert.Equal(0, offer.GilCost);
        Assert.Equal("Hunt item 5310 ×5 from Wild Boar (South Shroud, Lv 15)", offer.Description);
        Assert.True(offer.EstimatedSeconds > 0);
    }

    [Fact]
    public void OffersNothingWhenHuntingIsOffOrNoCombatPluginIsLoaded()
    {
        Assert.Null(Build(hunting: false).Source.Offer(RaptorSkin, 1));

        var h = Build();
        h.Driver.Available = false;
        Assert.Null(h.Source.Offer(RaptorSkin, 1));
    }

    [Fact]
    public void OffersNothingWhenTheMonsterOutlevelsTheJobOrNothingDropsTheItem()
    {
        var h = Build(jobLevel: 10);
        Assert.Null(h.Source.Offer(RaptorSkin, 1));

        // Two levels of allowance still is not five.
        h.Settings.HuntMaxLevelAbove = 2;
        Assert.Null(h.Source.Offer(RaptorSkin, 1));

        h.Settings.HuntMaxLevelAbove = 10;
        Assert.NotNull(h.Source.Offer(RaptorSkin, 1));

        Assert.Null(h.Source.Offer(BoarHide, 1));
    }

    [Fact]
    public void AnUnknownJobLevelAllowsTheHuntRatherThanPlanningItAway()
    {
        var clock = new FakeClock();
        var log = new ListLog();
        var bridge = new FakeHuntBridge();
        bridge.Attuned.Add(SouthShroud);
        var zones = new FakeZones();
        zones.Territories["South Shroud"] = SouthShroud;
        var settings = new AutomationSettings { HuntingEnabled = true, CombatJobId = Lancer };
        var table = new Dictionary<uint, IReadOnlyList<DropTableMob>>
        {
            [RaptorSkin] = [new DropTableMob(WildBoarNpc, "Wild Boar", 90, "South Shroud")],
        };
        var database = new CombatDatabase(table, zones, settings, log, clock, bridge.CanTeleportTo, () => bridge.CurrentTerritoryId);
        var source = new CombatSource(
            database, new FakeCombatDriver(), bridge, new FakeNavigation(bridge), settings, log, clock);

        Assert.NotNull(source.Offer(RaptorSkin, 1));
    }

    // -------------------------------------------------------------- the run

    /// <summary>Starts a hunt for the given amount at a remembered spot in the current zone.</summary>
    private static HuntRun StartHunt(Harness h, int amount = 2, Vector3? spot = null)
    {
        h.Database.RememberSpot(WildBoarNpc, SouthShroud, spot ?? new Vector3(50, 0, 50));
        var offer = h.Source.Offer(RaptorSkin, amount)!;
        return (HuntRun)h.Source.Start(offer);
    }

    [Fact]
    public void EquipsTravelsEngagesAndCompletesWhenTheDropsAreInTheBag()
    {
        var h = Build();
        h.Bridge.PlayerPosition = new Vector3(50, 0, 50);
        h.Bridge.Targets.Add(Target(1, WildBoarNpc, "Wild Boar", 15, new Vector3(52, 0, 50)));
        h.Bridge.Targets.Add(Target(2, WildBoarNpc, "Wild Boar", 15, new Vector3(56, 0, 50)));

        var run = StartHunt(h);
        Assert.Equal(HuntRunState.Equipping, run.State);

        // Gearset, then the settle before anything else happens.
        run.Tick();
        Assert.Contains($"Gearset({Lancer})", h.Bridge.Calls);
        run.Tick();
        Assert.Equal(HuntRunState.TravelingToSpot, run.State);

        h.Clock.Advance(Pacing.AfterJobChange.TotalSeconds + 0.1);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);
        Assert.Equal(HuntRunState.Fighting, run.State);
        Assert.Equal(1, h.Driver.Engages);
        Assert.Equal(1UL, h.Bridge.CurrentTargetId);

        h.Bridge.Kill(1, RaptorSkin);
        run.Tick();
        Assert.Equal(HuntRunState.AfterKill, run.State);
        Assert.Equal(1, h.Driver.Disengages);
        Assert.Equal(1, run.Obtained);

        h.Clock.Advance(3);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);
        Assert.Equal(2UL, h.Bridge.CurrentTargetId);
        Assert.Equal(2, h.Driver.Engages);

        h.Bridge.Kill(2, RaptorSkin);
        h.Clock.Advance(3);
        TickUntil(run, h.Clock, () => run.State is HuntRunState.Completed or HuntRunState.Failed);

        Assert.Equal(HuntRunState.Completed, run.State);
        Assert.Equal(SourceRunState.Completed, ((ISourceRun)run).State);
        Assert.Equal(2, run.Obtained);
        Assert.Equal(2, run.Kills);
        Assert.Equal(2, h.Driver.Engages);
        Assert.Equal(2, h.Driver.Disengages);
        Assert.False(h.Driver.IsEngaged);
    }

    [Fact]
    public void TeleportsWhenTheMonsterLivesInAnotherZone()
    {
        var table = new Dictionary<uint, IReadOnlyList<DropTableMob>>
        {
            [RaptorSkin] = [new DropTableMob(RaptorNpc, "Raptor", 30, "Coerthas Central Highlands")],
        };
        var h = Build(table: table);
        h.Bridge.Territory = SouthShroud;
        h.Bridge.Targets.Add(Target(7, RaptorNpc, "Raptor", 30, new Vector3(0, 0, 0)));

        var offer = h.Source.Offer(RaptorSkin, 1)!;
        var run = (HuntRun)h.Source.Start(offer);

        run.Tick();
        run.Tick();
        h.Clock.Advance(Pacing.AfterJobChange.TotalSeconds + 0.1);
        Assert.Equal(HuntRunState.Teleporting, run.State);

        run.Tick();
        Assert.Contains($"Teleport({CoerthasCentral})", h.Bridge.Calls);
        Assert.Equal(CoerthasCentral, h.Bridge.Territory);

        // The loading screen holds the run where it is.
        h.Bridge.Loading = true;
        run.Tick();
        Assert.Equal(HuntRunState.Teleporting, run.State);

        h.Bridge.Loading = false;
        run.Tick();
        h.Clock.Advance(Pacing.AfterZoneChange.TotalSeconds + 0.1);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);
        Assert.Equal(HuntRunState.Fighting, run.State);
    }

    [Fact]
    public void LeavesAlone_MonstersAnotherPlayerIsFightingAndOnesAboveTheLevelAllowance()
    {
        var h = Build(jobLevel: 20);
        h.Bridge.PlayerPosition = new Vector3(50, 0, 50);
        h.Bridge.Targets.Add(Target(1, WildBoarNpc, "Wild Boar", 15, new Vector3(51, 0, 50), others: true));
        h.Bridge.Targets.Add(Target(2, WildBoarNpc, "Wild Boar", 40, new Vector3(52, 0, 50)));
        h.Bridge.Targets.Add(Target(3, WildBoarNpc, "Wild Boar", 15, new Vector3(60, 0, 50)));

        var run = StartHunt(h, amount: 1);
        run.Tick();
        run.Tick();
        h.Clock.Advance(Pacing.AfterJobChange.TotalSeconds + 0.1);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);

        // Not the nearest: the nearest is someone else's fight and the next
        // one is 20 levels above the job.
        Assert.Equal(3UL, h.Bridge.CurrentTargetId);

        // With the etiquette setting off the nearest one is fair game again.
        h.Settings.HuntSkipMobsTargetedByOthers = false;
        var second = StartHunt(Build(jobLevel: 20), amount: 1);
        Assert.Equal(HuntRunState.Equipping, second.State);
    }

    [Fact]
    public void RetreatsBelowTheHpThresholdAndResumesOnceHealed()
    {
        var h = Build();
        h.Bridge.PlayerPosition = new Vector3(50, 0, 50);
        h.Bridge.Targets.Add(Target(1, WildBoarNpc, "Wild Boar", 15, new Vector3(52, 0, 50)));

        var run = StartHunt(h, amount: 1);
        run.Tick();
        run.Tick();
        h.Clock.Advance(Pacing.AfterJobChange.TotalSeconds + 0.1);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);
        Assert.Equal(1, h.Driver.Engages);

        h.Bridge.Hp = 12f;
        h.Bridge.InCombat = true;
        run.Tick();

        Assert.Equal(HuntRunState.Retreating, run.State);
        Assert.Equal(1, h.Driver.Disengages);
        Assert.False(h.Driver.IsEngaged);

        // Still in combat: the run waits rather than pulling again.
        run.Tick();
        Assert.Equal(HuntRunState.Retreating, run.State);
        Assert.Equal(1, h.Driver.Engages);

        h.Bridge.InCombat = false;
        run.Tick();
        Assert.Equal(HuntRunState.Retreating, run.State);

        h.Bridge.Hp = 100f;
        run.Tick();
        Assert.Equal(HuntRunState.Targeting, run.State);

        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);
        Assert.Equal(2, h.Driver.Engages);
    }

    [Fact]
    public void AnswersTheReturnPromptAfterADeathResumesOnceAndFailsOnTheSecond()
    {
        var h = Build();
        h.Bridge.PlayerPosition = new Vector3(50, 0, 50);
        h.Bridge.Targets.Add(Target(1, WildBoarNpc, "Wild Boar", 15, new Vector3(52, 0, 50)));

        var run = StartHunt(h, amount: 1);
        run.Tick();
        run.Tick();
        h.Clock.Advance(Pacing.AfterJobChange.TotalSeconds + 0.1);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);

        h.Bridge.Dead = true;
        run.Tick();
        Assert.Equal(HuntRunState.Recovering, run.State);
        Assert.Equal(1, h.Driver.Disengages);

        run.Tick(); // answers the prompt, which revives
        Assert.Equal(1, h.Bridge.ReturnPrompts);

        run.Tick(); // revived: back to the hunt
        h.Clock.Advance(Pacing.AfterZoneChange.TotalSeconds + 0.1);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);
        Assert.Equal(HuntRunState.Fighting, run.State);

        // The second death is the end of the hunt.
        h.Bridge.Dead = true;
        run.Tick();
        run.Tick();
        run.Tick();
        Assert.Equal(HuntRunState.Failed, run.State);
        Assert.Contains("died twice", run.StatusText);
        Assert.Equal(SourceRunState.Failed, ((ISourceRun)run).State);
    }

    [Fact]
    public void PauseResumeAndStopAlwaysDisengageTheCombatPlugin()
    {
        var h = Build();
        h.Bridge.PlayerPosition = new Vector3(50, 0, 50);
        h.Bridge.Targets.Add(Target(1, WildBoarNpc, "Wild Boar", 15, new Vector3(52, 0, 50)));

        var run = StartHunt(h, amount: 3);
        run.Tick();
        run.Tick();
        h.Clock.Advance(Pacing.AfterJobChange.TotalSeconds + 0.1);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);

        run.Pause("the user took over");
        Assert.Equal(HuntRunState.Paused, run.State);
        Assert.Equal(SourceRunState.Paused, ((ISourceRun)run).State);
        Assert.Equal(1, h.Driver.Disengages);
        Assert.False(h.Driver.IsEngaged);

        // A paused run does nothing at all.
        run.Tick();
        Assert.Equal(HuntRunState.Paused, run.State);

        run.Resume();
        Assert.Equal(HuntRunState.Targeting, run.State);

        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);
        Assert.True(h.Driver.IsEngaged);

        run.Stop();
        Assert.Equal(HuntRunState.Idle, run.State);
        Assert.False(h.Driver.IsEngaged);
        Assert.Equal(2, h.Driver.Disengages);
    }

    [Fact]
    public void SweepsTheZoneWhenNoSpotIsRememberedAndFailsWhenTheMonsterIsNowhere()
    {
        var h = Build();
        h.Bridge.PlayerPosition = new Vector3(0, 0, 0);

        var offer = h.Source.Offer(RaptorSkin, 1)!;
        var run = (HuntRun)h.Source.Start(offer);
        run.Tick();
        run.Tick();
        h.Clock.Advance(Pacing.AfterJobChange.TotalSeconds + 0.1);
        run.Tick();
        Assert.Equal(HuntRunState.Sweeping, run.State);

        // The monster turns up a few sweep points in.
        for (var i = 0; i < 60 && run.State == HuntRunState.Sweeping; i++)
        {
            run.Tick();
            h.Clock.Advance(2.5);
            if (h.Navigation.Moves.Count >= 3 && h.Bridge.Targets.Count == 0)
                h.Bridge.Targets.Add(Target(9, WildBoarNpc, "Wild Boar", 15, h.Bridge.PlayerPosition!.Value));
        }

        Assert.True(h.Navigation.Moves.Count >= 3, $"only {h.Navigation.Moves.Count} sweep legs were walked");
        Assert.NotEqual(HuntRunState.Sweeping, run.State);
        TickUntil(run, h.Clock, () => run.State == HuntRunState.Fighting);
        Assert.Equal(HuntRunState.Fighting, run.State);
    }

    [Fact]
    public void FailsWithAReasonWhenNothingCanDriveTheFight()
    {
        var h = Build();
        h.Bridge.PlayerPosition = new Vector3(50, 0, 50);
        var offer = h.Source.Offer(RaptorSkin, 1)!;
        h.Driver.Available = false;

        var run = (HuntRun)h.Source.Start(offer);

        Assert.Equal(HuntRunState.Failed, run.State);
        Assert.Contains("combat plugin", run.StatusText);
    }

    [Fact]
    public void FailsWhenTheCombatJobHasNoGearset()
    {
        var h = Build();
        var offer = h.Source.Offer(RaptorSkin, 1)!;
        h.Bridge.Gearsets.Clear();

        var run = (HuntRun)h.Source.Start(offer);

        Assert.Equal(HuntRunState.Failed, run.State);
        Assert.Contains("gearset", run.StatusText);
    }
}
