using System;
using System.Collections.Generic;
using CielCraft.Core;
using CielCraft.Fishing;
using CielCraft.Game;

namespace CielCraft.Sourcing;

/// <summary>
/// Fishing as a material source (roadmap 7.4, M3 contract): the production
/// runner asks for every raw material no MIN/BTN node yields, and this answers
/// for the ones that come out of the water. The offer stands only when the
/// database knows a hole and a bait, the character's Fisher level reaches the
/// hole, and the bait is in the bag — or a bait vendor (P2) can supply it, in
/// which case the run buys it first and then fishes.
/// </summary>
public sealed class FishingSource : IMaterialSource
{
    /// <summary>Rough seconds per fish for the schedule: a cast, a wait, a hook, a re-cast.</summary>
    private const int SecondsPerFish = 25;

    /// <summary>Bait bought in one go when the bag has none; a stack is 99 and bait is cheap.</summary>
    internal const int BaitPurchaseAmount = 99;

    private readonly IGameBridge bridge;
    private readonly FishingDatabase database;
    private readonly FishingController controller;
    private readonly IMaterialSource? baitVendor;
    private readonly ILog log;
    private readonly IClock clock;
    private readonly Func<uint, string> zoneName;

    public FishingSource(
        IGameBridge bridge,
        FishingDatabase database,
        FishingController controller,
        ILog log,
        IClock clock,
        IMaterialSource? baitVendor = null,
        Func<uint, string>? zoneName = null)
    {
        this.bridge = bridge;
        this.database = database;
        this.controller = controller;
        this.baitVendor = baitVendor;
        this.log = log;
        this.clock = clock;
        this.zoneName = zoneName ?? (id => $"zone {id}");
    }

    public MaterialSourceKind Kind => MaterialSourceKind.Fish;

    public string Name => "fishing";

    /// <summary>The fishing database's verdict on an item, for the planner's "is this gatherable" question.</summary>
    public bool IsFish(uint itemId) => database.IsFish(itemId);

    public SourceOffer? Offer(uint itemId, int amount)
    {
        if (amount < 1 || !database.IsFish(itemId))
            return null;

        var level = database.FisherLevel();
        var plan = database.FindSpot(itemId, bridge.CurrentTerritoryId, level > 0 ? level : int.MaxValue);
        if (plan == null)
        {
            // Say why once per ask: a silent "no" here looks like a planner bug
            // when the item plainly is a fish.
            if (database.RefusalReason(itemId) is { } reason)
                log.Information($"[Fishing] Not offering item {itemId}: {reason}.");
            return null;
        }

        if (!bridge.HasGearsetForJob(GatheringActions.FisherJobId))
        {
            log.Information($"[Fishing] Not offering {plan.FishName}: there is no FSH gearset.");
            return null;
        }

        var baitHeld = bridge.GetItemCount(plan.BaitItemId);
        var baitOffer = baitHeld > 0 ? null : baitVendor?.Offer(plan.BaitItemId, BaitPurchaseAmount);
        if (baitHeld == 0 && baitOffer == null)
        {
            log.Information(
                $"[Fishing] Not offering {plan.FishName}: no {plan.BaitName} in the bag and no vendor sells it.");
            return null;
        }

        var description = $"Fish {amount}× {plan.Describe(zoneName)}" +
                          (baitOffer == null ? "" : $"; buying {plan.BaitName} on the way");
        return new SourceOffer(
            itemId,
            amount,
            MaterialSourceKind.Fish,
            description,
            amount * SecondsPerFish + (baitOffer?.EstimatedSeconds ?? 0),
            baitOffer?.GilCost ?? 0);
    }

    public ISourceRun Start(SourceOffer offer)
    {
        var level = database.FisherLevel();
        var plan = database.FindSpot(offer.ItemId, bridge.CurrentTerritoryId, level > 0 ? level : int.MaxValue);
        SourceOffer? baitOffer = null;
        if (plan != null && bridge.GetItemCount(plan.BaitItemId) == 0)
            baitOffer = baitVendor?.Offer(plan.BaitItemId, BaitPurchaseAmount);

        return new FishingRun(bridge, controller, offer, plan, baitVendor, baitOffer, log, clock);
    }
}

/// <summary>
/// One "fish N of item X" job for the production runner (M3 `ISourceRun`):
/// optionally a bait run first (the P2 vendor source, chained), then the
/// fishing controller until the bag holds the amount. The run owns nothing
/// itself — travel, dialogs and verification belong to the two machines it
/// drives — and the bag count is the truth.
/// </summary>
public sealed class FishingRun : ISourceRun
{
    private readonly IGameBridge bridge;
    private readonly FishingController controller;
    private readonly SourceOffer offer;
    private readonly FishingPlan? plan;
    private readonly IMaterialSource? baitVendor;
    private readonly SourceOffer? baitOffer;
    private readonly ILog log;
    private readonly IClock clock;

    private ISourceRun? baitRun;
    private readonly int baseline;
    private readonly DateTime startedAt;
    private bool controllerStarted;
    private string pauseReason = "";

    internal FishingRun(
        IGameBridge bridge,
        FishingController controller,
        SourceOffer offer,
        FishingPlan? plan,
        IMaterialSource? baitVendor,
        SourceOffer? baitOffer,
        ILog log,
        IClock clock)
    {
        this.bridge = bridge;
        this.controller = controller;
        this.offer = offer;
        this.plan = plan;
        this.baitVendor = baitVendor;
        this.baitOffer = baitOffer;
        this.log = log;
        this.clock = clock;
        baseline = bridge.GetItemCount(offer.ItemId);
        startedAt = clock.UtcNow;

        if (plan == null)
        {
            State = SourceRunState.Failed;
            StatusText = $"No fishing hole is known for item {offer.ItemId} any more.";
            return;
        }

        State = SourceRunState.Running;
        StatusText = offer.Description;
    }

    public SourceRunState State { get; private set; }

    public string StatusText { get; private set; } = "";

    public int Obtained => Math.Max(0, bridge.GetItemCount(offer.ItemId) - baseline);

    public void Tick()
    {
        if (State != SourceRunState.Running || plan == null)
            return;

        // The bait run (P2 vendor) comes first: without bait there is nothing
        // to cast. It owns its own travel and verification.
        if (baitOffer != null && bridge.GetItemCount(plan.BaitItemId) == 0)
        {
            TickBaitRun();
            return;
        }

        if (Obtained >= offer.Amount)
        {
            Finish();
            return;
        }

        if (!controllerStarted)
        {
            if (!controller.Start(offer.ItemId, offer.Amount))
            {
                State = SourceRunState.Failed;
                StatusText = controller.StatusText;
                return;
            }

            controllerStarted = true;
        }

        controller.Tick();
        StatusText = controller.StatusText;
        switch (controller.State)
        {
            case FishingRunState.Completed:
                Finish();
                break;
            case FishingRunState.Failed:
                State = SourceRunState.Failed;
                StatusText = controller.StatusText;
                break;
            case FishingRunState.Paused:
                pauseReason = controller.FailureReason;
                State = SourceRunState.Paused;
                break;
            case FishingRunState.Idle:
                // The controller was stopped from outside (the user).
                State = SourceRunState.Idle;
                break;
        }
    }

    private void TickBaitRun()
    {
        if (baitRun == null)
        {
            baitRun = baitVendor!.Start(baitOffer!);
            log.Information($"[Fishing] No {plan!.BaitName} in the bag; {baitOffer!.Description} first.");
        }

        baitRun.Tick();
        StatusText = baitRun.StatusText;
        switch (baitRun.State)
        {
            case SourceRunState.Completed:
                log.Information($"[Fishing] Bait run finished with {bridge.GetItemCount(plan!.BaitItemId)}× {plan.BaitName}.");
                baitRun = null;
                break;
            case SourceRunState.Failed:
                State = SourceRunState.Failed;
                StatusText = $"The bait run failed ({baitRun.StatusText}).";
                break;
            case SourceRunState.Paused:
                pauseReason = baitRun.StatusText;
                State = SourceRunState.Paused;
                break;
        }
    }

    private void Finish()
    {
        controller.Stop();
        State = SourceRunState.Completed;
        StatusText = $"Fished {Obtained}/{offer.Amount} {plan?.FishName ?? "fish"}.";
    }

    public void Pause(string reason)
    {
        if (State != SourceRunState.Running)
            return;

        pauseReason = reason;
        baitRun?.Pause(reason);
        controller.Pause(reason);
        State = SourceRunState.Paused;
        StatusText = $"Paused: {reason}.";
    }

    public void Resume()
    {
        if (State != SourceRunState.Paused)
            return;

        baitRun?.Resume();
        controller.Resume();
        State = SourceRunState.Running;
        StatusText = offer.Description;
    }

    public void Stop()
    {
        baitRun?.Stop();
        if (controllerStarted)
            controller.Stop();

        if (State is SourceRunState.Running or SourceRunState.Paused)
        {
            State = SourceRunState.Idle;
            StatusText = $"Stopped at {Obtained}/{offer.Amount}.";
        }
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Fishing run {State} — {StatusText}";
        yield return $"Offer: {offer.Description} (item {offer.ItemId} ×{offer.Amount}); obtained {Obtained} " +
                     $"(baseline {baseline}); started {startedAt:HH:mm:ss}Z, now {clock.UtcNow:HH:mm:ss}Z";
        yield return plan == null
            ? "Plan: none"
            : $"Plan: {plan.FishName} at {plan.Spot.Name} with {plan.BaitName}" +
              $"; bait in bag {bridge.GetItemCount(plan.BaitItemId)}; bait run {(baitRun == null ? "-" : baitRun.State.ToString())}";
        if (pauseReason.Length > 0)
            yield return $"Last pause: {pauseReason}";

        foreach (var line in controller.Describe())
            yield return line;
    }
}
