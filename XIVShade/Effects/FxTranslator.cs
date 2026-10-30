using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace XIVShade.Effects;

/// <summary>
/// Translates preprocessed ReShade FX into plain HLSL for D3DCompile, and collects the effect's textures,
/// samplers, uniforms and techniques. Covers the common subset of FX: it works on global declarations and
/// leaves function bodies as they are apart from a few token rewrites.
/// </summary>
public sealed class FxTranslator
{
    private static readonly HashSet<string> Qualifiers = new()
    {
        "static", "const", "uniform", "extern", "precise", "volatile", "groupshared", "nointerpolation", "linear", "centroid",
    };

    private static readonly Dictionary<string, string> Intrinsics = new()
    {
        ["tex2D"] = "__tex2D",
        ["tex2Dlod"] = "__tex2Dlod",
        ["tex2Dbias"] = "__tex2Dbias",
        ["tex2Dgrad"] = "__tex2Dgrad",
        ["tex2Dfetch"] = "__tex2Dfetch",
        ["tex2Dsize"] = "__tex2Dsize",
        ["tex2Doffset"] = "__tex2Doffset",
        ["tex2Dlodoffset"] = "__tex2Dlodoffset",
        ["tex2Dgather"] = "__tex2Dgather",
        ["tex2Dgatheroffset"] = "__tex2Dgatheroffset",
        ["tex1D"] = "__tex1D",
        ["tex1Dlod"] = "__tex1Dlod",
        ["tex1Dfetch"] = "__tex1Dfetch",
        ["tex1Dsize"] = "__tex1Dsize",
        ["tex3D"] = "__tex3D",
        ["tex3Dlod"] = "__tex3Dlod",
        ["tex3Dfetch"] = "__tex3Dfetch",
        ["tex3Dsize"] = "__tex3Dsize",
        ["tex1Dstore"] = "__tex1Dstore",
        ["tex2Dstore"] = "__tex2Dstore",
        ["tex3Dstore"] = "__tex3Dstore",
    };

    /// <summary>Type names in parameter lists that become the prelude's wrapper types.</summary>
    private static readonly Dictionary<string, string> TypeRewrites = new()
    {
        ["sampler"] = "__sampler2D_float4",
        ["sampler2D"] = "__sampler2D_float4",
        ["sampler1D"] = "__sampler1D_float4",
        ["sampler3D"] = "__sampler3D_float4",
        ["storage"] = "RWTexture2D<float4>",
        ["storage2D"] = "RWTexture2D<float4>",
        ["storage1D"] = "RWTexture1D<float4>",
        ["storage3D"] = "RWTexture3D<float4>",
    };

    private const string Prelude = @"struct __sampler2D_float4 { Texture2D<float4> t; SamplerState s; };
float4 __tex2D(__sampler2D_float4 s, float2 uv) { return s.t.Sample(s.s, uv); }
float4 __tex2D(__sampler2D_float4 s, float2 uv, int2 o) { return s.t.Sample(s.s, uv, o); }
float4 __tex2Dlod(__sampler2D_float4 s, float4 uv) { return s.t.SampleLevel(s.s, uv.xy, uv.w); }
float4 __tex2Dlod(__sampler2D_float4 s, float4 uv, int2 o) { return s.t.SampleLevel(s.s, uv.xy, uv.w, o); }
float4 __tex2Dbias(__sampler2D_float4 s, float4 uv) { return s.t.SampleBias(s.s, uv.xy, uv.w); }
float4 __tex2Dgrad(__sampler2D_float4 s, float2 uv, float2 dx, float2 dy) { return s.t.SampleGrad(s.s, uv, dx, dy); }
float4 __tex2Dgrad(__sampler2D_float4 s, float2 uv, float2 dx, float2 dy, int2 o) { return s.t.SampleGrad(s.s, uv, dx, dy, o); }
float4 __tex2Dfetch(__sampler2D_float4 s, int2 c) { return s.t.Load(int3(c, 0)); }
float4 __tex2Dfetch(__sampler2D_float4 s, int2 c, int lod) { return s.t.Load(int3(c, lod)); }
float4 __tex2Dfetch(__sampler2D_float4 s, int4 c) { return s.t.Load(int3(c.xy, c.w)); }
int2 __tex2Dsize(__sampler2D_float4 s, int lod) { uint w, h, l; s.t.GetDimensions(lod, w, h, l); return int2(w, h); }
int2 __tex2Dsize(__sampler2D_float4 s) { uint w, h, l; s.t.GetDimensions(0, w, h, l); return int2(w, h); }
float4 __tex2Doffset(__sampler2D_float4 s, float2 uv, int2 o) { return s.t.Sample(s.s, uv, o); }
float4 __tex2Dlodoffset(__sampler2D_float4 s, float4 uv, int2 o) { return s.t.SampleLevel(s.s, uv.xy, uv.w, o); }
float4 __tex2Dgather(__sampler2D_float4 s, float2 uv, int comp) { return comp == 0 ? s.t.GatherRed(s.s, uv) : comp == 1 ? s.t.GatherGreen(s.s, uv) : comp == 2 ? s.t.GatherBlue(s.s, uv) : s.t.GatherAlpha(s.s, uv); }
float4 __tex2Dgatheroffset(__sampler2D_float4 s, float2 uv, int2 o, int comp) { return comp == 0 ? s.t.GatherRed(s.s, uv, o) : comp == 1 ? s.t.GatherGreen(s.s, uv, o) : comp == 2 ? s.t.GatherBlue(s.s, uv, o) : s.t.GatherAlpha(s.s, uv, o); }
struct __sampler1D_float4 { Texture1D<float4> t; SamplerState s; };
struct __sampler3D_float4 { Texture3D<float4> t; SamplerState s; };
float4 __tex1D(__sampler1D_float4 s, float u) { return s.t.Sample(s.s, u); }
float4 __tex1D(__sampler1D_float4 s, float u, int o) { return s.t.Sample(s.s, u, o); }
float4 __tex1Dlod(__sampler1D_float4 s, float4 uv) { return s.t.SampleLevel(s.s, uv.x, uv.w); }
float4 __tex1Dlod(__sampler1D_float4 s, float4 uv, int o) { return s.t.SampleLevel(s.s, uv.x, uv.w, o); }
float4 __tex1Dfetch(__sampler1D_float4 s, int c) { return s.t.Load(int2(c, 0)); }
float4 __tex1Dfetch(__sampler1D_float4 s, int c, int lod) { return s.t.Load(int2(c, lod)); }
int __tex1Dsize(__sampler1D_float4 s, int lod) { uint w, l; s.t.GetDimensions(lod, w, l); return int(w); }
int __tex1Dsize(__sampler1D_float4 s) { uint w, l; s.t.GetDimensions(0, w, l); return int(w); }
float4 __tex3D(__sampler3D_float4 s, float3 uvw) { return s.t.Sample(s.s, uvw); }
float4 __tex3D(__sampler3D_float4 s, float3 uvw, int3 o) { return s.t.Sample(s.s, uvw, o); }
float4 __tex3Dlod(__sampler3D_float4 s, float4 uvw) { return s.t.SampleLevel(s.s, uvw.xyz, uvw.w); }
float4 __tex3Dlod(__sampler3D_float4 s, float4 uvw, int3 o) { return s.t.SampleLevel(s.s, uvw.xyz, uvw.w, o); }
float4 __tex3Dfetch(__sampler3D_float4 s, int3 c) { return s.t.Load(int4(c, 0)); }
float4 __tex3Dfetch(__sampler3D_float4 s, int3 c, int lod) { return s.t.Load(int4(c, lod)); }
int3 __tex3Dsize(__sampler3D_float4 s, int lod) { uint w, h, d, l; s.t.GetDimensions(lod, w, h, d, l); return int3(w, h, d); }
int3 __tex3Dsize(__sampler3D_float4 s) { uint w, h, d, l; s.t.GetDimensions(0, w, h, d, l); return int3(w, h, d); }
void __tex1Dstore(RWTexture1D<float4> s, int c, float4 v) { s[c] = v; }
void __tex2Dstore(RWTexture2D<float4> s, int2 c, float4 v) { s[c] = v; }
void __tex3Dstore(RWTexture3D<float4> s, int3 c, float4 v) { s[c] = v; }
float4 __tex2Dfetch(RWTexture2D<float4> s, int2 c) { return s[c]; }
";

    private readonly List<FxToken> tokens;
    private readonly HashSet<string> namespaces = new();
    private readonly List<string> namespaceStack = new();

    // Scope ("" for global, "A::B" for namespaces) -> names declared directly in it. Names that appear in more than one
    // scope are renamed to Scope__Name so the flattened HLSL stays unambiguous.
    private readonly Dictionary<string, HashSet<string>> declared = new();
    private readonly HashSet<string> collisions = new();
    private readonly StringBuilder body = new();
    private readonly Dictionary<string, double> constants = new();
    private readonly HashSet<string> pixelEntries = new();
    private bool inPixelEntry;

    // Index of the '{' that starts the function being emitted: semantics only appear before it.
    private int signatureEnd;
    private readonly List<FxTextureInfo> textures = new();
    private readonly List<FxSamplerInfo> samplers = new();
    private readonly List<FxStorageInfo> storages = new();
    private readonly Dictionary<string, (int X, int Y, int Z)> computeEntries = new();
    private readonly List<FxSamplerState> samplerStates = new();
    private readonly List<FxUniformInfo> uniforms = new();
    private readonly List<FxTechniqueInfo> techniques = new();
    private int position;

    private FxTranslator(string source)
    {
        // The preprocessor keeps one line per source line; the translator doesn't need them.
        tokens = FxLexer.Tokenize(source);
    }

    public static FxEffectDescription Translate(string preprocessed) => new FxTranslator(preprocessed).Run();

    private FxToken Current => tokens[position];
    private FxToken Peek(int offset = 1) => tokens[Math.Min(position + offset, tokens.Count - 1)];

    private FxEffectDescription Run()
    {
        Scan();
        position = 0;

        while (Current.Kind != FxTokenKind.Eof)
        {
            var token = Current;
            if (token.Is(";"))
            {
                position++;
            }
            else if (token.Is("namespace"))
            {
                if (!Peek(2).Is("{"))
                    throw Error("Expected '{' after namespace");
                namespaceStack.Add(Peek().Text);
                position += 3;
            }
            else if (token.Is("}") && namespaceStack.Count > 0)
            {
                namespaceStack.RemoveAt(namespaceStack.Count - 1);
                position++;
            }
            else if (token.Is("technique") || token.Is("technique10") || token.Is("technique11"))
            {
                ParseTechnique();
            }
            else
            {
                ParseDeclaration();
            }
        }

        return Assemble();
    }

    private FxException Error(string message) => new($"{message} near '{Current.Text}'");

    // ---- Names and namespaces ----

    /// <summary>First pass: records which names each scope declares, to find the ones that collide.</summary>
    private void Scan()
    {
        var stack = new List<string>();
        var index = 0;
        while (tokens[index].Kind != FxTokenKind.Eof)
        {
            var token = tokens[index];
            if (token.Is(";"))
            {
                index++;
            }
            else if (token.Is("namespace"))
            {
                namespaces.Add(tokens[index + 1].Text);
                stack.Add(tokens[index + 1].Text);
                index += 3;
            }
            else if (token.Is("}") && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
                index++;
            }
            else if (token.Is("technique") || token.Is("technique10") || token.Is("technique11"))
            {
                while (!tokens[index].Is("{"))
                    index++;
                var close = MatchingBrace(index);
                for (var i = index; i < close; i++)
                {
                    if (tokens[i].Is("ComputeShader") && tokens[i + 1].Is("="))
                    {
                        // ComputeShader = ns::Name<x, y, z>;
                        var name = i + 2;
                        while (tokens[name + 1].Is("::"))
                            name += 2;

                        var threads = new List<int>();
                        for (var k = name + 2; k < close && !tokens[k].Is(">") && !tokens[k].Is(";"); k++)
                        {
                            if (tokens[k].Kind == FxTokenKind.Number && int.TryParse(tokens[k].Text, out var count))
                                threads.Add(count);
                        }

                        computeEntries[tokens[name].Text] = (threads.ElementAtOrDefault(0) is var x and > 0 ? x : 1, threads.ElementAtOrDefault(1) is var y and > 0 ? y : 1, threads.ElementAtOrDefault(2) is var z and > 0 ? z : 1);
                        continue;
                    }

                    if (!tokens[i].Is("PixelShader") || !tokens[i + 1].Is("="))
                        continue;

                    // PixelShader = ns::Name;
                    var last = i + 2;
                    while (tokens[last + 1].Is("::"))
                        last += 2;
                    pixelEntries.Add(tokens[last].Text);
                }

                index = close + 1;
            }
            else
            {
                var end = FindDeclarationEnd(index, out _);
                var name = DeclaredName(index, end);
                if (name != null)
                {
                    var scope = string.Join("::", stack);
                    if (!declared.TryGetValue(scope, out var names))
                        declared[scope] = names = new HashSet<string>();
                    names.Add(name);
                }

                index = end;
            }
        }

        var seen = new Dictionary<string, int>();
        foreach (var names in declared.Values)
        {
            foreach (var name in names)
                seen[name] = seen.GetValueOrDefault(name) + 1;
        }

        foreach (var (name, count) in seen)
        {
            if (count > 1)
                collisions.Add(name);
        }
    }

    private string? DeclaredName(int start, int end)
    {
        if (tokens[start].Is("struct"))
            return tokens[start + 1].Text;

        string? last = null;
        for (var i = start; i < end; i++)
        {
            var t = tokens[i];
            if (t.Is("(") || t.Is("=") || t.Is(";") || t.Is("[") || t.Is(":") || t.Is("<") || t.Is("{"))
                break;
            if (t.Kind == FxTokenKind.Ident)
                last = t.Text;
        }

        return last;
    }

    private string CurrentScope => string.Join("::", namespaceStack);

    private static string Mangle(string scope, string name) => scope.Length == 0 ? name : scope.Replace("::", "__") + "__" + name;

    /// <summary>The HLSL name of a declaration made in the current scope.</summary>
    private string DeclaredAs(string name) => collisions.Contains(name) ? Mangle(CurrentScope, name) : name;

    /// <summary>The HLSL name for a reference to <paramref name="name"/>, optionally qualified, from the current scope.</summary>
    private string ResolveName(IReadOnlyList<string> qualifier, string name)
    {
        if (!collisions.Contains(name))
            return name;

        for (var depth = namespaceStack.Count; depth >= 0; depth--)
        {
            var scope = string.Join("::", namespaceStack.Take(depth).Concat(qualifier));
            if (declared.TryGetValue(scope, out var names) && names.Contains(name))
                return Mangle(scope, name);
        }

        return name;
    }

    /// <summary>Resolves a name written in a property value, such as <c>ReShade::PostProcessVS</c>.</summary>
    private string ResolveReference(string written)
    {
        var parts = written.Split("::", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? written : ResolveName(parts.Take(parts.Length - 1).ToList(), parts[^1]);
    }

    // ---- Declarations ----

    private void ParseDeclaration()
    {
        var start = position;
        var index = position;
        var qualifiers = new HashSet<string>();
        while (tokens[index].Kind == FxTokenKind.Ident && Qualifiers.Contains(tokens[index].Text))
            qualifiers.Add(tokens[index++].Text);

        var keyword = tokens[index].Text;
        if (keyword is "texture" or "texture1D" or "texture2D" or "texture3D")
        {
            position = index + 1;
            ParseTexture(keyword == "texture1D" ? 1 : keyword == "texture3D" ? 3 : 2);
        }
        else if (keyword is "sampler" or "sampler1D" or "sampler2D" or "sampler3D" && (tokens[index + 2].Is("{") || tokens[index + 2].Is("<")))
        {
            position = index + 1;
            ParseSampler();
        }
        else if (keyword is "storage" or "storage1D" or "storage2D" or "storage3D" && (tokens[index + 2].Is("{") || tokens[index + 2].Is("<")))
        {
            position = index + 1;
            ParseStorage(keyword == "storage1D" ? 1 : keyword == "storage3D" ? 3 : 2);
        }
        else if (qualifiers.Contains("uniform") && !IsFunction(start))
        {
            position = index;
            ParseUniform();
        }
        else
        {
            CopyDeclaration(start, qualifiers);
        }
    }

    /// <summary>True when the statement starting at <paramref name="start"/> is a function (a '(' before any '=' or ';').</summary>
    private bool IsFunction(int start)
    {
        for (var i = start; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Is("(")) return true;
            if (t.Is("=") || t.Is(";") || t.Is("{") || t.Kind == FxTokenKind.Eof) return false;
        }

        return false;
    }

    private void CopyDeclaration(int start, HashSet<string> qualifiers)
    {
        var end = FindDeclarationEnd(start, out var isFunction);
        var isStruct = tokens[start].Is("struct");

        // Non-uniform globals are constants in FX; HLSL would otherwise put them in a constant buffer with no default.
        if (!isFunction && !isStruct && !qualifiers.Contains("static") && !qualifiers.Contains("uniform") && !qualifiers.Contains("groupshared"))
            body.Append("static ");

        if (!isFunction)
            TryRecordConstant(start, end, qualifiers);

        var functionName = isFunction ? DeclaredName(start, end) : null;
        if (functionName != null && computeEntries.TryGetValue(functionName, out var threads))
            body.Append($"[numthreads({threads.X}, {threads.Y}, {threads.Z})]\n");

        inPixelEntry = functionName != null && pixelEntries.Contains(functionName);
        signatureEnd = start;
        if (isFunction)
        {
            while (!tokens[signatureEnd].Is("{"))
                signatureEnd++;
        }

        EmitTokens(start, end);
        inPixelEntry = false;
        signatureEnd = 0;
        body.Append('\n');
        position = end;
    }

    /// <summary>Remembers <c>static const int N = expression;</c> so texture sizes and the like can refer to it.</summary>
    private void TryRecordConstant(int start, int end, HashSet<string> qualifiers)
    {
        if (!qualifiers.Contains("const") || !qualifiers.Contains("static"))
            return;

        var index = start;
        while (tokens[index].Kind == FxTokenKind.Ident && Qualifiers.Contains(tokens[index].Text))
            index++;

        if (tokens[index].Text is not ("int" or "uint" or "float" or "bool") || tokens[index + 1].Kind != FxTokenKind.Ident || !tokens[index + 2].Is("="))
            return;

        var initializer = tokens.GetRange(index + 3, end - 1 - (index + 3));
        try
        {
            constants[tokens[index + 1].Text] = Evaluate(initializer);
        }
        catch (FxException)
        {
            // Not a constant expression; it just can't be used where one is required.
        }
    }

    /// <summary>Index just past the end of the declaration that starts at <paramref name="start"/>.</summary>
    private int FindDeclarationEnd(int start, out bool isFunction)
    {
        isFunction = false;
        var sawParen = false;
        var sawEquals = false;
        for (var i = start; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == FxTokenKind.Eof)
                throw Error("Unexpected end of file in declaration");
            if (t.Is("("))
                sawParen = true;
            else if (t.Is("="))
                sawEquals = true;
            else if (t.Is(";"))
                return i + 1;
            else if (t.Is("{"))
            {
                var close = MatchingBrace(i);
                if (sawParen && !sawEquals)
                {
                    isFunction = true;
                    return close + 1;
                }

                // struct { } ; or an initialiser list: continue to the terminating ';'
                i = close;
            }
        }

        throw Error("Unterminated declaration");
    }

    private int MatchingBrace(int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Is("{"))
                depth++;
            else if (tokens[i].Is("}") && --depth == 0)
                return i;
        }

        throw Error("Unbalanced '{'");
    }

    private void EmitTokens(int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var t = tokens[i];

            // A qualified name such as A::B::x, or a plain identifier that may need its scope in front of it.
            if (t.Kind == FxTokenKind.Ident && !(i > start && tokens[i - 1].Is(".")))
            {
                var qualifier = new List<string>();
                var last = i;
                while (tokens[last + 1].Is("::") && tokens[last + 2].Kind == FxTokenKind.Ident && namespaces.Contains(tokens[last].Text))
                {
                    qualifier.Add(tokens[last].Text);
                    last += 2;
                }

                if (qualifier.Count > 0 || collisions.Contains(t.Text))
                {
                    body.Append(t.Lead).Append(ResolveName(qualifier, tokens[last].Text));
                    i = last;
                    continue;
                }
            }

            if (t.Is("::"))
                continue;

            // Attributes the compiler in Wine doesn't implement; they only hint at optimisation.
            if (t.Is("[") && tokens[i + 1].Is("fastopt") && tokens[i + 2].Is("]"))
            {
                i += 2;
                continue;
            }

            // SM3-era semantics: ReShade FX accepts them, SM5 doesn't.
            if (t.Kind == FxTokenKind.Ident && i < signatureEnd && tokens[i - 1].Is(":") && SemanticName(t.Text) is { } semantic)
            {
                body.Append(t.Lead).Append(semantic);
                continue;
            }

            // 10f is valid FX but not every HLSL compiler lexes it; 10.0f is accepted everywhere.
            if (t.Kind == FxTokenKind.Number && t.Text.Length > 1 && t.Text[^1] is 'f' or 'F' or 'h' or 'H' && t.Text[..^1].All(char.IsDigit))
            {
                body.Append(t.Lead).Append(t.Text[..^1]).Append(".0").Append(t.Text[^1]);
                continue;
            }

            if (t.Kind == FxTokenKind.Ident)
            {
                if (Intrinsics.TryGetValue(t.Text, out var renamed) && tokens[i + 1].Is("("))
                {
                    body.Append(t.Lead).Append(renamed);
                    continue;
                }

                if (TypeRewrites.TryGetValue(t.Text, out var rewritten))
                {
                    body.Append(t.Lead).Append(rewritten);
                    continue;
                }
            }

            body.Append(t.Lead).Append(t.Text);
        }
    }

    private string? SemanticName(string semantic)
    {
        // Semantics are written in capitals; this keeps a local called "color" safe.
        if (semantic != semantic.ToUpperInvariant())
            return null;

        var upper = semantic;
        if (upper is "POSITION" or "POSITION0" or "VPOS")
            return "SV_Position";
        if (inPixelEntry && upper.StartsWith("COLOR") && (upper.Length == 5 || (upper.Length == 6 && char.IsDigit(upper[5]))))
            return "SV_Target" + upper[5..];
        if (inPixelEntry && upper == "DEPTH")
            return "SV_Depth";
        return null;
    }

    // ---- Annotations and property blocks ----

    /// <summary>Parses <c>&lt; key = value; ... &gt;</c> if present.</summary>
    private Dictionary<string, string> ParseAnnotations()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Current.Is("<"))
            return result;

        position++;
        while (!Current.Is(">"))
        {
            if (Current.Kind == FxTokenKind.Eof)
                throw Error("Unterminated annotations");
            if (Current.Is(";"))
            {
                position++;
                continue;
            }

            // Optional type in front of the name: float ui_min = 0.0;
            if (Current.Kind == FxTokenKind.Ident && Peek().Kind == FxTokenKind.Ident && Peek(2).Is("="))
                position++;

            var key = Current.Text;
            position++;
            if (!Current.Is("="))
                throw Error("Expected '=' in annotation");
            position++;

            var value = new List<FxToken>();
            while (!Current.Is(";") && !Current.Is(">") && Current.Kind != FxTokenKind.Eof)
                value.Add(tokens[position++]);

            result[key] = Unquote(value);
        }

        position++;
        return result;
    }

    private static string Unquote(List<FxToken> value)
    {
        if (value.Count == 1 && value[0].Kind == FxTokenKind.String)
        {
            var text = value[0].Text;
            return text.Substring(1, text.Length - 2).Replace("\\n", "\n").Replace("\\\"", "\"");
        }

        // Adjacent string literals concatenate.
        if (value.Count > 0 && value.All(t => t.Kind == FxTokenKind.String))
            return string.Concat(value.Select(t => t.Text.Substring(1, t.Text.Length - 2).Replace("\\n", "\n")));

        return FxLexer.Join(value).Trim();
    }

    /// <summary>Parses <c>{ Name = value; ... }</c>.</summary>
    private Dictionary<string, List<FxToken>> ParseProperties()
    {
        var result = new Dictionary<string, List<FxToken>>(StringComparer.OrdinalIgnoreCase);
        if (!Current.Is("{"))
            throw Error("Expected '{'");
        position++;

        while (!Current.Is("}"))
        {
            if (Current.Kind == FxTokenKind.Eof)
                throw Error("Unterminated property block");
            if (Current.Is(";"))
            {
                position++;
                continue;
            }

            var key = Current.Text;
            position++;
            if (!Current.Is("="))
                throw Error($"Expected '=' after {key}");
            position++;

            var value = new List<FxToken>();
            while (!Current.Is(";") && Current.Kind != FxTokenKind.Eof)
                value.Add(tokens[position++]);
            result[key] = value;
        }

        position++;
        return result;
    }

    private static string Raw(List<FxToken> value) => Unquote(value);

    private string Property(Dictionary<string, List<FxToken>> properties, string name, string fallback) =>
        properties.TryGetValue(name, out var value) ? Raw(value).ToUpperInvariant() : fallback;

    // ---- texture / sampler ----

    private void ParseTexture(int dimension)
    {
        var name = Current.Text;
        position++;
        ParseAnnotations();

        string? semantic = null;
        if (Current.Is(":"))
        {
            semantic = Peek().Text.ToUpperInvariant();
            position += 2;
        }

        var properties = new Dictionary<string, List<FxToken>>();
        if (Current.Is("{"))
            properties = ParseProperties();
        if (Current.Is(";"))
            position++;

        textures.Add(new FxTextureInfo
        {
            Name = DeclaredAs(name),
            Semantic = semantic,
            Dimension = dimension,
            Width = properties.TryGetValue("Width", out var w) ? (int)Evaluate(w) : null,
            Height = properties.TryGetValue("Height", out var h) ? (int)Evaluate(h) : null,
            Depth = properties.TryGetValue("Depth", out var d) ? (int)Evaluate(d) : null,
            MipLevels = properties.TryGetValue("MipLevels", out var m) ? (int)Evaluate(m) : 1,
            Format = properties.TryGetValue("Format", out var f) ? Raw(f).ToUpperInvariant() : "RGBA8",
            Source = properties.TryGetValue("Source", out var s) ? Raw(s) : null,
        });
    }

    private void ParseSampler()
    {
        var name = Current.Text;
        position++;
        ParseAnnotations();
        var properties = ParseProperties();
        if (Current.Is(";"))
            position++;

        if (!properties.TryGetValue("Texture", out var textureTokens))
            throw new FxException($"Sampler {name} has no Texture");

        var state = new FxSamplerState(
            Property(properties, "AddressU", "CLAMP"),
            Property(properties, "AddressV", "CLAMP"),
            Property(properties, "AddressW", "CLAMP"),
            Property(properties, "MinFilter", "LINEAR"),
            Property(properties, "MagFilter", "LINEAR"),
            Property(properties, "MipFilter", "LINEAR"),
            properties.TryGetValue("MinLOD", out var minLod) ? (float)Evaluate(minLod) : 0,
            properties.TryGetValue("MaxLOD", out var maxLod) ? (float)Evaluate(maxLod) : float.MaxValue,
            properties.TryGetValue("MipLODBias", out var bias) ? (float)Evaluate(bias) : 0);

        var stateIndex = samplerStates.IndexOf(state);
        if (stateIndex < 0)
        {
            samplerStates.Add(state);
            stateIndex = samplerStates.Count - 1;
        }

        if (samplerStates.Count > 16)
            throw new FxException("An effect can use at most 16 distinct sampler states");

        var slot = samplers.Count;
        var texture = ResolveReference(Raw(textureTokens));
        name = DeclaredAs(name);
        samplers.Add(new FxSamplerInfo
        {
            Name = name,
            TextureName = texture,
            Slot = slot,
            StateIndex = stateIndex,
            Srgb = properties.TryGetValue("SRGBTexture", out var srgb) && Raw(srgb).Equals("true", StringComparison.OrdinalIgnoreCase),
        });

        var dimension = textures.FirstOrDefault(t => t.Name == texture)?.Dimension ?? 2;
        body.Append($"Texture{dimension}D<float4> {name}_t : register(t{slot});\n");
        body.Append($"static const __sampler{dimension}D_float4 {name} = {{ {name}_t, __ss{stateIndex} }};\n");
    }

    private void ParseStorage(int dimension)
    {
        var name = Current.Text;
        position++;
        ParseAnnotations();
        var properties = ParseProperties();
        if (Current.Is(";"))
            position++;

        if (!properties.TryGetValue("Texture", out var textureTokens))
            throw new FxException($"Storage {name} has no Texture");

        var slot = storages.Count;
        name = DeclaredAs(name);
        storages.Add(new FxStorageInfo
        {
            Name = name,
            TextureName = ResolveReference(Raw(textureTokens)),
            MipLevel = properties.TryGetValue("MipLevel", out var mip) ? (int)Evaluate(mip) : 0,
            Slot = slot,
        });

        body.Append($"RWTexture{dimension}D<float4> {name} : register(u{slot});\n");
    }

    // ---- uniforms ----

    private static readonly System.Text.RegularExpressions.Regex UniformType = new(@"^(float|int|uint|bool)(?:([1-4])(?:x([1-4]))?)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void ParseUniform()
    {
        var type = Current.Text;
        position++;
        var name = Current.Text;
        position++;

        var match = UniformType.Match(type);
        if (!match.Success)
            throw new FxException($"Unsupported uniform type '{type}' ({name})");

        var kind = match.Groups[1].Value;
        var isMatrix = match.Groups[3].Success;
        var rows = isMatrix ? int.Parse(match.Groups[2].Value) : 1;
        var columns = isMatrix ? int.Parse(match.Groups[3].Value) : match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;

        var arrayLength = 1;
        var isArray = false;
        if (Current.Is("["))
        {
            var size = new List<FxToken>();
            position++;
            while (!Current.Is("]") && Current.Kind != FxTokenKind.Eof)
                size.Add(tokens[position++]);
            position++;
            arrayLength = (int)Evaluate(size);
            isArray = true;
            if (arrayLength < 1)
                throw new FxException($"Uniform array {name} needs at least one element");
        }

        var annotations = ParseAnnotations();
        var count = rows * columns * arrayLength;

        var defaults = new List<double>();
        if (Current.Is("="))
        {
            position++;
            var initializer = new List<FxToken>();
            while (!Current.Is(";") && Current.Kind != FxTokenKind.Eof)
                initializer.Add(tokens[position++]);
            defaults = FlattenValues(initializer);
        }

        if (Current.Is(";"))
            position++;

        // A single scalar initialises every component of an element; anything missing is zero.
        if (defaults.Count == 1 && rows * columns > 1)
            defaults = Enumerable.Repeat(defaults[0], rows * columns).ToList();
        while (defaults.Count < count)
            defaults.Add(0);

        uniforms.Add(new FxUniformInfo
        {
            Name = DeclaredAs(name),
            Kind = kind,
            Rows = rows,
            Columns = columns,
            ArrayLength = arrayLength,
            IsArray = isArray,
            IsMatrix = isMatrix,
            Offset = 0,
            Default = defaults.Take(count).ToArray(),
            Annotations = annotations,
        });
    }

    /// <summary>The numbers in an initialiser such as <c>float3(0.1, 0.2, 0.3)</c> or <c>0.5</c>, in order.</summary>
    private List<double> FlattenValues(List<FxToken> initializer)
    {
        var result = new List<double>();
        var index = 0;
        void Parse()
        {
            if (index < initializer.Count && initializer[index].Kind == FxTokenKind.Ident && index + 1 < initializer.Count && initializer[index + 1].Is("("))
            {
                index += 2;
                while (index < initializer.Count && !initializer[index].Is(")"))
                {
                    if (initializer[index].Is(",") || initializer[index].Is("{") || initializer[index].Is("}"))
                        index++;
                    else
                        Parse();
                }

                index++;
                return;
            }

            // A scalar expression up to the next ',' or ')' at this level
            var expression = new List<FxToken>();
            var depth = 0;
            while (index < initializer.Count)
            {
                var t = initializer[index];
                if (depth == 0 && (t.Is(",") || t.Is(")") || t.Is("{") || t.Is("}")))
                    break;
                if (t.Is("(")) depth++;
                if (t.Is(")")) depth--;
                expression.Add(t);
                index++;
            }

            result.Add(Evaluate(expression));
        }

        while (index < initializer.Count)
        {
            // Braces only group; the values stay in order.
            if (initializer[index].Is("{") || initializer[index].Is("}"))
            {
                index++;
                continue;
            }

            Parse();
            if (index < initializer.Count && initializer[index].Is(","))
                index++;
        }

        return result;
    }

    // ---- techniques ----

    private void ParseTechnique()
    {
        position++;
        var name = Current.Text;
        position++;
        var annotations = ParseAnnotations();
        if (!Current.Is("{"))
            throw Error("Expected '{' after technique name");
        position++;

        var passes = new List<FxPassInfo>();
        while (!Current.Is("}"))
        {
            if (Current.Kind == FxTokenKind.Eof)
                throw Error("Unterminated technique");
            if (Current.Is(";"))
            {
                position++;
                continue;
            }

            if (!Current.Is("pass"))
                throw Error("Expected 'pass'");
            position++;

            var passName = "";
            if (Current.Kind == FxTokenKind.Ident)
            {
                passName = Current.Text;
                position++;
            }

            ParseAnnotations();
            var properties = ParseProperties();
            var raw = properties.ToDictionary(p => p.Key, p => Raw(p.Value), StringComparer.OrdinalIgnoreCase);
            foreach (var key in raw.Keys.ToList())
            {
                // Shader entry points and render targets are names; everything else is a literal.
                if (key is "VertexShader" or "PixelShader" || key.StartsWith("RenderTarget", StringComparison.OrdinalIgnoreCase))
                    raw[key] = ResolveReference(raw[key]);
            }

            var compute = raw.GetValueOrDefault("ComputeShader");
            if (compute == null)
            {
                if (!raw.ContainsKey("VertexShader") || !raw.ContainsKey("PixelShader"))
                    throw new FxException($"Pass {passName} of {name} needs a VertexShader and a PixelShader, or a ComputeShader");
            }
            else
            {
                // The thread group size after the name was only needed for the [numthreads] attribute.
                var angle = compute.IndexOf('<');
                compute = ResolveReference(angle < 0 ? compute : compute[..angle].Trim());
            }

            int Dispatch(string key) => properties.TryGetValue(key, out var value) ? Math.Max(1, (int)Evaluate(value)) : 1;

            passes.Add(new FxPassInfo
            {
                Name = passName,
                VertexShader = raw.GetValueOrDefault("VertexShader"),
                PixelShader = raw.GetValueOrDefault("PixelShader"),
                ComputeShader = compute,
                DispatchX = Dispatch("DispatchSizeX"),
                DispatchY = Dispatch("DispatchSizeY"),
                DispatchZ = Dispatch("DispatchSizeZ"),
                Properties = raw,
            });
        }

        position++;
        techniques.Add(new FxTechniqueInfo { Name = name, Annotations = annotations, Passes = passes });
    }

    // ---- assembly ----

    private FxEffectDescription Assemble()
    {
        // Pack the uniforms the way HLSL packs a constant buffer: 4-byte scalars, vectors may not straddle a 16-byte row,
        // and arrays and matrices start a new row, with every array element (and matrix row) taking a whole row.
        var offset = 0;
        foreach (var uniform in uniforms)
        {
            if (uniform.IsMatrix)
            {
                var elementSize = (uniform.Rows - 1) * 16 + uniform.Columns * 4;
                uniform.Stride = (elementSize + 15) / 16 * 16;
                offset = (offset + 15) / 16 * 16;
                uniform.Offset = offset;
                offset += (uniform.ArrayLength - 1) * uniform.Stride + elementSize;
            }
            else if (uniform.IsArray)
            {
                var elementSize = uniform.Columns * 4;
                uniform.Stride = 16;
                offset = (offset + 15) / 16 * 16;
                uniform.Offset = offset;
                offset += (uniform.ArrayLength - 1) * 16 + elementSize;
            }
            else
            {
                var size = uniform.Columns * 4;
                if (offset % 16 + size > 16)
                    offset = (offset + 15) / 16 * 16;
                uniform.Offset = offset;
                offset += size;
            }
        }

        var constantBufferSize = Math.Max(16, (offset + 15) / 16 * 16);

        var sb = new StringBuilder();
        sb.Append(Prelude);
        for (var i = 0; i < samplerStates.Count; i++)
            sb.Append($"SamplerState __ss{i} : register(s{i});\n");

        if (uniforms.Count > 0)
        {
            sb.Append("cbuffer _Globals : register(b0)\n{\n");
            // Explicit offsets, so the layout the runtime writes does not depend on the compiler's packing rules.
            foreach (var uniform in uniforms)
            {
                var component = "xyzw"[uniform.Offset % 16 / 4];
                var register = $"c{uniform.Offset / 16}" + (uniform.Offset % 16 == 0 ? "" : "." + component);
                sb.Append($"    {(uniform.IsMatrix ? "row_major " : "")}{uniform.Type} {uniform.Name}{(uniform.IsArray ? $"[{uniform.ArrayLength}]" : "")} : packoffset({register});\n");
            }
            sb.Append("};\n");
        }

        sb.Append(body);

        return new FxEffectDescription
        {
            Hlsl = sb.ToString(),
            Textures = textures,
            Samplers = samplers,
            Storages = storages,
            SamplerStates = samplerStates,
            Uniforms = uniforms,
            Techniques = techniques,
            ConstantBufferSize = constantBufferSize,
        };
    }

    // ---- constant expressions ----

    private double Evaluate(List<FxToken> expression) => new ExpressionParser(expression, constants).Parse();

    /// <summary>Constant expressions: C operators, numbers, known constants and a few math functions.</summary>
    private sealed class ExpressionParser
    {
        private readonly List<FxToken> tokens;
        private readonly Dictionary<string, double> constants;
        private int position;

        public ExpressionParser(List<FxToken> tokens, Dictionary<string, double> constants)
        {
            this.tokens = tokens;
            this.constants = constants;
        }

        public double Parse()
        {
            if (tokens.Count == 0)
                return 0;
            var value = Conditional();
            if (position < tokens.Count)
                throw new FxException($"Unexpected '{tokens[position].Text}' in constant expression");
            return value;
        }

        private bool Accept(string op)
        {
            if (position < tokens.Count && tokens[position].Is(op))
            {
                position++;
                return true;
            }

            return false;
        }

        private double Conditional()
        {
            var condition = LogicalOr();
            if (!Accept("?"))
                return condition;

            var whenTrue = Conditional();
            if (!Accept(":"))
                throw new FxException("Expected ':' in constant expression");
            var whenFalse = Conditional();
            return condition != 0 ? whenTrue : whenFalse;
        }

        private double LogicalOr()
        {
            var value = LogicalAnd();
            while (Accept("||"))
                value = LogicalAnd() != 0 || value != 0 ? 1 : 0;
            return value;
        }

        private double LogicalAnd()
        {
            var value = BitOr();
            while (Accept("&&"))
                value = BitOr() != 0 && value != 0 ? 1 : 0;
            return value;
        }

        private double BitOr()
        {
            var value = BitXor();
            while (position < tokens.Count && tokens[position].Is("|") && Accept("|"))
                value = (long)value | (long)BitXor();
            return value;
        }

        private double BitXor()
        {
            var value = BitAnd();
            while (Accept("^"))
                value = (long)value ^ (long)BitAnd();
            return value;
        }

        private double BitAnd()
        {
            var value = Equality();
            while (position < tokens.Count && tokens[position].Is("&") && Accept("&"))
                value = (long)value & (long)Equality();
            return value;
        }

        private double Equality()
        {
            var value = Relational();
            while (true)
            {
                if (Accept("=="))
                    value = value == Relational() ? 1 : 0;
                else if (Accept("!="))
                    value = value != Relational() ? 1 : 0;
                else
                    return value;
            }
        }

        private double Relational()
        {
            var value = Shift();
            while (true)
            {
                if (Accept("<="))
                    value = value <= Shift() ? 1 : 0;
                else if (Accept(">="))
                    value = value >= Shift() ? 1 : 0;
                else if (Accept("<"))
                    value = value < Shift() ? 1 : 0;
                else if (Accept(">"))
                    value = value > Shift() ? 1 : 0;
                else
                    return value;
            }
        }

        private double Shift()
        {
            var value = Additive();
            while (true)
            {
                if (Accept("<<"))
                    value = (long)value << (int)Additive();
                else if (Accept(">>"))
                    value = (long)value >> (int)Additive();
                else
                    return value;
            }
        }

        private double Additive()
        {
            var value = Multiplicative();
            while (true)
            {
                if (Accept("+"))
                    value += Multiplicative();
                else if (Accept("-"))
                    value -= Multiplicative();
                else
                    return value;
            }
        }

        private double Multiplicative()
        {
            var value = Unary();
            while (true)
            {
                if (Accept("*"))
                    value *= Unary();
                else if (Accept("/"))
                    value /= Unary();
                else if (Accept("%"))
                    value %= Unary();
                else
                    return value;
            }
        }

        private double Unary()
        {
            if (Accept("-"))
                return -Unary();
            if (Accept("+"))
                return Unary();
            if (Accept("!"))
                return Unary() == 0 ? 1 : 0;
            if (Accept("~"))
                return ~(long)Unary();
            return Primary();
        }

        private double Primary()
        {
            if (Accept("("))
            {
                var value = Conditional();
                if (!Accept(")"))
                    throw new FxException("Expected ')' in constant expression");
                return value;
            }

            if (position >= tokens.Count)
                throw new FxException("Unexpected end of constant expression");

            var token = tokens[position++];
            switch (token.Kind)
            {
                case FxTokenKind.Number:
                {
                    var text = token.Text;
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        return long.Parse(text.AsSpan(2).TrimEnd("uUlL"), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    text = text.TrimEnd('f', 'F', 'u', 'U', 'l', 'L', 'h', 'H');
                    return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                case FxTokenKind.Ident when token.Text == "true":
                    return 1;
                case FxTokenKind.Ident when token.Text == "false":
                    return 0;
                case FxTokenKind.Ident when position < tokens.Count && tokens[position].Is("("):
                    return Call(token.Text);
                case FxTokenKind.Ident when constants.TryGetValue(token.Text, out var constant):
                    return constant;
                default:
                    throw new FxException($"'{token.Text}' is not a constant");
            }
        }

        private double Call(string name)
        {
            position++; // (
            var arguments = new List<double>();
            while (!Accept(")"))
            {
                if (position >= tokens.Count)
                    throw new FxException("Unterminated call in constant expression");
                arguments.Add(Conditional());
                Accept(",");
            }

            double Argument(int index) => index < arguments.Count ? arguments[index] : throw new FxException($"Too few arguments to {name}");
            return name switch
            {
                "int" or "uint" or "trunc" => Math.Truncate(Argument(0)),
                "float" or "double" or "half" => Argument(0),
                "floor" => Math.Floor(Argument(0)),
                "ceil" => Math.Ceiling(Argument(0)),
                "round" => Math.Round(Argument(0), MidpointRounding.AwayFromZero),
                "abs" => Math.Abs(Argument(0)),
                "sqrt" => Math.Sqrt(Argument(0)),
                "log2" => Math.Log2(Argument(0)),
                "log" => Math.Log(Argument(0)),
                "log10" => Math.Log10(Argument(0)),
                "exp2" => Math.Pow(2, Argument(0)),
                "pow" => Math.Pow(Argument(0), Argument(1)),
                "min" => Math.Min(Argument(0), Argument(1)),
                "max" => Math.Max(Argument(0), Argument(1)),
                "clamp" => Math.Clamp(Argument(0), Argument(1), Argument(2)),
                "saturate" => Math.Clamp(Argument(0), 0, 1),
                _ => throw new FxException($"'{name}' is not a constant function"),
            };
        }
    }
}
