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
    private string rootInput;
    private string filter = "";

    public MainWindow(Configuration configuration, PostProcessor? postProcessor)
        : base("XIVShade###XIVShadeMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.configuration = configuration;
        this.postProcessor = postProcessor;
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
        ImGui.TextDisabled($"Found {effects.Entries.Count} effect files{(effects.IsBusy ? " (working...)" : "")}.");
    }
}
