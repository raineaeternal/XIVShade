using System.Collections.Generic;

namespace XIVShade.Effects;

public sealed class FxTextureInfo
{
    public required string Name { get; init; }

    /// <summary>"COLOR" (the image being processed), "DEPTH" (the scene depth) or null for a texture the effect owns.</summary>
    public string? Semantic { get; init; }

    /// <summary>1, 2 or 3.</summary>
    public int Dimension { get; init; } = 2;

    public int? Width { get; init; }
    public int? Height { get; init; }

    /// <summary>Depth of a 3D texture.</summary>
    public int? Depth { get; init; }
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

/// <summary>A texture an effect writes to from a compute shader (an unordered access view).</summary>
public sealed class FxStorageInfo
{
    public required string Name { get; init; }
    public required string TextureName { get; init; }
    public required int MipLevel { get; init; }

    /// <summary>Index of the UAV slot (u#).</summary>
    public required int Slot { get; init; }
}

public sealed record FxSamplerState(string AddressU, string AddressV, string AddressW, string MinFilter, string MagFilter, string MipFilter, float MinLod, float MaxLod, float LodBias);

public sealed class FxUniformInfo
{
    public required string Name { get; init; }

    /// <summary>HLSL scalar kind: "float", "int", "uint" or "bool".</summary>
    public required string Kind { get; init; }

    /// <summary>1 for scalars and vectors, rows of a matrix otherwise.</summary>
    public required int Rows { get; init; }

    /// <summary>Components of a vector, columns of a matrix, 1 for a scalar.</summary>
    public required int Columns { get; init; }

    /// <summary>Elements of an array uniform; 1 for anything else.</summary>
    public int ArrayLength { get; init; } = 1;

    public bool IsArray { get; init; }
    public bool IsMatrix { get; init; }

    public required int Offset { get; set; }

    /// <summary>Bytes between array elements.</summary>
    public int Stride { get; set; }

    public required double[] Default { get; init; }
    public required Dictionary<string, string> Annotations { get; init; }

    /// <summary>Scalars in one element.</summary>
    public int Components => Rows * Columns;

    /// <summary>Scalars in the whole uniform; the length of its value array.</summary>
    public int Count => Components * ArrayLength;

    /// <summary>The type as written in HLSL, without the array suffix.</summary>
    public string Type => IsMatrix ? $"{Kind}{Rows}x{Columns}" : Columns == 1 ? Kind : Kind + Columns;

    public string? Source => Annotations.GetValueOrDefault("source");
    public string Label => Annotations.GetValueOrDefault("ui_label") ?? Name;

    /// <summary>Where scalar number <paramref name="index"/> of the value array lives in the constant buffer.</summary>
    public int ByteOffset(int index)
    {
        var element = index / Components;
        var within = index % Components;
        var local = IsMatrix ? within / Columns * 16 + within % Columns * 4 : within * 4;
        return Offset + element * Stride + local;
    }
}

public sealed class FxPassInfo
{
    public required string Name { get; init; }
    public string? VertexShader { get; init; }
    public string? PixelShader { get; init; }

    /// <summary>Entry point of a compute pass, which dispatches instead of drawing.</summary>
    public string? ComputeShader { get; init; }

    public int DispatchX { get; init; } = 1;
    public int DispatchY { get; init; } = 1;
    public int DispatchZ { get; init; } = 1;

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
    public required List<FxStorageInfo> Storages { get; init; }
    public required List<FxSamplerState> SamplerStates { get; init; }
    public required List<FxUniformInfo> Uniforms { get; init; }
    public required List<FxTechniqueInfo> Techniques { get; init; }
    public required int ConstantBufferSize { get; init; }
}
