using System;
using System.Collections.Generic;

namespace CielCraft.Core;

/// <summary>
/// Common shape of the automation state machines (production runner, batch
/// crafter, craft automator, gathering loop, gathering controller): a state,
/// a status line, logged transitions, a per-frame tick that never lets an
/// exception escape, and a Describe() for the diagnostic report. Review
/// follow-up under roadmap 5.1: one base instead of five copies.
/// </summary>
public abstract class AutomationMachine<TState> where TState : struct, Enum
{
    protected AutomationMachine(ILog log, IClock clock, string logPrefix, TState initialState, string initialStatus)
    {
        Log = log;
        Clock = clock;
        LogPrefix = logPrefix;
        State = initialState;
        StatusText = initialStatus;
    }

    public TState State { get; private set; }

    public string StatusText { get; protected set; }

    protected ILog Log { get; }

    protected IClock Clock { get; }

    /// <summary>The "[Gather]" / "[Production]" tag every transition logs with.</summary>
    protected string LogPrefix { get; }

    /// <summary>Drive one frame. Exceptions are logged (rate-limited) and swallowed so one bad frame never kills the run.</summary>
    public void Tick()
    {
        try
        {
            OnTick();
        }
        catch (Exception e)
        {
            Log.TickError(GetType().Name, e);
        }
    }

    protected abstract void OnTick();

    /// <summary>Change state and status, logging the status line.</summary>
    protected void Transition(TState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Log.Information($"{LogPrefix} {statusText}");
    }

    /// <summary>Change state and status without a log line (high-frequency progress text).</summary>
    protected void SetState(TState state, string statusText)
    {
        State = state;
        StatusText = statusText;
    }

    /// <summary>Internal state for the diagnostic report; the first line is always "State X — status".</summary>
    public virtual IEnumerable<string> Describe()
    {
        yield return $"State {State} — {StatusText}";
    }
}
