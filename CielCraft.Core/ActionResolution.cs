using System;

namespace CielCraft.Core;

public enum ActionOutcome
{
    /// <summary>The action has not visibly resolved yet; keep observing.</summary>
    Pending,

    /// <summary>The step number advanced — the action resolved (spec §12).</summary>
    StepAdvanced,

    /// <summary>The craft ended (completed, failed, or cancelled) while waiting.</summary>
    CraftEnded,

    /// <summary>No observable transition within the deadline.</summary>
    TimedOut,
}

/// <summary>
/// Pure decision logic for "did the requested action resolve?" — kept free of
/// game dependencies so it is unit-testable offline (spec §50/§51).
/// </summary>
public static class ActionResolution
{
    public static ActionOutcome Evaluate(
        CraftSnapshot baseline,
        CraftSnapshot? current,
        bool isCrafting,
        TimeSpan elapsed,
        TimeSpan timeout,
        bool advancesStep = true)
    {
        if (!isCrafting || current == null)
            return ActionOutcome.CraftEnded;

        if (advancesStep)
        {
            if (current.Step > baseline.Step)
                return ActionOutcome.StepAdvanced;
        }
        else if (!CraftSnapshot.BuffsEqual(baseline.Buffs, current.Buffs))
        {
            // Specialist actions (Heart and Soul, Quick Innovation) do not
            // advance the step counter; their buff appearing is the signal.
            return ActionOutcome.StepAdvanced;
        }

        return elapsed >= timeout ? ActionOutcome.TimedOut : ActionOutcome.Pending;
    }
}
