using System;
using CielCraft.Core;
using CielCraft.Core.Rotations;
using CielCraft.Game;
using System.Collections.Generic;

namespace CielCraft.Crafting;

public enum BatchState
{
    Idle,
    Solving,
    StartingCraft,
    Crafting,
    QuickStarting,
    QuickRunning,
    Paused,
    Completed,
    Failed,
}

/// <summary>
/// The rotation a batch runs (roadmap 7.8): where it came from and the
/// numbers the rotation view needs to replay it. A mid-craft re-solve carries
/// the live state it was solved from so the replay starts there.
/// </summary>
public sealed record ActiveRotation(
    IReadOnlyList<uint> ActionIds,
    string Source,
    CraftSetup Setup,
    int BaseProgress,
    int BaseQuality,
    int TargetQuality,
    int InitialQuality,
    CraftSnapshot? StartState = null,
    CraftLiveEffects? StartEffects = null);

/// <summary>
/// Batch crafting (spec §18/§58): repeat the solved rotation until the target
/// quantity is produced. Every craft is verified against the player inventory
/// — completion is never assumed. Fails safe: anything unexpected pauses or
/// fails with a reason.
/// </summary>
public sealed class BatchCrafter : AutomationMachine<BatchState>
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuickDialogTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Recipes the game refused to quick-synthesize this session (never crafted by this character).</summary>
    private readonly HashSet<uint> quickSynthRefused = [];

    private readonly IGameBridge gameBridge;
    private readonly CraftStateMonitor craftMonitor;
    private readonly CraftAutomator automator;
    private readonly SolverService solverService;
    private readonly Game.DalamudRecipeProvider recipeProvider;
    private readonly AutomationSettings configuration;
    private readonly Game.MaintenanceService maintenance;
    private readonly IActionResolver actionResolver;

    private int targetQuantity;
    private ushort recipeId;
    private uint resultItemId;
    private int resultAmount;
    private int baselineItemCount;
    private bool requireHq;            // ForceHq order (roadmap 7.13): only HQ results count toward the target
    private int qualityOverride;       // collectable tier threshold (roadmap 7.23); 0 = settings
    private int hqBaseline;            // HQ count of the result item when the batch started
    private int verifiedCrafts;        // every craft that landed in the bag, HQ or not
    private CraftSolution? solution;
    private bool solveRequested;
    private bool synthesisFired;
    private bool automatorStarted;
    private bool wasCrafting;
    private DateTime waitStartedAt;
    private bool quickMode;
    private bool quickDialogRequested;
    private DateTime quickLastProgressAt;
    private CraftSetup? solvedSetup;
    private int solveTargetQuality;
    private CraftSolution? midSolution;
    private bool midSolveTried;
    private bool midSolve;
    private DateTime lastRecipeOpenAttempt = DateTime.MinValue;
    private DateTime verifyUntil = DateTime.MinValue; // pending inventory verification of a finished craft
    private DateTime lastSynthesisPress = DateTime.MinValue;
    private DateTime lastFillAttempt = DateTime.MinValue;
    private bool fillPressedForThisPress; // the fill button went down this craft; Synthesize follows on the next frame
    private DateTime craftStartedAt = DateTime.MinValue;   // pacing: first action waits for the start animation
    private DateTime lastCraftEndedAt = DateTime.MinValue; // pacing: next Synthesize waits for the end animation
    private static readonly TimeSpan SynthesisRetryInterval = TimeSpan.FromSeconds(3);
    private int solveInitialQuality;       // live quality when the full solve was requested (HQ materials)
    private CraftSnapshot? midSolveState;  // what the mid-craft re-solve started from, for the rotation view
    private CraftLiveEffects? midSolveEffects;
    private bool assistCandidate;          // assist mode (roadmap 7.18): a hand-started craft not yet adopted
    private bool assisted;                 // this batch was attached by assist mode
    private int hqFirst;                   // HQ intermediates (roadmap 7.22): crafts to synthesize normally to HQ before the rest
    private int hqMade;                    // HQ results landed so far in that phase
    private bool quickAfterHq;             // the rest of the batch quick-synthesizes once the HQ phase is over

    public int CompletedCrafts { get; private set; }
    public int TargetQuantity => targetQuantity;

    /// <summary>Recipe of the batch; 0 for a batch attached to a craft whose recipe could not be read.</summary>
    public ushort RecipeId => recipeId;

    /// <summary>The rotation in use (solved, cached, manual or re-solved mid-craft); null until one exists (roadmap 7.8).</summary>
    public ActiveRotation? Rotation { get; private set; }

    public BatchCrafter(
        IGameBridge gameBridge,
        CraftStateMonitor craftMonitor,
        CraftAutomator automator,
        SolverService solverService,
        Game.DalamudRecipeProvider recipeProvider,
        AutomationSettings configuration,
        Game.MaintenanceService maintenance,
        IActionResolver actionResolver,
        ILog log,
        IClock clock)
        : base(log, clock, "[Production]", BatchState.Idle, "Idle.")
    {
        this.configuration = configuration;
        this.maintenance = maintenance;
        this.gameBridge = gameBridge;
        this.craftMonitor = craftMonitor;
        this.automator = automator;
        this.solverService = solverService;
        this.recipeProvider = recipeProvider;
        this.actionResolver = actionResolver;
    }

    /// <summary>
    /// Starts a batch. Requires either the crafting log open with a recipe
    /// selected, or an active craft still on its first step (which then counts
    /// as craft #1 of the batch). With <paramref name="requireHq"/> (roadmap
    /// 7.13 ForceHq) the rotation is solved for full quality with HQ materials
    /// and the batch runs until the HQ count has risen by the quantity; NQ
    /// results are logged but do not count, and quick synthesis is never used.
    /// With <paramref name="hqFirst"/> (roadmap 7.22 HQ intermediates) the
    /// first crafts are normal syntheses solved for 100% quality until that
    /// many HQ results have landed (an NQ result still counts as a craft of
    /// the step, so the batch never spends more materials than the plan
    /// allotted); the rest run as today, quick when asked.
    /// </summary>
    public bool Start(int quantity, bool quickSynth = false, bool requireHq = false, int targetQuality = 0, int hqFirst = 0)
    {
        if (State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting
            or BatchState.QuickStarting or BatchState.QuickRunning or BatchState.Paused)
            return false;

        if (quantity < 1)
            return false;

        // A craft already in progress is adopted as craft #1: on step 1 it is
        // solved like a fresh craft, later it is solved from the live state
        // (the way a paused batch resumes), so a run interrupted by a plugin
        // reload can be picked up without quitting the synthesis.
        var attached = gameBridge.IsCrafting && craftMonitor.Current != null;
        if (!attached && !gameBridge.IsReadyToStartCraft)
        {
            Transition(BatchState.Idle, "Cannot start: open the crafting log on a recipe, or be in a craft.");
            return false;
        }

        // Inventory awareness (spec §60): refuse quantities the materials
        // cannot cover instead of failing mid-batch.
        if (!attached)
        {
            var requirements = gameBridge.GetRecipeRequirements(gameBridge.SelectedRecipeId);
            var craftable = InventoryMath.CraftableCount(requirements);
            if (requirements.Count > 0 && craftable < quantity)
            {
                Transition(
                    BatchState.Idle,
                    $"Cannot start: materials cover only {craftable} of {quantity} crafts.");
                return false;
            }
        }

        if (gameBridge.GetFreeInventorySlots() < 1)
        {
            Transition(BatchState.Idle, "Cannot start: inventory is full.");
            return false;
        }

        targetQuantity = quantity;
        CompletedCrafts = 0;
        verifiedCrafts = 0;
        recipeId = gameBridge.SelectedRecipeId;
        resultItemId = 0;
        resultAmount = 0;
        baselineItemCount = 0;
        this.requireHq = requireHq;
        qualityOverride = targetQuality;
        hqBaseline = 0;
        // An HQ batch (7.13) already wants every craft HQ; the phase is for
        // intermediates only.
        this.hqFirst = requireHq ? 0 : Math.Clamp(hqFirst, 0, quantity);
        hqMade = 0;
        quickAfterHq = quickSynth && this.hqFirst > 0;
        solution = null;
        Rotation = null;
        assisted = false;
        solveRequested = false;
        solvedSetup = null;
        solveTargetQuality = 0;
        midSolution = null;
        midSolveTried = false;
        midSolve = attached && craftMonitor.Current is { Step: > 1 };
        synthesisFired = false;
        automatorStarted = false;
        wasCrafting = gameBridge.IsCrafting;
        waitStartedAt = Clock.UtcNow;
        verifyUntil = DateTime.MinValue;

        // Quick synthesis only ever yields NQ, so an HQ batch never uses it,
        // and a batch with HQ crafts first switches to it after them.
        quickMode = quickSynth && !requireHq && this.hqFirst == 0 && !gameBridge.IsCrafting && gameBridge.IsQuickSynthAvailable
                    && !quickSynthRefused.Contains(gameBridge.SelectedRecipeId);
        if (quickMode)
        {
            var recipe = recipeProvider.GetRecipeById(gameBridge.SelectedRecipeId);
            if (recipe == null)
            {
                Transition(BatchState.Idle, "Cannot quick synth: recipe could not be read.");
                return false;
            }

            resultItemId = recipe.ResultItemId;
            resultAmount = recipe.ResultAmount;
            baselineItemCount = gameBridge.GetItemCount(resultItemId);
            quickDialogRequested = false;
            waitStartedAt = Clock.UtcNow;
            Transition(BatchState.QuickStarting, $"Quick synthesis batch of {quantity} started.");
            return true;
        }

        Transition(
            gameBridge.IsCrafting ? BatchState.Solving : BatchState.StartingCraft,
            requireHq ? $"HQ batch of {quantity} started."
            : this.hqFirst > 0 ? $"Batch of {quantity} started, {this.hqFirst} HQ first{(quickAfterHq ? ", then quick synthesis" : "")}."
            : $"Batch of {quantity} started.");
        return true;
    }

    /// <summary>The HQ-first phase (7.22) is on until enough HQ results landed (or the batch is out of crafts).</summary>
    private bool InHqPhase => !requireHq && hqFirst > 0 && hqMade < hqFirst;

    /// <summary>
    /// Whether to press the crafting log's HQ fill: the setting, an HQ order,
    /// or a recipe the plan seeded HQ intermediates into (7.22) — that seed
    /// must be consumed even with the setting off. HQ-first crafts of an
    /// intermediate get no HQ fill of their own unless one of those holds, so
    /// the bag's other HQ stock is not spent on them.
    /// </summary>
    private bool PreferHqFill => configuration.PreferHqMaterials || requireHq || HqIntermediatePlanner.ConsumesSeededHq(recipeId);

    public void Pause(string reason)
    {
        if (State is BatchState.QuickStarting or BatchState.QuickRunning)
        {
            gameBridge.CancelQuickSynthesis();
            Transition(BatchState.Paused, $"Paused: {reason}.");
            return;
        }

        if (State is BatchState.Solving or BatchState.StartingCraft or BatchState.Crafting)
        {
            if (automator.State == AutomationState.Running)
                automator.Pause("batch paused");

            Transition(BatchState.Paused, $"Paused: {reason}.");
        }
    }

    public void Resume()
    {
        if (State != BatchState.Paused)
            return;

        waitStartedAt = Clock.UtcNow;
        if (quickMode && !gameBridge.IsCrafting)
        {
            quickDialogRequested = false;
            Transition(BatchState.QuickStarting, ProgressText());
        }
        else if (gameBridge.IsCrafting)
        {
            if (automator.State == AutomationState.Paused && automator.Resume())
            {
                // The automator holds a consistent position in its rotation.
                Transition(BatchState.Crafting, ProgressText());
                return;
            }

            // No resumable automation. Mid-craft the only sound continuation
            // is a rotation solved from the live state; a fresh craft (or a
            // pause taken during Solving) just needs the solve (re)run. An
            // automator that refused to resume (the craft moved while paused)
            // also invalidates any earlier mid-craft solution.
            if (automator.State == AutomationState.Failed)
                midSolution = null;

            var craft = craftMonitor.Current;
            if (solution == null || (craft is { Step: > 1 } && midSolution == null))
            {
                midSolve = craft is { Step: > 1 };
                solveRequested = false;
                automatorStarted = false;
                Transition(BatchState.Solving, "Re-solving after resume...");
                return;
            }

            automatorStarted = false;
            Transition(BatchState.Crafting, ProgressText());
        }
        else
        {
            synthesisFired = false;
            Transition(BatchState.StartingCraft, ProgressText());
        }
    }

    public void Stop()
    {
        automator.Stop();
        maintenance.Abort();
        verifyUntil = DateTime.MinValue;
        if (State is BatchState.QuickStarting or BatchState.QuickRunning)
            gameBridge.CancelQuickSynthesis();
        if (State is not (BatchState.Idle or BatchState.Completed or BatchState.Failed))
            Transition(BatchState.Idle, $"Stopped by user after {CompletedCrafts}/{targetQuantity} crafts.");
    }

    protected override void OnTick()
    {
        var isCrafting = gameBridge.IsCrafting;
        var craftJustEnded = wasCrafting && !isCrafting;
        var craftJustStarted = isCrafting && !wasCrafting;
        wasCrafting = isCrafting;

        // A finished craft whose inventory update has not landed yet.
        if (verifyUntil != DateTime.MinValue)
        {
            TryVerifyCraft();
            return;
        }

        if (TickAssist(isCrafting, craftJustStarted))
            return;

        // A craft can end while the batch is paused (in-flight last action);
        // its verification must not be lost or the count drifts by one.
        if (craftJustEnded && State == BatchState.Paused && automatorStarted)
        {
            HandleCraftEnded();
            return;
        }

        switch (State)
        {
            case BatchState.Solving:
                TickSolving(isCrafting);
                break;
            case BatchState.StartingCraft:
                TickStartingCraft(isCrafting);
                break;
            case BatchState.Crafting:
                TickCrafting(isCrafting, craftJustEnded);
                break;
            case BatchState.QuickStarting:
                TickQuickStarting();
                break;
            case BatchState.QuickRunning:
                TickQuickRunning();
                break;
        }
    }

    /// <summary>
    /// Assist mode (roadmap 7.18): a synthesis the user starts by hand while
    /// nothing is running becomes a batch of one, adopted on step 1 the way
    /// <see cref="Start"/> attaches to a craft in progress. Only the craft
    /// that began while the batch was idle qualifies, and only once — a craft
    /// the assisted batch gave up on is never re-adopted, and a craft the
    /// runner started is never touched (the batch is not idle then).
    /// </summary>
    private bool TickAssist(bool isCrafting, bool craftJustStarted)
    {
        var idle = State is BatchState.Idle or BatchState.Completed or BatchState.Failed;
        if (!isCrafting)
        {
            assistCandidate = false;
            return false;
        }

        if (craftJustStarted && idle)
            assistCandidate = true;

        if (!assistCandidate || !idle || !configuration.AssistMode)
            return false;

        var craft = craftMonitor.Current;
        if (craft == null || gameBridge.IsQuickSynthesisActive)
            return false;

        // Past step 1 the user is clearly crafting by hand; leave it alone.
        assistCandidate = false;
        if (craft.Step > 1)
            return false;

        if (!Start(1))
        {
            Log.Information($"[Production] Assist: could not attach to the synthesis ({StatusText})");
            return false;
        }

        assisted = true;
        Log.Information("[Production] Assist: attaching to the synthesis started by hand.");
        return true;
    }

    private void TickQuickStarting()
    {
        if (gameBridge.IsQuickSynthesisActive)
        {
            quickLastProgressAt = Clock.UtcNow;
            Transition(BatchState.QuickRunning, $"Quick synthesizing {CompletedCrafts}/{targetQuantity}...");
            return;
        }

        if (Clock.UtcNow - waitStartedAt > StartTimeout)
        {
            Fail("quick synthesis did not start (out of materials, or the dialog did not respond)");
            return;
        }

        if (!quickDialogRequested)
        {
            // After a normal craft (an HQ-first phase, 7.22) the completion
            // animation is still playing: same breather as before a Synthesize.
            if (Clock.UtcNow - lastCraftEndedAt < Core.Pacing.BetweenCrafts)
                return;

            // The game refuses a synthesis without a free slot ("Insufficient
            // inventory space", seen 2026-09-15 on the second craft of a batch):
            // pause with the reason instead of pressing into the void.
            if (gameBridge.GetFreeInventorySlots() < 1)
            {
                Pause("inventory is full");
                return;
            }

            if (gameBridge.OpenQuickSynthesisDialog())
                quickDialogRequested = true;
            return;
        }

        // The game refuses quick synthesis for a recipe this character has
        // never crafted ("not available until after successfully crafting an
        // item") and the dialog never opens. Fall back to a normal synthesis
        // for this step and remember the recipe for the session.
        if (!gameBridge.IsAddonVisible("SynthesisSimpleDialog"))
        {
            if (Clock.UtcNow - waitStartedAt > QuickDialogTimeout)
            {
                recipeId = gameBridge.SelectedRecipeId;
                quickSynthRefused.Add(recipeId);
                Log.Information(
                    $"[Production] Quick synthesis dialog did not open for recipe {recipeId} " +
                    "(recipe never crafted?); switching this batch to normal synthesis.");
                quickMode = false;
                quickDialogRequested = false;
                synthesisFired = false;
                waitStartedAt = Clock.UtcNow;
                Transition(BatchState.StartingCraft, ProgressText());
            }

            return;
        }

        // The dialog takes the count; batches are capped at 99 by the game and
        // re-entered here for larger targets.
        gameBridge.ConfirmQuickSynthesisDialog(targetQuantity - CompletedCrafts);
    }

    private void TickQuickRunning()
    {
        var produced = (gameBridge.GetItemCount(resultItemId) - baselineItemCount) / Math.Max(resultAmount, 1);
        if (produced > CompletedCrafts)
        {
            CompletedCrafts = produced;
            quickLastProgressAt = Clock.UtcNow;
            StatusText = $"Quick synthesizing {CompletedCrafts}/{targetQuantity}...";
        }

        if (CompletedCrafts >= targetQuantity)
        {
            if (gameBridge.IsQuickSynthesisActive)
                gameBridge.CancelQuickSynthesis();
            else
                Transition(BatchState.Completed, $"Completed: {CompletedCrafts}/{targetQuantity} quick synths.");
            return;
        }

        if (!gameBridge.IsQuickSynthesisActive)
        {
            // A 99-batch finished (or materials/space ran out): start the next
            // round; QuickStarting fails cleanly if nothing can be crafted.
            quickDialogRequested = false;
            waitStartedAt = Clock.UtcNow;
            Transition(BatchState.QuickStarting, $"Quick synthesis round done ({CompletedCrafts}/{targetQuantity}); continuing.");
            return;
        }

        if (Clock.UtcNow - quickLastProgressAt > TimeSpan.FromSeconds(30))
            Pause("quick synthesis made no progress for 30s");
    }

    private void TickSolving(bool isCrafting)
    {
        if (!isCrafting)
        {
            Fail("craft ended while the solver was preparing");
            return;
        }

        CaptureCraftResult();

        if (!solveRequested)
        {
            var craft = craftMonitor.Current;
            var player = gameBridge.GetPlayerState();
            if (craft == null || player == null)
                return;

            // The Synthesis window's numbers fill in a frame or two after the
            // craft starts; solving from zeros only yields "no solution".
            if (craft.MaxProgress <= 0 || craft.MaxQuality <= 0 || craft.MaxDurability <= 0)
                return;

            // Specialist one-shots: usable at craft start only when the job is
            // an equipped specialist with charges (roadmap 3.5).
            var jobId = player.ClassJobId;
            var heartAndSoul = IsSpecialistActionReady(CraftActionData.HeartAndSoul, jobId);
            var quickInnovation = IsSpecialistActionReady(CraftActionData.QuickInnovation, jobId);

            var recipeInfo = recipeId != 0 ? recipeProvider.GetRecipeById(recipeId) : null;

            var setup = new CraftSetup(
                RecipeLevel: craft.RecipeLevel,
                MaxProgress: (ushort)craft.MaxProgress,
                MaxQuality: (ushort)craft.MaxQuality,
                MaxDurability: (ushort)craft.MaxDurability,
                IsExpert: recipeInfo?.IsExpert ?? false,
                Craftsmanship: (ushort)player.Craftsmanship,
                Control: (ushort)player.Control,
                Cp: (ushort)player.MaxCp,
                Level: (byte)player.Level,
                Manipulation: player.Level >= 65,
                HeartAndSoul: heartAndSoul,
                QuickInnovation: quickInnovation);

            solvedSetup = setup;
            // Collectables (roadmap 4.1): never solve below the recipe's
            // required quality. A mid-craft recovery keeps the original target
            // — recomputing from live quality would inflate it for the rest of
            // the batch. An HQ batch (7.13) always solves for full quality.
            if (!midSolve || solveTargetQuality == 0)
            {
                // An HQ-first intermediate craft (7.22) solves for 100% too.
                var percent = requireHq || InHqPhase ? 100 : Math.Clamp(configuration.TargetQualityPercent, 1, 100);
                // A collectable tier threshold (7.23) replaces the percentage.
                var wanted = qualityOverride > 0 ? qualityOverride : craft.MaxQuality * percent / 100;
                solveTargetQuality = Math.Max(
                    Math.Max((int)craft.Quality, craft.RequiredQuality),
                    Math.Min(wanted, craft.MaxQuality));
            }

            if (midSolve)
            {
                var context = new CraftSolveContext(
                    TrainedPerfectionAvailable: IsSpecialistActionReady(CraftActionData.TrainedPerfection, jobId));
                if (!solverService.BeginSolveFromState(setup, craft, solveTargetQuality, context))
                    return;

                midSolveState = craft;
                midSolveEffects = CraftLiveEffects.FromSnapshot(craft, setup, context);
            }
            else if (TryManualRotation(setup, craft))
            {
                return;
            }
            else if (!solverService.BeginSolve(setup, new CraftObjective(
                    // Live quality at solve time reflects HQ materials, so the
                    // rotation accounts for them (every craft of the batch
                    // shares the same HQ fill).
                    TargetQuality: (ushort)solveTargetQuality,
                    InitialQuality: (ushort)craft.Quality)))
            {
                return;
            }

            solveInitialQuality = craft.Quality;
            solveRequested = true;
            StatusText = midSolve ? "Re-solving from the current state..." : "Solving rotation...";
            return;
        }

        switch (solverService.Status)
        {
            case SolverStatus.Done when solverService.Solution is { Success: true } solved:
                if (midSolve)
                {
                    midSolution = solved;
                    midSolve = false;
                    Rotation = new ActiveRotation(
                        solved.ActionIds, "mid-craft re-solve", solvedSetup!, solved.BaseProgress, solved.BaseQuality,
                        solveTargetQuality, solveInitialQuality, midSolveState, midSolveEffects);
                }
                else
                {
                    solution = solved;
                    Rotation = new ActiveRotation(
                        solved.ActionIds, solverService.LastSolveCached ? "cached solve" : "solved", solvedSetup!,
                        solved.BaseProgress, solved.BaseQuality, solveTargetQuality, solveInitialQuality);
                }

                automatorStarted = false;
                Transition(BatchState.Crafting, ProgressText());
                break;
            case SolverStatus.Failed:
                if (midSolve)
                {
                    midSolve = false;
                    Pause($"mid-craft re-solve failed ({solverService.Solution?.Error})");
                }
                else
                {
                    Fail($"solver failed ({solverService.Solution?.Error})");
                }

                break;
        }
    }

    /// <summary>
    /// Manual rotation (roadmap 7.8): the text saved for the recipe replaces
    /// the solver. The base progress/quality the adaptive rules need are
    /// computed from the recipe level table with raphael-data's formula
    /// (<see cref="RecipeSheet.BaseValues"/>) rather than by running a solve
    /// for them — the point of a manual rotation is skipping the solve, and
    /// the numbers are the same. Unparseable text is logged and the solver
    /// takes over; a mid-craft recovery still re-solves with Raphael.
    /// </summary>
    private bool TryManualRotation(CraftSetup setup, CraftSnapshot craft)
    {
        if (recipeId == 0 || !configuration.ManualRotations.TryGetValue(recipeId, out var text))
            return false;

        var parsed = RotationText.Parse(text);
        if (!parsed.Success)
        {
            Log.Warning($"[Production] Manual rotation for recipe {recipeId} ignored ({string.Join("; ", parsed.Errors)}); solving instead.");
            return false;
        }

        var (baseProgress, baseQuality) = RecipeSheet.BaseValues(setup) ?? (0, 0);
        if (baseProgress == 0)
            Log.Warning($"[Production] No level-table row for rlvl {setup.RecipeLevel}; the adaptive progress rules are off for this manual rotation.");

        solution = new CraftSolution(parsed.ActionIds, BaseProgress: baseProgress, BaseQuality: baseQuality);
        solveInitialQuality = craft.Quality;
        Rotation = new ActiveRotation(
            parsed.ActionIds, "manual rotation", setup, baseProgress, baseQuality, solveTargetQuality, craft.Quality);
        Log.Information(
            $"[Production] Manual rotation for recipe {recipeId}: {parsed.ActionIds.Count} actions " +
            $"(base progress {baseProgress}, base quality {baseQuality}); skipping the solve.");

        automatorStarted = false;
        Transition(BatchState.Crafting, ProgressText());
        return true;
    }

    private bool IsSpecialistActionReady(uint raphaelActionId, uint jobId)
    {
        var resolved = actionResolver.ResolveForJob(raphaelActionId, jobId);
        return resolved != null && gameBridge.IsCraftActionReady(resolved.Value);
    }

    private void TickStartingCraft(bool isCrafting)
    {
        if (!isCrafting && !synthesisFired)
        {
            // Repair gear / refresh food between crafts (roadmap 6.1/6.2).
            if (maintenance.Tick())
            {
                StatusText = maintenance.StatusText;
                waitStartedAt = Clock.UtcNow;
                return;
            }

            if (maintenance.BlockedReason != null)
            {
                Pause(maintenance.BlockedReason);
                return;
            }

            // Maintenance may have closed the crafting log; reopen our recipe —
            // but only when the window is actually gone. Right after a craft the
            // log is briefly "not ready" while it reopens on its own, and
            // reopening it through the agent drops the crafting stance and
            // re-enters it, which looks nothing like a person crafting again.
            if (!gameBridge.IsAddonVisible("RecipeNote") && recipeId != 0
                && Clock.UtcNow - lastRecipeOpenAttempt > TimeSpan.FromSeconds(2))
            {
                lastRecipeOpenAttempt = Clock.UtcNow;
                gameBridge.OpenRecipe(recipeId);
            }
        }

        if (isCrafting)
        {
            // Craft #(CompletedCrafts+1) has begun.
            synthesisFired = false;
            automatorStarted = false;
            craftStartedAt = Clock.UtcNow;
            Transition(solution == null ? BatchState.Solving : BatchState.Crafting, ProgressText());
            return;
        }

        if (!synthesisFired)
        {
            // Breathe between crafts (pacing): the completion animation is
            // still playing and back-to-back presses are what got us kicked.
            if (Clock.UtcNow - lastCraftEndedAt < Core.Pacing.BetweenCrafts)
                return;

            if (gameBridge.IsReadyToStartCraft)
            {
                if (recipeId != 0 && gameBridge.SelectedRecipeId != recipeId)
                {
                    Fail($"selected recipe changed ({gameBridge.SelectedRecipeId} != {recipeId})");
                    return;
                }

                recipeId = gameBridge.SelectedRecipeId;
                // Re-register the materials with the log's own fill button on one
                // frame and press Synthesize on the next: a fill and a press in
                // the same frame were swallowed after a completed craft (Boiled
                // Egg 2/2, 2026-09-15), and so were presses without a fill.
                if (!fillPressedForThisPress)
                {
                    fillPressedForThisPress = true;
                    gameBridge.FillIngredients(PreferHqFill);
                    return;
                }

                fillPressedForThisPress = false;
                if (gameBridge.StartSynthesis())
                {
                    synthesisFired = true;
                    waitStartedAt = Clock.UtcNow;
                    lastSynthesisPress = Clock.UtcNow;
                    Log.Information($"[Production] Starting synthesis of recipe {recipeId}.");
                }

                return;
            }

            // An HQ batch keeps crafting past its nominal count while NQ
            // results land (7.13); the materials are what ends it, so say so
            // instead of timing out on a log that can never become ready.
            if (requireHq && gameBridge.IsAddonVisible("RecipeNote") && gameBridge.SelectedRecipeId != 0)
            {
                var requirements = gameBridge.GetRecipeRequirements(gameBridge.SelectedRecipeId);
                if (requirements.Count > 0 && InventoryMath.CraftableCount(requirements) < 1)
                {
                    Fail($"materials ran out with {CompletedCrafts}/{targetQuantity} HQ made ({verifiedCrafts} crafts)");
                    return;
                }
            }

            // The log is open on our recipe but the game has not assigned the
            // materials (ingredients owned only as HQ stay at 0 until the HQ
            // column is selected): press the log's own fill button.
            if (gameBridge.IsAddonVisible("RecipeNote") && gameBridge.SelectedRecipeId != 0
                && !gameBridge.AreIngredientsAssigned()
                && Clock.UtcNow - lastFillAttempt > TimeSpan.FromSeconds(2))
            {
                lastFillAttempt = Clock.UtcNow;
                var preferHq = PreferHqFill;
                var assigned = gameBridge.FillIngredients(preferHq);
                Log.Information($"[Production] Assigning materials via the crafting log's {(preferHq ? "HQ" : "NQ")} fill button: {(assigned ? "all assigned" : "still incomplete")}.");
            }

            if (Clock.UtcNow - waitStartedAt > StartTimeout)
                Fail("crafting log did not become ready");

            return;
        }

        if (Clock.UtcNow - waitStartedAt > StartTimeout)
        {
            Fail("synthesis did not start after pressing Synthesize");
            return;
        }

        // A press made right after the previous craft is swallowed by the
        // completion animation (observed: the log was ready, nothing started).
        // Press again every few seconds until the craft begins or we time out.
        if (Clock.UtcNow - lastSynthesisPress > SynthesisRetryInterval && gameBridge.IsReadyToStartCraft)
        {
            if (!fillPressedForThisPress)
            {
                fillPressedForThisPress = true; // fill this frame, press the next (see above)
                gameBridge.FillIngredients(PreferHqFill);
                return;
            }

            fillPressedForThisPress = false;
            lastSynthesisPress = Clock.UtcNow;
            if (gameBridge.StartSynthesis())
                Log.Information($"[Production] Synthesis has not started yet; pressing Synthesize again.");
        }
    }

    private void TickCrafting(bool isCrafting, bool craftJustEnded)
    {
        if (isCrafting)
        {
            CaptureCraftResult();

            // A batch attached mid-craft has only the live-state solution.
            if (!automatorStarted && (solution != null || midSolution != null) && automator.State != AutomationState.Running)
            {
                // Let the synthesis start animation play before the first action (pacing).
                if (Clock.UtcNow - craftStartedAt < Core.Pacing.AfterCraftStart)
                    return;

                var player = gameBridge.GetPlayerState();
                if (player == null)
                    return;

                // Food/potion expiry between crafts (roadmap 3.3): stats that
                // no longer match the solve invalidate the rotation.
                if (midSolution == null && solvedSetup is { } solved
                    && ((ushort)player.Craftsmanship != solved.Craftsmanship
                        || (ushort)player.Control != solved.Control
                        || (ushort)player.MaxCp != solved.Cp))
                {
                    Log.Information("[Raphael] Crafter stats changed (food/potion?); re-solving.");
                    solution = null;
                    solveRequested = false;
                    Transition(BatchState.Solving, "Stats changed; re-solving...");
                    return;
                }

                var active = midSolution ?? solution;
                if (active == null)
                    return;

                if (automator.Start(active.ActionIds, player.ClassJobId, active.BaseProgress, solveTargetQuality))
                {
                    automatorStarted = true;
                    StatusText = ProgressText();
                }

                return;
            }

            // A lock-step hold (roadmap 7.18) is the user's pause, not a problem to recover from.
            if (automatorStarted && automator.WaitingForStep)
            {
                StatusText = $"Lock-step: {automator.StatusText}";
                return;
            }

            if (automatorStarted && automator.State is AutomationState.Paused or AutomationState.Failed)
            {
                // One recovery attempt per craft (roadmap 3.2): re-solve the
                // remainder from the live state instead of giving up.
                if (!midSolveTried && automator.State == AutomationState.Paused
                    && craftMonitor.Current != null && solvedSetup != null)
                {
                    midSolveTried = true;
                    midSolve = true;
                    solveRequested = false;
                    automatorStarted = false;
                    automator.Stop();
                    Log.Information($"[Adaptive] Automation paused ({automator.StatusText}); re-solving the remainder.");
                    Transition(BatchState.Solving, "Recovering: re-solving the remaining craft...");
                    return;
                }

                Pause($"craft automation stopped ({automator.StatusText})");
            }

            return;
        }

        if (!craftJustEnded)
            return;

        HandleCraftEnded();
    }

    /// <summary>Verifies and counts a finished craft (spec §18); safe to run while paused.</summary>
    private void HandleCraftEnded()
    {
        // A craft ended: verify against inventory before counting it (spec §18).
        automatorStarted = false;
        midSolution = null;
        midSolveTried = false;
        midSolve = false;

        if (automator.State != AutomationState.Completed)
        {
            // An assisted craft the user cancelled or finished by hand is
            // simply over: staying Paused would block the next assist.
            if (assisted)
            {
                Transition(BatchState.Idle, $"Assisted craft ended before the rotation completed ({automator.StatusText}).");
                return;
            }

            Pause($"craft ended without completing the rotation ({automator.StatusText})");
            return;
        }

        // The inventory update can land a few frames after the Synthesis
        // window closes; keep checking briefly before calling it a failure.
        verifyUntil = Clock.UtcNow + TimeSpan.FromSeconds(3);
        TryVerifyCraft();
    }

    private void TryVerifyCraft()
    {
        if (resultItemId != 0)
        {
            var expected = baselineItemCount + (verifiedCrafts + 1) * Math.Max(resultAmount, 1);
            var actual = gameBridge.GetItemCount(resultItemId);
            if (actual < expected)
            {
                if (Clock.UtcNow < verifyUntil)
                    return;

                verifyUntil = DateTime.MinValue;
                Pause($"inventory verification failed (item {resultItemId}: have {actual}, expected {expected})");
                return;
            }
        }

        verifyUntil = DateTime.MinValue;
        lastCraftEndedAt = Clock.UtcNow;
        verifiedCrafts++;

        if (requireHq)
        {
            // Only the HQ gain counts (7.13 ForceHq); an NQ result is a craft
            // spent, not progress, and the batch keeps going while materials last.
            var hqGain = Math.Max(0, gameBridge.GetHqItemCount(resultItemId) - hqBaseline) / Math.Max(resultAmount, 1);
            if (hqGain > CompletedCrafts)
            {
                CompletedCrafts = hqGain;
                Log.Information($"[Production] Craft {verifiedCrafts} verified HQ ({CompletedCrafts}/{targetQuantity} HQ).");
            }
            else
            {
                Log.Information($"[Production] Craft {verifiedCrafts} landed NQ; not counted ({CompletedCrafts}/{targetQuantity} HQ).");
            }
        }
        else if (InHqPhase)
        {
            // HQ-first intermediate (7.22): the craft counts either way; the
            // HQ tally decides when the phase ends.
            CompletedCrafts++;
            var hqGain = Math.Max(0, gameBridge.GetHqItemCount(resultItemId) - hqBaseline) / Math.Max(resultAmount, 1);
            if (hqGain > hqMade)
            {
                hqMade = hqGain;
                Log.Information($"[Production] Craft {CompletedCrafts}/{targetQuantity} verified HQ ({hqMade}/{hqFirst} HQ intermediates).");
            }
            else
            {
                Log.Information($"[Production] Craft {CompletedCrafts}/{targetQuantity} landed NQ ({hqMade}/{hqFirst} HQ intermediates; still crafting normally).");
            }

            if (!InHqPhase && CompletedCrafts < targetQuantity)
                EndHqPhase();
        }
        else
        {
            CompletedCrafts++;
            Log.Information($"[Production] Craft {CompletedCrafts}/{targetQuantity} verified.");
        }

        if (CompletedCrafts >= targetQuantity)
        {
            Transition(BatchState.Completed, requireHq
                ? $"Completed: {CompletedCrafts}/{targetQuantity} HQ in {verifiedCrafts} crafts."
                : hqFirst > 0 ? $"Completed: {CompletedCrafts}/{targetQuantity} crafts ({hqMade} of {hqFirst} wanted HQ)."
                : $"Completed: {CompletedCrafts}/{targetQuantity} crafts.");
            return;
        }

        synthesisFired = false;
        waitStartedAt = Clock.UtcNow;
        if (State == BatchState.Paused)
            StatusText = $"Paused ({CompletedCrafts}/{targetQuantity} crafts verified).";
        else if (quickMode)
            Transition(BatchState.QuickStarting, $"Quick synthesizing the remaining {targetQuantity - CompletedCrafts} craft(s)...");
        else
            Transition(BatchState.StartingCraft, ProgressText());
    }

    /// <summary>
    /// The HQ-first phase is over (7.22): the remaining crafts solve for the
    /// configured quality again (the rotation is dropped so the next craft
    /// re-solves — a cache hit when the recipe was crafted before) or switch
    /// to quick synthesis when the step asked for it and the game offers it.
    /// </summary>
    private void EndHqPhase()
    {
        solution = null;
        solveRequested = false;
        solveTargetQuality = 0;
        var remaining = targetQuantity - CompletedCrafts;
        quickMode = quickAfterHq && gameBridge.IsQuickSynthAvailable && !quickSynthRefused.Contains(recipeId);
        if (quickMode)
            quickDialogRequested = false;
        Log.Information(
            $"[Production] HQ intermediates done ({hqMade} HQ in {CompletedCrafts} crafts); " +
            $"the remaining {remaining} craft(s) {(quickMode ? "quick synthesize" : "craft normally at the configured quality")}.");
    }

    private void CaptureCraftResult()
    {
        if (resultItemId != 0)
            return;

        // The recipe we pressed Synthesize on is the authority. The craft
        // event handler's result item is only read when no recipe is known
        // (a batch attached to a craft already in progress): right after a
        // synthesis starts it still holds the *previous* craft's item, which
        // made a White Gold Ingot batch verify against Titanium Gold Nuggets.
        var recipe = recipeId != 0 ? recipeProvider.GetRecipeById(recipeId) : null;
        if (recipe != null)
        {
            resultItemId = recipe.ResultItemId;
            resultAmount = recipe.ResultAmount;
        }
        else
        {
            var result = gameBridge.CurrentCraftResult;
            if (result == null)
                return;

            resultItemId = result.Value.ItemId;
            resultAmount = result.Value.Amount;
        }
        baselineItemCount = gameBridge.GetItemCount(resultItemId) - verifiedCrafts * Math.Max(resultAmount, 1);
        // Only an HQ batch's completed crafts are all HQ; an HQ-first batch
        // (7.22) knows its HQ tally separately.
        hqBaseline = gameBridge.GetHqItemCount(resultItemId) - (requireHq ? CompletedCrafts : hqMade) * Math.Max(resultAmount, 1);
        Log.Information(
            $"[Production] Batch target item {resultItemId} x{resultAmount} per craft; " +
            $"inventory baseline {baselineItemCount}" + (requireHq ? $" (HQ {hqBaseline})" : "") + ".");
    }

    private string ProgressText() => requireHq
        ? $"Crafting for HQ {CompletedCrafts}/{targetQuantity} (craft #{verifiedCrafts + 1})..."
        : InHqPhase ? $"Crafting {CompletedCrafts + 1}/{targetQuantity} (HQ {hqMade}/{hqFirst})..."
        : $"Crafting {CompletedCrafts + 1}/{targetQuantity}...";

    private void Fail(string reason) => Transition(BatchState.Failed, $"Failed: {reason}.");

    /// <summary>Internal state for the diagnostic report.</summary>
    public override IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
        yield return $"Crafts {CompletedCrafts}/{targetQuantity} (verified {verifiedCrafts}, requireHq {requireHq}, hqFirst {hqFirst}, hqMade {hqMade}, quickAfterHq {quickAfterHq}); recipe {recipeId}; result item {resultItemId} ×{resultAmount}; baseline count {baselineItemCount} (HQ {hqBaseline}), now {(resultItemId != 0 ? gameBridge.GetItemCount(resultItemId) : 0)}";
        yield return $"solveRequested {solveRequested}; synthesisFired {synthesisFired}; automatorStarted {automatorStarted}; wasCrafting {wasCrafting}; quickMode {quickMode}; quickDialogRequested {quickDialogRequested}; midSolve {midSolve}; midSolveTried {midSolveTried}";
        yield return $"Target quality {solveTargetQuality}; wait started {waitStartedAt:HH:mm:ss}Z; quick last progress {quickLastProgressAt:HH:mm:ss}Z; last recipe open attempt {lastRecipeOpenAttempt:HH:mm:ss}Z";
        yield return $"Solved setup: {solvedSetup?.ToString() ?? "none"}";
        yield return $"Solution: {DescribeSolution(solution)}; mid-craft solution: {DescribeSolution(midSolution)}";
        yield return $"Rotation source: {Rotation?.Source ?? "none"}; assistMode {configuration.AssistMode}; assistCandidate {assistCandidate}; assisted {assisted}; lockStep {configuration.LockStep}";
    }

    private static string DescribeSolution(CraftSolution? candidate) =>
        candidate == null ? "none"
        : candidate.Success ? $"{candidate.ActionIds.Count} actions (base {candidate.BaseProgress}/{candidate.BaseQuality})"
        : $"failed: {candidate.Error}";
}
