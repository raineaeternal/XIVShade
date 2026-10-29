using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect;
using FFXIVClientStructs.Interop;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace XIVShade;

/// <summary>
/// Runs a fullscreen pixel shader over the game's scene image, after the post-effect chain and before the UI is drawn.
/// The game records its render commands on the main thread and executes them on the render thread, so the pass is
/// queued as a callback command rather than run from a hook.
/// </summary>
public sealed unsafe class PostProcessor : IDisposable
{
    // Mirrors the HLSL shape reshadefx generates (sampler as a struct, samplers passed into functions,
    // uniforms in a cbuffer) so we know the game's shader compiler accepts it.
    private const string Shader = @"
struct __sampler2D_float4 { Texture2D<float4> t; SamplerState s; };

Texture2D<float4> BackBufferTex : register(t0);
SamplerState BackBufferSamp : register(s0);
static const __sampler2D_float4 BackBuffer = { BackBufferTex, BackBufferSamp };

cbuffer _Globals : register(b0)
{
    float Strength = 1.0;
};

float4 tex2D(__sampler2D_float4 s, float2 uv) { return s.t.Sample(s.s, uv); }

float3 ApplyLuma(__sampler2D_float4 s, float2 uv, float amount)
{
    float3 c = tex2D(s, uv).rgb;
    float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
    return lerp(c, luma.xxx, amount);
}

void VS(uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD0)
{
    uv = float2((id << 1) & 2, id & 2);
    pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    return float4(ApplyLuma(BackBuffer, uv, 1.0), 1.0);
}";

    // Command buffer layout of a callback command (ImmediateContext.ExecuteCommands, type 0xE): function at +0x8,
    // argument at +0x10, "reset the state cache around the call" flag at +0x18.
    private const uint CallbackCommandType = 0xE;
    private const int CallbackSize = 0x20;

    private delegate void CallbackDelegate(nint* arguments);
    private delegate void RenderChainDelegate(PostEffectChain* chain);
    private delegate int PresentDelegate(IDXGISwapChain* swapChain, uint syncInterval, uint flags);

    // PostEffectChain.Render: records the commands for each part of the chain.
    private const string RenderChainSignature = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 65 48 8B 04 25 ?? ?? ?? ?? 48 8B F9";

    private readonly IPluginLog log;
    private readonly Hook<RenderChainDelegate> renderChainHook;
    private readonly Hook<PresentDelegate> presentHook;

    // Held for the plugin's lifetime: the render thread calls it from the command buffer.
    private readonly CallbackDelegate callback;
    private readonly nint callbackFunction;
    private volatile bool stopped;
    private volatile bool enabled;

    private ID3D11Device* device;
    private ID3D11DeviceContext* context;
    private ID3D11VertexShader* vertexShader;
    private ID3D11PixelShader* pixelShader;
    private ID3D11SamplerState* sampler;
    private ID3D11RasterizerState* rasterizer;

    private ID3D11Texture2D* copy;
    private ID3D11ShaderResourceView* copyView;
    private uint copyWidth, copyHeight;
    private DXGI_FORMAT copyFormat;

    public PostProcessor(ISigScanner sigScanner, IGameInteropProvider interop, IPluginLog log)
    {
        this.log = log;

        var gameDevice = Device.Instance();
        if (gameDevice == null)
            throw new InvalidOperationException("Game renderer does not exist");

        var swapChain = (IDXGISwapChain*)gameDevice->SwapChain->DXGISwapChain;
        fixed (ID3D11Device** pDevice = &device)
            ThrowIfFailed(swapChain->GetDevice(__uuidof<ID3D11Device>(), (void**)pDevice));
        fixed (ID3D11DeviceContext** pContext = &context)
            device->GetImmediateContext(pContext);

        CreatePipeline();

        callback = Run;
        callbackFunction = Marshal.GetFunctionPointerForDelegate(callback);

        // The scene is finished once the vignetting chain's commands run, and the UI is drawn on top afterwards.
        // Queue ours right after that chain's commands, with the same view and sort key, so it executes next
        // (commands are merge-sorted by key, equal keys keep submission order).
        renderChainHook = interop.HookFromAddress<RenderChainDelegate>(sigScanner.ScanText(RenderChainSignature), RenderChainDetour);
        renderChainHook.Enable();

        // For IncludeUi: the UI may be drawn on its own layer and frames may be generated and written straight to
        // the back buffer (UpscaleBuddy), so the only place every presented image, UI included, is reliably
        // visible is the swapchain's Present.
        presentHook = interop.HookFromAddress<PresentDelegate>((*(nint**)swapChain)[8], PresentDetour);
        presentHook.Enable();
        log.Information("Post-processor installed");
    }

    public bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }

    /// <summary>Apply the effect after the UI has been drawn, instead of to the scene only.</summary>
    public bool IncludeUi { get; set; }


    private static void ThrowIfFailed(HRESULT hr)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"HRESULT 0x{hr.Value:X8}");
    }

    private ID3DBlob* Compile(string entry, string target)
    {
        var source = Encoding.ASCII.GetBytes(Shader);
        var entryName = Encoding.ASCII.GetBytes(entry + "\0");
        var targetName = Encoding.ASCII.GetBytes(target + "\0");

        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        HRESULT hr;
        fixed (byte* pSource = source)
        fixed (byte* pEntry = entryName)
        fixed (byte* pTarget = targetName)
            hr = D3DCompile(pSource, (nuint)source.Length, null, null, null, (sbyte*)pEntry, (sbyte*)pTarget, 0, 0, &code, &errors);

        if (hr.FAILED)
        {
            var message = errors != null
                ? Encoding.ASCII.GetString((byte*)errors->GetBufferPointer(), (int)errors->GetBufferSize())
                : $"HRESULT 0x{hr.Value:X8}";
            if (errors != null) errors->Release();
            throw new InvalidOperationException($"Shader compile failed ({entry}): {message}");
        }

        if (errors != null) errors->Release();
        return code;
    }

    private void CreatePipeline()
    {
        var vs = Compile("VS", "vs_5_0");
        var ps = Compile("PS", "ps_5_0");
        try
        {
            fixed (ID3D11VertexShader** pVs = &vertexShader)
                ThrowIfFailed(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), null, pVs));
            fixed (ID3D11PixelShader** pPs = &pixelShader)
                ThrowIfFailed(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), null, pPs));
        }
        finally
        {
            vs->Release();
            ps->Release();
        }

        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_POINT,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            MaxLOD = float.MaxValue,
        };
        fixed (ID3D11SamplerState** pSampler = &sampler)
            ThrowIfFailed(device->CreateSamplerState(&samplerDesc, pSampler));

        var rasterizerDesc = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
            DepthClipEnable = true,
        };
        fixed (ID3D11RasterizerState** pRasterizer = &rasterizer)
            ThrowIfFailed(device->CreateRasterizerState(&rasterizerDesc, pRasterizer));
    }

    private void ReleaseCopy()
    {
        if (copyView != null) { copyView->Release(); copyView = null; }
        if (copy != null) { copy->Release(); copy = null; }
        copyWidth = copyHeight = 0;
    }

    private void EnsureCopy(D3D11_TEXTURE2D_DESC backDesc)
    {
        if (copy != null && copyWidth == backDesc.Width && copyHeight == backDesc.Height && copyFormat == backDesc.Format)
            return;

        ReleaseCopy();

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = backDesc.Width,
            Height = backDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = backDesc.Format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        fixed (ID3D11Texture2D** pCopy = &copy)
            ThrowIfFailed(device->CreateTexture2D(&desc, null, pCopy));
        fixed (ID3D11ShaderResourceView** pView = &copyView)
            ThrowIfFailed(device->CreateShaderResourceView((ID3D11Resource*)copy, null, pView));

        copyWidth = backDesc.Width;
        copyHeight = backDesc.Height;
        copyFormat = backDesc.Format;
    }

    /// <summary>Runs the effect over <paramref name="target"/>, reading a copy of it and drawing back through <paramref name="rtv"/>.</summary>
    private void Apply(ID3D11Texture2D* target, ID3D11RenderTargetView* rtv)
    {
        D3D11_TEXTURE2D_DESC desc;
        target->GetDesc(&desc);
        EnsureCopy(desc);

        context->CopyResource((ID3D11Resource*)copy, (ID3D11Resource*)target);

        var saved = new SavedState(context);
        try
        {
            var viewport = new D3D11_VIEWPORT(0, 0, desc.Width, desc.Height);
            context->IASetInputLayout(null);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(vertexShader, null, 0);
            context->HSSetShader(null, null, 0);
            context->DSSetShader(null, null, 0);
            context->GSSetShader(null, null, 0);
            context->PSSetShader(pixelShader, null, 0);

            var view = copyView;
            var samp = sampler;
            context->PSSetShaderResources(0, 1, &view);
            context->PSSetSamplers(0, 1, &samp);

            context->RSSetState(rasterizer);
            context->RSSetViewports(1, &viewport);
            context->OMSetBlendState(null, null, 0xFFFFFFFF);
            context->OMSetDepthStencilState(null, 0);
            var target0 = rtv;
            context->OMSetRenderTargets(1, &target0, null);

            context->Draw(3, 0);
        }
        finally
        {
            saved.Restore(context);
        }
    }

    /// <summary>Main thread: a post-effect chain's commands are being recorded.</summary>
    private void RenderChainDetour(PostEffectChain* chain)
    {
        renderChainHook.Original(chain);

        if (!Enabled || stopped || IncludeUi)
            return;

        try
        {
            if (chain == &PostEffectManager.Instance()->VignettingChain)
                QueueCallback();
        }
        catch (Exception ex)
        {
            Enabled = false;
            log.Error(ex, "Failed to queue post-processing, disabling");
        }
    }

    /// <summary>Render thread: a frame is about to be presented.</summary>
    private int PresentDetour(IDXGISwapChain* swapChain, uint syncInterval, uint flags)
    {
        if (Enabled && IncludeUi && !stopped)
        {
            try
            {
                ApplyToBackBuffer(swapChain);
            }
            catch (Exception ex)
            {
                Enabled = false;
                log.Error(ex, "Post-processing failed, disabling");
            }
        }

        return presentHook.Original(swapChain, syncInterval, flags);
    }

    private void ApplyToBackBuffer(IDXGISwapChain* swapChain)
    {
        ID3D11Texture2D* backBuffer = null;
        ThrowIfFailed(swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&backBuffer));

        ID3D11RenderTargetView* rtv = null;
        try
        {
            ThrowIfFailed(device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, &rtv));
            Apply(backBuffer, rtv);
        }
        finally
        {
            if (rtv != null) rtv->Release();
            backBuffer->Release();
        }
    }

    private void QueueCallback()
    {
        var kernelContext = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
        var command = (byte*)kernelContext->AllocateCommand(CallbackSize);
        if (command == null)
            return;

        *(uint*)command = CallbackCommandType;
        *(nint*)(command + 0x8) = callbackFunction;
        *(nint*)(command + 0x10) = 0;
        command[0x18] = 1; // callbacks that change state directly need the game to reset its state cache around them
        kernelContext->PushBackCommand(command);
    }

    /// <summary>Render thread, after the target texture is complete.</summary>
    private void Run(nint* arguments)
    {
        if (!Enabled || stopped)
            return;

        try
        {
            // The last part of the vignetting chain writes the display-resolution image the UI is then drawn on.
            var chain = &PostEffectManager.Instance()->VignettingChain;
            if (chain->PartCount == 0)
                return;

            var output = chain->Parts[chain->PartCount - 1]->OutputTextures[0].Value;
            if (output == null || output->D3D11Texture2D == null || output->MipRenderTargets == null)
                return;

            Apply((ID3D11Texture2D*)output->D3D11Texture2D, (ID3D11RenderTargetView*)output->GetMipRenderTarget(0)->D3D11RenderTargetViewOrDepthStencilView);
        }
        catch (Exception ex)
        {
            Enabled = false;
            log.Error(ex, "Post-processing failed, disabling");
        }
    }

    public void Dispose()
    {
        Enabled = false;
        stopped = true;
        renderChainHook.Dispose();
        presentHook.Dispose();

        // Commands already queued still call into this plugin; give the render thread a moment to drain them.
        Thread.Sleep(250);

        ReleaseCopy();
        if (rasterizer != null) rasterizer->Release();
        if (sampler != null) sampler->Release();
        if (pixelShader != null) pixelShader->Release();
        if (vertexShader != null) vertexShader->Release();
        if (context != null) context->Release();
        if (device != null) device->Release();
    }

    /// <summary>The pipeline state touched by <see cref="Apply"/>, restored afterwards so the game and other plugins are unaffected.</summary>
    private struct SavedState
    {
        private ID3D11InputLayout* inputLayout;
        private D3D_PRIMITIVE_TOPOLOGY topology;
        private ID3D11VertexShader* vs;
        private ID3D11HullShader* hs;
        private ID3D11DomainShader* ds;
        private ID3D11GeometryShader* gs;
        private ID3D11PixelShader* ps;
        private ID3D11ShaderResourceView* psView;
        private ID3D11SamplerState* psSampler;
        private ID3D11RasterizerState* rasterizer;
        private uint viewportCount;
        private fixed byte viewports[16 * 24];
        private ID3D11BlendState* blend;
        private fixed float blendFactor[4];
        private uint sampleMask;
        private ID3D11DepthStencilState* depthStencil;
        private uint stencilRef;
        private fixed ulong rtvs[8];
        private ID3D11DepthStencilView* dsv;

        public SavedState(ID3D11DeviceContext* c)
        {
            this = default;
            fixed (ID3D11InputLayout** p = &inputLayout) c->IAGetInputLayout(p);
            fixed (D3D_PRIMITIVE_TOPOLOGY* p = &topology) c->IAGetPrimitiveTopology(p);
            fixed (ID3D11VertexShader** p = &vs) c->VSGetShader(p, null, null);
            fixed (ID3D11HullShader** p = &hs) c->HSGetShader(p, null, null);
            fixed (ID3D11DomainShader** p = &ds) c->DSGetShader(p, null, null);
            fixed (ID3D11GeometryShader** p = &gs) c->GSGetShader(p, null, null);
            fixed (ID3D11PixelShader** p = &ps) c->PSGetShader(p, null, null);
            fixed (ID3D11ShaderResourceView** p = &psView) c->PSGetShaderResources(0, 1, p);
            fixed (ID3D11SamplerState** p = &psSampler) c->PSGetSamplers(0, 1, p);
            fixed (ID3D11RasterizerState** p = &rasterizer) c->RSGetState(p);
            viewportCount = 16;
            fixed (byte* p = viewports)
            fixed (uint* n = &viewportCount) c->RSGetViewports(n, (D3D11_VIEWPORT*)p);
            fixed (ID3D11BlendState** p = &blend)
            fixed (float* f = blendFactor)
            fixed (uint* m = &sampleMask) c->OMGetBlendState(p, f, m);
            fixed (ID3D11DepthStencilState** p = &depthStencil)
            fixed (uint* r = &stencilRef) c->OMGetDepthStencilState(p, r);
            fixed (ulong* p = rtvs)
            fixed (ID3D11DepthStencilView** d = &dsv) c->OMGetRenderTargets(8, (ID3D11RenderTargetView**)p, d);
        }

        public void Restore(ID3D11DeviceContext* c)
        {
            c->IASetInputLayout(inputLayout);
            c->IASetPrimitiveTopology(topology);
            c->VSSetShader(vs, null, 0);
            c->HSSetShader(hs, null, 0);
            c->DSSetShader(ds, null, 0);
            c->GSSetShader(gs, null, 0);
            c->PSSetShader(ps, null, 0);
            var view = psView;
            c->PSSetShaderResources(0, 1, &view);
            var samp = psSampler;
            c->PSSetSamplers(0, 1, &samp);
            c->RSSetState(rasterizer);
            fixed (byte* p = viewports) c->RSSetViewports(viewportCount, (D3D11_VIEWPORT*)p);
            fixed (float* f = blendFactor) c->OMSetBlendState(blend, f, sampleMask);
            c->OMSetDepthStencilState(depthStencil, stencilRef);
            fixed (ulong* p = rtvs) c->OMSetRenderTargets(8, (ID3D11RenderTargetView**)p, dsv);

            Release(inputLayout); Release(vs); Release(hs); Release(ds); Release(gs); Release(ps);
            Release(psView); Release(psSampler); Release(rasterizer); Release(blend);
            Release(depthStencil); Release(dsv);
            for (var i = 0; i < 8; i++)
                Release((IUnknown*)rtvs[i]);
        }

        private static void Release(void* p)
        {
            if (p != null) ((IUnknown*)p)->Release();
        }
    }
}
