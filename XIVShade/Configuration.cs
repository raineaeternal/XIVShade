using Dalamud.Configuration;
using System;

namespace XIVShade;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Master switch for all effects.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Apply effects after the UI is drawn, instead of to the scene only.</summary>
    public bool IncludeUi { get; set; }

    /// <summary>Folder holding the <c>Shaders</c> and <c>Textures</c> directories; empty for the default inside the plugin's config folder.</summary>
    public string ShaderRoot { get; set; } = "";

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
