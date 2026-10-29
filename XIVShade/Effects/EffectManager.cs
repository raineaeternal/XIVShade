using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using TerraFX.Interop.DirectX;

namespace XIVShade.Effects;

/// <summary>One .fx file found in the shaders folder.</summary>
public sealed class EffectEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }

    /// <summary>Translated effect, for listing its techniques; null if it failed to translate.</summary>
    public FxEffectDescription? Description { get; set; }

    /// <summary>Why the effect could not be translated or built.</summary>
    public string? Error { get; set; }

    /// <summary>The effect's GPU objects, present while any of its techniques is enabled.</summary>
    public EffectInstance? Instance { get; set; }
}

/// <summary>
/// Finds effects in the shaders folder, builds the ones the preset enables on a background thread, and runs them
/// over the frame in preset order.
/// </summary>
public sealed unsafe class EffectManager : IDisposable
{
    private readonly record struct RunItem(EffectInstance Instance, FxTechniqueInfo Technique);

    private readonly ID3D11Device* device;
    private readonly ITextureProvider textureProvider;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly string presetPath;
    private readonly object buildLock = new();
    private readonly List<EffectInstance> retired = new();

    private volatile RunItem[] runItems = Array.Empty<RunItem>();
    private volatile bool stopped;
    private (uint Width, uint Height) bufferSize;
    private DateTime? dirtySince;

    public EffectManager(ID3D11Device* device, ITextureProvider textureProvider, IPluginLog log, Configuration configuration, string configDirectory)
    {
        this.device = device;
        this.textureProvider = textureProvider;
        this.log = log;
        this.configuration = configuration;
        presetPath = Path.Combine(configDirectory, "XIVShade.ini");
        Preset = FxPreset.Load(presetPath);
        Directory.CreateDirectory(ShadersDirectory);
        Directory.CreateDirectory(TexturesDirectory);
    }

    public FxPreset Preset { get; }

    public string Root => string.IsNullOrWhiteSpace(configuration.ShaderRoot)
        ? Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "reshade-shaders")
        : configuration.ShaderRoot;

    public string ShadersDirectory => Path.Combine(Root, "Shaders");
    public string TexturesDirectory => Path.Combine(Root, "Textures");

    /// <summary>Effects found in the last scan, sorted by name. Replaced as a whole.</summary>
    public IReadOnlyList<EffectEntry> Entries { get; private set; } = Array.Empty<EffectEntry>();

    public bool IsBusy { get; private set; }

    // ---- scanning and building (background) ----

    public void Rescan() => Run(() =>
    {
        Directory.CreateDirectory(ShadersDirectory);
        var entries = new List<EffectEntry>();
        foreach (var file in Directory.EnumerateFiles(ShadersDirectory, "*.fx", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var entry = new EffectEntry { Path = file, Name = System.IO.Path.GetRelativePath(ShadersDirectory, file) };
            Translate(entry, bufferSize);
            entries.Add(entry);
        }

        Entries = entries;
        Build();
    });

    /// <summary>Rebuilds whatever the preset enables, for example after a technique was toggled.</summary>
    public void Refresh() => Run(Build);

    private void Run(Action action)
    {
        IsBusy = true;
        Task.Run(() =>
        {
            try
            {
                lock (buildLock)
                {
                    if (!stopped)
                        action();
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "Effect loading failed");
            }
            finally
            {
                IsBusy = false;
            }
        });
    }

    private Dictionary<string, string> Definitions((uint Width, uint Height) size)
    {
        var width = Math.Max(1u, size.Width);
        var height = Math.Max(1u, size.Height);
        var definitions = new Dictionary<string, string>
        {
            ["__RESHADE__"] = "60800",
            ["__RENDERER__"] = "0xb000",
            ["__VENDOR__"] = "0",
            ["__APPLICATION__"] = "0",
            ["__RESHADE_PERFORMANCE_MODE__"] = "0",
            ["BUFFER_WIDTH"] = width.ToString(),
            ["BUFFER_HEIGHT"] = height.ToString(),
            ["BUFFER_RCP_WIDTH"] = $"(1.0 / {width})",
            ["BUFFER_RCP_HEIGHT"] = $"(1.0 / {height})",
            ["BUFFER_COLOR_BIT_DEPTH"] = "8",
        };

        foreach (var definition in Preset.PreprocessorDefinitions)
        {
            var equals = definition.IndexOf('=');
            if (equals > 0)
                definitions[definition[..equals]] = definition[(equals + 1)..];
        }

        return definitions;
    }

    private FxEffectDescription TranslateFile(string path, (uint Width, uint Height) size)
    {
        var preprocessor = new FxPreprocessor(new[] { ShadersDirectory }, Definitions(size));
        return FxTranslator.Translate(preprocessor.Process(path));
    }

    private void Translate(EffectEntry entry, (uint Width, uint Height) size)
    {
        try
        {
            entry.Description = TranslateFile(entry.Path, size);
            entry.Error = null;
        }
        catch (Exception ex)
        {
            entry.Description = null;
            entry.Error = ex.Message;
        }
    }

    /// <summary>Creates instances for effects with enabled techniques, drops the others, and publishes the run list.</summary>
    private void Build()
    {
        var size = bufferSize;
        foreach (var entry in Entries)
        {
            var wanted = entry.Description != null && entry.Description.Techniques.Any(t => Preset.IsEnabled(t.Name, entry.Path));
            if (!wanted)
            {
                Retire(entry);
                continue;
            }

            if (entry.Instance != null)
                continue;

            if (size.Width == 0)
                continue; // the frame size isn't known yet; Run asks again once it is

            try
            {
                // Translate again: BUFFER_WIDTH and friends are baked into the generated code.
                var description = TranslateFile(entry.Path, size);
                entry.Description = description;
                var instance = EffectInstance.Create(device, entry.Path, description, size.Width, size.Height, LoadImage);
                ApplyPresetValues(entry, instance);
                entry.Instance = instance;
                entry.Error = null;
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
                log.Warning($"{entry.Name}: {ex.Message}");
            }
        }

        var items = new List<RunItem>();
        foreach (var key in Preset.Techniques)
        {
            var at = key.LastIndexOf('@');
            if (at < 0)
                continue;

            var technique = key[..at];
            var file = key[(at + 1)..];
            var entry = Entries.FirstOrDefault(e => string.Equals(System.IO.Path.GetFileName(e.Path), file, StringComparison.OrdinalIgnoreCase) && e.Instance != null);
            var match = entry?.Instance?.Description.Techniques.FirstOrDefault(t => t.Name == technique);
            if (entry?.Instance != null && match != null)
                items.Add(new RunItem(entry.Instance, match));
        }

        runItems = items.ToArray();
        FlushRetired();
    }

    private void ApplyPresetValues(EffectEntry entry, EffectInstance instance)
    {
        if (!Preset.Values.TryGetValue(System.IO.Path.GetFileName(entry.Path), out var saved))
            return;

        foreach (var uniform in instance.Description.Uniforms)
        {
            if (saved.TryGetValue(uniform.Name, out var text) && FxPreset.TryParse(uniform, text, out var values))
                instance.Values[uniform.Name] = values;
        }
    }

    private void Retire(EffectEntry entry)
    {
        if (entry.Instance == null)
            return;

        // The render thread may still be using it this frame; free it a little later.
        retired.Add(entry.Instance);
        entry.Instance = null;
    }

    private void FlushRetired()
    {
        if (retired.Count == 0)
            return;

        var old = retired.ToList();
        retired.Clear();
        Task.Delay(1000).ContinueWith(_ =>
        {
            foreach (var instance in old)
                instance.Dispose();
        });
    }

    private EffectInstance.LoadedImage? LoadImage(string source)
    {
        var path = System.IO.Path.Combine(TexturesDirectory, source);
        if (!File.Exists(path))
            return null;

        try
        {
            var wrap = textureProvider.GetFromFile(path).RentAsync().GetAwaiter().GetResult();
            return new EffectInstance.LoadedImage((nint)wrap.Handle.Handle, wrap, (uint)wrap.Width, (uint)wrap.Height);
        }
        catch (Exception ex)
        {
            log.Warning($"Texture {source}: {ex.Message}");
            return null;
        }
    }

    // ---- UI-side changes ----

    public void SetTechniqueEnabled(EffectEntry entry, FxTechniqueInfo technique, bool enabled)
    {
        var key = FxPreset.Key(technique.Name, entry.Path);
        Preset.Techniques.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (enabled)
            Preset.Techniques.Add(key);

        MarkDirty();
        Refresh();
    }

    /// <summary>Call after changing a uniform's value so the preset gets saved.</summary>
    public void NotifyUniformChanged(EffectEntry entry, FxUniformInfo uniform)
    {
        if (entry.Instance == null)
            return;

        var file = System.IO.Path.GetFileName(entry.Path);
        if (!Preset.Values.TryGetValue(file, out var values))
            Preset.Values[file] = values = new Dictionary<string, string>();
        values[uniform.Name] = FxPreset.Format(uniform, entry.Instance.Values[uniform.Name]);
        MarkDirty();
    }

    public void ResetUniform(EffectEntry entry, FxUniformInfo uniform)
    {
        if (entry.Instance == null)
            return;

        entry.Instance.Values[uniform.Name] = uniform.Default.ToArray();
        NotifyUniformChanged(entry, uniform);
    }

    private void MarkDirty() => dirtySince = DateTime.UtcNow;

    /// <summary>Writes the preset once changes have settled.</summary>
    public void SaveIfDue(bool force = false)
    {
        if (dirtySince is not { } since || (!force && DateTime.UtcNow - since < TimeSpan.FromSeconds(1)))
            return;

        dirtySince = null;
        try
        {
            Preset.Save(presetPath);
        }
        catch (Exception ex)
        {
            log.Warning($"Could not save preset: {ex.Message}");
        }
    }

    // ---- rendering (render thread) ----

    /// <summary>
    /// Tells the manager the size of the frame about to be processed. BUFFER_WIDTH and the sizes of effect textures
    /// depend on it, so everything is rebuilt when it changes. Returns whether there is anything to run.
    /// </summary>
    public bool PrepareFrame(uint width, uint height)
    {
        if (stopped)
            return false;

        if (bufferSize != (width, height))
        {
            bufferSize = (width, height);
            runItems = Array.Empty<RunItem>();
            Run(() =>
            {
                foreach (var entry in Entries)
                    Retire(entry);
                Build();
            });
            return false;
        }

        return runItems.Length > 0;
    }

    /// <summary>Runs the enabled techniques over the frame, in order.</summary>
    public void Render(ref FrameInputs frame)
    {
        if (stopped)
            return;

        foreach (var item in runItems)
        {
            // Each technique starts from the image the previous one left behind.
            frame.Context->CopyResource((ID3D11Resource*)frame.ColorCopy, (ID3D11Resource*)frame.Target);
            try
            {
                item.Instance.Run(item.Technique, in frame);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"{item.Instance.FileName}: {item.Technique.Name} failed, disabling it");
                var key = FxPreset.Key(item.Technique.Name, item.Instance.FileName);
                Preset.Techniques.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                runItems = runItems.Where(r => r != item).ToArray();
            }
        }
    }

    public void Dispose()
    {
        stopped = true;
        SaveIfDue(true);
        lock (buildLock)
        {
            runItems = Array.Empty<RunItem>();
            foreach (var entry in Entries)
            {
                entry.Instance?.Dispose();
                entry.Instance = null;
            }

            foreach (var instance in retired)
                instance.Dispose();
            retired.Clear();
        }
    }
}
