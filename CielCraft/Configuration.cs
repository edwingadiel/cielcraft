using System;
using Dalamud.Configuration;

namespace CielCraft;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool OpenMainWindowOnLogin { get; set; } = false;

    /// <summary>Allow intentional deviations from the Raphael plan based on live craft state (spec §15/§16).</summary>
    public bool AdaptiveCrafting { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
