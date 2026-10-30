using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XIVShade.Effects;

namespace XIVShade.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly PostProcessor? postProcessor;
    private readonly Hotkeys? hotkeys;
    private string rootInput;
    private string filter = "";
    private string newPresetName = "";
    private string presetMessage = "";

    public MainWindow(Configuration configuration, PostProcessor? postProcessor, Hotkeys? hotkeys)
        : base("XIVShade###XIVShadeMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.configuration = configuration;
        this.postProcessor = postProcessor;
        this.hotkeys = hotkeys;
        rootInput = postProcessor?.Effects.Root ?? "";
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (postProcessor == null)
        {
            ImGui.Text("Post-processing unavailable, see /xllog.");
            return;
        }

        var effects = postProcessor.Effects;
        effects.SaveIfDue();

        var enabled = postProcessor.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            postProcessor.Enabled = enabled;
            configuration.Enabled = enabled;
            configuration.Save();
        }

        ImGui.SameLine();
        var includeUi = configuration.IncludeUi;
        if (ImGui.Checkbox("Include UI", ref includeUi))
        {
            configuration.IncludeUi = includeUi;
            postProcessor.IncludeUi = includeUi;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Apply effects after the UI is drawn. Off: only the 3D scene is affected.");

        DrawPresetBar(effects);

        if (!ImGui.BeginTabBar("##tabs"))
            return;

        if (ImGui.BeginTabItem("Effects"))
        {
            DrawEffects(effects);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettings(effects);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Hotkeys"))
        {
            DrawHotkeys(effects);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Folder"))
        {
            DrawFolder(effects);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // ---- effects ----

    private void DrawEffects(EffectManager effects)
    {
        if (effects.Entries.Count == 0)
        {
            ImGui.TextWrapped(effects.IsBusy
                ? "Scanning for effects..."
                : $"No effects found in {effects.ShadersDirectory}. Put ReShade .fx files there, or point to a folder on the Folder tab.");
            return;
        }

        DrawOrder(effects);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter", ref filter, 128);

        if (!ImGui.BeginChild("##effectlist", Vector2.Zero, false))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var entry in effects.Entries)
        {
            if (filter.Length > 0 && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Description == null)
            {
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), entry.Name);
                if (ImGui.IsItemHovered() && entry.Error != null)
                    ImGui.SetTooltip(entry.Error);
                continue;
            }

            foreach (var technique in entry.Description.Techniques)
            {
                var on = effects.Preset.IsEnabled(technique.Name, entry.Path);
                if (ImGui.Checkbox($"{technique.Label}##{entry.Name}:{technique.Name}", ref on))
                    effects.SetTechniqueEnabled(entry, technique, on);

                ImGui.SameLine();
                ImGui.TextDisabled(entry.Name);
                if (technique.Annotations.TryGetValue("ui_tooltip", out var tooltip) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(tooltip);
            }

            if (entry.Error != null && entry.Instance == null)
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"  {entry.Error.Split('\n')[0]}");
        }

        ImGui.EndChild();
    }

    /// <summary>The enabled techniques in the order they run, with buttons to move them.</summary>
    private static void DrawOrder(EffectManager effects)
    {
        var order = effects.Preset.TechniqueSnapshot();
        if (order.Length < 2 || !ImGui.CollapsingHeader($"Run order ({order.Length})"))
            return;

        for (var i = 0; i < order.Length; i++)
        {
            var key = order[i];
            ImGui.PushID(key);
            if (ImGui.SmallButton("Up") && i > 0)
                effects.MoveTechnique(key, -1);
            ImGui.SameLine();
            if (ImGui.SmallButton("Down") && i < order.Length - 1)
                effects.MoveTechnique(key, 1);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}. {key.Replace("@", "  (")})");
            ImGui.PopID();
        }

        ImGui.Separator();
    }

    // ---- presets ----

    private void DrawPresetBar(EffectManager effects)
    {
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("##preset", effects.ActivePreset))
        {
            foreach (var name in effects.PresetNames)
            {
                if (ImGui.Selectable(name, string.Equals(name, effects.ActivePreset, StringComparison.OrdinalIgnoreCase)))
                    effects.SwitchPreset(name);
            }

            if (effects.InstalledPresets.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextDisabled("Installed (select to import)");
                foreach (var installed in effects.InstalledPresets)
                {
                    if (ImGui.Selectable($"{installed.Name}##{installed.Path}"))
                        effects.ImportPreset(installed);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(installed.Path);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("New..."))
        {
            presetMessage = "";
            ImGui.OpenPopup("##newpreset");
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete"))
            ImGui.OpenPopup("##deletepreset");

        if (ImGui.BeginPopup("##newpreset"))
        {
            ImGui.TextUnformatted("Preset name");
            ImGui.SetNextItemWidth(220);
            ImGui.InputText("##newname", ref newPresetName, 64);
            var created = false;
            if (ImGui.Button("Create empty"))
                created = Create(effects, false);
            ImGui.SameLine();
            if (ImGui.Button("Copy current"))
                created = Create(effects, true);
            if (created)
                ImGui.CloseCurrentPopup();

            if (presetMessage.Length > 0)
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), presetMessage);
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("##deletepreset"))
        {
            ImGui.TextUnformatted($"Delete preset '{effects.ActivePreset}'?");
            if (ImGui.Button("Delete"))
            {
                effects.DeletePreset(effects.ActivePreset);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private bool Create(EffectManager effects, bool copy)
    {
        if (effects.CreatePreset(newPresetName, copy))
        {
            newPresetName = "";
            return true;
        }

        presetMessage = "Enter a name that is not already used.";
        return false;
    }

    // ---- hotkeys ----

    private void DrawHotkeys(EffectManager effects)
    {
        if (hotkeys == null)
        {
            ImGui.TextUnformatted("Hotkeys are unavailable.");
            return;
        }

        var techniques = effects.Entries
            .Where(e => e.Description != null)
            .SelectMany(e => e.Description!.Techniques.Select(t => (Key: FxPreset.Key(t.Name, e.Path), Label: $"{t.Label}  ({e.Name})")))
            .ToList();

        var remove = (HotkeyBinding?)null;
        foreach (var binding in configuration.Hotkeys)
        {
            ImGui.PushID(binding.GetHashCode());

            var action = (int)binding.Action;
            var actions = Enum.GetNames<HotkeyAction>();
            ImGui.SetNextItemWidth(150);
            if (ImGui.Combo("##action", ref action, actions, actions.Length))
            {
                binding.Action = (HotkeyAction)action;
                binding.Argument = "";
                configuration.Save();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(220);
            if (binding.Action == HotkeyAction.SetPreset)
            {
                if (ImGui.BeginCombo("##argument", binding.Argument.Length > 0 ? binding.Argument : "Choose preset"))
                {
                    foreach (var name in effects.PresetNames)
                    {
                        if (ImGui.Selectable(name, name == binding.Argument))
                        {
                            binding.Argument = name;
                            configuration.Save();
                        }
                    }

                    ImGui.EndCombo();
                }
            }
            else if (binding.Action == HotkeyAction.ToggleTechnique)
            {
                var current = techniques.FirstOrDefault(t => string.Equals(t.Key, binding.Argument, StringComparison.OrdinalIgnoreCase)).Label ?? "Choose technique";
                if (ImGui.BeginCombo("##argument", current))
                {
                    foreach (var (key, label) in techniques)
                    {
                        if (ImGui.Selectable(label, string.Equals(key, binding.Argument, StringComparison.OrdinalIgnoreCase)))
                        {
                            binding.Argument = key;
                            configuration.Save();
                        }
                    }

                    ImGui.EndCombo();
                }
            }
            else
            {
                ImGui.Dummy(new Vector2(220, 1));
            }

            ImGui.SameLine();
            var capturing = hotkeys.Capturing == binding;
            if (ImGui.Button(capturing ? "Press a key (Esc cancels)" : Hotkeys.Describe(binding), new Vector2(190, 0)))
                hotkeys.BeginCapture(binding, _ => configuration.Save());

            ImGui.SameLine();
            if (ImGui.Button("Remove"))
                remove = binding;

            ImGui.PopID();
        }

        if (remove != null)
        {
            hotkeys.CancelCapture();
            configuration.Hotkeys.Remove(remove);
            configuration.Save();
        }

        if (ImGui.Button("Add hotkey"))
        {
            configuration.Hotkeys.Add(new HotkeyBinding());
            configuration.Save();
        }

        ImGui.TextDisabled("Hotkeys do not fire while you are typing in chat or in a text box.");
    }

    // ---- uniforms ----

    private void DrawSettings(EffectManager effects)
    {
        var active = effects.Entries.Where(e => e.Instance != null).ToList();
        if (active.Count == 0)
        {
            ImGui.TextWrapped("Enable a technique on the Effects tab to adjust its settings here.");
            return;
        }

        if (!ImGui.BeginChild("##settings", Vector2.Zero, false))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var entry in active)
        {
            if (!ImGui.CollapsingHeader(entry.Name, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.PushID(entry.Name);
            foreach (var uniform in entry.Instance!.Description.Uniforms)
            {
                // Uniforms fed by the engine (timers and the like), and ones the effect hides, have no control.
                if (uniform.Source != null || uniform.Annotations.GetValueOrDefault("ui_type") == "none")
                    continue;

                if (DrawUniform(entry.Instance, uniform))
                    effects.NotifyUniformChanged(entry, uniform);

                if (ImGui.BeginPopupContextItem($"##reset{uniform.Name}"))
                {
                    if (ImGui.MenuItem("Reset to default"))
                        effects.ResetUniform(entry, uniform);
                    ImGui.EndPopup();
                }
            }

            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private static double Number(FxUniformInfo uniform, string key, double fallback) =>
        uniform.Annotations.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool DrawUniform(EffectInstance instance, FxUniformInfo uniform)
    {
        var values = instance.Values[uniform.Name];
        var label = $"{uniform.Label}##{uniform.Name}";
        var uiType = uniform.Annotations.GetValueOrDefault("ui_type") ?? "";
        var hasRange = uniform.Annotations.ContainsKey("ui_min") && uniform.Annotations.ContainsKey("ui_max");
        var min = Number(uniform, "ui_min", 0);
        var max = Number(uniform, "ui_max", 1);
        var step = Number(uniform, "ui_step", 0);
        var changed = false;

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
        if (uniform.IsArray || uniform.IsMatrix)
        {
            return DrawRows(uniform, values, hasRange, min, max);
        }

        if (uniform.Kind == "bool")
        {
            var value = values[0] != 0;
            if (changed = ImGui.Checkbox(label, ref value))
                values[0] = value ? 1 : 0;
        }
        else if (uniform.Kind == "float")
        {
            switch (uniform.Components)
            {
                case 1:
                {
                    var value = (float)values[0];
                    var speed = step > 0 ? (float)step : (float)((max - min) / 200);
                    changed = hasRange && uiType != "drag"
                        ? ImGui.SliderFloat(label, ref value, (float)min, (float)max)
                        : ImGui.DragFloat(label, ref value, speed <= 0 ? 0.01f : speed, hasRange ? (float)min : 0, hasRange ? (float)max : 0);
                    if (changed)
                        values[0] = value;
                    break;
                }
                case 2:
                {
                    var value = new Vector2((float)values[0], (float)values[1]);
                    if (changed = hasRange ? ImGui.SliderFloat2(label, ref value, (float)min, (float)max) : ImGui.DragFloat2(label, ref value, 0.01f))
                    {
                        values[0] = value.X;
                        values[1] = value.Y;
                    }

                    break;
                }
                case 3:
                {
                    var value = new Vector3((float)values[0], (float)values[1], (float)values[2]);
                    if (changed = uiType == "color" ? ImGui.ColorEdit3(label, ref value) : hasRange ? ImGui.SliderFloat3(label, ref value, (float)min, (float)max) : ImGui.DragFloat3(label, ref value, 0.01f))
                    {
                        values[0] = value.X;
                        values[1] = value.Y;
                        values[2] = value.Z;
                    }

                    break;
                }
                default:
                {
                    var value = new Vector4((float)values[0], (float)values[1], (float)values[2], (float)values[3]);
                    if (changed = uiType == "color" ? ImGui.ColorEdit4(label, ref value) : hasRange ? ImGui.SliderFloat4(label, ref value, (float)min, (float)max) : ImGui.DragFloat4(label, ref value, 0.01f))
                    {
                        values[0] = value.X;
                        values[1] = value.Y;
                        values[2] = value.Z;
                        values[3] = value.W;
                    }

                    break;
                }
            }
        }
        else if (uniform.Components == 1)
        {
            var value = (int)values[0];
            var items = uniform.Annotations.GetValueOrDefault("ui_items");
            if (items != null && uiType is "combo" or "list" or "radio" or "")
            {
                // Items are separated by a literal \0 in the source.
                var names = items.Split("\\0", StringSplitOptions.RemoveEmptyEntries);
                if (names.Length > 0)
                {
                    if (changed = ImGui.Combo(label, ref value, names, names.Length))
                        values[0] = value;
                    return Tooltip(uniform, changed);
                }
            }

            if (changed = hasRange && uiType != "drag" ? ImGui.SliderInt(label, ref value, (int)min, (int)max) : ImGui.DragInt(label, ref value, step > 0 ? (float)step : 1, hasRange ? (int)min : 0, hasRange ? (int)max : 0))
                values[0] = value;
        }
        else
        {
            ImGui.TextDisabled($"{uniform.Label} ({uniform.Type})");
        }

        return Tooltip(uniform, changed);
    }

    /// <summary>Array and matrix uniforms: one row of controls per array element or matrix row.</summary>
    private static bool DrawRows(FxUniformInfo uniform, double[] values, bool hasRange, double min, double max)
    {
        var changed = false;
        ImGui.TextUnformatted(uniform.Label);
        var rowCount = uniform.ArrayLength * uniform.Rows;
        for (var row = 0; row < rowCount; row++)
        {
            var offset = row * uniform.Columns;
            var label = $"##{uniform.Name}{row}";
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
            if (uniform.Kind == "float")
            {
                var v = new float[] { (float)values[offset], uniform.Columns > 1 ? (float)values[offset + 1] : 0, uniform.Columns > 2 ? (float)values[offset + 2] : 0, uniform.Columns > 3 ? (float)values[offset + 3] : 0 };
                var edited = uniform.Columns switch
                {
                    1 => hasRange ? ImGui.SliderFloat(label, ref v[0], (float)min, (float)max) : ImGui.DragFloat(label, ref v[0], 0.01f),
                    2 => RowVector2(label, v, hasRange, min, max),
                    3 => RowVector3(label, v, hasRange, min, max),
                    _ => RowVector4(label, v, hasRange, min, max),
                };

                if (edited)
                {
                    for (var c = 0; c < uniform.Columns; c++)
                        values[offset + c] = v[c];
                    changed = true;
                }
            }
            else
            {
                for (var c = 0; c < uniform.Columns; c++)
                {
                    if (c > 0)
                        ImGui.SameLine();
                    ImGui.SetNextItemWidth(60);
                    if (uniform.Kind == "bool")
                    {
                        var flag = values[offset + c] != 0;
                        if (ImGui.Checkbox($"{label}{c}", ref flag))
                        {
                            values[offset + c] = flag ? 1 : 0;
                            changed = true;
                        }
                    }
                    else
                    {
                        var v = (int)values[offset + c];
                        if (ImGui.DragInt($"{label}{c}", ref v))
                        {
                            values[offset + c] = v;
                            changed = true;
                        }
                    }
                }
            }
        }

        return changed;
    }

    private static bool RowVector2(string label, float[] v, bool hasRange, double min, double max)
    {
        var value = new Vector2(v[0], v[1]);
        var edited = hasRange ? ImGui.SliderFloat2(label, ref value, (float)min, (float)max) : ImGui.DragFloat2(label, ref value, 0.01f);
        v[0] = value.X;
        v[1] = value.Y;
        return edited;
    }

    private static bool RowVector3(string label, float[] v, bool hasRange, double min, double max)
    {
        var value = new Vector3(v[0], v[1], v[2]);
        var edited = hasRange ? ImGui.SliderFloat3(label, ref value, (float)min, (float)max) : ImGui.DragFloat3(label, ref value, 0.01f);
        v[0] = value.X;
        v[1] = value.Y;
        v[2] = value.Z;
        return edited;
    }

    private static bool RowVector4(string label, float[] v, bool hasRange, double min, double max)
    {
        var value = new Vector4(v[0], v[1], v[2], v[3]);
        var edited = hasRange ? ImGui.SliderFloat4(label, ref value, (float)min, (float)max) : ImGui.DragFloat4(label, ref value, 0.01f);
        v[0] = value.X;
        v[1] = value.Y;
        v[2] = value.Z;
        v[3] = value.W;
        return edited;
    }

    private static bool Tooltip(FxUniformInfo uniform, bool changed)
    {
        if (ImGui.IsItemHovered() && uniform.Annotations.TryGetValue("ui_tooltip", out var tooltip))
            ImGui.SetTooltip(tooltip);
        return changed;
    }

    // ---- folder ----

    private void DrawFolder(EffectManager effects)
    {
        ImGui.TextWrapped("A ReShade shader repository: a folder containing Shaders and Textures directories.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##root", ref rootInput, 512);

        if (ImGui.Button("Use this folder"))
        {
            configuration.ShaderRoot = rootInput.Trim();
            configuration.Save();
            effects.Rescan();
        }

        ImGui.SameLine();
        if (ImGui.Button("Rescan"))
            effects.Rescan();

        ImGui.SameLine();
        if (ImGui.Button("Default"))
        {
            configuration.ShaderRoot = "";
            configuration.Save();
            rootInput = effects.Root;
            effects.Rescan();
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"Shaders:  {effects.ShadersDirectory}");
        ImGui.TextDisabled($"Textures: {effects.TexturesDirectory}");
        ImGui.TextDisabled($"Found {effects.Entries.Count} effect files and {effects.InstalledPresets.Count} installed presets{(effects.IsBusy ? " (working...)" : "")}.");
    }
}
