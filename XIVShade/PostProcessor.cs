using System;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace XIVShade;

/// <summary>
/// Runs a fullscreen pixel shader over the game's backbuffer just before it is presented.
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

    private delegate int PresentDelegate(IDXGISwapChain* swapChain, uint syncInterval, uint flags);
    private delegate int ResizeBuffersDelegate(IDXGISwapChain* swapChain, uint bufferCount, uint width, uint height, DXGI_FORMAT format, uint flags);

    private readonly IPluginLog log;
    private readonly Hook<PresentDelegate> presentHook;
    private readonly Hook<ResizeBuffersDelegate> resizeHook;

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

    public PostProcessor(IGameInteropProvider interop, IPluginLog log)
    {
        this.log = log;

        var gameDevice = Device.Instance();
        if (gameDevice == null)
            throw new InvalidOperationException("Game renderer does not exist");

        var swapChain = (IDXGISwapChain*)gameDevice->SwapChain->DXGISwapChain;
        var vtable = *(nint**)swapChain;

        fixed (ID3D11Device** pDevice = &device)
            ThrowIfFailed(swapChain->GetDevice(__uuidof<ID3D11Device>(), (void**)pDevice));
        fixed (ID3D11DeviceContext** pContext = &context)
            device->GetImmediateContext(pContext);

        CreatePipeline();

        presentHook = interop.HookFromAddress<PresentDelegate>(vtable[8], PresentDetour);
        resizeHook = interop.HookFromAddress<ResizeBuffersDelegate>(vtable[13], ResizeBuffersDetour);
        presentHook.Enable();
        resizeHook.Enable();
        log.Information("Post-processor installed");
    }

    public bool Enabled { get; set; }

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

    private void Apply(IDXGISwapChain* swapChain)
    {
        ID3D11Texture2D* backBuffer = null;
        ThrowIfFailed(swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&backBuffer));

        ID3D11RenderTargetView* rtv = null;
        try
        {
            D3D11_TEXTURE2D_DESC backDesc;
            backBuffer->GetDesc(&backDesc);
            EnsureCopy(backDesc);

            context->CopyResource((ID3D11Resource*)copy, (ID3D11Resource*)backBuffer);
            ThrowIfFailed(device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, &rtv));

            var saved = new SavedState(context);
            try
            {
                var viewport = new D3D11_VIEWPORT(0, 0, backDesc.Width, backDesc.Height);
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
                context->OMSetRenderTargets(1, &rtv, null);

                context->Draw(3, 0);
            }
            finally
            {
                saved.Restore(context);
            }
        }
        finally
        {
            if (rtv != null) rtv->Release();
            backBuffer->Release();
        }
    }

    private int PresentDetour(IDXGISwapChain* swapChain, uint syncInterval, uint flags)
    {
        if (Enabled)
        {
            try
            {
                Apply(swapChain);
            }
            catch (Exception ex)
            {
                Enabled = false;
                log.Error(ex, "Post-processing failed, disabling");
            }
        }

        return presentHook.Original(swapChain, syncInterval, flags);
    }

    private int ResizeBuffersDetour(IDXGISwapChain* swapChain, uint bufferCount, uint width, uint height, DXGI_FORMAT format, uint flags)
    {
        ReleaseCopy();
        return resizeHook.Original(swapChain, bufferCount, width, height, format, flags);
    }

    public void Dispose()
    {
        Enabled = false;
        presentHook.Dispose();
        resizeHook.Dispose();

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
