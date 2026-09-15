using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace CielCraft.Infrastructure;

/// <summary>
/// The one Framework.Update subscription. Ticks the automation layers in a
/// fixed order every frame (monitor before executor before automator before
/// batch before runner — the order the layers used to subscribe in), so the
/// layers themselves no longer depend on Dalamud (roadmap 5.1).
/// </summary>
public sealed class FrameworkDriver : IDisposable
{
    private readonly IFramework framework;
    // Copy-on-write: a tick registered while a frame is being ticked (a panel
    // built after the plugin subscribed) must not break the enumeration.
    private Action[] ticks = [];

    public FrameworkDriver(IFramework framework)
    {
        this.framework = framework;
        framework.Update += OnUpdate;
    }

    /// <summary>Register a per-frame tick; order of registration is order of execution.</summary>
    public void Add(Action tick) => ticks = [.. ticks, tick];

    private void OnUpdate(IFramework _)
    {
        // Each machine's Tick already catches and rate-limits its own exceptions.
        foreach (var tick in ticks)
            tick();
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        ticks = [];
    }
}
