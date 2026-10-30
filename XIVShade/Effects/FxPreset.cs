using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace XIVShade.Effects;

/// <summary>A ReShade-style preset: the ordered list of enabled techniques and uniform values per effect file.</summary>
public sealed class FxPreset
{
    /// <summary>Enabled techniques in execution order, as <c>Technique@File.fx</c>.</summary>
    public List<string> Techniques { get; } = new();

    /// <summary>Global preprocessor definitions, as <c>NAME=value</c> entries.</summary>
    public List<string> PreprocessorDefinitions { get; } = new();

    /// <summary>Uniform values by effect file name, then uniform name, as written in the file.</summary>
    public Dictionary<string, Dictionary<string, string>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Held while changing or enumerating the preset: the UI, build and render threads all touch it.</summary>
    public object Sync { get; } = new();

    /// <summary>A copy of <see cref="Techniques"/> that is safe to enumerate.</summary>
    public string[] TechniqueSnapshot()
    {
        lock (Sync)
            return Techniques.ToArray();
    }

    public static string Key(string technique, string file) => technique + "@" + Path.GetFileName(file);

    public bool IsEnabled(string technique, string file)
    {
        lock (Sync)
            return Techniques.Contains(Key(technique, file), StringComparer.OrdinalIgnoreCase);
    }

    public static FxPreset Load(string path)
    {
        var preset = new FxPreset();
        if (!File.Exists(path))
            return preset;

        Dictionary<string, string>? section = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = new Dictionary<string, string>(StringComparer.Ordinal);
                preset.Values[line[1..^1]] = section;
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 0)
                continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (section != null)
                section[key] = value;
            else if (key == "Techniques")
                preset.Techniques.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else if (key == "PreprocessorDefinitions")
                preset.PreprocessorDefinitions.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return preset;
    }

    public void Save(string path)
    {
        string text;
        lock (Sync)
            text = Serialize();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, true);
    }

    private string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("PreprocessorDefinitions=").Append(string.Join(',', PreprocessorDefinitions)).Append('\n');
        sb.Append("Techniques=").Append(string.Join(',', Techniques)).Append('\n');
        sb.Append("TechniqueSorting=").Append(string.Join(',', Techniques)).Append('\n');
        foreach (var (file, values) in Values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (values.Count == 0)
                continue;

            sb.Append('\n').Append('[').Append(file).Append("]\n");
            foreach (var (name, value) in values)
                sb.Append(name).Append('=').Append(value).Append('\n');
        }

        return sb.ToString();
    }

    public static string Format(FxUniformInfo uniform, double[] values) =>
        string.Join(',', values.Take(uniform.Count).Select(v => uniform.Kind switch
        {
            "float" => ((float)v).ToString("R", CultureInfo.InvariantCulture),
            "bool" => v != 0 ? "1" : "0",
            _ => ((long)v).ToString(CultureInfo.InvariantCulture),
        }));

    public static bool TryParse(FxUniformInfo uniform, string text, out double[] values)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        values = new double[uniform.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var part = i < parts.Length ? parts[i] : parts[^1];
            if (part.Equals("true", StringComparison.OrdinalIgnoreCase))
                values[i] = 1;
            else if (part.Equals("false", StringComparison.OrdinalIgnoreCase))
                values[i] = 0;
            else if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        return true;
    }
}
