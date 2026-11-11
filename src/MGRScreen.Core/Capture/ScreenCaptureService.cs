using System.Diagnostics;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace MGRScreen.Core.Capture;

/// <summary>
/// Um frame capturado. ATENÇÃO: <see cref="Data"/> é um buffer REUTILIZADO entre frames — só é
/// válido durante a execução do handler do evento. Se precisar guardar os pixels, copie.
/// (Alocar um array novo por frame significaria ~8 MB no Large Object Heap 60x por segundo, o
/// que derrubava o FPS real com coletas de lixo constantes.)
/// </summary>
public readonly struct CapturedFrame
{
    public required byte[] Data { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>
/// Captura de tela via Windows.Graphics.Capture (WGC) — a mesma API usada por OBS, Discord e
/// Xbox Game Bar. Fluxo: usuário escolhe monitor/janela no picker nativo do Windows -> criamos
/// um Direct3D11CaptureFramePool "free-threaded" -> a cada frame copiamos a textura da GPU pra
/// uma staging texture e lemos os bytes (BGRA) pra CPU -> evento <see cref="FrameCaptured"/>.
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    public event Action<CapturedFrame>? FrameCaptured;

    /// <summary>
    /// Limita quantos frames por segundo são efetivamente copiados da GPU pra CPU. A WGC entrega
    /// frames na taxa do monitor; frames acima desse limite são descartados ANTES da cópia
    /// GPU->CPU (a parte cara), em vez de depois.
    /// </summary>
    private volatile int _targetFps = 60;
    public int TargetFps { get => _targetFps; set => _targetFps = value; }

    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _stagingTexture;
    private byte[]? _frameBuffer;
    private int _stagingWidth;
    private int _stagingHeight;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _nextFrameTicks;
    private int _pacedFps;

    /// <summary>
    /// Abre o picker nativo do Windows para o usuário escolher um monitor/janela e inicia a
    /// captura. <paramref name="ownerHwnd"/> é o HWND da janela do app (necessário para o
    /// picker saber onde exibir a UI de seleção em apps desktop não empacotados).
    /// </summary>
    /// <summary>
    /// Qual monitor foi capturado da última vez (nome de dispositivo do Windows, tipo
    /// "\\.\DISPLAY1"), ou null se foi uma janela / não deu pra identificar. É o que permite
    /// oferecer "compartilhar de novo" sem abrir o seletor.
    /// </summary>
    public string? LastMonitorDeviceName { get; private set; }

    public async Task<bool> StartAsync(IntPtr ownerHwnd)
    {
        var picker = new GraphicsCapturePicker();
        CaptureInterop.InitializeWithWindow(picker, ownerHwnd);

        var item = await picker.PickSingleItemAsync();
        if (item is null)
        {
            return false; // usuário cancelou o picker
        }

        LastMonitorDeviceName = IdentifyMonitor(item);

        await StartForItemAsync(item);
        return true;
    }

    /// <summary>Captura um monitor conhecido direto, sem passar pelo seletor do Windows.</summary>
    public async Task<bool> StartForMonitorAsync(string deviceName)
    {
        var monitor = MonitorEnumerator.GetMonitors().FirstOrDefault(m => m.DeviceName == deviceName);
        if (monitor is null)
        {
            return false; // monitor foi desconectado desde a última vez
        }

        var item = CaptureInterop.CreateItemForMonitor(monitor.Handle);
        LastMonitorDeviceName = deviceName;

        await StartForItemAsync(item);
        return true;
    }

    /// <summary>
    /// Descobre se o que o usuário escolheu no seletor é um monitor, casando o tamanho do alvo
    /// com o dos monitores. Se dois monitores tiverem exatamente a mesma resolução não dá pra
    /// saber qual é, e aí prefiro não lembrar nada a lembrar errado e compartilhar a tela errada
    /// da próxima vez. (O WGC não expõe o HMONITOR do item escolhido.)
    /// </summary>
    private static string? IdentifyMonitor(GraphicsCaptureItem item)
    {
        var matches = MonitorEnumerator.GetMonitors()
            .Where(m => m.Width == item.Size.Width && m.Height == item.Size.Height)
            .ToList();

        return matches.Count == 1 ? matches[0].DeviceName : null;
    }

    private async Task StartForItemAsync(GraphicsCaptureItem item)
    {
        _d3dDevice = Direct3D11Helpers.CreateD3DDevice();
        _d3dContext = _d3dDevice.ImmediateContext;
        var winrtDevice = Direct3D11Helpers.CreateDirect3DDeviceFromD3D11Device(_d3dDevice);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            item.Size);

        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(item);

        try
        {
            _session.IsCursorCaptureEnabled = false; // não queremos o cursor do mouse no stream
        }
        catch
        {
            // Propriedade só existe a partir de builds mais novas do Windows 10/11.
        }

        // Remove a borda amarela que o Windows desenha em volta do que está sendo capturado.
        // Exige Windows 11 (build 22000+) e uma permissão explícita de "captura sem borda";
        // no Windows 10 as duas chamadas simplesmente falham e a borda continua aparecendo.
        try
        {
            await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            _session.IsBorderRequired = false;
        }
        catch
        {
        }

        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        // Keep the cadence anchored to its deadline, not the last callback. A slightly
        // early callback at 60 Hz must not cause a whole refresh to be skipped.
        var fps = Math.Max(1, TargetFps);
        var interval = (double)Stopwatch.Frequency / fps;
        var now = _clock.ElapsedTicks;
        if (_pacedFps != fps || now >= _nextFrameTicks + interval)
        {
            _pacedFps = fps;
            _nextFrameTicks = now;
        }
        if (now + Math.Min(interval / 4, Stopwatch.Frequency * 0.0015) < _nextFrameTicks)
        {
            return;
        }
        _nextFrameTicks += interval;

        var width = frame.ContentSize.Width;
        var height = frame.ContentSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // O monitor/janela pode ser redimensionado a qualquer momento — o frame pool precisa
        // ser recriado no novo tamanho, e a staging texture/buffer também.
        if (width != _stagingWidth || height != _stagingHeight)
        {
            sender.Recreate(
                Direct3D11Helpers.CreateDirect3DDeviceFromD3D11Device(_d3dDevice!),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                frame.ContentSize);

            _stagingTexture?.Dispose();
            _stagingTexture = CreateStagingTexture(width, height);
            _frameBuffer = new byte[width * height * 4];
            _stagingWidth = width;
            _stagingHeight = height;
        }

        using var sourceTexture = Direct3D11Helpers.GetTexture2DFromSurface(frame.Surface);
        _d3dContext!.CopySubresourceRegion(_stagingTexture!, 0, 0, 0, 0, sourceTexture, 0, null);

        var mapped = _d3dContext.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var data = _frameBuffer!;
            unsafe
            {
                var src = (byte*)mapped.DataPointer;
                fixed (byte* destPtr = data)
                {
                    var rowBytes = width * 4;
                    if (mapped.RowPitch == (uint)rowBytes)
                    {
                        Buffer.MemoryCopy(src, destPtr, data.Length, data.Length);
                    }
                    else
                    {
                        for (var row = 0; row < height; row++)
                        {
                            Buffer.MemoryCopy(src + (row * mapped.RowPitch), destPtr + (row * rowBytes), rowBytes, rowBytes);
                        }
                    }
                }
            }

            FrameCaptured?.Invoke(new CapturedFrame { Data = data, Width = width, Height = height });
        }
        finally
        {
            _d3dContext.Unmap(_stagingTexture!, 0);
        }
    }

    private ID3D11Texture2D CreateStagingTexture(int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        };
        return _d3dDevice!.CreateTexture2D(description);
    }

    public void Stop()
    {
        _session?.Dispose();
        _session = null;

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }

        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _frameBuffer = null;
        _stagingWidth = 0;
        _stagingHeight = 0;
        _pacedFps = 0;

        _d3dContext?.Dispose();
        _d3dContext = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

    public void Dispose() => Stop();
}
