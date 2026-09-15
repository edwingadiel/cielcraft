using System.Text.Json;

namespace CielCraft.Core;

/// <summary>
/// Everything a full solve depends on (roadmap 7.7). Records compare every
/// field, so any stat, buff flag or objective change is a different key.
/// </summary>
public sealed record SolutionCacheKey(CraftSetup Setup, CraftObjective Objective);

/// <summary>
/// LRU cache of full-craft rotations keyed on the complete solve input, so a
/// recipe that repeats with the same stats skips the solver (roadmap 7.7).
/// Only successful full solves are stored; mid-craft solves are never cached
/// (their live state is unique). Persists to a JSON file tagged with the
/// plugin version so a newer solver starts from an empty cache.
/// </summary>
public sealed class SolutionCache
{
    public const int DefaultCapacity = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object gate = new();
    private readonly Dictionary<SolutionCacheKey, LinkedListNode<Entry>> entries = new();
    // Most recently used at the front; eviction takes from the back.
    private readonly LinkedList<Entry> order = new();

    public string Version { get; }
    public string? Path { get; }
    public int Capacity { get; }

    public int Count { get { lock (gate) return entries.Count; } }
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public SolutionCache(string version, string? path = null, int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Version = version;
        Path = path;
        Capacity = capacity;
    }

    public bool TryGet(CraftSetup setup, CraftObjective objective, out CraftSolution solution)
    {
        var key = new SolutionCacheKey(setup, objective);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var node))
            {
                order.Remove(node);
                order.AddFirst(node);
                Hits++;
                solution = node.Value.Solution;
                return true;
            }

            Misses++;
            solution = null!;
            return false;
        }
    }

    /// <summary>Stores a successful solution; a failed one is ignored (it may succeed later).</summary>
    public void Add(CraftSetup setup, CraftObjective objective, CraftSolution solution)
    {
        if (!solution.Success)
            return;

        lock (gate)
            Insert(new SolutionCacheKey(setup, objective), solution);
    }

    private void Insert(SolutionCacheKey key, CraftSolution solution)
    {
        if (entries.TryGetValue(key, out var existing))
            order.Remove(existing);

        var node = order.AddFirst(new Entry(key, solution));
        entries[key] = node;

        while (entries.Count > Capacity)
        {
            var last = order.Last!;
            order.RemoveLast();
            entries.Remove(last.Value.Key);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            order.Clear();
        }
    }

    /// <summary>
    /// Replaces the contents with the file at <see cref="Path"/>. A missing
    /// file or a version mismatch leaves the cache empty; malformed JSON
    /// throws so the caller can log it. Returns the number of entries loaded.
    /// </summary>
    public int Load()
    {
        if (Path == null || !File.Exists(Path))
            return 0;

        var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(Path), JsonOptions);
        lock (gate)
        {
            entries.Clear();
            order.Clear();
            if (file?.Entries == null || file.Version != Version)
                return 0;

            // Saved oldest first, so re-inserting in order restores the LRU order.
            foreach (var entry in file.Entries)
            {
                if (entry.Setup == null || entry.Objective == null || entry.ActionIds == null)
                    continue;

                Insert(
                    new SolutionCacheKey(entry.Setup, entry.Objective),
                    new CraftSolution(entry.ActionIds, BaseProgress: entry.BaseProgress, BaseQuality: entry.BaseQuality));
            }

            return entries.Count;
        }
    }

    /// <summary>Writes the contents to <see cref="Path"/>; a no-op without a path.</summary>
    public void Save()
    {
        if (Path == null)
            return;

        CacheFile file;
        lock (gate)
        {
            file = new CacheFile
            {
                Version = Version,
                Entries = order.Reverse().Select(e => new FileEntry
                {
                    Setup = e.Key.Setup,
                    Objective = e.Key.Objective,
                    ActionIds = e.Solution.ActionIds.ToArray(),
                    BaseProgress = e.Solution.BaseProgress,
                    BaseQuality = e.Solution.BaseQuality,
                }).ToList(),
            };
        }

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(Path, JsonSerializer.Serialize(file, JsonOptions));
    }

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        int count, hits, misses;
        lock (gate)
        {
            count = entries.Count;
            hits = Hits;
            misses = Misses;
        }

        yield return $"Solution cache: {count} entries (cap {Capacity}, version {Version}); this session {hits} hits, {misses} misses";
        yield return $"Solution cache file: {Path ?? "none"}";
    }

    private sealed record Entry(SolutionCacheKey Key, CraftSolution Solution);

    private sealed class CacheFile
    {
        public string? Version { get; set; }
        public List<FileEntry>? Entries { get; set; }
    }

    private sealed class FileEntry
    {
        public CraftSetup? Setup { get; set; }
        public CraftObjective? Objective { get; set; }
        public uint[]? ActionIds { get; set; }
        public int BaseProgress { get; set; }
        public int BaseQuality { get; set; }
    }
}
