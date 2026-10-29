using System;
using System.Text;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.DirectX;

namespace XIVShade.Effects;

internal static unsafe class ShaderCompiler
{
    /// <summary>Compiles one entry point with D3DCompile and returns the bytecode.</summary>
    public static byte[] Compile(string hlsl, string entry, string profile)
    {
        var source = Encoding.UTF8.GetBytes(hlsl);
        var entryName = Encoding.ASCII.GetBytes(entry + "\0");
        var targetName = Encoding.ASCII.GetBytes(profile + "\0");

        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        int hr;
        fixed (byte* pSource = source)
        fixed (byte* pEntry = entryName)
        fixed (byte* pTarget = targetName)
            hr = D3DCompile(pSource, (nuint)source.Length, null, null, null, (sbyte*)pEntry, (sbyte*)pTarget, 0, 0, &code, &errors);

        try
        {
            if (hr < 0)
            {
                var message = errors != null
                    ? Encoding.UTF8.GetString((byte*)errors->GetBufferPointer(), (int)errors->GetBufferSize()).Trim()
                    : $"HRESULT 0x{hr:X8}";
                throw new FxException($"{entry} ({profile}): {message}");
            }

            var bytes = new byte[code->GetBufferSize()];
            new ReadOnlySpan<byte>(code->GetBufferPointer(), bytes.Length).CopyTo(bytes);
            return bytes;
        }
        finally
        {
            if (code != null) code->Release();
            if (errors != null) errors->Release();
        }
    }
}
