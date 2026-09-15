using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Combat;
using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

/// <summary>
/// The combat drivers (roadmap 7.5, M4 package B). Neither combat plugin is
/// installed on the test machine and neither can be, so the call gates sit
/// behind <see cref="IRotationSolverIpc"/> / <see cref="IBossModIpc"/> and
/// these tests drive fakes: what the drivers ask for, what they do when the
/// plugin is away, and how the selector picks between them.
/// </summary>
public class CombatDriverTests
{
    // ------------------------------------------------------------- fakes

    private sealed class FakeRsr : IRotationSolverIpc
    {
        public readonly List<string> Calls = [];

        public bool IsLoaded { get; set; } = true;

        /// <summary>Null stands for "the call gate did not answer", as in the real binding.</summary>
        public bool? Active { get; set; } = false;

        public bool Accept { get; set; } = true;

        public bool? AutorotationActive()
        {
            Calls.Add("AutorotationActive");
            return Active;
        }

        public bool ChangeOperatingMode(int state)
        {
            Calls.Add($"ChangeOperatingMode({state})");
            if (!Accept)
                return false;

            Active = state != RotationSolverStates.Off;
            return true;
        }

        public bool SetPriorityNameId(uint bnpcNameId, bool add)
        {
            Calls.Add($"{(add ? "AddPriority" : "RemovePriority")}({bnpcNameId})");
            return Accept;
        }

        /// <summary>The calls that are not the availability probe; the probe is noise here.</summary>
        public List<string> Commands => Calls.Where(c => c != "AutorotationActive").ToList();
    }

    private sealed class FakeBmr : IBossModIpc
    {
        public readonly List<string> Calls = [];

        public bool IsLoaded { get; set; } = true;

        /// <summary>Empty = no preset active; null = the call gate did not answer.</summary>
        public string? Active { get; set; } = "";

        public readonly HashSet<string> KnownPresets = [BossModDriver.DefaultPreset];

        public string? GetActivePreset()
        {
            Calls.Add("GetActive");
            return Active;
        }

        public bool SetActivePreset(string name)
        {
            Calls.Add($"SetActive({name})");
            if (!KnownPresets.Contains(name))
                return false;

            Active = name;
            return true;
        }

        public bool ClearActivePreset()
        {
            Calls.Add("ClearActive");
            var had = Active is { Length: > 0 };
            if (Active != null)
                Active = "";
            return had;
        }

        public bool? HasQueuedActions()
        {
            Calls.Add("HasEntries");
            return false;
        }

        public List<string> Commands => Calls.Where(c => c != "GetActive" && c != "HasEntries").ToList();
    }

    /// <summary>A driver the selector tests can switch on and off by hand.</summary>
    private sealed class StubDriver : ICombatDriver
    {
        public StubDriver(CombatDriverKind kind, bool available) => (Kind, IsAvailable) = (kind, available);

        public CombatDriverKind Kind { get; }

        public string Name => Kind.ToString();

        public bool IsAvailable { get; set; }

        public bool IsEngaged { get; private set; }

        public int Engages;
        public int Disengages;

        /// <summary>Set to make every probe throw, the way a half-unloaded plugin does.</summary>
        public bool Throws { get; set; }

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
            if (Throws)
                throw new InvalidOperationException("probe blew up");
            yield return $"{Name}: stub";
        }
    }

    private static CombatDriverSelector Selector(FakeClock clock, ListLog log, params ICombatDriver[] drivers) =>
        new(log, clock, drivers);

    // ------------------------------------------- Rotation Solver Reborn

    [Fact]
    public void RotationSolverEngagesInManualModeAndTurnsOffOnDisengage()
    {
        var ipc = new FakeRsr();
        var driver = new RotationSolverDriver(ipc, new ListLog());

        driver.Engage();

        Assert.True(driver.IsEngaged);
        Assert.Equal(1, driver.Engagements);
        // Manual, not Auto: CielCraft picks the target, so the plugin must not retarget.
        Assert.Contains($"ChangeOperatingMode({RotationSolverStates.Manual})", ipc.Commands);

        driver.Disengage();

        Assert.False(driver.IsEngaged);
        Assert.Equal($"ChangeOperatingMode({RotationSolverStates.Off})", ipc.Commands.Last());
    }

    [Fact]
    public void RotationSolverEngageIsIdempotentButDisengageAlwaysAsks()
    {
        var ipc = new FakeRsr();
        var driver = new RotationSolverDriver(ipc, new ListLog());

        driver.Engage();
        driver.Engage();
        Assert.Equal(1, driver.Engagements);
        Assert.Single(ipc.Commands);

        driver.Disengage();
        driver.Disengage();
        // A stray /rotation Auto or a reload leaves the plugin running, so the
        // stop is issued every time even when the driver thinks it is idle.
        Assert.Equal(2, ipc.Commands.Count(c => c == $"ChangeOperatingMode({RotationSolverStates.Off})"));
    }

    [Fact]
    public void RotationSolverDoesNothingWhenThePluginIsNotLoaded()
    {
        var ipc = new FakeRsr { IsLoaded = false };
        var log = new ListLog();
        var driver = new RotationSolverDriver(ipc, log);

        Assert.False(driver.IsAvailable);

        driver.Engage();

        Assert.False(driver.IsEngaged);
        Assert.Empty(ipc.Commands);
        Assert.Contains(log.Lines, l => l.Contains("not answering", StringComparison.Ordinal));
        Assert.Contains("not answering", driver.LastProblem);
    }

    [Fact]
    public void RotationSolverIsUnavailableWhenTheGateDoesNotAnswer()
    {
        var ipc = new FakeRsr { Active = null };
        var driver = new RotationSolverDriver(ipc, new ListLog());

        Assert.False(driver.IsAvailable);
        Assert.Contains(driver.Describe(), l => l.Contains("no answer", StringComparison.Ordinal));
    }

    [Fact]
    public void RotationSolverEngageModeCanBePutBackToAuto()
    {
        var ipc = new FakeRsr();
        var driver = new RotationSolverDriver(ipc, new ListLog()) { EngageState = RotationSolverStates.Auto };

        driver.Engage();

        Assert.Contains($"ChangeOperatingMode({RotationSolverStates.Auto})", ipc.Commands);
        Assert.Contains(driver.Describe(), l => l.Contains("Auto", StringComparison.Ordinal));
    }

    [Fact]
    public void RotationSolverPrioritisesTheHuntedMobOnlyWhileItIsThere()
    {
        var ipc = new FakeRsr();
        var driver = new RotationSolverDriver(ipc, new ListLog());

        Assert.True(driver.PrioritizeMob(1234, true));
        Assert.Contains("AddPriority(1234)", ipc.Commands);

        Assert.True(driver.PrioritizeMob(1234, false));
        Assert.Contains("RemovePriority(1234)", ipc.Commands);

        // Nothing to prioritise, and nothing to talk to.
        Assert.False(driver.PrioritizeMob(0, true));
        ipc.IsLoaded = false;
        Assert.False(driver.PrioritizeMob(1234, true));
    }

    [Fact]
    public void RotationSolverReportsARefusedEngageOnceAndKeepsItsState()
    {
        var ipc = new FakeRsr { Accept = false };
        var log = new ListLog();
        var driver = new RotationSolverDriver(ipc, log);

        driver.Engage();
        driver.Engage();

        Assert.False(driver.IsEngaged);
        Assert.Equal(0, driver.Engagements);
        Assert.Contains("refused", driver.LastProblem);
        Assert.Single(log.Lines, l => l.Contains("refused to start", StringComparison.Ordinal));
    }

    // -------------------------------------------------- BossMod Reborn

    [Fact]
    public void BossModEngagesAnAutorotationPresetAndClearsIt()
    {
        var ipc = new FakeBmr();
        var driver = new BossModDriver(ipc, new ListLog());

        Assert.Equal(BossModDriver.DefaultPreset, driver.PresetName);

        driver.Engage();

        Assert.True(driver.IsEngaged);
        Assert.Equal($"SetActive({BossModDriver.DefaultPreset})", ipc.Commands.Last());
        Assert.Equal(BossModDriver.DefaultPreset, ipc.Active);

        driver.Disengage();

        Assert.False(driver.IsEngaged);
        Assert.Contains("ClearActive", ipc.Commands);
        Assert.Equal("", ipc.Active);
    }

    [Fact]
    public void BossModRefusesAPresetItDoesNotKnow()
    {
        var ipc = new FakeBmr();
        var log = new ListLog();
        var driver = new BossModDriver(ipc, log, "Hunting");

        driver.Engage();

        Assert.False(driver.IsEngaged);
        Assert.Equal("", ipc.Active);
        Assert.Contains("refused", driver.LastProblem);
    }

    [Fact]
    public void BossModIsUnavailableWhenTheGateDoesNotAnswer()
    {
        var ipc = new FakeBmr { Active = null };
        var driver = new BossModDriver(ipc, new ListLog());

        Assert.False(driver.IsAvailable);

        ipc.Active = "";
        Assert.True(driver.IsAvailable);

        ipc.IsLoaded = false;
        Assert.False(driver.IsAvailable);
    }

    [Fact]
    public void BossModDisengageSucceedsEvenWhenNothingWasActive()
    {
        var ipc = new FakeBmr();
        var driver = new BossModDriver(ipc, new ListLog());

        driver.Disengage();

        // ClearActive answers false with no preset active; that is the wanted
        // state, so it must not be reported as a failure.
        Assert.Equal("", driver.LastProblem);
    }

    // ------------------------------------------------------- the no-op

    [Fact]
    public void NoCombatDriverIsNeverAvailableAndNeverThrows()
    {
        var driver = NoCombatDriver.Instance;

        driver.Engage();
        driver.Disengage();

        Assert.Equal(CombatDriverKind.None, driver.Kind);
        Assert.False(driver.IsAvailable);
        Assert.False(driver.IsEngaged);
        Assert.NotEmpty(driver.Describe());
    }

    // ------------------------------------------------------ the selector

    [Fact]
    public void SelectorPrefersRotationSolverThenBossModThenNothing()
    {
        var clock = new FakeClock();
        var rsr = new StubDriver(CombatDriverKind.RotationSolverReborn, available: true);
        var bmr = new StubDriver(CombatDriverKind.BossModReborn, available: true);
        var selector = Selector(clock, new ListLog(), rsr, bmr);

        Assert.Same(rsr, selector.Current);

        rsr.IsAvailable = false;
        clock.Advance(10);
        Assert.Same(bmr, selector.Current);

        bmr.IsAvailable = false;
        clock.Advance(10);
        Assert.Same(NoCombatDriver.Instance, selector.Current);
        Assert.False(selector.IsAvailable);
    }

    [Fact]
    public void SelectorReprobesOnlyEveryFewSecondsUnlessAsked()
    {
        var clock = new FakeClock();
        var rsr = new StubDriver(CombatDriverKind.RotationSolverReborn, available: false);
        var selector = Selector(clock, new ListLog(), rsr);

        Assert.Same(NoCombatDriver.Instance, selector.Current);

        rsr.IsAvailable = true;
        Assert.Same(NoCombatDriver.Instance, selector.Current); // still inside the probe interval

        Assert.Same(rsr, selector.Refresh());                   // the panel's button ignores it
    }

    [Fact]
    public void SelectorNeverSwapsDriversDuringAFight()
    {
        var clock = new FakeClock();
        var rsr = new StubDriver(CombatDriverKind.RotationSolverReborn, available: true);
        var bmr = new StubDriver(CombatDriverKind.BossModReborn, available: true);
        var selector = Selector(clock, new ListLog(), rsr, bmr);

        selector.Current.Engage();
        rsr.IsAvailable = false;
        clock.Advance(60);

        Assert.Same(rsr, selector.Current);

        selector.DisengageAll();
        clock.Advance(60);

        Assert.Same(bmr, selector.Current);
        Assert.Equal(1, rsr.Disengages);
        Assert.Equal(1, bmr.Disengages); // the stop goes to every adapter, engaged or not
    }

    [Fact]
    public void SelectorSurvivesADriverThatThrowsWhileDescribing()
    {
        var clock = new FakeClock();
        var rsr = new StubDriver(CombatDriverKind.RotationSolverReborn, available: true) { Throws = true };
        var selector = Selector(clock, new ListLog(), rsr);

        var lines = selector.Describe().ToList();

        Assert.Contains(lines, l => l.Contains("blew up", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectorWithNoAdaptersSaysSo()
    {
        var selector = Selector(new FakeClock(), new ListLog());

        Assert.Same(NoCombatDriver.Instance, selector.Current);
        Assert.Contains(selector.Describe(), l => l.Contains("No combat plugin adapters", StringComparison.Ordinal));
    }

    // --------------------------------------------------- job selection

    [Fact]
    public void BestCombatJobTakesTheHighestLevelledJobWithAGearset()
    {
        var caps = CharacterCapabilities.Unknown with
        {
            IsKnown = true,
            JobLevels = new Dictionary<uint, int>
            {
                [8] = 100,   // CRP — a crafter is never a hunting job
                [16] = 100,  // MIN
                [2] = 90,    // PGL, the class behind MNK
                [20] = 90,   // MNK
                [21] = 80,   // WAR
                [39] = 95,   // RPR, highest but no gearset
            },
        };

        // Everything but the reaper has a gearset.
        Assert.Equal(20u, caps.BestCombatJob(jobId => jobId != 39));

        // With one saved for the reaper it wins on level.
        Assert.Equal(39u, caps.BestCombatJob(_ => true));

        // Nothing saved at all.
        Assert.Equal(0u, caps.BestCombatJob(_ => false));
    }

    [Fact]
    public void BestCombatJobIgnoresCraftersGatherersAndUnlevelledJobs()
    {
        var caps = CharacterCapabilities.Unknown with
        {
            IsKnown = true,
            JobLevels = new Dictionary<uint, int> { [8] = 100, [15] = 100, [18] = 100, [24] = 0 },
        };

        Assert.Equal(0u, caps.BestCombatJob(_ => true));
        Assert.False(CharacterCapabilities.IsCombatJob(8));
        Assert.False(CharacterCapabilities.IsCombatJob(18));
        Assert.False(CharacterCapabilities.IsCombatJob(0));
        Assert.True(CharacterCapabilities.IsCombatJob(1));
        Assert.True(CharacterCapabilities.IsCombatJob(19));
    }
}
