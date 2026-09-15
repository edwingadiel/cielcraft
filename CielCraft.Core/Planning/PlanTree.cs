using System.Text;

namespace CielCraft.Core.Planning;

/// <summary>
/// One node of the production breakdown (roadmap 7.12): a target, a
/// sub-craft, or a raw material. Counts are per branch: a step shared by two
/// parents appears twice, each visit carrying the crafts that visit added to
/// the plan step, so a branch's numbers add up to the plan's.
/// </summary>
public sealed class PlanNode
{
    public uint ItemId { get; init; }

    public bool IsTarget { get; init; }

    /// <summary>A materials-only target: its ingredients are planned, its own craft is not.</summary>
    public bool MaterialsOnly { get; init; }

    /// <summary>Index into <see cref="ProductionPlan.CraftSteps"/>; -1 for raw materials and materials-only targets.</summary>
    public int StepIndex { get; init; } = -1;

    public bool IsCraft => StepIndex >= 0 || MaterialsOnly;

    public uint RecipeId { get; init; }

    /// <summary>Crafting job of a craft node; gathering job of a raw node when its zone is known; 0 otherwise.</summary>
    public uint JobId { get; init; }

    /// <summary>Crafts this branch contributes (for a materials-only target: the crafts whose ingredients are planned).</summary>
    public int Crafts { get; init; }

    public int Yield { get; init; }

    public int Produced => Crafts * Yield;

    /// <summary>What the parent (or the order) asks of this node.</summary>
    public int Need { get; init; }

    /// <summary>Owned count at build time, before any stock was taken by earlier branches.</summary>
    public int Owned { get; init; }

    /// <summary>Taken from the bag by this branch (the shared stock is consumed once across the tree).</summary>
    public int FromStock { get; init; }

    /// <summary>The HQ part of <see cref="FromStock"/>, assuming HQ stock is used first.</summary>
    public int HqFromStock { get; init; }

    /// <summary>Raw material to gather or buy in this branch; 0 for craft nodes.</summary>
    public int Missing { get; init; }

    /// <summary>Gathering zone of a raw node; 0 when unknown.</summary>
    public uint TerritoryId { get; init; }

    /// <summary>
    /// How a registered material source would supply this raw material
    /// ("buy from Engerrand (Limsa Lominsa Lower Decks), 15 gil each",
    /// roadmap 7.3b); null when none would, or for craft nodes.
    /// </summary>
    public string? SourceLabel { get; init; }

    public bool Timed { get; init; }

    public ProductionMode Mode { get; init; }

    /// <summary>The plan step's total crafts; larger than <see cref="Crafts"/> when the step is shared with another branch.</summary>
    public int StepCrafts { get; init; }

    public bool SharedStep => StepIndex >= 0 && StepCrafts != Crafts;

    /// <summary>The plan step's crafts synthesized normally to HQ (roadmap 7.22); 0 when all are quick.</summary>
    public int HqCrafts { get; init; }

    public IReadOnlyList<PlanNode> Children { get; init; } = [];
}

/// <summary>Raw materials grouped by the zone that supplies them (which teleports a run makes).</summary>
public sealed record ZoneRollup(uint TerritoryId, IReadOnlyList<ZoneItem> Items)
{
    public int TotalAmount => Items.Sum(i => i.Amount);
}

/// <summary>
/// One raw material in a zone roll-up. <paramref name="SourceLabel"/> is set
/// when a material source (vendor, exchange, …) would supply it instead of a
/// node (roadmap 7.3b), in which case the zone is 0 and no trip is planned.
/// </summary>
public sealed record ZoneItem(uint ItemId, int Amount, uint JobId, bool Timed, string? SourceLabel = null);

/// <summary>Craft steps grouped by job (how many job switches, how much work each).</summary>
public sealed record JobRollup(uint JobId, int Steps, int Crafts);

/// <summary>HQ stock a craft will consume: the material, how much, and the recipe that eats it.</summary>
public sealed record HqUse(uint ItemId, int Amount, uint ConsumerItemId);

/// <summary>
/// Live position of a run over the plan: steps before <see cref="CompletedSteps"/>
/// are done, that step is in flight with its batch progress, raw materials are
/// gathered once the runner is past its gathering phase.
/// </summary>
public sealed record PlanProgress(int CompletedSteps, int BatchDone, int BatchTarget, bool Gathering, bool GatheringDone);

/// <summary>
/// The resolved graph of a <see cref="ProductionPlan"/> as a tree (roadmap
/// 7.12): targets → sub-crafts → raw materials, each node with crafts × yield,
/// job and need / owned / missing, plus roll-ups per gathering zone, per job
/// and of HQ materials consumed. The walk replays the resolver's allocation
/// (stock consumed once, target stock reserved, yield surplus feeding later
/// branches) but follows the plan's own decisions about what is a craft step
/// and what is raw, so a locked-book intermediate (7.16) stays a raw leaf.
/// Built from the inventory of the moment; the plan's totals are the
/// authority for the roll-ups.
/// </summary>
public sealed class PlanTree
{
    private const int MaxDepth = 10;

    private PlanTree(
        ProductionPlan plan,
        IReadOnlyList<PlanNode> targets,
        IReadOnlyList<ZoneRollup> zones,
        IReadOnlyList<JobRollup> jobs,
        IReadOnlyList<HqUse> hqMaterials)
    {
        Plan = plan;
        Targets = targets;
        Zones = zones;
        Jobs = jobs;
        HqMaterials = hqMaterials;
    }

    public ProductionPlan Plan { get; }

    public IReadOnlyList<PlanNode> Targets { get; }

    public IReadOnlyList<ZoneRollup> Zones { get; }

    public IReadOnlyList<JobRollup> Jobs { get; }

    public IReadOnlyList<HqUse> HqMaterials { get; }

    public int TotalCrafts => Plan.CraftSteps.Sum(s => s.Crafts);

    /// <summary>Intermediate crafts the plan synthesizes normally to HQ (roadmap 7.22).</summary>
    public int TotalHqCrafts => Plan.CraftSteps.Sum(s => s.HqCrafts);

    /// <summary>
    /// Builds the tree. <paramref name="sourceLabel"/> (roadmap 7.3b) is
    /// asked about every raw material still missing: when a registered
    /// material source would supply it, its answer is shown instead of the
    /// gathering zone.
    /// </summary>
    public static PlanTree Build(
        ProductionPlan plan,
        IRecipeProvider recipes,
        Func<uint, int> ownedOf,
        Func<uint, int> hqOwnedOf,
        Func<uint, GatheringLocation?> zones,
        Func<uint, string?>? sourceLabel = null)
    {
        var walk = new Walk(plan, recipes, ownedOf, hqOwnedOf, zones, sourceLabel);
        var targets = walk.Run();
        return new PlanTree(plan, targets, ZoneRollups(plan, zones, sourceLabel), JobRollups(plan, recipes), walk.HqUses());
    }

    /// <summary>Every node in tree order (pre-order), for callers that want a flat pass.</summary>
    public IEnumerable<PlanNode> AllNodes()
    {
        var stack = new Stack<PlanNode>();
        for (var i = Targets.Count - 1; i >= 0; i--)
            stack.Push(Targets[i]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }

    /// <summary>Progress mark for a node: "✓" done, "▶" in flight, "·" pending; "" when no run is in progress.</summary>
    public static string Mark(PlanNode node, PlanProgress? progress)
    {
        if (progress == null)
            return "";

        if (node.StepIndex >= 0)
        {
            if (node.StepIndex < progress.CompletedSteps)
                return "✓";
            return node.StepIndex == progress.CompletedSteps && progress.GatheringDone ? "▶" : "·";
        }

        if (node.MaterialsOnly)
        {
            // No craft of its own: done when everything under it is.
            var marks = node.Children.Select(child => Mark(child, progress)).ToList();
            return marks.Count > 0 && marks.All(m => m == "✓") ? "✓" : marks.Contains("▶") ? "▶" : "·";
        }

        // Raw leaf: done once the runner is past its gathering phase.
        if (node.Missing == 0)
            return "✓";
        return progress.GatheringDone ? "✓" : progress.Gathering ? "▶" : "·";
    }

    /// <summary>
    /// The tree as indented text lines for "/cielcraft plan" and the report.
    /// Names come from the recipe provider; job abbreviations and zone names
    /// are game data the plugin supplies.
    /// </summary>
    public IEnumerable<string> RenderLines(
        IRecipeProvider recipes,
        Func<uint, string> jobName,
        Func<uint, string> zoneName,
        PlanProgress? progress = null)
    {
        yield return $"Plan: {Plan.Targets.Count} target(s); {Plan.CraftSteps.Count} craft step(s), {TotalCrafts} craft(s)"
                     + (TotalHqCrafts > 0 ? $" ({TotalHqCrafts} HQ intermediate craft(s))" : "")
                     + $"; {Plan.RawMaterials.Count} raw material(s)"
                     + (progress == null ? "" : $"; step {Math.Min(progress.CompletedSteps + 1, Math.Max(Plan.CraftSteps.Count, 1))}/{Plan.CraftSteps.Count} in flight");

        foreach (var target in Targets)
        {
            foreach (var line in RenderNode(target, 0, recipes, jobName, zoneName, progress))
                yield return line;
        }

        if (Zones.Count > 0)
        {
            yield return "Gathering by zone:";
            foreach (var zone in Zones)
            {
                var items = string.Join(", ", zone.Items.Select(i =>
                    $"{recipes.GetItemName(i.ItemId)} ×{i.Amount}"
                    + (i.SourceLabel is { Length: > 0 } source ? $" ({source})"
                        : i.JobId != 0 ? $" ({jobName(i.JobId)}{(i.Timed ? ", timed" : "")})"
                        : i.Timed ? " (timed)" : "")));
                yield return $"  {ZoneLabel(zone, zoneName)}: {items}";
            }
        }

        if (Jobs.Count > 0)
        {
            yield return "Crafts by job:";
            foreach (var job in Jobs)
                yield return $"  {jobName(job.JobId)}: {job.Crafts} craft(s) in {job.Steps} step(s)";
        }

        if (HqMaterials.Count > 0)
        {
            yield return "HQ materials consumed:";
            foreach (var use in HqMaterials)
                yield return $"  {recipes.GetItemName(use.ItemId)} ×{use.Amount} HQ → {recipes.GetItemName(use.ConsumerItemId)}";
        }
    }

    public string Render(IRecipeProvider recipes, Func<uint, string> jobName, Func<uint, string> zoneName, PlanProgress? progress = null) =>
        string.Join("\n", RenderLines(recipes, jobName, zoneName, progress));

    /// <summary>
    /// What a zone roll-up is called: the zone's name, "bought" when every
    /// material in it comes from a source instead of a node (roadmap 7.3b),
    /// "unknown zone" otherwise. Shared by the text and the panel.
    /// </summary>
    public static string ZoneLabel(ZoneRollup zone, Func<uint, string> zoneName) =>
        zone.TerritoryId != 0 ? zoneName(zone.TerritoryId)
        : zone.Items.All(i => i.SourceLabel is { Length: > 0 }) ? "bought"
        : "unknown zone";

    /// <summary>The per-node detail after the name: crafts × yield, job, need / owned / missing, mode; shared by the text and the panel.</summary>
    public static string Describe(PlanNode node, Func<uint, string> jobName, Func<uint, string> zoneName)
    {
        var sb = new StringBuilder();
        if (node.MaterialsOnly)
        {
            sb.Append($"materials only for {node.Crafts} craft(s) × {node.Yield}");
        }
        else if (node.StepIndex >= 0 && node.Crafts == 0)
        {
            sb.Append($"{node.FromStock} from stock (crafted elsewhere in the plan)");
        }
        else if (node.StepIndex >= 0)
        {
            sb.Append($"{node.Crafts} craft(s) × {node.Yield} = {node.Produced}");
            if (node.SharedStep)
                sb.Append($" (step total {node.StepCrafts})");
            if (node.JobId != 0)
                sb.Append($" [{jobName(node.JobId)}]");
            if (node.FromStock > 0)
                sb.Append($"; {node.FromStock} from stock");
            // HQ-seeded intermediate (7.22): the step's count, since the HQ
            // crafts are not attributed to one branch.
            if (node.HqCrafts > 0)
                sb.Append($"; {node.HqCrafts} HQ of {node.StepCrafts} crafts");
            var mode = node.Mode switch
            {
                ProductionMode.ForceHq => "; HQ",
                ProductionMode.QuickSynth => "; quick synth",
                ProductionMode.Collectable => "; collectable",
                _ => "",
            };
            sb.Append(mode);
        }
        else
        {
            sb.Append($"need {node.Need}, owned {node.Owned}, missing {node.Missing}");
            if (node.Missing > 0)
            {
                // A registered source (7.3b) speaks for the material instead
                // of the gathering zone: "buy from Engerrand (Limsa …)".
                if (node.SourceLabel is { Length: > 0 } source)
                {
                    sb.Append($" [{source}]");
                }
                else
                {
                    sb.Append(node.JobId != 0 ? $" [{jobName(node.JobId)}]" : " [no known node]");
                    if (node.TerritoryId != 0)
                        sb.Append($" {zoneName(node.TerritoryId)}");
                    if (node.Timed)
                        sb.Append(" (timed)");
                }
            }
        }

        if (node.HqFromStock > 0)
            sb.Append($"; {node.HqFromStock} HQ");
        return sb.ToString();
    }

    private static IEnumerable<string> RenderNode(
        PlanNode node, int depth, IRecipeProvider recipes, Func<uint, string> jobName, Func<uint, string> zoneName, PlanProgress? progress)
    {
        var mark = Mark(node, progress);
        var tick = mark.Length == 0 ? "" : mark + " ";
        if (mark == "▶" && node.StepIndex >= 0 && progress!.BatchTarget > 0)
            tick = $"▶ {progress.BatchDone}/{progress.BatchTarget} ";

        yield return new string(' ', depth * 2) + tick + $"{recipes.GetItemName(node.ItemId)} ×{node.Need}: " + Describe(node, jobName, zoneName);
        foreach (var child in node.Children)
        {
            foreach (var line in RenderNode(child, depth + 1, recipes, jobName, zoneName, progress))
                yield return line;
        }
    }

    private static IReadOnlyList<ZoneRollup> ZoneRollups(
        ProductionPlan plan, Func<uint, GatheringLocation?> zones, Func<uint, string?>? sourceLabel)
    {
        var order = new List<uint>();
        var byZone = new Dictionary<uint, List<ZoneItem>>();
        foreach (var raw in plan.RawMaterials)
        {
            // A material a source supplies (7.3b) is no trip: it lands in the
            // zone-less group with the source's own label.
            var label = sourceLabel?.Invoke(raw.ItemId);
            var location = label == null ? zones(raw.ItemId) : null;
            var territory = location?.TerritoryId ?? 0;
            if (!byZone.TryGetValue(territory, out var items))
            {
                items = [];
                byZone[territory] = items;
                order.Add(territory);
            }

            items.Add(new ZoneItem(raw.ItemId, raw.Amount, location?.JobId ?? 0, location?.IsTimed ?? false, label));
        }

        // Unknown zone last: it is the odd one out, not a teleport.
        return order.OrderBy(t => t == 0 ? 1 : 0).Select(t => new ZoneRollup(t, byZone[t])).ToList();
    }

    private static IReadOnlyList<JobRollup> JobRollups(ProductionPlan plan, IRecipeProvider recipes)
    {
        var order = new List<uint>();
        var byJob = new Dictionary<uint, (int Steps, int Crafts)>();
        foreach (var step in plan.CraftSteps)
        {
            var job = RecipeFor(step, recipes)?.ClassJobId ?? 0;
            if (!byJob.TryGetValue(job, out var totals))
                order.Add(job);
            byJob[job] = (totals.Steps + 1, totals.Crafts + step.Crafts);
        }

        return order.Select(j => new JobRollup(j, byJob[j].Steps, byJob[j].Crafts)).ToList();
    }

    private static RecipeInfo? RecipeFor(PlannedCraft step, IRecipeProvider recipes) =>
        recipes.GetRecipeById(step.RecipeId) ?? recipes.FindRecipeForItem(step.ItemId);

    /// <summary>The resolver's expansion replayed over the plan's decisions, producing nodes instead of merged steps.</summary>
    private sealed class Walk
    {
        private readonly ProductionPlan plan;
        private readonly IRecipeProvider recipes;
        private readonly Func<uint, int> ownedOf;
        private readonly Func<uint, int> hqOwnedOf;
        private readonly Func<uint, GatheringLocation?> zones;
        private readonly Func<uint, string?>? sourceLabel;
        private readonly Dictionary<uint, int> stepIndex = new();
        private readonly Dictionary<uint, int> stock = new();
        private readonly Dictionary<uint, int> hqStock = new();
        private readonly HashSet<uint> reserved = [];
        private readonly HashSet<uint> expanding = [];
        private readonly List<HqUse> hqUses = [];

        public Walk(
            ProductionPlan plan, IRecipeProvider recipes, Func<uint, int> ownedOf, Func<uint, int> hqOwnedOf,
            Func<uint, GatheringLocation?> zones, Func<uint, string?>? sourceLabel)
        {
            this.plan = plan;
            this.recipes = recipes;
            this.ownedOf = ownedOf;
            this.hqOwnedOf = hqOwnedOf;
            this.zones = zones;
            this.sourceLabel = sourceLabel;
            for (var i = 0; i < plan.CraftSteps.Count; i++)
                stepIndex[plan.CraftSteps[i].ItemId] = i;
        }

        public IReadOnlyList<HqUse> HqUses() => hqUses;

        public IReadOnlyList<PlanNode> Run()
        {
            // Same reservation rule as the resolver: ordered items keep their owned stock.
            foreach (var target in plan.Targets)
            {
                if (!target.MaterialsOnly)
                    reserved.Add(target.ItemId);
            }

            var nodes = new List<PlanNode>();
            foreach (var target in plan.Targets)
                nodes.Add(target.MaterialsOnly ? MaterialsOnlyNode(target) : Expand(target.ItemId, target.Quantity, useStock: false, depth: 0, target));
            return nodes;
        }

        private PlanNode MaterialsOnlyNode(PlanTarget target)
        {
            var recipe = recipes.FindRecipeForItem(target.ItemId);
            if (recipe == null || target.Quantity <= 0)
            {
                return new PlanNode
                {
                    ItemId = target.ItemId, IsTarget = true, MaterialsOnly = true, Need = target.Quantity,
                    Owned = Math.Max(0, ownedOf(target.ItemId)), Yield = recipe?.ResultAmount ?? 1, JobId = recipe?.ClassJobId ?? 0,
                };
            }

            var crafts = (target.Quantity + recipe.ResultAmount - 1) / recipe.ResultAmount;
            var children = recipe.Ingredients
                .Select(ingredient => Expand(ingredient.ItemId, crafts * ingredient.Amount, useStock: true, depth: 1, null, target.ItemId))
                .ToList();
            return new PlanNode
            {
                ItemId = target.ItemId, IsTarget = true, MaterialsOnly = true, Need = target.Quantity,
                Owned = Math.Max(0, ownedOf(target.ItemId)), RecipeId = recipe.RecipeId, JobId = recipe.ClassJobId,
                Crafts = crafts, Yield = recipe.ResultAmount, Children = children,
            };
        }

        private PlanNode Expand(uint itemId, int need, bool useStock, int depth, PlanTarget? target, uint consumer = 0)
        {
            var owned = Math.Max(0, ownedOf(itemId));
            var remaining = Math.Max(0, need);
            var fromStock = 0;
            var hqFromStock = 0;
            if (useStock && remaining > 0)
            {
                var available = StockOf(itemId);
                fromStock = Math.Min(available, remaining);
                stock[itemId] = available - fromStock;
                remaining -= fromStock;

                // HQ first: the batch's HQ fill prefers it, so that is what the bag loses.
                var hqAvailable = HqStockOf(itemId);
                hqFromStock = Math.Min(hqAvailable, fromStock);
                hqStock[itemId] = hqAvailable - hqFromStock;
                if (hqFromStock > 0)
                    RecordHq(itemId, hqFromStock, consumer);
            }

            var step = stepIndex.TryGetValue(itemId, out var index) ? plan.CraftSteps[index] : null;
            var mode = target?.Mode ?? step?.Mode ?? ProductionMode.Any;

            if (step == null)
            {
                // Raw in the plan (not craftable, or a locked book made it so).
                // A registered source (7.3b) answers for it before the zone does.
                var label = remaining > 0 ? sourceLabel?.Invoke(itemId) : null;
                var location = remaining > 0 && label == null ? zones(itemId) : null;
                return new PlanNode
                {
                    ItemId = itemId, IsTarget = target != null, Need = need, Owned = owned, FromStock = fromStock, HqFromStock = hqFromStock,
                    Missing = remaining, JobId = location?.JobId ?? 0, TerritoryId = location?.TerritoryId ?? 0,
                    Timed = location?.IsTimed ?? false, Mode = mode, SourceLabel = label,
                };
            }

            if (remaining == 0)
            {
                // Fully stocked here, crafted elsewhere: keep the step so the tick tracks it.
                return new PlanNode
                {
                    ItemId = itemId, IsTarget = target != null, Need = need, Owned = owned, FromStock = fromStock, HqFromStock = hqFromStock,
                    StepIndex = index, RecipeId = step.RecipeId, Yield = step.ResultAmount, StepCrafts = step.Crafts, HqCrafts = step.HqCrafts,
                    JobId = RecipeFor(step, recipes)?.ClassJobId ?? 0, Mode = mode,
                };
            }

            var crafts = (remaining + step.ResultAmount - 1) / step.ResultAmount;
            var recipe = RecipeFor(step, recipes);
            var children = new List<PlanNode>();
            if (recipe != null && depth < MaxDepth && expanding.Add(itemId))
            {
                foreach (var (ingredientId, amount) in recipe.Ingredients)
                    children.Add(Expand(ingredientId, crafts * amount, useStock: true, depth + 1, null, itemId));
                expanding.Remove(itemId);
            }

            // Yield surplus is real stock for later branches, as in the resolver.
            var surplus = crafts * step.ResultAmount - remaining;
            if (surplus > 0)
                stock[itemId] = StockOf(itemId) + surplus;

            return new PlanNode
            {
                ItemId = itemId, IsTarget = target != null, Need = need, Owned = owned, FromStock = fromStock, HqFromStock = hqFromStock,
                StepIndex = index, RecipeId = step.RecipeId, JobId = recipe?.ClassJobId ?? 0,
                Crafts = crafts, Yield = step.ResultAmount, StepCrafts = step.Crafts, HqCrafts = step.HqCrafts, Mode = mode, Children = children,
            };
        }

        private void RecordHq(uint itemId, int amount, uint consumer)
        {
            for (var i = 0; i < hqUses.Count; i++)
            {
                if (hqUses[i].ItemId == itemId && hqUses[i].ConsumerItemId == consumer)
                {
                    hqUses[i] = hqUses[i] with { Amount = hqUses[i].Amount + amount };
                    return;
                }
            }

            hqUses.Add(new HqUse(itemId, amount, consumer));
        }

        private int StockOf(uint itemId)
        {
            if (!stock.TryGetValue(itemId, out var value))
            {
                value = reserved.Contains(itemId) ? 0 : Math.Max(0, ownedOf(itemId));
                stock[itemId] = value;
            }

            return value;
        }

        private int HqStockOf(uint itemId)
        {
            if (!hqStock.TryGetValue(itemId, out var value))
            {
                value = reserved.Contains(itemId) ? 0 : Math.Max(0, hqOwnedOf(itemId));
                hqStock[itemId] = value;
            }

            return value;
        }
    }
}
