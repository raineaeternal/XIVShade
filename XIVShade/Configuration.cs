using Dalamud.Configuration;
using System;
using System.Collections.Generic;

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

    /// <summary>Name of the preset in use; presets are the files in the plugin's Presets folder.</summary>
    public string ActivePreset { get; set; } = "Default";

    public List<HotkeyBinding> Hotkeys { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

public enum HotkeyAction
{
    ToggleEffects,
    ToggleIncludeUi,
    NextPreset,
    PreviousPreset,

    /// <summary>Switches to the preset named in <see cref="HotkeyBinding.Argument"/>.</summary>
    SetPreset,

    /// <summary>Toggles the technique <see cref="HotkeyBinding.Argument"/> (as <c>Technique@File.fx</c>).</summary>
    ToggleTechnique,
}

[Serializable]
public class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public string Argument { get; set; } = "";

    /// <summary>A Windows virtual key code; 0 while unbound.</summary>
    public int Key { get; set; }

    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
}
