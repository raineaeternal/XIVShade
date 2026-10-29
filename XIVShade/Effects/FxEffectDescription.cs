using System.Collections.Generic;

namespace XIVShade.Effects;

public sealed class FxTextureInfo
{
    public required string Name { get; init; }

    /// <summary>"COLOR" (the image being processed), "DEPTH" (the scene depth) or null for a texture the effect owns.</summary>
    public string? Semantic { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }
    public int MipLevels { get; init; } = 1;
    public string Format { get; init; } = "RGBA8";

    /// <summary>File name relative to the textures directory, for textures loaded from an image.</summary>
    public string? Source { get; init; }
}

public sealed class FxSamplerInfo
{
    public required string Name { get; init; }
    public required string TextureName { get; init; }

    /// <summary>Index of the SRV slot (t#) the sampler's texture is bound to.</summary>
    public required int Slot { get; init; }

    /// <summary>Index of the shared sampler state (s#).</summary>
    public required int StateIndex { get; init; }

    public bool Srgb { get; init; }
}

public sealed record FxSamplerState(string AddressU, string AddressV, string AddressW, string MinFilter, string MagFilter, string MipFilter, float MinLod, float MaxLod, float LodBias);

public sealed class FxUniformInfo
{
    public required string Name { get; init; }

    /// <summary>HLSL scalar kind: "float", "int", "uint" or "bool".</summary>
    public required string Kind { get; init; }

    public required int Components { get; init; }
    public required int Offset { get; set; }
    public required double[] Default { get; init; }
    public required Dictionary<string, string> Annotations { get; init; }

    public string Type => Components == 1 ? Kind : Kind + Components;
    public string? Source => Annotations.GetValueOrDefault("source");
    public string Label => Annotations.GetValueOrDefault("ui_label") ?? Name;
}

public sealed class FxPassInfo
{
    public required string Name { get; init; }
    public required string VertexShader { get; init; }
    public required string PixelShader { get; init; }

    /// <summary>All pass properties as written (RenderTarget0, BlendEnable, SrcBlend, ...).</summary>
    public required Dictionary<string, string> Properties { get; init; }
}

public sealed class FxTechniqueInfo
{
    public required string Name { get; init; }
    public required Dictionary<string, string> Annotations { get; init; }
    public required List<FxPassInfo> Passes { get; init; }
    public string Label => Annotations.GetValueOrDefault("ui_label") ?? Name;
}

public sealed class FxEffectDescription
{
    public required string Hlsl { get; init; }
    public required List<FxTextureInfo> Textures { get; init; }
    public required List<FxSamplerInfo> Samplers { get; init; }
    public required List<FxSamplerState> SamplerStates { get; init; }
    public required List<FxUniformInfo> Uniforms { get; init; }
    public required List<FxTechniqueInfo> Techniques { get; init; }
    public required int ConstantBufferSize { get; init; }
}
