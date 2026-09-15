using System;
using System.Collections.Generic;
using CielCraft.Core;

namespace CielCraft.Combat;

/// <summary>
/// The Rotation Solver Reborn calls a driver needs, as a seam so the driver's
/// bookkeeping is testable without Dalamud. <see cref="CombatPluginIpc"/> binds
/// it to the real call gates; every method there is already fail-soft.
/// </summary>
public interface IRotationSolverIpc
{
    /// <summary>The plugin is installed and loaded (Dalamud's own plugin list).</summary>
    bool IsLoaded { get; }

    /// <summary>"RotationSolverReborn.AutorotationActive" — null when the call gate did not answer.</summary>
    bool? AutorotationActive();

    /// <summary>"RotationSolverReborn.ChangeOperatingMode" with a <see cref="RotationSolverStates"/> value.</summary>
    bool ChangeOperatingMode(int state);

    /// <summary>"RotationSolverReborn.AddPriorityNameID" / "RemovePriorityNameID".</summary>
    bool SetPriorityNameId(uint bnpcNameId, bool add);
}

/// <summary>
/// The BossMod Reborn calls a driver needs. <see cref="CombatPluginIpc"/> binds
/// it to the real call gates.
/// </summary>
public interface IBossModIpc
{
    bool IsLoaded { get; }

    /// <summary>
    /// "BossMod.Presets.GetActive" — the active autorotation preset's name,
    /// empty when none is active, and null only when the call gate did not
    /// answer at all (so null is the "plugin is not there" signal).
    /// </summary>
    string? GetActivePreset();

    /// <summary>"BossMod.Presets.SetActive" — false when the preset is unknown or the call gate did not answer.</summary>
    bool SetActivePreset(string name);

    /// <summary>"BossMod.Presets.ClearActive".</summary>
    bool ClearActivePreset();

    /// <summary>"BossMod.Rotation.ActionQueue.HasEntries" — null when the call gate did not answer.</summary>
    bool? HasQueuedActions();
}

/// <summary>
/// Rotation Solver Reborn's operating modes. Read from the plugin's own
/// <c>StateCommandType</c> (RotationSolver.Basic/Data/RSCommandType.cs, a
/// <c>byte</c> enum) on 2026-09-15; they travel over the call gate as plain
/// numbers because Dalamud converts every IPC argument through JSON when the
/// declared types differ (Dalamud CallGateChannel.ConvertObject).
/// </summary>
public static class RotationSolverStates
{
    public const int Off = 0;
    public const int Auto = 1;
    public const int TargetOnly = 2;
    public const int Manual = 3;
    public const int AutoDuty = 4;
    public const int Henched = 5;
    public const int PvP = 6;
}

/// <summary>Shared Engage / Disengage bookkeeping: one engage per kill, always safe to call twice.</summary>
public abstract class CombatDriverBase : ICombatDriver
{
    private readonly ILog log;

    protected CombatDriverBase(ILog log) => this.log = log;

    public abstract CombatDriverKind Kind { get; }

    public abstract string Name { get; }

    public abstract bool IsAvailable { get; }

    /// <summary>The driver asked the plugin to fight and has not asked it to stop again.</summary>
    public bool IsEngaged { get; private set; }

    /// <summary>How many engages this session; the panel and the report show it.</summary>
    public int Engagements { get; private set; }

    /// <summary>Why the last Engage / Disengage did nothing; empty when all is well.</summary>
    public string LastProblem { get; private set; } = "";

    public void Engage()
    {
        if (IsEngaged)
            return;

        if (!IsAvailable)
        {
            Fail($"{Name} is not answering; nothing was engaged.");
            return;
        }

        if (!TryEngage())
        {
            Fail($"{Name} refused to start the rotation.");
            return;
        }

        IsEngaged = true;
        Engagements++;
        LastProblem = "";
        log.Information($"[Combat] {Name} engaged (#{Engagements}).");
    }

    public void Disengage()
    {
        // Always attempt the stop, engaged or not: a reload, a crash or a
        // manual /rotation leaves the plugin running and the character would
        // keep swinging. Only the log line is conditional.
        var wasEngaged = IsEngaged;
        IsEngaged = false;

        if (!IsAvailable)
        {
            if (wasEngaged)
                Fail($"{Name} stopped answering before it could be disengaged.");
            return;
        }

        if (!TryDisengage())
        {
            Fail($"{Name} refused to stop the rotation.");
            return;
        }

        LastProblem = "";
        if (wasEngaged)
            log.Information($"[Combat] {Name} disengaged.");
    }

    public abstract IEnumerable<string> Describe();

    /// <summary>Turn the rotation on. False when the call gate refused.</summary>
    protected abstract bool TryEngage();

    /// <summary>Turn the rotation off. False when the call gate refused.</summary>
    protected abstract bool TryDisengage();

    private void Fail(string message)
    {
        if (LastProblem != message)
            log.Warning("[Combat] " + message);
        LastProblem = message;
    }
}

/// <summary>
/// Rotation Solver Reborn over its <c>RotationSolverReborn.</c> call gates
/// (roadmap 7.5). CielCraft picks the target itself and only wants the
/// rotation run on it, so Engage asks for <see cref="RotationSolverStates.Manual"/>
/// — "you need to choose the target manually […] will start attacking
/// immediately once something is targeted" in the plugin's own description.
/// Auto would let RSR retarget, which breaks the hunt's level ceiling and the
/// "leave other players' mobs alone" rule; <see cref="EngageState"/> is public
/// so the coordinator can put it back to Auto if that is ever wanted.
/// Disengage asks for Off.
/// </summary>
public sealed class RotationSolverDriver : CombatDriverBase
{
    private readonly IRotationSolverIpc ipc;

    public RotationSolverDriver(IRotationSolverIpc ipc, ILog log)
        : base(log) => this.ipc = ipc;

    public override CombatDriverKind Kind => CombatDriverKind.RotationSolverReborn;

    public override string Name => "Rotation Solver Reborn";

    /// <summary>The mode Engage asks for; Manual by default (see the class remarks).</summary>
    public int EngageState { get; set; } = RotationSolverStates.Manual;

    public override bool IsAvailable => ipc.IsLoaded && ipc.AutorotationActive() != null;

    /// <summary>
    /// Tells RSR to prefer (or stop preferring) a BNpcName, so a stray add
    /// does not pull the rotation off the mob the hunt is counting. Harmless
    /// when the plugin is away.
    /// </summary>
    public bool PrioritizeMob(uint bnpcNameId, bool prioritize) =>
        bnpcNameId != 0 && IsAvailable && ipc.SetPriorityNameId(bnpcNameId, prioritize);

    protected override bool TryEngage() => ipc.ChangeOperatingMode(EngageState);

    protected override bool TryDisengage() => ipc.ChangeOperatingMode(RotationSolverStates.Off);

    public override IEnumerable<string> Describe()
    {
        yield return $"{Name}: {(ipc.IsLoaded ? "installed and loaded" : "not loaded")}";
        var active = ipc.AutorotationActive();
        yield return "  AutorotationActive(): " + (active == null ? "no answer (IPC missing)" : active.Value ? "yes" : "no");
        yield return $"  engage mode {EngageState} ({StateName(EngageState)}), engaged: {IsEngaged}, engagements this session: {Engagements}";
        if (LastProblem.Length > 0)
            yield return "  last problem: " + LastProblem;
    }

    private static string StateName(int state) => state switch
    {
        RotationSolverStates.Off => "Off",
        RotationSolverStates.Auto => "Auto",
        RotationSolverStates.TargetOnly => "TargetOnly",
        RotationSolverStates.Manual => "Manual",
        RotationSolverStates.AutoDuty => "AutoDuty",
        RotationSolverStates.Henched => "Henched",
        RotationSolverStates.PvP => "PvP",
        _ => "?",
    };
}

/// <summary>
/// BossMod Reborn over its <c>BossMod.</c> call gates (roadmap 7.5). Engage
/// activates an autorotation preset (<c>Presets.SetActive</c>), Disengage
/// clears it (<c>Presets.ClearActive</c>). The AI (<c>AI.SetPreset</c>) is
/// deliberately not used: it takes over movement, and CielCraft's travel
/// already drives the character through vnavmesh — two movers fight each
/// other. The default preset name is BMR's own bundled "VBM Default"
/// (BossMod/DefaultRotationPresets.json); any preset the user made can be
/// named instead.
/// </summary>
public sealed class BossModDriver : CombatDriverBase
{
    /// <summary>BossMod Reborn's bundled autorotation preset.</summary>
    public const string DefaultPreset = "VBM Default";

    private readonly IBossModIpc ipc;

    public BossModDriver(IBossModIpc ipc, ILog log, string? presetName = null)
        : base(log) => (this.ipc, PresetName) = (ipc, string.IsNullOrWhiteSpace(presetName) ? DefaultPreset : presetName!);

    public override CombatDriverKind Kind => CombatDriverKind.BossModReborn;

    public override string Name => "BossMod Reborn";

    /// <summary>The autorotation preset Engage activates.</summary>
    public string PresetName { get; set; }

    public override bool IsAvailable => ipc.IsLoaded && ipc.GetActivePreset() != null;

    protected override bool TryEngage() => ipc.SetActivePreset(PresetName);

    protected override bool TryDisengage()
    {
        // ClearActive answers false when nothing was active, which is the
        // state we wanted anyway, so the result that counts is what the plugin
        // reports afterwards: empty = cleared, null = the call gate went away.
        ipc.ClearActivePreset();
        var active = ipc.GetActivePreset();
        return active != null && active.Length == 0;
    }

    public override IEnumerable<string> Describe()
    {
        yield return $"{Name}: {(ipc.IsLoaded ? "installed and loaded" : "not loaded")}";
        var preset = ipc.GetActivePreset();
        yield return "  Presets.GetActive(): " + (preset == null ? "no answer (IPC missing)" : preset.Length == 0 ? "none" : preset);
        var queued = ipc.HasQueuedActions();
        yield return "  Rotation.ActionQueue.HasEntries(): " + (queued == null ? "no answer" : queued.Value ? "yes" : "no");
        yield return $"  engage preset \"{PresetName}\", engaged: {IsEngaged}, engagements this session: {Engagements}";
        if (LastProblem.Length > 0)
            yield return "  last problem: " + LastProblem;
    }
}

/// <summary>
/// What the selector hands out when no combat plugin is installed: never
/// available, never engaged, and every call is a no-op so the hunt source can
/// simply decline to offer instead of guarding at every call site.
/// </summary>
public sealed class NoCombatDriver : ICombatDriver
{
    public static readonly NoCombatDriver Instance = new();

    public CombatDriverKind Kind => CombatDriverKind.None;

    public string Name => "none";

    public bool IsAvailable => false;

    public bool IsEngaged => false;

    public void Engage()
    {
    }

    public void Disengage()
    {
    }

    public IEnumerable<string> Describe()
    {
        yield return "No combat plugin: install Rotation Solver Reborn or BossMod Reborn to enable hunting (roadmap 7.5).";
    }
}

/// <summary>
/// Picks the combat driver (roadmap 7.5): Rotation Solver Reborn first, then
/// BossMod Reborn, else <see cref="NoCombatDriver"/>. Availability is an IPC
/// round trip, so it is re-probed at most once every few seconds; a driver
/// that is engaged is never swapped out under a running fight.
/// </summary>
public sealed class CombatDriverSelector
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<ICombatDriver> candidates;
    private readonly ILog log;
    private readonly Throttle probe;

    private ICombatDriver current = NoCombatDriver.Instance;
    private CombatDriverKind lastReported = CombatDriverKind.None;
    private bool probed;

    public CombatDriverSelector(ILog log, IClock clock, params ICombatDriver[] candidates)
    {
        this.log = log;
        this.candidates = candidates;
        probe = new Throttle(clock, ProbeInterval);
    }

    /// <summary>The driver to fight with; <see cref="NoCombatDriver"/> when none answers.</summary>
    public ICombatDriver Current
    {
        get
        {
            Refresh(force: false);
            return current;
        }
    }

    /// <summary>A combat plugin is there and answering, so hunting can be offered.</summary>
    public bool IsAvailable => Current.IsAvailable;

    /// <summary>Re-probes now, whatever the throttle says (the panel's refresh button).</summary>
    public ICombatDriver Refresh()
    {
        Refresh(force: true);
        return current;
    }

    /// <summary>Stops whichever driver is engaged; the emergency stop and every run teardown call it.</summary>
    public void DisengageAll()
    {
        foreach (var driver in candidates)
        {
            try
            {
                driver.Disengage();
            }
            catch (Exception e)
            {
                log.Warning($"[Combat] {driver.Name} threw while disengaging: {e.Message}");
            }
        }
    }

    public IEnumerable<string> Describe()
    {
        Refresh(force: false);
        yield return $"Driver: {current.Name} ({current.Kind}), available: {current.IsAvailable}, engaged: {current.IsEngaged}";
        foreach (var driver in candidates)
        {
            foreach (var line in Safe(driver))
                yield return line;
        }

        if (candidates.Count == 0)
            yield return "No combat plugin adapters registered.";
    }

    private IEnumerable<string> Safe(ICombatDriver driver)
    {
        try
        {
            return new List<string>(driver.Describe());
        }
        catch (Exception e)
        {
            return [$"{driver.Name}: describing it threw ({e.Message})."];
        }
    }

    private void Refresh(bool force)
    {
        // A fight in progress keeps its driver: swapping plugins mid-kill
        // would leave the old one running.
        if (current.IsEngaged)
            return;

        if (force)
            probe.Reset();
        else if (probed && !probe.IsReady)
            return;

        probe.Touch();
        probed = true;

        var picked = NoCombatDriver.Instance as ICombatDriver;
        foreach (var driver in candidates)
        {
            try
            {
                if (!driver.IsAvailable)
                    continue;
            }
            catch (Exception e)
            {
                log.Warning($"[Combat] Probing {driver.Name} threw: {e.Message}");
                continue;
            }

            picked = driver;
            break;
        }

        current = picked;
        if (picked.Kind == lastReported)
            return;

        lastReported = picked.Kind;
        log.Information(picked.Kind == CombatDriverKind.None
            ? "[Combat] No combat plugin is answering; hunting stays off."
            : $"[Combat] Combat driver: {picked.Name}.");
    }
}
