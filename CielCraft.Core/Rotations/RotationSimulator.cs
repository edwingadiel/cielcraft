using System;
using System.Collections.Generic;

namespace CielCraft.Core.Rotations;

/// <summary>The RecipeLevelTable fields the base-value formula reads.</summary>
public sealed record RecipeLevelInfo(
    int ClassJobLevel,
    int ProgressDivider,
    int QualityDivider,
    int ProgressModifier,
    int QualityModifier);

/// <summary>Crafting arithmetic shared by the solver boundary and the simulator.</summary>
public static class CraftMath
{
    /// <summary>
    /// Progress and quality per 100% efficiency, computed the way
    /// raphael-data does (single-precision, truncated) so a manual rotation
    /// (roadmap 7.8) gets the same base numbers a solve would have carried.
    /// </summary>
    public static (int BaseProgress, int BaseQuality) BaseValues(int craftsmanship, int control, int level, RecipeLevelInfo rlvl)
    {
        if (rlvl.ProgressDivider <= 0 || rlvl.QualityDivider <= 0)
            return (0, 0);

        var progress = craftsmanship * 10f / rlvl.ProgressDivider + 2f;
        var quality = control * 10f / rlvl.QualityDivider + 35f;
        if (level <= rlvl.ClassJobLevel)
        {
            progress = progress * rlvl.ProgressModifier / 100f;
            quality = quality * rlvl.QualityModifier / 100f;
        }

        return ((int)progress, (int)quality);
    }
}

/// <summary>One simulated action: the craft numbers after it landed.</summary>
public sealed record SimulatedStep(
    int Index,
    uint ActionId,
    int Progress,
    int Quality,
    int Durability,
    int Cp,
    string? Error = null,
    string? Note = null);

/// <summary>Where a rotation is expected to leave the craft (roadmap 7.8/7.18).</summary>
public sealed record RotationSimulation(
    IReadOnlyList<SimulatedStep> Steps,
    int Progress,
    int MaxProgress,
    int Quality,
    int MaxQuality,
    int Durability,
    int Cp,
    string? Error)
{
    public bool Finished => Progress >= MaxProgress;

    public int QualityPercent => MaxQuality <= 0 ? 100 : (int)Math.Min(100, (long)Quality * 100 / MaxQuality);

    public int HqChancePercent => HqChance.For(Quality, MaxQuality);
}

/// <summary>
/// Deterministic replay of a rotation under Normal condition, mirroring
/// raphael-sim's action semantics (costs, efficiencies, buff windows,
/// combos, one-shots) so the UI can show the outcome a solve or a manual
/// rotation is expected to reach. The native library only reports base
/// progress/quality, so the numbers per step come from here. Chance-based
/// actions (Rapid Synthesis, Hasty/Daring Touch) are assumed to succeed
/// and noted as such.
/// </summary>
public static class RotationSimulator
{
    private const uint BasicSynthesis = 100001, BasicTouch = 100002, MastersMend = 100003, Observe = 100010,
        TricksOfTheTrade = 100371, WasteNot = 4631, Veneration = 19297, StandardTouch = 100004, GreatStrides = 260,
        Innovation = 19004, WasteNot2 = 4639, ByregotsBlessing = 100339, PreciseTouch = 100128, MuscleMemory = 100379,
        CarefulSynthesis = 100203, Manipulation = 4574, PrudentTouch = 100227, AdvancedTouch = 100411, Reflect = 100387,
        PreparatoryTouch = 100299, Groundwork = 100403, DelicateSynthesis = 100323, IntensiveSynthesis = 100315,
        TrainedEye = 100283, HeartAndSoul = 100419, PrudentSynthesis = 100427, TrainedFinesse = 100435,
        RefinedTouch = 100443, QuickInnovation = 100459, ImmaculateMend = 100467, TrainedPerfection = 100475,
        StellarSteadyHand = 46843, RapidSynthesis = 100363, HastyTouch = 100355, DaringTouch = 100451;

    private sealed class State
    {
        public int Cp, Durability, Progress, Quality;
        public int InnerQuiet, WasteNot, Innovation, Veneration, GreatStrides, MuscleMemory, Manipulation;
        public int StellarSteadyHand, StellarCharges;
        public bool TrainedPerfectionAvailable, HeartAndSoulAvailable, QuickInnovationAvailable;
        public bool TrainedPerfectionActive, HeartAndSoulActive, Expedience;
        public CraftCombo Combo;
    }

    /// <summary>Replays the rotation from synthesis begin (quality starts at <paramref name="initialQuality"/>: HQ materials).</summary>
    public static RotationSimulation Run(
        CraftSetup setup, int baseProgress, int baseQuality, IReadOnlyList<uint> actions, int initialQuality = 0)
    {
        var state = new State
        {
            Cp = setup.Cp,
            Durability = setup.MaxDurability,
            Quality = Math.Max(0, initialQuality),
            TrainedPerfectionAvailable = setup.Level >= 100,
            HeartAndSoulAvailable = setup.HeartAndSoul && setup.Level >= 86,
            QuickInnovationAvailable = setup.QuickInnovation && setup.Level >= 96,
            StellarCharges = 1, // unknown outside a stellar mission; the action is noted, not refused
            Combo = CraftCombo.SynthesisBegin,
        };

        return Run(setup, baseProgress, baseQuality, actions, state);
    }

    /// <summary>Replays the remainder of a rotation from a live craft (a mid-craft re-solve's starting point).</summary>
    public static RotationSimulation Run(
        CraftSetup setup, int baseProgress, int baseQuality, IReadOnlyList<uint> actions,
        CraftSnapshot live, CraftLiveEffects effects)
    {
        var state = new State
        {
            Cp = (int)Math.Min(live.CurrentCp, setup.Cp),
            Durability = Math.Min(live.Durability, setup.MaxDurability),
            Progress = live.Progress,
            Quality = live.Quality,
            InnerQuiet = effects.InnerQuiet,
            WasteNot = effects.WasteNot,
            Innovation = effects.Innovation,
            Veneration = effects.Veneration,
            GreatStrides = effects.GreatStrides,
            MuscleMemory = effects.MuscleMemory,
            Manipulation = effects.Manipulation,
            TrainedPerfectionAvailable = effects.TrainedPerfectionAvailable && setup.Level >= 100,
            HeartAndSoulAvailable = effects.HeartAndSoulAvailable,
            QuickInnovationAvailable = effects.QuickInnovationAvailable,
            TrainedPerfectionActive = effects.TrainedPerfectionActive,
            HeartAndSoulActive = effects.HeartAndSoulActive,
            StellarCharges = 1,
            Combo = effects.Combo,
        };

        return Run(setup, baseProgress, baseQuality, actions, state);
    }

    private static RotationSimulation Run(
        CraftSetup setup, int baseProgress, int baseQuality, IReadOnlyList<uint> actions, State state)
    {
        var steps = new List<SimulatedStep>(actions.Count);
        string? firstError = null;

        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var error = Apply(state, action, setup, baseProgress, baseQuality, out var note);
            if (error != null)
            {
                steps.Add(new SimulatedStep(i, action, state.Progress, state.Quality, state.Durability, state.Cp, error, note));
                firstError = $"step {i + 1} ({RaphaelActionNames.NameOf(action)}): {error}";
                break;
            }

            // The synthesis window closes on the finishing action; whatever
            // follows in the text never runs, which is worth pointing out for
            // a manual rotation but is not a mistake.
            var finished = state.Durability <= 0 || state.Progress >= setup.MaxProgress;
            if (finished && i < actions.Count - 1)
                note = $"the craft ends here; {actions.Count - i - 1} more action(s) never run";

            steps.Add(new SimulatedStep(i, action, state.Progress, state.Quality, state.Durability, state.Cp, null, note));
            if (finished)
                break;
        }

        return new RotationSimulation(
            steps, state.Progress, setup.MaxProgress, state.Quality, setup.MaxQuality, state.Durability, state.Cp, firstError);
    }

    /// <summary>One action, in raphael-sim's order: checks, costs, gains, resets, ticks, transform, sets.</summary>
    private static string? Apply(State s, uint action, CraftSetup setup, int baseProgress, int baseQuality, out string? note)
    {
        note = null;
        if (!RaphaelActionNames.ById.ContainsKey(action))
            return "unknown action";

        var level = setup.Level;
        if (level < LevelRequirement(action))
            return $"requires level {LevelRequirement(action)}";

        if (s.Durability <= 0 || s.Progress >= setup.MaxProgress)
            return "the craft is already finished";

        var cpCost = CpCost(action, s);
        if (cpCost > s.Cp)
            return $"not enough CP (needs {cpCost}, has {s.Cp})";

        var precondition = Precondition(action, s, setup, ref note);
        if (precondition != null)
            return precondition;

        // Gains read the state before the action; Groundwork's halving looks at durability before the cost.
        var durabilityCost = DurabilityCost(action, s);
        var progressGain = ProgressGain(action, s, level, baseProgress, durabilityCost);
        var qualityGain = QualityGain(action, s, setup, baseQuality);
        var increasesStep = !CraftActionData.IsSpecialist(action);
        var wasSynthesisBegin = s.Combo == CraftCombo.SynthesisBegin;

        if (BaseDurabilityCost(action) != 0)
            s.Durability = Math.Max(0, s.Durability - durabilityCost);
        s.Cp -= cpCost;

        s.Quality = Math.Min(ushort.MaxValue, s.Quality + qualityGain);
        if (qualityGain != 0 && level >= 11)
            s.InnerQuiet = Math.Min(10, s.InnerQuiet + 1);
        s.Progress = Math.Min(ushort.MaxValue, s.Progress + progressGain);

        if (s.Durability <= 0 || s.Progress >= setup.MaxProgress)
            return null;

        Reset(action, s);
        if (!increasesStep && wasSynthesisBegin)
            s.Combo = CraftCombo.SynthesisBegin; // specialist one-shots do not spend step 1

        if (increasesStep)
        {
            if (s.Manipulation != 0)
                s.Durability = Math.Min(setup.MaxDurability, s.Durability + 5);
            TickDown(s);
        }

        Transform(action, s, setup, level);
        Set(action, s);
        return null;
    }

    private static string? Precondition(uint action, State s, CraftSetup setup, ref string? note)
    {
        switch (action)
        {
            case TricksOfTheTrade or PreciseTouch or IntensiveSynthesis:
                return s.HeartAndSoulActive ? null : "needs a Good or Excellent condition (or Heart and Soul)";
            case ByregotsBlessing:
                return s.InnerQuiet == 0 ? "needs Inner Quiet" : null;
            case MuscleMemory or Reflect or TrainedEye:
                return s.Combo == CraftCombo.SynthesisBegin ? null : "only usable on the first step";
            case PrudentTouch or PrudentSynthesis:
                return s.WasteNot != 0 ? "not usable under Waste Not" : null;
            case TrainedFinesse:
                return s.InnerQuiet < 10 ? "needs 10 Inner Quiet stacks" : null;
            case RefinedTouch:
                return s.Combo == CraftCombo.BasicTouch ? null : "must follow Basic Touch";
            case HeartAndSoul:
                return s.HeartAndSoulAvailable ? null : "not available (specialist one-shot already used, or not a specialist)";
            case QuickInnovation:
                if (s.Innovation != 0)
                    return "not usable while Innovation is active";
                return s.QuickInnovationAvailable ? null : "not available (specialist one-shot already used, or not a specialist)";
            case TrainedPerfection:
                return s.TrainedPerfectionAvailable ? null : "already used this craft";
            case Manipulation:
                return setup.Manipulation ? null : "Manipulation is not unlocked";
            case StellarSteadyHand:
                if (s.StellarCharges == 0)
                    note = "charges depend on the stellar mission";
                return null;
            case RapidSynthesis:
                if (s.StellarSteadyHand == 0)
                    note = "50% success assumed";
                return null;
            case HastyTouch:
                if (s.Expedience)
                    return "becomes Daring Touch right after Hasty Touch";
                if (s.StellarSteadyHand == 0)
                    note = "60% success assumed";
                return null;
            case DaringTouch:
                if (!s.Expedience)
                    return "must follow Hasty Touch";
                if (s.StellarSteadyHand == 0)
                    note = "60% success assumed";
                return null;
            default:
                return null;
        }
    }

    private static int LevelRequirement(uint action) => action switch
    {
        BasicSynthesis => 1,
        BasicTouch => 5,
        MastersMend => 7,
        RapidSynthesis or HastyTouch => 9,
        Observe or TricksOfTheTrade => 13,
        WasteNot or Veneration => 15,
        StandardTouch => 18,
        GreatStrides => 21,
        Innovation => 26,
        WasteNot2 => 47,
        ByregotsBlessing => 50,
        PreciseTouch => 53,
        MuscleMemory => 54,
        CarefulSynthesis => 62,
        Manipulation => 65,
        PrudentTouch => 66,
        AdvancedTouch => 68,
        Reflect => 69,
        PreparatoryTouch => 71,
        Groundwork => 72,
        DelicateSynthesis => 76,
        IntensiveSynthesis => 78,
        TrainedEye => 80,
        HeartAndSoul => 86,
        PrudentSynthesis => 88,
        TrainedFinesse or StellarSteadyHand => 90,
        RefinedTouch => 92,
        QuickInnovation or DaringTouch => 96,
        ImmaculateMend => 98,
        TrainedPerfection => 100,
        _ => 1,
    };

    private static int CpCost(uint action, State s) => action switch
    {
        StandardTouch => s.Combo == CraftCombo.BasicTouch ? 18 : 32,
        AdvancedTouch => s.Combo == CraftCombo.StandardTouch ? 18 : 46,
        StellarSteadyHand => 0,
        _ => CraftActionData.CpCost(action),
    };

    private static int BaseDurabilityCost(uint action) =>
        action == StellarSteadyHand ? 0 : CraftActionData.DurabilityCost(action);

    private static int DurabilityCost(uint action, State s)
    {
        var cost = BaseDurabilityCost(action);
        if (cost == 0 || s.TrainedPerfectionActive)
            return 0;
        return s.WasteNot != 0 ? (cost + 1) / 2 : cost;
    }

    private static int ProgressModifier(uint action, State s, int level, int durabilityCost) => action switch
    {
        BasicSynthesis => level < 31 ? 100 : 120,
        MuscleMemory => 300,
        CarefulSynthesis => level < 82 ? 150 : 180,
        Groundwork => (level < 86 ? 300 : 360) / (durabilityCost > s.Durability ? 2 : 1),
        DelicateSynthesis => level < 94 ? 100 : 150,
        IntensiveSynthesis => 400,
        PrudentSynthesis => 180,
        RapidSynthesis => level < 63 ? 250 : 500,
        _ => 0,
    };

    private static int ProgressGain(uint action, State s, int level, int baseProgress, int durabilityCost)
    {
        var actionMod = ProgressModifier(action, s, level, durabilityCost);
        if (actionMod == 0)
            return 0;

        var effectMod = 10 + (s.MuscleMemory != 0 ? 10 : 0) + (s.Veneration != 0 ? 5 : 0);
        return (int)Math.Min(ushort.MaxValue, (long)baseProgress * actionMod * effectMod / 1000);
    }

    private static int QualityModifier(uint action, State s) => action switch
    {
        BasicTouch or PrudentTouch or DelicateSynthesis or TrainedFinesse or RefinedTouch or HastyTouch => 100,
        StandardTouch => 125,
        AdvancedTouch or PreciseTouch or DaringTouch => 150,
        PreparatoryTouch => 200,
        Reflect => 300,
        ByregotsBlessing => 100 + 20 * s.InnerQuiet,
        _ => 0,
    };

    private static int QualityGain(uint action, State s, CraftSetup setup, int baseQuality)
    {
        if (action == TrainedEye)
            return Math.Max(0, setup.MaxQuality - s.Quality);

        var actionMod = QualityModifier(action, s);
        if (actionMod == 0)
            return 0;

        var effectMod = (s.InnerQuiet + 10) * (10 + (s.GreatStrides != 0 ? 10 : 0) + (s.Innovation != 0 ? 5 : 0));
        const int normalCondition = 2;
        return (int)Math.Min(ushort.MaxValue, (long)baseQuality * actionMod * effectMod * normalCondition / 20000);
    }

    /// <summary>raphael-sim's EFFECT_RESET_MASK per action; the default clears the combo.</summary>
    private static void Reset(uint action, State s)
    {
        if (action != StandardTouch)
            s.Combo = CraftCombo.None;

        switch (action)
        {
            case BasicSynthesis or CarefulSynthesis or Groundwork or IntensiveSynthesis or PrudentSynthesis
                or RapidSynthesis or MuscleMemory:
                s.MuscleMemory = 0;
                s.TrainedPerfectionActive = false;
                break;
            case BasicTouch or StandardTouch or PreciseTouch or PrudentTouch or AdvancedTouch or PreparatoryTouch
                or TrainedEye or RefinedTouch or HastyTouch or DaringTouch:
                s.GreatStrides = 0;
                s.TrainedPerfectionActive = false;
                break;
            case ByregotsBlessing:
                s.GreatStrides = 0;
                s.InnerQuiet = 0;
                s.TrainedPerfectionActive = false;
                break;
            case DelicateSynthesis:
                s.MuscleMemory = 0;
                s.GreatStrides = 0;
                s.TrainedPerfectionActive = false;
                break;
            case Reflect:
                s.TrainedPerfectionActive = false;
                break;
            case TrainedFinesse:
                s.GreatStrides = 0;
                break;
            case WasteNot or WasteNot2:
                s.WasteNot = 0;
                break;
            case Veneration:
                s.Veneration = 0;
                break;
            case GreatStrides:
                s.GreatStrides = 0;
                break;
            case Innovation:
                s.Innovation = 0;
                break;
            case Manipulation:
                s.Manipulation = 0;
                break;
            case QuickInnovation:
                s.QuickInnovationAvailable = false;
                s.Innovation = 0;
                break;
            case HeartAndSoul:
                s.HeartAndSoulAvailable = false;
                break;
            case TrainedPerfection:
                s.TrainedPerfectionAvailable = false;
                break;
            case StellarSteadyHand:
                s.StellarSteadyHand = 0;
                break;
        }
    }

    private static void TickDown(State s)
    {
        if (s.WasteNot > 0) s.WasteNot--;
        if (s.Innovation > 0) s.Innovation--;
        if (s.Veneration > 0) s.Veneration--;
        if (s.GreatStrides > 0) s.GreatStrides--;
        if (s.MuscleMemory > 0) s.MuscleMemory--;
        if (s.Manipulation > 0) s.Manipulation--;
        if (s.StellarSteadyHand > 0) s.StellarSteadyHand--;
        s.Expedience = false;
    }

    private static void Transform(uint action, State s, CraftSetup setup, int level)
    {
        switch (action)
        {
            case MastersMend:
                s.Durability = Math.Min(setup.MaxDurability, s.Durability + 30);
                break;
            case ImmaculateMend:
                s.Durability = setup.MaxDurability;
                break;
            case TricksOfTheTrade:
                s.Cp = Math.Min(setup.Cp, s.Cp + 20);
                s.HeartAndSoulActive = false; // Normal condition: the one-shot is what allowed it
                break;
            case StandardTouch:
                s.Combo = s.Combo == CraftCombo.BasicTouch ? CraftCombo.StandardTouch : CraftCombo.None;
                break;
            case PreciseTouch:
                s.InnerQuiet = Math.Min(10, s.InnerQuiet + 1);
                s.HeartAndSoulActive = false;
                break;
            case IntensiveSynthesis:
                s.HeartAndSoulActive = false;
                break;
            case Reflect or PreparatoryTouch or RefinedTouch:
                s.InnerQuiet = Math.Min(10, s.InnerQuiet + 1);
                break;
            case HastyTouch:
                s.Expedience = level >= 96;
                break;
            case StellarSteadyHand:
                s.StellarCharges = Math.Max(0, s.StellarCharges - 1);
                break;
        }
    }

    /// <summary>raphael-sim's EFFECT_SET_MASK per action.</summary>
    private static void Set(uint action, State s)
    {
        switch (action)
        {
            case BasicTouch: s.Combo = CraftCombo.BasicTouch; break;
            case Observe: s.Combo = CraftCombo.StandardTouch; break;
            case WasteNot: s.WasteNot = 4; break;
            case WasteNot2: s.WasteNot = 8; break;
            case Veneration: s.Veneration = 4; break;
            case GreatStrides: s.GreatStrides = 3; break;
            case Innovation: s.Innovation = 4; break;
            case MuscleMemory: s.MuscleMemory = 5; break;
            case Manipulation: s.Manipulation = 8; break;
            case HeartAndSoul: s.HeartAndSoulActive = true; break;
            case QuickInnovation: s.Innovation = 1; break;
            case TrainedPerfection: s.TrainedPerfectionActive = true; break;
            case StellarSteadyHand: s.StellarSteadyHand = 3; break;
        }
    }
}
