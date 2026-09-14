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

    /// <summary>Use quick synthesis for intermediate production steps (NQ output).</summary>
    public bool QuickSynthIntermediates { get; set; } = true;

    /// <summary>Spend GP on yield and integrity actions while gathering (spec §37).</summary>
    public bool UseGatheringBuffs { get; set; } = true;

    /// <summary>Drink cordials between nodes when GP is low.</summary>
    public bool UseCordials { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
