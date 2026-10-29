using System;
using System.Collections.Generic;
using System.Linq;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.D3D_PRIMITIVE_TOPOLOGY;

namespace XIVShade.Effects;

/// <summary>What an effect needs to know about the frame it is run on.</summary>
public unsafe struct FrameInputs
{
    public ID3D11DeviceContext* Context;

    /// <summary>The image being processed and a view that draws into it.</summary>
    public ID3D11Texture2D* Target;
    public ID3D11RenderTargetView* TargetView;

    /// <summary>A copy of the target an effect can sample (the COLOR texture), as linear and sRGB views.</summary>
    public ID3D11Texture2D* ColorCopy;
    public ID3D11ShaderResourceView* ColorLinear;
    public ID3D11ShaderResourceView* ColorSrgb;

    /// <summary>The scene depth (the DEPTH texture), or null when unavailable.</summary>
    public ID3D11ShaderResourceView* Depth;

    public uint Width;
    public uint Height;
    public float FrameTimeMs;
    public float TimerMs;
    public uint FrameCount;
}

/// <summary>A translated effect with its GPU resources: shaders, samplers, textures and the constant buffer.</summary>
public sealed unsafe class EffectInstance : IDisposable
{
    /// <summary>A loaded image: its shader resource view, and the object that keeps it alive.</summary>
    public readonly record struct LoadedImage(nint View, IDisposable Owner, uint Width, uint Height);

    private sealed class TextureResource
    {
        public ID3D11Texture2D* Texture;
        public ID3D11ShaderResourceView* Linear;
        public ID3D11ShaderResourceView* Srgb;
        public ID3D11RenderTargetView* RenderTarget;
        public uint Width, Height;
        public uint MipLevels;
        public IDisposable? Owner;
    }

    private sealed class PassState
    {
        public required FxPassInfo Info;
        public required ID3D11VertexShader* VertexShader;
        public required ID3D11PixelShader* PixelShader;
        public required string[] RenderTargets;
        public required bool Clear;
        public required ID3D11BlendState* Blend;
        public required D3D_PRIMITIVE_TOPOLOGY Topology;
        public required uint VertexCount;
    }

    private readonly ID3D11Device* device;
    private readonly Dictionary<string, nint> vertexShaders = new();
    private readonly Dictionary<string, nint> pixelShaders = new();
    private readonly List<nint> samplerStates = new();
    private readonly List<nint> blendStates = new();
    private readonly Dictionary<string, TextureResource> textures = new();
    private readonly Dictionary<string, FxTextureInfo> textureInfos = new();
    private readonly Dictionary<FxTechniqueInfo, PassState[]> passes = new();
    private readonly byte[] constantData;
    private readonly Random random = new();
    private ID3D11Buffer* constantBuffer;

    private EffectInstance(ID3D11Device* device, string fileName, FxEffectDescription description)
    {
        this.device = device;
        FileName = fileName;
        Description = description;
        constantData = new byte[description.ConstantBufferSize];
        foreach (var uniform in description.Uniforms)
            Values[uniform.Name] = uniform.Default.ToArray();
    }

    public string FileName { get; }
    public FxEffectDescription Description { get; }

    /// <summary>Current uniform values by name; written by the UI, read each frame.</summary>
    public Dictionary<string, double[]> Values { get; } = new();

    /// <summary>
    /// Creates the GPU objects for <paramref name="description"/>. Safe to call from any thread, as the device is free-threaded.
    /// </summary>
    public static EffectInstance Create(ID3D11Device* device, string fileName, FxEffectDescription description, uint width, uint height, Func<string, LoadedImage?> loadImage)
    {
        var instance = new EffectInstance(device, fileName, description);
        try
        {
            instance.Build(width, height, loadImage);
            return instance;
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    private void Build(uint width, uint height, Func<string, LoadedImage?> loadImage)
    {
        // Shaders: each distinct entry point once.
        var errors = new List<string>();
        foreach (var technique in Description.Techniques)
        {
            foreach (var pass in technique.Passes)
            {
                CompileOnce(vertexShaders, pass.VertexShader, "vs_5_0", errors);
                CompileOnce(pixelShaders, pass.PixelShader, "ps_5_0", errors);
            }
        }

        if (errors.Count > 0)
            throw new FxException(string.Join("\n", errors));

        foreach (var state in Description.SamplerStates)
            samplerStates.Add((nint)CreateSampler(state));

        if (Description.Uniforms.Count > 0)
        {
            var desc = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)Description.ConstantBufferSize,
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            fixed (ID3D11Buffer** pBuffer = &constantBuffer)
                Check(device->CreateBuffer(&desc, null, pBuffer));
        }

        foreach (var info in Description.Textures)
        {
            textureInfos[info.Name] = info;
            if (info.Semantic != null)
                continue;

            if (info.Source != null)
            {
                var image = loadImage(info.Source) ?? throw new FxException($"Texture image '{info.Source}' could not be loaded");
                textures[info.Name] = new TextureResource
                {
                    Linear = (ID3D11ShaderResourceView*)image.View,
                    Srgb = (ID3D11ShaderResourceView*)image.View,
                    Width = image.Width,
                    Height = image.Height,
                    MipLevels = 1,
                    Owner = image.Owner,
                };
            }
            else
            {
                textures[info.Name] = CreateTexture(info, width, height);
            }
        }

        foreach (var technique in Description.Techniques)
            passes[technique] = technique.Passes.Select(PreparePass).ToArray();
    }

    private void CompileOnce(Dictionary<string, nint> shaders, string entry, string profile, List<string> errors)
    {
        if (shaders.ContainsKey(entry))
            return;

        try
        {
            var bytecode = ShaderCompiler.Compile(Description.Hlsl, entry, profile);
            fixed (byte* pBytes = bytecode)
            {
                if (profile.StartsWith("vs"))
                {
                    ID3D11VertexShader* shader;
                    Check(device->CreateVertexShader(pBytes, (nuint)bytecode.Length, null, &shader));
                    shaders[entry] = (nint)shader;
                }
                else
                {
                    ID3D11PixelShader* shader;
                    Check(device->CreatePixelShader(pBytes, (nuint)bytecode.Length, null, &shader));
                    shaders[entry] = (nint)shader;
                }
            }
        }
        catch (FxException ex)
        {
            errors.Add(ex.Message);
        }
    }

    private static D3D11_FILTER Filter(FxSamplerState state)
    {
        var min = state.MinFilter == "POINT";
        var mag = state.MagFilter == "POINT";
        var mip = state.MipFilter == "POINT";
        var bits = (min ? 0 : 0x10) | (mag ? 0 : 0x04) | (mip ? 0 : 0x01);
        return (D3D11_FILTER)bits;
    }

    private static D3D11_TEXTURE_ADDRESS_MODE Address(string mode) => mode switch
    {
        "WRAP" or "REPEAT" => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
        "MIRROR" => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_MIRROR,
        "BORDER" => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_BORDER,
        _ => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
    };

    private ID3D11SamplerState* CreateSampler(FxSamplerState state)
    {
        var desc = new D3D11_SAMPLER_DESC
        {
            Filter = Filter(state),
            AddressU = Address(state.AddressU),
            AddressV = Address(state.AddressV),
            AddressW = Address(state.AddressW),
            MipLODBias = state.LodBias,
            MaxAnisotropy = 1,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            MinLOD = state.MinLod,
            MaxLOD = state.MaxLod,
        };
        ID3D11SamplerState* sampler;
        Check(device->CreateSamplerState(&desc, &sampler));
        return sampler;
    }

    /// <summary>Texture format for an FX format name: the resource format, and the views to read it as (linear, sRGB).</summary>
    private static (DXGI_FORMAT Resource, DXGI_FORMAT Linear, DXGI_FORMAT Srgb) Format(string name) => name switch
    {
        "R8" => (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM),
        "R16" => (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16_UNORM),
        "R16F" => (DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT),
        "R32F" => (DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT),
        "RG8" => (DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM),
        "RG16" => (DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM),
        "RG16F" => (DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT),
        "RG32F" => (DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT),
        "RGBA16" => (DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM),
        "RGBA16F" => (DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT),
        "RGBA32F" => (DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT),
        "RGB10A2" => (DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM),
        _ => (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB),
    };

    private TextureResource CreateTexture(FxTextureInfo info, uint bufferWidth, uint bufferHeight)
    {
        var width = (uint)(info.Width ?? (int)bufferWidth);
        var height = (uint)(info.Height ?? (int)bufferHeight);
        var mips = (uint)Math.Max(1, info.MipLevels);
        var (resourceFormat, linearFormat, srgbFormat) = Format(info.Format);

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = mips,
            ArraySize = 1,
            Format = resourceFormat,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET),
            MiscFlags = mips > 1 ? (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS : 0,
        };

        var resource = new TextureResource { Width = width, Height = height, MipLevels = mips };
        ID3D11Texture2D* texture;
        Check(device->CreateTexture2D(&desc, null, &texture));
        resource.Texture = texture;

        resource.Linear = CreateView(texture, linearFormat, mips);
        resource.Srgb = srgbFormat == linearFormat ? resource.Linear : CreateView(texture, srgbFormat, mips);
        if (resource.Srgb == resource.Linear)
            ((IUnknown*)resource.Linear)->AddRef();

        var rtvDesc = new D3D11_RENDER_TARGET_VIEW_DESC
        {
            Format = linearFormat,
            ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D,
        };
        ID3D11RenderTargetView* rtv;
        Check(device->CreateRenderTargetView((ID3D11Resource*)texture, &rtvDesc, &rtv));
        resource.RenderTarget = rtv;
        return resource;
    }

    private ID3D11ShaderResourceView* CreateView(ID3D11Texture2D* texture, DXGI_FORMAT format, uint mips)
    {
        var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = format,
            ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D,
        };
        desc.Texture2D.MostDetailedMip = 0;
        desc.Texture2D.MipLevels = mips;
        ID3D11ShaderResourceView* view;
        Check(device->CreateShaderResourceView((ID3D11Resource*)texture, &desc, &view));
        return view;
    }

    // ---- passes ----

    private static D3D11_BLEND Blend(string name, D3D11_BLEND fallback) => name switch
    {
        "ZERO" => D3D11_BLEND.D3D11_BLEND_ZERO,
        "ONE" => D3D11_BLEND.D3D11_BLEND_ONE,
        "SRCCOLOR" => D3D11_BLEND.D3D11_BLEND_SRC_COLOR,
        "INVSRCCOLOR" => D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR,
        "SRCALPHA" => D3D11_BLEND.D3D11_BLEND_SRC_ALPHA,
        "INVSRCALPHA" => D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
        "DESTALPHA" => D3D11_BLEND.D3D11_BLEND_DEST_ALPHA,
        "INVDESTALPHA" => D3D11_BLEND.D3D11_BLEND_INV_DEST_ALPHA,
        "DESTCOLOR" => D3D11_BLEND.D3D11_BLEND_DEST_COLOR,
        "INVDESTCOLOR" => D3D11_BLEND.D3D11_BLEND_INV_DEST_COLOR,
        _ => fallback,
    };

    private static D3D11_BLEND_OP BlendOp(string name) => name switch
    {
        "SUBTRACT" => D3D11_BLEND_OP.D3D11_BLEND_OP_SUBTRACT,
        "REVSUBTRACT" => D3D11_BLEND_OP.D3D11_BLEND_OP_REV_SUBTRACT,
        "MIN" => D3D11_BLEND_OP.D3D11_BLEND_OP_MIN,
        "MAX" => D3D11_BLEND_OP.D3D11_BLEND_OP_MAX,
        _ => D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
    };

    private static D3D_PRIMITIVE_TOPOLOGY Topology(string name) => name.ToUpperInvariant() switch
    {
        "POINTLIST" => D3D_PRIMITIVE_TOPOLOGY_POINTLIST,
        "LINELIST" => D3D_PRIMITIVE_TOPOLOGY_LINELIST,
        "LINESTRIP" => D3D_PRIMITIVE_TOPOLOGY_LINESTRIP,
        "TRIANGLESTRIP" => D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP,
        _ => D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
    };

    private PassState PreparePass(FxPassInfo pass)
    {
        var properties = pass.Properties;
        string Get(string key, string fallback) => properties.TryGetValue(key, out var value) ? value.ToUpperInvariant() : fallback;
        bool Flag(string key, bool fallback) => properties.TryGetValue(key, out var value) ? value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" : fallback;

        if (properties.ContainsKey("DispatchSizeX"))
            throw new FxException($"Pass {pass.Name}: compute passes are not supported");

        var targets = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var key = i == 0 && properties.ContainsKey("RenderTarget") ? "RenderTarget" : "RenderTarget" + i;
            if (properties.TryGetValue(key, out var name) && name.Length > 0 && name != "0")
                targets.Add(name);
        }

        var blendEnable = Flag("BlendEnable", false);
        var srcBlend = Blend(Get("SrcBlend", "ONE"), D3D11_BLEND.D3D11_BLEND_ONE);
        var destBlend = Blend(Get("DestBlend", "ZERO"), D3D11_BLEND.D3D11_BLEND_ZERO);
        var blendDesc = new D3D11_BLEND_DESC { IndependentBlendEnable = false };
        var renderTarget = default(D3D11_RENDER_TARGET_BLEND_DESC);
        renderTarget.BlendEnable = blendEnable;
        renderTarget.SrcBlend = srcBlend;
        renderTarget.DestBlend = destBlend;
        renderTarget.BlendOp = BlendOp(Get("BlendOp", "ADD"));
        renderTarget.SrcBlendAlpha = Blend(Get("SrcBlendAlpha", "ONE"), D3D11_BLEND.D3D11_BLEND_ONE);
        renderTarget.DestBlendAlpha = Blend(Get("DestBlendAlpha", "ZERO"), D3D11_BLEND.D3D11_BLEND_ZERO);
        renderTarget.BlendOpAlpha = BlendOp(Get("BlendOpAlpha", "ADD"));
        renderTarget.RenderTargetWriteMask = properties.TryGetValue("RenderTargetWriteMask", out var mask) || properties.TryGetValue("ColorWriteMask", out mask)
            ? (byte)(int.TryParse(mask, out var parsed) ? parsed : 15)
            : (byte)15;
        blendDesc.RenderTarget[0] = renderTarget;

        ID3D11BlendState* blend;
        Check(device->CreateBlendState(&blendDesc, &blend));
        blendStates.Add((nint)blend);

        return new PassState
        {
            Info = pass,
            VertexShader = (ID3D11VertexShader*)vertexShaders[pass.VertexShader],
            PixelShader = (ID3D11PixelShader*)pixelShaders[pass.PixelShader],
            RenderTargets = targets.ToArray(),
            Clear = Flag("ClearRenderTargets", true),
            Blend = blend,
            Topology = Topology(Get("PrimitiveTopology", "TRIANGLELIST")),
            VertexCount = properties.TryGetValue("VertexCount", out var count) && uint.TryParse(count, out var vertexCount) ? vertexCount : 3,
        };
    }

    // ---- running ----

    /// <summary>Runs every pass of <paramref name="technique"/> over the frame's target.</summary>
    public void Run(FxTechniqueInfo technique, in FrameInputs frame)
    {
        UpdateConstants(frame);

        var context = frame.Context;
        var buffer = constantBuffer;
        context->VSSetConstantBuffers(0, 1, &buffer);
        context->PSSetConstantBuffers(0, 1, &buffer);

        var states = stackalloc ID3D11SamplerState*[Math.Max(1, samplerStates.Count)];
        for (var i = 0; i < samplerStates.Count; i++)
            states[i] = (ID3D11SamplerState*)samplerStates[i];
        context->PSSetSamplers(0, (uint)samplerStates.Count, states);

        var viewport = new D3D11_VIEWPORT(0, 0, frame.Width, frame.Height);
        var targetViews = stackalloc ID3D11RenderTargetView*[8];
        var boundTextures = stackalloc ID3D11Texture2D*[8];
        var views = stackalloc ID3D11ShaderResourceView*[Math.Max(1, Description.Samplers.Count)];
        var transparent = stackalloc float[4] { 0, 0, 0, 0 };
        var passList = passes[technique];
        for (var index = 0; index < passList.Length; index++)
        {
            var pass = passList[index];
            var targetCount = 0u;
            var writesTarget = pass.RenderTargets.Length == 0;

            if (writesTarget)
            {
                targetViews[0] = frame.TargetView;
                boundTextures[0] = frame.Target;
                targetCount = 1;
                viewport = new D3D11_VIEWPORT(0, 0, frame.Width, frame.Height);
            }
            else
            {
                foreach (var name in pass.RenderTargets)
                {
                    if (!textures.TryGetValue(name, out var resource) || resource.RenderTarget == null)
                        throw new FxException($"Pass {pass.Info.Name}: render target '{name}' is not available");
                    targetViews[targetCount] = resource.RenderTarget;
                    boundTextures[targetCount] = resource.Texture;
                    targetCount++;
                    viewport = new D3D11_VIEWPORT(0, 0, resource.Width, resource.Height);
                }
            }

            // Shader resources, with anything that is also a render target of this pass unbound.
            for (var i = 0; i < Description.Samplers.Count; i++)
            {
                var sampler = Description.Samplers[i];
                views[i] = ResolveView(sampler, frame, boundTextures, (int)targetCount);
            }

            context->PSSetShaderResources(0, (uint)Description.Samplers.Count, views);
            context->OMSetRenderTargets(targetCount, targetViews, null);

            if (pass.Clear && !writesTarget)
            {
                for (var i = 0; i < targetCount; i++)
                    context->ClearRenderTargetView(targetViews[i], transparent);
            }

            context->RSSetViewports(1, &viewport);
            context->OMSetBlendState(pass.Blend, null, 0xFFFFFFFF);
            context->IASetPrimitiveTopology(pass.Topology);
            context->VSSetShader(pass.VertexShader, null, 0);
            context->PSSetShader(pass.PixelShader, null, 0);
            context->Draw(pass.VertexCount, 0);

            // Leave nothing bound that the next pass or the game could trip over.
            for (var i = 0; i < Description.Samplers.Count; i++)
                views[i] = null;
            context->PSSetShaderResources(0, (uint)Description.Samplers.Count, views);
            context->OMSetRenderTargets(0, null, null);

            if (!writesTarget)
            {
                foreach (var name in pass.RenderTargets)
                {
                    var resource = textures[name];
                    if (resource.MipLevels > 1)
                        context->GenerateMips(resource.Linear);
                }
            }
            else if (index < passList.Length - 1)
            {
                // The next pass samples the image this one produced.
                context->CopyResource((ID3D11Resource*)frame.ColorCopy, (ID3D11Resource*)frame.Target);
            }
        }
    }

    private ID3D11ShaderResourceView* ResolveView(FxSamplerInfo sampler, in FrameInputs frame, ID3D11Texture2D** bound, int boundCount)
    {
        textureInfos.TryGetValue(sampler.TextureName, out var info);
        if (info?.Semantic == "COLOR")
            return sampler.Srgb ? frame.ColorSrgb : frame.ColorLinear;
        if (info?.Semantic == "DEPTH")
            return frame.Depth;

        if (!textures.TryGetValue(sampler.TextureName, out var resource))
            return null;

        for (var i = 0; i < boundCount; i++)
        {
            if (resource.Texture != null && resource.Texture == bound[i])
                return null;
        }

        return sampler.Srgb ? resource.Srgb : resource.Linear;
    }

    // ---- uniforms ----

    private void UpdateConstants(in FrameInputs frame)
    {
        if (constantBuffer == null)
            return;

        foreach (var uniform in Description.Uniforms)
        {
            var values = Values[uniform.Name];
            switch (uniform.Source)
            {
                case "frametime":
                    values[0] = frame.FrameTimeMs;
                    break;
                case "framecount":
                    values[0] = frame.FrameCount;
                    break;
                case "timer":
                    values[0] = frame.TimerMs;
                    break;
                case "bufferwidth":
                    values[0] = frame.Width;
                    break;
                case "bufferheight":
                    values[0] = frame.Height;
                    break;
                case "random":
                {
                    var min = ParseAnnotation(uniform, "ui_min", 0);
                    var max = ParseAnnotation(uniform, "ui_max", 1);
                    values[0] = uniform.Kind == "float" ? min + random.NextDouble() * (max - min) : random.Next((int)min, (int)max + 1);
                    break;
                }
                case "date":
                {
                    var now = DateTime.Now;
                    values[0] = now.Year;
                    if (values.Length > 1) values[1] = now.Month;
                    if (values.Length > 2) values[2] = now.Day;
                    if (values.Length > 3) values[3] = now.TimeOfDay.TotalSeconds;
                    break;
                }
            }

            for (var i = 0; i < uniform.Components; i++)
                Write(uniform, i, values[i]);
        }

        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(frame.Context->Map((ID3D11Resource*)constantBuffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
        constantData.AsSpan().CopyTo(new Span<byte>(mapped.pData, constantData.Length));
        frame.Context->Unmap((ID3D11Resource*)constantBuffer, 0);
    }

    private static double ParseAnnotation(FxUniformInfo uniform, string key, double fallback) =>
        uniform.Annotations.TryGetValue(key, out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void Write(FxUniformInfo uniform, int component, double value)
    {
        var span = constantData.AsSpan(uniform.Offset + component * 4, 4);
        switch (uniform.Kind)
        {
            case "float":
                BitConverter.TryWriteBytes(span, (float)value);
                break;
            case "int":
                BitConverter.TryWriteBytes(span, (int)value);
                break;
            default:
                BitConverter.TryWriteBytes(span, uniform.Kind == "bool" ? (value != 0 ? 1u : 0u) : (uint)value);
                break;
        }
    }

    // ---- lifetime ----

    private static void Check(HRESULT hr)
    {
        if (hr.FAILED)
            throw new FxException($"Direct3D call failed: HRESULT 0x{hr.Value:X8}");
    }

    private static void Release(void* pointer)
    {
        if (pointer != null)
            ((IUnknown*)pointer)->Release();
    }

    public void Dispose()
    {
        foreach (var shader in vertexShaders.Values) Release((void*)shader);
        foreach (var shader in pixelShaders.Values) Release((void*)shader);
        foreach (var state in samplerStates) Release((void*)state);
        foreach (var state in blendStates) Release((void*)state);
        foreach (var texture in textures.Values)
        {
            if (texture.Owner != null)
            {
                texture.Owner.Dispose();
                continue;
            }

            Release(texture.RenderTarget);
            Release(texture.Srgb);
            Release(texture.Linear);
            Release(texture.Texture);
        }

        Release(constantBuffer);
        vertexShaders.Clear();
        pixelShaders.Clear();
        samplerStates.Clear();
        blendStates.Clear();
        textures.Clear();
        constantBuffer = null;
    }
}
