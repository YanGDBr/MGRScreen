using System.Runtime.InteropServices;

namespace MGRScreen.Core.Capture;

/// <summary>
/// Compila HLSL em tempo de execução usando o d3dcompiler_47.dll que já vem com o Windows 10/11.
/// Usado pelos shaders de conversão de cor: BGRA -> NV12 na captura e YUV -> RGB na exibição.
/// </summary>
public static class HlslCompiler
{
    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(
        byte[] source, nuint sourceSize, string? sourceName, IntPtr defines, IntPtr include,
        string entryPoint, string target, uint flags1, uint flags2,
        [MarshalAs(UnmanagedType.Interface)] out ID3DBlob? code,
        [MarshalAs(UnmanagedType.Interface)] out ID3DBlob? errors);

    public static byte[] Compile(string source, string entryPoint, string profile)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(source);
        var hr = D3DCompile(bytes, (nuint)bytes.Length, null, IntPtr.Zero, IntPtr.Zero, entryPoint, profile, 0, 0,
            out var code, out var errors);

        try
        {
            if (hr < 0 || code is null)
            {
                var message = errors is null
                    ? $"HRESULT 0x{hr:X8}"
                    : Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize());
                throw new InvalidOperationException($"Falha ao compilar o shader {entryPoint}: {message}");
            }

            var result = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), result, 0, result.Length);
            return result;
        }
        finally
        {
            if (code is not null) Marshal.ReleaseComObject(code);
            if (errors is not null) Marshal.ReleaseComObject(errors);
        }
    }
}
