using Dalamud.Configuration;
using System;

namespace XIVShade;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Apply effects after the UI is drawn, instead of to the scene only.</summary>
    public bool IncludeUi { get; set; }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
