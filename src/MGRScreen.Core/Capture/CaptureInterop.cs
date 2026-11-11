using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace MGRScreen.Core.Capture;

/// <summary>
/// GUID/interfaces COM de baixo nível, estáveis e documentadas, necessárias para usar WGC
/// (Windows.Graphics.Capture) a partir de um app desktop "clássico" (não empacotado/UWP).
/// </summary>
[ComImport]
[Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithWindow
{
    void Initialize(IntPtr hwnd);
}

/// <summary>
/// Permite extrair o IDXGISurface nativo de dentro de um IDirect3DSurface entregue pelo
/// Direct3D11CaptureFrame — necessário para copiar os pixels pra CPU via staging texture.
/// </summary>
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

/// <summary>
/// Permite criar um alvo de captura direto de um monitor (HMONITOR), sem passar pelo picker.
/// É o que torna possível o "compartilhar de novo" sem o usuário escolher a tela toda vez.
/// </summary>
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

internal static class CaptureInterop
{
    /// <summary>
    /// Pickers do Windows.Graphics.Capture (GraphicsCapturePicker) exigem uma janela "dona"
    /// (owner hwnd) quando chamados de um app desktop não empacotado — sem isso o picker não
    /// sabe em qual monitor exibir a UI de seleção. Este é o padrão oficial recomendado pela
    /// Microsoft para apps WinUI3/WPF não empacotados que usam pickers do WinRT.
    /// </summary>
    public static void InitializeWithWindow(object picker, IntPtr hwnd)
    {
        // IMPORTANTE: um cast C# direto ((IInitializeWithWindow)picker) NÃO funciona aqui e
        // lança InvalidCastException — objetos projetados via CsWinRT (como GraphicsCapturePicker)
        // não implementam interfaces [ComImport] arbitrárias como "interface .NET normal"; é
        // preciso passar pelo QueryInterface de verdade via WinRT.CastExtensions.As<T>(). Bug
        // real encontrado e corrigido nesta sessão (era a causa do crash ao clicar "Escolher tela").
        var initializeWithWindow = picker.As<IInitializeWithWindow>();
        initializeWithWindow.Initialize(hwnd);
    }

    public static IntPtr GetDxgiInterface(IDirect3DSurface surface, Guid iid)
    {
        // Mesmo motivo do comentário acima — surface também é um objeto CsWinRT.
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        return access.GetInterface(ref iid);
    }

    /// <summary>GUID de IDXGISurface, usado com <see cref="GetDxgiInterface"/>.</summary>
    public static readonly Guid IDXGISurfaceGuid = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>Cria o alvo de captura de um monitor específico (sem picker).</summary>
    public static Windows.Graphics.Capture.GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        var interop = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem")
            .AsInterface<IGraphicsCaptureItemInterop>();

        var iid = GraphicsCaptureItemGuid;
        var abi = interop.CreateForMonitor(hMonitor, ref iid);

        return WinRT.MarshalInterface<Windows.Graphics.Capture.GraphicsCaptureItem>.FromAbi(abi);
    }
}
