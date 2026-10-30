using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using XIVShade.Effects;

namespace XIVShade;

/// <summary>Runs the configured key bindings, and captures a new key press for the settings UI.</summary>
public sealed unsafe class Hotkeys : IDisposable
{
    private static readonly HashSet<VirtualKey> Modifiers = new()
    {
        VirtualKey.CONTROL, VirtualKey.LCONTROL, VirtualKey.RCONTROL,
        VirtualKey.SHIFT, VirtualKey.LSHIFT, VirtualKey.RSHIFT,
        VirtualKey.MENU, VirtualKey.LMENU, VirtualKey.RMENU,
        VirtualKey.LBUTTON, VirtualKey.RBUTTON, VirtualKey.MBUTTON, VirtualKey.XBUTTON1, VirtualKey.XBUTTON2,
    };

    private readonly Configuration configuration;
    private readonly PostProcessor postProcessor;
    private readonly IFramework framework;
    private readonly IKeyState keyState;
    private readonly IToastGui toasts;
    private readonly HashSet<HotkeyBinding> held = new();
    private Action<HotkeyBinding>? capture;
    private HotkeyBinding? capturing;

    public Hotkeys(Configuration configuration, PostProcessor postProcessor, IFramework framework, IKeyState keyState, IToastGui toasts)
    {
        this.configuration = configuration;
        this.postProcessor = postProcessor;
        this.framework = framework;
        this.keyState = keyState;
        this.toasts = toasts;
        framework.Update += OnUpdate;
    }

    /// <summary>The binding currently waiting for a key press, if any.</summary>
    public HotkeyBinding? Capturing => capturing;

    /// <summary>The next key press (with the modifiers held) is stored into <paramref name="binding"/>. Escape cancels.</summary>
    public void BeginCapture(HotkeyBinding binding, Action<HotkeyBinding> done)
    {
        capturing = binding;
        capture = done;
    }

    public void CancelCapture()
    {
        capturing = null;
        capture = null;
    }

    public static string Describe(HotkeyBinding binding)
    {
        if (binding.Key == 0)
            return "Unbound";

        var parts = new List<string>();
        if (binding.Ctrl) parts.Add("Ctrl");
        if (binding.Shift) parts.Add("Shift");
        if (binding.Alt) parts.Add("Alt");
        parts.Add(((VirtualKey)binding.Key).GetFancyName());
        return string.Join("+", parts);
    }

    private bool TextInputActive()
    {
        if (ImGui.GetIO().WantTextInput)
            return true;

        var atk = RaptureAtkModule.Instance();
        return atk != null && atk->AtkModule.IsTextInputActive();
    }

    private void OnUpdate(IFramework _)
    {
        if (capturing != null)
        {
            PollCapture();
            return;
        }

        if (configuration.Hotkeys.Count == 0)
            return;

        if (TextInputActive())
        {
            held.Clear();
            return;
        }

        bool Down(VirtualKey key) => keyState[key];
        var ctrl = Down(VirtualKey.CONTROL);
        var shift = Down(VirtualKey.SHIFT);
        var alt = Down(VirtualKey.MENU);

        foreach (var binding in configuration.Hotkeys)
        {
            var pressed = binding.Key != 0 && Down((VirtualKey)binding.Key) && ctrl == binding.Ctrl && shift == binding.Shift && alt == binding.Alt;
            if (!pressed)
            {
                held.Remove(binding);
                continue;
            }

            // Only the transition from released to pressed triggers the action.
            if (held.Add(binding))
                Run(binding);
        }
    }

    private void PollCapture()
    {
        if (keyState[VirtualKey.ESCAPE])
        {
            keyState[VirtualKey.ESCAPE] = false;
            CancelCapture();
            return;
        }

        foreach (var key in keyState.GetValidVirtualKeys())
        {
            if (Modifiers.Contains(key) || !keyState[key])
                continue;

            var binding = capturing!;
            binding.Key = (int)key;
            binding.Ctrl = keyState[VirtualKey.CONTROL];
            binding.Shift = keyState[VirtualKey.SHIFT];
            binding.Alt = keyState[VirtualKey.MENU];

            // The press that bound it must not also fire it.
            held.Add(binding);
            var done = capture;
            CancelCapture();
            done?.Invoke(binding);
            return;
        }
    }

    private void Run(HotkeyBinding binding)
    {
        var effects = postProcessor.Effects;
        switch (binding.Action)
        {
            case HotkeyAction.ToggleEffects:
                postProcessor.Enabled = !postProcessor.Enabled;
                configuration.Enabled = postProcessor.Enabled;
                configuration.Save();
                toasts.ShowNormal($"XIVShade: effects {(postProcessor.Enabled ? "on" : "off")}");
                break;
            case HotkeyAction.ToggleIncludeUi:
                postProcessor.IncludeUi = !postProcessor.IncludeUi;
                configuration.IncludeUi = postProcessor.IncludeUi;
                configuration.Save();
                toasts.ShowNormal($"XIVShade: effects {(postProcessor.IncludeUi ? "include" : "exclude")} the UI");
                break;
            case HotkeyAction.NextPreset:
                toasts.ShowNormal($"XIVShade: preset {effects.StepPreset(1)}");
                break;
            case HotkeyAction.PreviousPreset:
                toasts.ShowNormal($"XIVShade: preset {effects.StepPreset(-1)}");
                break;
            case HotkeyAction.SetPreset:
                if (effects.PresetNames.Contains(binding.Argument))
                {
                    effects.SwitchPreset(binding.Argument);
                    toasts.ShowNormal($"XIVShade: preset {binding.Argument}");
                }

                break;
            case HotkeyAction.ToggleTechnique:
                ToggleTechnique(effects, binding.Argument);
                break;
        }
    }

    private void ToggleTechnique(EffectManager effects, string key)
    {
        var at = key.LastIndexOf('@');
        if (at < 0)
            return;

        var technique = key[..at];
        var file = key[(at + 1)..];
        var entry = effects.Entries.FirstOrDefault(e => string.Equals(System.IO.Path.GetFileName(e.Path), file, StringComparison.OrdinalIgnoreCase));
        var info = entry?.Description?.Techniques.FirstOrDefault(t => t.Name == technique);
        if (entry == null || info == null)
            return;

        var enable = !effects.Preset.IsEnabled(technique, entry.Path);
        effects.SetTechniqueEnabled(entry, info, enable);
        toasts.ShowNormal($"XIVShade: {info.Label} {(enable ? "on" : "off")}");
    }

    public void Dispose() => framework.Update -= OnUpdate;
}
