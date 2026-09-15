using System;
using System.Collections.Generic;
using System.Linq;
using CielCraft.Core;
using CielCraft.Core.Crafting;
using CielCraft.Core.Rotations;
using CielCraft.Game;

namespace CielCraft.Crafting;

/// <summary>Crafter stats seen on a job this session; part of the session cache key with the recipe and target.</summary>
public sealed record CrafterStats(int Craftsmanship, int Control, int Cp, int Level);

/// <summary>
/// The plugin side of "reachable from zero?" (roadmap 7.22): feeds
/// <see cref="HqSeeding.Apply"/> with game data and answers its rotation
/// questions with a detached solve (<see cref="SolverService.SolveDetached"/>)
/// replayed by <see cref="RotationSimulator"/>. Answers are cached for the
/// session per (recipe, stats, initial quality, target). Solves take seconds,
/// so the plan is prepared on the framework thread (<see cref="Prepare"/>,
/// which snapshots every sheet lookup the pass needs) and applied off it
/// (<see cref="Apply"/>); <see cref="ApplyCached"/> re-applies known answers
/// synchronously for previews and replans. The stats a recipe is solved with
/// are the ones last seen on its job this session (<see cref="RememberStats"/>,
/// fed by the order runner's tick); until the character has been on the job
/// once, that recipe is skipped with a log line.
/// </summary>
public sealed class HqIntermediatePlanner
{
    private static readonly TimeSpan SolveTimeout = TimeSpan.FromSeconds(90);

    // Recipes whose ingredients the plan in flight seeds HQ: their batches
    // fill HQ materials even when PreferHqMaterials is off, so the seed is
    // actually consumed (7.22). Session state shared with the batch crafter.
    private static readonly object ConsumersGate = new();
    private static readonly HashSet<uint> SeededConsumers = [];

    private readonly SolverService solver;
    private readonly DalamudRecipeProvider recipes;
    private readonly AutomationSettings settings;
    private readonly ILog log;
    private readonly object gate = new();
    private readonly Dictionary<uint, CrafterStats> statsByJob = new();
    private readonly Dictionary<(uint RecipeId, CrafterStats Stats, int Initial, int Target), int?> answers = new();
    private int solves;
    private string lastOutcome = "none yet";

    public HqIntermediatePlanner(SolverService solver, DalamudRecipeProvider recipes, AutomationSettings settings, ILog log)
    {
        this.solver = solver;
        this.recipes = recipes;
        this.settings = settings;
        this.log = log;
        Current = this;
    }

    /// <summary>
    /// The planner the order runner built, for code that re-resolves a plan
    /// in flight without a reference to it (the production runner's replan /
    /// saved-resume): <c>HqIntermediatePlanner.Current?.ApplyCached(plan, inFlight: true) ?? plan</c>.
    /// </summary>
    public static HqIntermediatePlanner? Current { get; private set; }

    /// <summary>The pass runs only with the setting on and a solver to ask.</summary>
    public bool Enabled => settings.HqIntermediates && CielCraft.Raphael.RaphaelSolver.IsAvailable;

    /// <summary>Whether the plan in flight seeds HQ materials into this recipe (its batch then fills HQ).</summary>
    public static bool ConsumesSeededHq(uint recipeId)
    {
        lock (ConsumersGate)
            return SeededConsumers.Contains(recipeId);
    }

    /// <summary>Record the current job's crafter stats (crafter jobs only); call once per frame from the framework thread.</summary>
    public void RememberStats(PlayerSnapshot? player)
    {
        if (player == null || player.ClassJobId is < 8 or > 15 || player.Craftsmanship == 0)
            return;

        var stats = new CrafterStats((int)player.Craftsmanship, (int)player.Control, (int)player.MaxCp, player.Level);
        lock (gate)
            statsByJob[player.ClassJobId] = stats;
    }

    /// <summary>Everything the off-thread pass reads, looked up on the framework thread.</summary>
    public sealed class Snapshot
    {
        internal ProductionPlan Plan = null!;
        internal readonly Dictionary<uint, RecipeInfo> Recipes = new();
        internal readonly Dictionary<uint, string> Names = new();
        internal readonly Dictionary<uint, RecipeSheet.RecipeParameters> Parameters = new();
        internal readonly Dictionary<uint, CrafterStats> Stats = new();
        internal readonly Dictionary<uint, int?> TargetQualities = new();   // by recipe id, target steps only
    }

    /// <summary>
    /// Snapshots the plan's recipes, names, recipe parameters, target
    /// qualities and per-job stats. Null when the pass has nothing to do
    /// (disabled, no craft steps, or no step wants quality).
    /// </summary>
    public Snapshot? Prepare(ProductionPlan plan, bool quiet = false)
    {
        if (!Enabled || plan.CraftSteps.Count == 0)
            return null;

        var snapshot = new Snapshot { Plan = Strip(plan) };
        var wantsQuality = false;
        var missingStats = new HashSet<uint>();
        foreach (var step in snapshot.Plan.CraftSteps)
        {
            var recipe = recipes.GetRecipeById(step.RecipeId);
            if (recipe == null)
                continue;

            snapshot.Recipes[step.RecipeId] = recipe;
            snapshot.Names[step.ItemId] = recipes.GetItemName(step.ItemId);
            foreach (var (ingredientId, _) in recipe.Ingredients)
                snapshot.Names.TryAdd(ingredientId, recipes.GetItemName(ingredientId));

            if (RecipeSheet.Read(step.RecipeId) is { } parameters)
                snapshot.Parameters[step.RecipeId] = parameters;

            var isTarget = plan.Targets.Any(t => !t.MaterialsOnly && t.Kind == OrderKind.Craft && t.ItemId == step.ItemId);
            if (isTarget)
            {
                var target = TargetQualityFor(step, recipe, snapshot.Parameters.GetValueOrDefault(step.RecipeId));
                snapshot.TargetQualities[step.RecipeId] = target;
                wantsQuality |= target > 0;
            }

            lock (gate)
            {
                if (statsByJob.TryGetValue(recipe.ClassJobId, out var stats))
                    snapshot.Stats[recipe.ClassJobId] = stats;
                else
                    missingStats.Add(recipe.ClassJobId);
            }
        }

        if (!wantsQuality)
            return null;

        foreach (var job in quiet ? [] : missingStats)
        {
            log.Information(
                $"[Production] HQ intermediates: no crafter stats seen for {recipes.GetJobAbbreviation(job)} this session; " +
                "recipes of that job are not checked until the character has been on the job once.");
        }

        return snapshot;
    }

    /// <summary>The pass with real solves; run it off the framework thread (Task.Run) and hand the result to the runner.</summary>
    public ProductionPlan Apply(Snapshot snapshot) => Run(snapshot, cacheOnly: false, inFlight: true);

    /// <summary>
    /// Re-applies cached answers only (no solve, no log) on the framework
    /// thread: previews, and — with <paramref name="inFlight"/> — a replanned
    /// or resumed plan the runner is about to execute (its consumers then
    /// replace the ones remembered for the batch's HQ fill).
    /// </summary>
    public ProductionPlan ApplyCached(ProductionPlan plan, bool inFlight = false)
    {
        var snapshot = Prepare(plan, quiet: true);
        return snapshot == null ? plan : Run(snapshot, cacheOnly: true, inFlight);
    }

    private ProductionPlan Run(Snapshot snapshot, bool cacheOnly, bool inFlight)
    {
        var provider = new SnapshotProvider(snapshot);
        var passLog = cacheOnly ? new ListLog() : log;
        var result = HqSeeding.Apply(
            snapshot.Plan,
            provider,
            (step, isTarget) => RequestFor(snapshot, step, isTarget),
            (recipeId, initial, target) => Reachable(snapshot, recipeId, initial, target, cacheOnly),
            passLog);

        if (inFlight)
            RememberConsumers(result, snapshot);
        var seeded = result.CraftSteps.Sum(s => s.HqCrafts);
        lock (gate)
            lastOutcome = $"{(cacheOnly ? "cached" : "solved")} pass: {seeded} HQ intermediate craft(s) over {result.CraftSteps.Count} step(s)";
        if (!cacheOnly)
            log.Information($"[Production] HQ intermediates: {(seeded > 0 ? $"{seeded} intermediate craft(s) will be synthesized to HQ" : "no HQ intermediates needed")}.");
        return result;
    }

    private static HqSeedRequest? RequestFor(Snapshot snapshot, PlannedCraft step, bool isTarget)
    {
        if (!snapshot.Parameters.TryGetValue(step.RecipeId, out var parameters)
            || !snapshot.Recipes.TryGetValue(step.RecipeId, out var recipe)
            || !snapshot.Stats.ContainsKey(recipe.ClassJobId))
        {
            return null;
        }

        // A seeded intermediate must land HQ: 100%.
        if (!isTarget)
            return new HqSeedRequest(parameters.MaxQuality, parameters.MaxQuality);

        var target = snapshot.TargetQualities.GetValueOrDefault(step.RecipeId);
        return target is > 0 ? new HqSeedRequest(parameters.MaxQuality, target.Value) : null;
    }

    /// <summary>The quality a target step's batch will solve for, the way the runner and batch derive it; 0 when the mode does not care.</summary>
    private int TargetQualityFor(PlannedCraft step, RecipeInfo recipe, RecipeSheet.RecipeParameters? parameters)
    {
        if (parameters == null || parameters.MaxQuality == 0)
            return 0;

        var max = (int)parameters.MaxQuality;
        var percent = Math.Clamp(settings.TargetQualityPercent, 1, 100);
        var wanted = step.Mode switch
        {
            ProductionMode.QuickSynth => 0,
            ProductionMode.ForceHq => max,
            ProductionMode.Collectable => recipes.GetCollectableTargetQuality(step.ItemId, step.CollectableTier) ?? max * percent / 100,
            _ => max * percent / 100,
        };
        if (wanted <= 0)
            return 0;

        return Math.Min(max, Math.Max(wanted, (int)recipe.RequiredQuality));
    }

    /// <summary>The oracle: solve (or recall) the rotation for the recipe at an initial quality and replay it; null when unknown.</summary>
    private int? Reachable(Snapshot snapshot, uint recipeId, int initial, int target, bool cacheOnly)
    {
        if (!snapshot.Parameters.TryGetValue(recipeId, out var parameters) || !snapshot.Recipes.TryGetValue(recipeId, out var recipe)
            || !snapshot.Stats.TryGetValue(recipe.ClassJobId, out var stats))
        {
            return null;
        }

        var key = (recipeId, stats, initial, target);
        lock (gate)
        {
            if (answers.TryGetValue(key, out var known))
                return known;
        }

        if (cacheOnly)
            return null;

        // Same setup the batch builds at synthesis start, minus the specialist
        // one-shots it reads from action readiness: a little conservative.
        var setup = RecipeSheet.SetupFor(
            parameters, stats.Craftsmanship, stats.Control, stats.Cp, stats.Level,
            manipulation: stats.Level >= 65, heartAndSoul: false, quickInnovation: false);
        var objective = new CraftObjective(
            TargetQuality: (ushort)Math.Clamp(target, 0, setup.MaxQuality),
            InitialQuality: (ushort)Math.Clamp(initial, 0, setup.MaxQuality));

        var solution = solver.SolveDetached(setup, objective, SolveTimeout);
        int? answer = null;
        if (solution.Success)
        {
            var simulation = RotationSimulator.Run(setup, solution.BaseProgress, solution.BaseQuality, solution.ActionIds, initial);
            answer = simulation.Quality;
        }

        lock (gate)
        {
            solves++;
            answers[key] = answer;
        }

        return answer;
    }

    /// <summary>Which recipes of the plan consume seeded HQ: every step whose recipe uses an item some earlier step makes HQ.</summary>
    private static void RememberConsumers(ProductionPlan plan, Snapshot snapshot)
    {
        var seededItems = plan.CraftSteps.Where(s => s.HqCrafts > 0).Select(s => s.ItemId).ToHashSet();
        var consumers = new HashSet<uint>();
        foreach (var step in plan.CraftSteps)
        {
            if (snapshot.Recipes.TryGetValue(step.RecipeId, out var recipe) && recipe.Ingredients.Any(i => seededItems.Contains(i.ItemId)))
                consumers.Add(step.RecipeId);
        }

        lock (ConsumersGate)
        {
            SeededConsumers.Clear();
            SeededConsumers.UnionWith(consumers);
        }
    }

    /// <summary>A plan with every HqCrafts cleared, so a pass never stacks on an earlier one.</summary>
    private static ProductionPlan Strip(ProductionPlan plan) =>
        plan.CraftSteps.All(s => s.HqCrafts == 0)
            ? plan
            : plan with { CraftSteps = plan.CraftSteps.Select(s => s.HqCrafts == 0 ? s : s with { HqCrafts = 0 }).ToList() };

    /// <summary>Internal state for the diagnostic report.</summary>
    public IEnumerable<string> Describe()
    {
        var lines = new List<string>();
        lock (gate)
        {
            lines.Add($"HQ intermediates {(settings.HqIntermediates ? "on" : "off")}; solver {(CielCraft.Raphael.RaphaelSolver.IsAvailable ? "available" : "missing")}; last {lastOutcome}; {solves} solve(s), {answers.Count} cached answer(s)");
            lines.Add("Stats by job: " + (statsByJob.Count == 0
                ? "none seen"
                : string.Join("; ", statsByJob.Select(pair => $"{recipes.GetJobAbbreviation(pair.Key)} {pair.Value.Craftsmanship}/{pair.Value.Control}/{pair.Value.Cp} @ Lv{pair.Value.Level}"))));
        }

        lines.Add("Seeded consumers: " + string.Join(", ", ConsumersSnapshot().Select(id => $"recipe {id}")));
        return lines;
    }

    private static uint[] ConsumersSnapshot()
    {
        lock (ConsumersGate)
            return SeededConsumers.ToArray();
    }

    /// <summary>The pass's recipe lookup over the snapshot: never touches game sheets off the framework thread.</summary>
    private sealed class SnapshotProvider(Snapshot snapshot) : IRecipeProvider
    {
        public RecipeInfo? FindRecipeForItem(uint itemId) => snapshot.Recipes.Values.FirstOrDefault(r => r.ResultItemId == itemId);

        public RecipeInfo? GetRecipeById(uint recipeId) => snapshot.Recipes.GetValueOrDefault(recipeId);

        public string GetItemName(uint itemId) => snapshot.Names.TryGetValue(itemId, out var name) ? name : $"Item {itemId}";
    }
}
