using System;
using System.Collections.Generic;
using System.Numerics;
using CielCraft.Core;
using CielCraft.Game;

namespace CielCraft.Npc;

/// <summary>
/// Goes to an NPC and drives its dialog (roadmap 7.3): teleport when the NPC
/// is in another zone, travel with the shared <see cref="TravelDriver"/>, find
/// the NPC in the object table by its data id, interact, then walk the dialog
/// script step by step — one action per step, each confirmed by an observed
/// change, with a per-step timeout and the travel layer's interference rule
/// (the character moving on its own means the user took over). The script
/// normally ends on a <see cref="WaitForAddon"/>: the caller then operates that
/// window and calls <see cref="Stop"/>, which closes whatever dialog is left.
/// </summary>
public sealed class NpcInteractor : AutomationMachine<NpcInteractionState>, INpcInteractor
{
    /// <summary>Teleport cast + loading screen, as in the production runner.</summary>
    private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(90);

    /// <summary>One travel leg to the NPC; long enough for a walk across a zone without flight.</summary>
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Dismount, target, interact, first dialog frame.</summary>
    private static readonly TimeSpan InteractTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Every dialog step gets its own budget (m3: 10 s per step).</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A freshly opened window is not clicked the same frame it appears
    /// (Pacing): the node window's settle delay is the same kind of wait.
    /// </summary>
    private static readonly TimeSpan AfterWindowOpen = Pacing.AfterNodeOpen;

    /// <summary>Close enough to talk to an NPC; the travel driver walks the last stretch.</summary>
    internal const float InteractRange = 3f;

    /// <summary>Beyond this the NPC stands somewhere else than the sheet said: approach it again.</summary>
    private const float ReapproachRange = 6f;

    private const int MaxReapproaches = 2;
    private const int MaxTeleportAttempts = 3;

    /// <summary>The character moving this far while it should stand still means a person is at the keyboard (spec §49).</summary>
    private const float InterferenceRange = 3f;

    private static readonly string[] MenuAddons = ["SelectString", "SelectIconString"];

    private readonly IGameBridge gameBridge;
    private readonly INavigationProvider navigation;
    private readonly Func<CharacterCapabilities> capabilities;
    private readonly Func<uint, string> territoryName;
    private readonly TravelDriver travel;
    private readonly Throttle attempts;
    private readonly Throttle talkGate;

    private NpcTarget? target;
    private IReadOnlyList<DialogStep> script = [];
    private string description = "";
    private int stepIndex;
    private bool stepFired;
    private DateTime phaseStartedAt;
    private DateTime stepStartedAt;
    private DateTime windowSeenAt = DateTime.MinValue;
    private DateTime zoneArrivedAt = DateTime.MinValue;
    private bool sawLoadingScreen;
    private int teleportAttempts;
    private int reapproaches;
    private ulong npcObjectId;
    private Vector3? interferenceAnchor;
    private NpcInteractionState pausedFrom = NpcInteractionState.Idle;

    public NpcInteractor(
        IGameBridge gameBridge,
        INavigationProvider navigation,
        ILog log,
        IClock clock,
        Func<CharacterCapabilities>? capabilities = null,
        Func<uint, string>? territoryName = null)
        : base(log, clock, "[Npc]", NpcInteractionState.Idle, "Idle.")
    {
        this.gameBridge = gameBridge;
        this.navigation = navigation;
        this.capabilities = capabilities ?? (() => CharacterCapabilities.Unknown);
        this.territoryName = territoryName ?? (id => $"zone {id}");
        travel = new TravelDriver(navigation, gameBridge, clock, log, "[Npc]");
        attempts = new Throttle(clock, RetryInterval);
        talkGate = new Throttle(clock, RetryInterval);
    }

    /// <summary>Why the last interaction failed; empty otherwise.</summary>
    public string FailureReason { get; private set; } = "";

    /// <summary>The NPC of the current (or last) interaction; null before the first.</summary>
    public NpcTarget? Target => target;

    /// <summary>An interaction is in flight.</summary>
    public bool IsBusy => State is NpcInteractionState.Teleporting or NpcInteractionState.Traveling
        or NpcInteractionState.Interacting or NpcInteractionState.InDialog or NpcInteractionState.Paused;

    public bool Start(NpcTarget npcTarget, IReadOnlyList<DialogStep> dialogScript, string what)
    {
        if (IsBusy)
            return false;

        target = npcTarget;
        script = dialogScript;
        description = what;
        FailureReason = "";
        stepIndex = 0;
        stepFired = false;
        teleportAttempts = 0;
        reapproaches = 0;
        npcObjectId = 0;
        sawLoadingScreen = false;
        zoneArrivedAt = DateTime.MinValue;
        interferenceAnchor = null;

        if (gameBridge.CurrentTerritoryId != npcTarget.TerritoryId)
        {
            EnterPhase(
                NpcInteractionState.Teleporting,
                $"Teleporting to {territoryName(npcTarget.TerritoryId)} for {what}.");
            return true;
        }

        StartApproach();
        return true;
    }

    protected override void OnTick()
    {
        // Manual movement while the character should be standing at the NPC
        // means the user has taken over (spec §49), exactly as in the runner.
        if (State is NpcInteractionState.Interacting or NpcInteractionState.InDialog
            && !gameBridge.IsBetweenAreas && !navigation.IsMoving)
        {
            var position = gameBridge.PlayerPosition;
            if (position != null)
            {
                if (interferenceAnchor is { } anchor && Vector3.Distance(anchor, position.Value) > InterferenceRange)
                {
                    interferenceAnchor = null;
                    Pause("manual movement detected");
                    return;
                }

                interferenceAnchor ??= position;
            }
        }
        else
        {
            interferenceAnchor = null;
        }

        switch (State)
        {
            case NpcInteractionState.Teleporting:
                TickTeleporting();
                break;
            case NpcInteractionState.Traveling:
                TickTraveling();
                break;
            case NpcInteractionState.Interacting:
                TickInteracting();
                break;
            case NpcInteractionState.InDialog:
                TickDialog();
                break;
        }
    }

    private void TickTeleporting()
    {
        if (target == null)
            return;

        if (Clock.UtcNow - phaseStartedAt > TeleportTimeout)
        {
            Fail("the teleport did not complete (cast interrupted or loading took too long)");
            return;
        }

        if (gameBridge.IsBetweenAreas)
        {
            sawLoadingScreen = true;
            zoneArrivedAt = DateTime.MinValue;
            return;
        }

        if (sawLoadingScreen && gameBridge.CurrentTerritoryId == target.TerritoryId && gameBridge.PlayerPosition != null)
        {
            // Let the zone settle before the next server-visible action (pacing).
            if (zoneArrivedAt == DateTime.MinValue)
                zoneArrivedAt = Clock.UtcNow;
            if (Clock.UtcNow - zoneArrivedAt < Pacing.AfterZoneChange)
                return;

            StartApproach();
            return;
        }

        // Telepo refuses the cast while casting, in combat or mid-animation:
        // a few tries, then the zone is simply not attuned.
        attempts.Try(() =>
        {
            if (gameBridge.TeleportToTerritory(target.TerritoryId))
                return;

            teleportAttempts++;
            if (teleportAttempts >= MaxTeleportAttempts)
                Fail($"no attuned aetheryte in {territoryName(target.TerritoryId)}");
        });
    }

    private void StartApproach()
    {
        if (target == null)
            return;

        // The object table beats the sheet when the NPC is already loaded.
        var live = gameBridge.FindNpcObject(target.DataId);
        var destination = live?.Position ?? target.Position;
        var fly = capabilities().CanFlyIn(gameBridge.CurrentTerritoryId);
        travel.Start(destination, InteractRange, fly, preciseArrival: true, TravelTimeout, target.Name);
        EnterPhase(NpcInteractionState.Traveling, $"Moving to {target.Name} for {description}.");
    }

    private void TickTraveling()
    {
        travel.Tick();
        switch (travel.State)
        {
            case TravelState.Arrived:
                EnterPhase(NpcInteractionState.Interacting, $"Arrived at {target?.Name}; interacting.");
                break;
            case TravelState.Failed:
                Fail(travel.FailureReason);
                break;
            default:
                StatusText = travel.StatusText;
                break;
        }
    }

    private void TickInteracting()
    {
        if (target == null)
            return;

        // A target without a data id is a spot to reach, not an NPC to talk
        // to (a summoning bell, 7.17): the caller interacts, the script waits.
        if (target.DataId == 0)
        {
            EnterPhase(NpcInteractionState.InDialog, $"At {target.Name}; the caller takes it from here.");
            stepStartedAt = Clock.UtcNow;
            windowSeenAt = DateTime.MinValue;
            return;
        }

        // The dialog may already be up: some NPCs open their window straight
        // from the interaction, without a menu.
        if (AnyDialogOpen())
        {
            EnterPhase(NpcInteractionState.InDialog, $"{target.Name} responded; running the dialog.");
            stepStartedAt = Clock.UtcNow;
            windowSeenAt = DateTime.MinValue;
            return;
        }

        if (Clock.UtcNow - phaseStartedAt > InteractTimeout)
        {
            Fail(npcObjectId == 0
                ? $"{target.Name} (data id {target.DataId}) is not in the object table here"
                : $"{target.Name} did not respond to the interaction");
            return;
        }

        // Talking is a dismounted business, and the game refuses the
        // interaction from the saddle of a flying mount.
        if (gameBridge.IsMounted)
        {
            attempts.Try(gameBridge.TryDismount);
            StatusText = $"Dismounting at {target.Name}...";
            return;
        }

        var found = gameBridge.FindNpcObject(target.DataId);
        if (found == null)
        {
            StatusText = $"Looking for {target.Name}...";
            return;
        }

        var position = gameBridge.PlayerPosition;
        if (position != null && Vector3.Distance(position.Value, found.Value.Position) > ReapproachRange
            && reapproaches < MaxReapproaches)
        {
            // The sheet put the NPC where it belongs, the object table says
            // where it actually stands: walk the difference.
            reapproaches++;
            Log.Information($"[Npc] {target.Name} stands {Vector3.Distance(position.Value, found.Value.Position):F0}y from the approach point; walking up to it.");
            travel.Start(found.Value.Position, InteractRange, fly: false, preciseArrival: true, TravelTimeout, target.Name);
            EnterPhase(NpcInteractionState.Traveling, $"Walking up to {target.Name}.");
            return;
        }

        // A beat between arriving and clicking, as at a gathering node.
        if (Clock.UtcNow - phaseStartedAt < Pacing.BeforeInteract)
            return;

        npcObjectId = found.Value.ObjectId;
        attempts.Try(() => gameBridge.InteractWithObject(npcObjectId));
        StatusText = $"Talking to {target.Name}...";
    }

    private void TickDialog()
    {
        SkipToOpenWindow();
        if (stepIndex >= script.Count)
        {
            Complete();
            return;
        }

        switch (script[stepIndex])
        {
            case SelectOption option:
                TickSelectOption(option);
                break;
            case AdvanceTalk:
                TickAdvanceTalk();
                break;
            case Confirm confirm:
                TickConfirm(confirm);
                break;
            case WaitForAddon wait:
                TickWaitForAddon(wait);
                break;
            default:
                Fail($"unknown dialog step {script[stepIndex].GetType().Name}");
                break;
        }
    }

    /// <summary>
    /// A later <see cref="WaitForAddon"/> whose window is already up means the
    /// steps before it never happened — a mender with nothing but repairs to
    /// offer opens the Repair window straight from the interaction instead of
    /// showing a menu. Jump to it rather than time out on a menu that will
    /// never appear.
    /// </summary>
    private void SkipToOpenWindow()
    {
        for (var i = script.Count - 1; i > stepIndex; i--)
        {
            if (script[i] is not WaitForAddon ahead || !gameBridge.IsAddonVisible(ahead.AddonName))
                continue;

            Log.Information($"[Npc] {ahead.AddonName} is already open; skipping {i - stepIndex} dialog step(s).");
            stepIndex = i;
            stepFired = false;
            stepStartedAt = Clock.UtcNow;
            windowSeenAt = DateTime.MinValue;
            attempts.Reset();
            return;
        }
    }

    private void TickSelectOption(SelectOption option)
    {
        if (!MenuOpen())
        {
            if (stepFired)
            {
                // The menu closed after the click: that is the confirmation.
                AdvanceStep();
                return;
            }

            AutoAdvanceTalk();
            StatusText = $"Waiting for the option \"{option.TextContains}\"...";
            CheckStepTimeout($"no dialog menu offered an option matching \"{option.TextContains}\"");
            return;
        }

        if (!Settled())
            return;

        attempts.Try(() =>
        {
            if (gameBridge.SelectDialogOption(option.TextContains))
            {
                stepFired = true;
                Log.Information($"[Npc] Chose \"{option.TextContains}\" at {target?.Name}.");
            }
        });

        CheckStepTimeout(
            $"no option matching \"{option.TextContains}\" in [{string.Join(", ", gameBridge.ReadDialogOptions())}]");
    }

    private void TickAdvanceTalk()
    {
        if (!gameBridge.IsAddonVisible("Talk"))
        {
            if (stepFired)
            {
                AdvanceStep();
                return;
            }

            CheckStepTimeout("no talk box appeared to advance");
            return;
        }

        if (!Settled())
            return;

        attempts.Try(() =>
        {
            if (gameBridge.AdvanceTalk())
                stepFired = true;
        });

        // A talk box that stays up after the click is a talk chain: one
        // advance is one step, so move on once the click landed.
        if (stepFired)
        {
            AdvanceStep();
            return;
        }

        CheckStepTimeout("the talk box did not advance");
    }

    private void TickConfirm(Confirm confirm)
    {
        if (!gameBridge.IsAddonVisible("SelectYesno"))
        {
            if (stepFired)
            {
                AdvanceStep();
                return;
            }

            AutoAdvanceTalk();
            StatusText = "Waiting for the confirmation...";
            CheckStepTimeout("no confirmation dialog appeared");
            return;
        }

        if (!Settled())
            return;

        attempts.Try(() =>
        {
            if (gameBridge.FireAddonCallbackInt("SelectYesno", confirm.Yes ? 0 : 1))
                stepFired = true;
        });

        CheckStepTimeout($"the confirmation did not respond to {(confirm.Yes ? "Yes" : "No")}");
    }

    private void TickWaitForAddon(WaitForAddon wait)
    {
        if (!gameBridge.IsAddonVisible(wait.AddonName))
        {
            AutoAdvanceTalk();
            StatusText = $"Waiting for {wait.AddonName}...";
            CheckStepTimeout($"{wait.AddonName} did not open");
            return;
        }

        // The caller gets a window that has had a moment to populate.
        if (!Settled())
            return;

        AdvanceStep();
    }

    /// <summary>
    /// NPCs talk before they offer anything: while a step waits for its own
    /// window, a Talk box in the way is clicked through.
    /// </summary>
    private void AutoAdvanceTalk()
    {
        if (gameBridge.IsAddonVisible("Talk"))
            talkGate.Try(() => gameBridge.AdvanceTalk());
    }

    private bool MenuOpen()
    {
        foreach (var addon in MenuAddons)
        {
            if (gameBridge.IsAddonVisible(addon))
                return true;
        }

        return false;
    }

    private bool AnyDialogOpen()
    {
        if (MenuOpen() || gameBridge.IsAddonVisible("Talk") || gameBridge.IsAddonVisible("SelectYesno"))
            return true;

        // The window the script waits for counts as a response too.
        foreach (var step in script)
        {
            if (step is WaitForAddon wait && gameBridge.IsAddonVisible(wait.AddonName))
                return true;
        }

        return false;
    }

    /// <summary>A window is not clicked on the frame it appears; it gets the pacing beat first.</summary>
    private bool Settled()
    {
        if (windowSeenAt == DateTime.MinValue)
        {
            windowSeenAt = Clock.UtcNow;
            return false;
        }

        return Clock.UtcNow - windowSeenAt >= AfterWindowOpen;
    }

    private void AdvanceStep()
    {
        stepIndex++;
        stepFired = false;
        stepStartedAt = Clock.UtcNow;
        windowSeenAt = DateTime.MinValue;
        attempts.Reset();
        if (stepIndex >= script.Count)
            Complete();
    }

    private void CheckStepTimeout(string reason)
    {
        if (Clock.UtcNow - stepStartedAt > StepTimeout)
            Fail(reason);
    }

    private void Complete()
    {
        Transition(NpcInteractionState.Completed, $"{Capitalize(description)}: ready.");
    }

    private void EnterPhase(NpcInteractionState next, string statusText)
    {
        phaseStartedAt = Clock.UtcNow;
        stepStartedAt = Clock.UtcNow;
        windowSeenAt = DateTime.MinValue;
        attempts.Reset();
        talkGate.Reset();
        Transition(next, statusText);
    }

    private void Fail(string reason)
    {
        FailureReason = reason;
        travel.Stop();
        navigation.Stop();
        // Never leave a dialog open behind a failure: an open menu blocks
        // movement, gearset swaps and the crafting log downstream.
        CloseDialogs();
        Transition(NpcInteractionState.Failed, $"Failed: {reason}.");
    }

    public void Pause(string reason)
    {
        if (!IsBusy || State == NpcInteractionState.Paused)
            return;

        pausedFrom = State;
        travel.Stop();
        navigation.Stop();
        Transition(NpcInteractionState.Paused, $"Paused: {reason}.");
    }

    public void Resume()
    {
        if (State != NpcInteractionState.Paused)
            return;

        switch (pausedFrom)
        {
            case NpcInteractionState.Traveling:
                StartApproach();
                break;
            case NpcInteractionState.Teleporting:
                EnterPhase(NpcInteractionState.Teleporting, $"Resuming: teleporting to {territoryName(target?.TerritoryId ?? 0)}.");
                break;
            case NpcInteractionState.Interacting:
                EnterPhase(NpcInteractionState.Interacting, $"Resuming: talking to {target?.Name}.");
                break;
            case NpcInteractionState.InDialog:
                EnterPhase(NpcInteractionState.InDialog, $"Resuming: {description}.");
                break;
            default:
                Transition(NpcInteractionState.Idle, "Idle.");
                break;
        }
    }

    public void Stop()
    {
        travel.Stop();
        navigation.Stop();
        CloseDialogs();
        if (State != NpcInteractionState.Idle)
            Transition(NpcInteractionState.Idle, "Idle.");
    }

    /// <summary>Dismisses whatever the interaction left open: a confirmation (No), an option menu, a talk box stays.</summary>
    private void CloseDialogs()
    {
        gameBridge.FireAddonCallbackInt("SelectYesno", 1);
        gameBridge.FireAddonCallbackInt("SelectString", -1);
        gameBridge.FireAddonCallbackInt("SelectIconString", -1);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public override IEnumerable<string> Describe()
    {
        foreach (var line in base.Describe())
            yield return line;

        yield return target == null
            ? "No target."
            : $"Target {target.Name} (npc {target.NpcId}, data {target.DataId}) in {territoryName(target.TerritoryId)} at {target.Position.X:F1}, {target.Position.Y:F1}, {target.Position.Z:F1}; object {npcObjectId}; reapproaches {reapproaches}; teleport attempts {teleportAttempts}";
        yield return $"Script [{string.Join(" → ", DescribeScript())}] at step {Math.Min(stepIndex + 1, script.Count)}/{script.Count}{(stepFired ? " (fired)" : "")}; phase since {phaseStartedAt:HH:mm:ss}Z; step since {stepStartedAt:HH:mm:ss}Z; window seen {(windowSeenAt == DateTime.MinValue ? "-" : windowSeenAt.ToString("HH:mm:ss") + "Z")}; failure: {(FailureReason.Length == 0 ? "-" : FailureReason)}";
        foreach (var line in travel.Describe())
            yield return line;
    }

    private IEnumerable<string> DescribeScript()
    {
        foreach (var step in script)
        {
            yield return step switch
            {
                SelectOption option => $"option \"{option.TextContains}\"",
                AdvanceTalk => "talk",
                Confirm confirm => confirm.Yes ? "yes" : "no",
                WaitForAddon wait => $"wait {wait.AddonName}",
                _ => step.GetType().Name,
            };
        }
    }
}
