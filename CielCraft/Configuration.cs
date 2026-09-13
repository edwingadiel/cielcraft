using System;
using Dalamud.Configuration;

namespace CielCraft;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool OpenMainWindowOnLogin { get; set; } = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
