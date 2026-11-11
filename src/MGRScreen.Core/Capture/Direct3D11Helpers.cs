using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace MGRScreen.Core.Capture;

/// <summary>
/// Ponte entre o mundo "gerenciado COM" (Vortice/Direct3D11 puro) e o mundo WinRT
/// (Windows.Graphics.Capture só entende IDirect3DDevice/IDirect3DSurface, não ID3D11Device
/// "cru"). Esse bridging é feito via a função nativa CreateDirect3D11DeviceFromDXGIDevice,
/// exportada por d3d11.dll — é o mesmo mecanismo usado em todos os samples oficiais de WGC.
/// </summary>
internal static class Direct3D11Helpers
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static ID3D11Device CreateD3DDevice()
    {
        FeatureLevel[] levels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        // BgraSupport é obrigatório: WGC entrega os frames em B8G8R8A8, e sem essa flag o
        // device D3D11 recusa criar/usar texturas nesse formato.
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device? device);

        result.CheckError();
        return device!;
    }

    public static IDirect3DDevice CreateDirect3DDeviceFromD3D11Device(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevicePtr);
        Marshal.ThrowExceptionForHR(hr);
        var device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevicePtr);
        Marshal.Release(graphicsDevicePtr);
        return device;
    }

    public static ID3D11Texture2D GetTexture2DFromSurface(IDirect3DSurface surface)
    {
        var pointer = CaptureInterop.GetDxgiInterface(surface, typeof(ID3D11Texture2D).GUID);
        try
        {
            // O construtor de ComObject do Vortice não faz AddRef; GetInterface (via
            // IDirect3DDxgiInterfaceAccess) já entrega um ponteiro com referência própria,
            // então basta envolver — sem Marshal.Release aqui, quem libera é o Dispose()
            // do ID3D11Texture2D retornado.
            return new ID3D11Texture2D(pointer);
        }
        catch
        {
            Marshal.Release(pointer);
            throw;
        }
    }
}
