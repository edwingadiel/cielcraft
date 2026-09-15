using CielCraft.Core;
using Xunit;

namespace CielCraft.Tests;

public class SolutionCacheTests
{
    private const string Version = "1.2.3.4";

    private static readonly CraftSetup Setup = new(
        RecipeLevel: 580, MaxProgress: 3900, MaxQuality: 10920, MaxDurability: 70, IsExpert: false,
        Craftsmanship: 4021, Control: 3998, Cp: 601, Level: 100,
        Manipulation: true, HeartAndSoul: false, QuickInnovation: false);

    private static readonly CraftObjective Objective = new(TargetQuality: 10920, InitialQuality: 1200);

    private static readonly CraftSolution Rotation = new([100390u, 100131u, 19004u, 100128u], BaseProgress: 250, BaseQuality: 300);

    /// <summary>One variant per key field: each must miss against the base key.</summary>
    public static TheoryData<string, CraftSetup, CraftObjective> Variants => new()
    {
        { "RecipeLevel", Setup with { RecipeLevel = 581 }, Objective },
        { "MaxProgress", Setup with { MaxProgress = 3901 }, Objective },
        { "MaxQuality", Setup with { MaxQuality = 10921 }, Objective },
        { "MaxDurability", Setup with { MaxDurability = 80 }, Objective },
        { "IsExpert", Setup with { IsExpert = true }, Objective },
        { "Craftsmanship", Setup with { Craftsmanship = 4022 }, Objective },
        { "Control", Setup with { Control = 3999 }, Objective },
        { "Cp", Setup with { Cp = 602 }, Objective },
        { "Level", Setup with { Level = 99 }, Objective },
        { "Manipulation", Setup with { Manipulation = false }, Objective },
        { "HeartAndSoul", Setup with { HeartAndSoul = true }, Objective },
        { "QuickInnovation", Setup with { QuickInnovation = true }, Objective },
        { "TargetQuality", Setup, Objective with { TargetQuality = 10000 } },
        { "InitialQuality", Setup, Objective with { InitialQuality = 0 } },
        { "Adversarial", Setup, Objective with { Adversarial = true } },
        { "BackloadProgress", Setup, Objective with { BackloadProgress = true } },
        { "ExcludeFirstStepActions", Setup, Objective with { ExcludeFirstStepActions = true } },
        { "ExcludePrudent", Setup, Objective with { ExcludePrudent = true } },
    };

    [Fact]
    public void EqualInputsHit()
    {
        var cache = new SolutionCache(Version);
        cache.Add(Setup, Objective, Rotation);

        // A fresh but equal pair of records is the same key.
        Assert.True(cache.TryGet(Setup with { }, Objective with { }, out var hit));
        Assert.Equal(Rotation.ActionIds, hit.ActionIds);
        Assert.Equal(250, hit.BaseProgress);
        Assert.Equal(300, hit.BaseQuality);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void AnyFieldChangeMisses(string field, CraftSetup setup, CraftObjective objective)
    {
        var cache = new SolutionCache(Version);
        cache.Add(Setup, Objective, Rotation);

        Assert.False(cache.TryGet(setup, objective, out _), field);
        Assert.NotEqual(new SolutionCacheKey(Setup, Objective), new SolutionCacheKey(setup, objective));
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void FailedSolutionsAreNotStored()
    {
        var cache = new SolutionCache(Version);
        cache.Add(Setup, Objective, CraftSolution.Failed("no solution"));

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(Setup, Objective, out _));
    }

    [Fact]
    public void EvictsTheLeastRecentlyUsedEntry()
    {
        var cache = new SolutionCache(Version, capacity: 2);
        var a = Setup with { Cp = 1 };
        var b = Setup with { Cp = 2 };
        var c = Setup with { Cp = 3 };

        cache.Add(a, Objective, Rotation);
        cache.Add(b, Objective, Rotation);
        Assert.True(cache.TryGet(a, Objective, out _)); // a is now the most recent
        cache.Add(c, Objective, Rotation);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(a, Objective, out _));
        Assert.False(cache.TryGet(b, Objective, out _));
        Assert.True(cache.TryGet(c, Objective, out _));
    }

    [Fact]
    public void ReAddingAnEntryReplacesItWithoutGrowing()
    {
        var cache = new SolutionCache(Version, capacity: 2);
        cache.Add(Setup, Objective, Rotation);
        cache.Add(Setup, Objective, new CraftSolution([100390u]));

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(Setup, Objective, out var hit));
        Assert.Equal(new[] { 100390u }, hit.ActionIds);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        using var file = new TempFile();
        var cache = new SolutionCache(Version, file.Path, capacity: 3);
        var a = Setup with { Cp = 1 };
        var b = Setup with { Cp = 2 };
        var c = Setup with { Cp = 3 };
        cache.Add(a, Objective, Rotation);
        cache.Add(b, Objective with { Adversarial = true }, new CraftSolution([19004u], BaseProgress: 7, BaseQuality: 9));
        cache.Add(c, Objective, Rotation);
        Assert.True(cache.TryGet(a, Objective, out _)); // order is now a, c, b
        cache.Save();

        var loaded = new SolutionCache(Version, file.Path, capacity: 3);
        Assert.Equal(3, loaded.Load());
        Assert.Equal(3, loaded.Count);
        Assert.True(loaded.TryGet(b, Objective with { Adversarial = true }, out var hit));
        Assert.Equal(new[] { 19004u }, hit.ActionIds);
        Assert.Equal(7, hit.BaseProgress);
        Assert.Equal(9, hit.BaseQuality);
        Assert.False(loaded.TryGet(b, Objective, out _));

        // The LRU order survives the round trip: b was least recent before the
        // TryGet above touched it, so c is the one evicted by the next insert.
        loaded.Add(Setup with { Cp = 4 }, Objective, Rotation);
        Assert.True(loaded.TryGet(a, Objective, out _));
        Assert.False(loaded.TryGet(c, Objective, out _));
    }

    [Fact]
    public void DifferentVersionDiscardsTheFile()
    {
        using var file = new TempFile();
        var cache = new SolutionCache("1.0.0.0", file.Path);
        cache.Add(Setup, Objective, Rotation);
        cache.Save();

        var newer = new SolutionCache("1.1.0.0", file.Path);
        Assert.Equal(0, newer.Load());
        Assert.Equal(0, newer.Count);

        var same = new SolutionCache("1.0.0.0", file.Path);
        Assert.Equal(1, same.Load());
    }

    [Fact]
    public void MissingFileLoadsEmpty()
    {
        var cache = new SolutionCache(Version, System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cielcraft-{Guid.NewGuid():N}.json"));
        Assert.Equal(0, cache.Load());
    }

    [Fact]
    public void MalformedFileThrowsOnLoad()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, "{ not json");

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => new SolutionCache(Version, file.Path).Load());
    }

    [Fact]
    public void ClearThenSaveWritesAnEmptyCache()
    {
        using var file = new TempFile();
        var cache = new SolutionCache(Version, file.Path);
        cache.Add(Setup, Objective, Rotation);
        cache.Save();
        cache.Clear();
        cache.Save();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, new SolutionCache(Version, file.Path).Load());
    }

    [Fact]
    public void DescribeReportsEntriesAndCounters()
    {
        var cache = new SolutionCache(Version);
        cache.Add(Setup, Objective, Rotation);
        cache.TryGet(Setup, Objective, out _);
        cache.TryGet(Setup with { Cp = 1 }, Objective, out _);

        var lines = cache.Describe().ToList();
        Assert.Contains("1 entries", lines[0]);
        Assert.Contains("1 hits, 1 misses", lines[0]);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cielcraft-cache-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
