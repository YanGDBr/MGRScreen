using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace MGRScreen.Core.Capture;

/// <summary>Uma câmera conectada nesta máquina.</summary>
public sealed record CameraInfo(string Id, string Name);

/// <summary>
/// Captura a webcam e entrega frames em BGRA — o mesmo formato que a captura de tela entrega, pra
/// câmera reaproveitar exatamente o mesmo encoder, relay e decoder da tela.
///
/// Usa o MediaCapture do próprio Windows (e não o dshow do ffmpeg) por dois motivos: o frame
/// chega aqui no C#, então dá pra mostrar o preview local sem um segundo processo; e o Windows
/// deixa duas apps lerem a mesma câmera ao mesmo tempo (modo compartilhado) — importante porque
/// o Discord também pode estar com a câmera aberta.
/// </summary>
public sealed class CameraCaptureService : IDisposable
{
    public event Action<CapturedFrame>? FrameCaptured;

    /// <summary>A câmera parou sozinha (desconectada, outro app tomou o controle...).</summary>
    public event Action<string>? Failed;

    /// <summary>Resolução e fps pedidos à câmera. 720p30 é o suficiente pra um rosto, e deixa a
    /// banda pra tela, que é o que importa.</summary>
    private const int MaxHeight = 720;
    private const int PreferredFps = 30;

    private readonly object _sync = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[]? _buffer;
    private volatile bool _running;

    /// <summary>Se a câmera ficou só em modo leitura (outro app já controla o formato dela).</summary>
    public bool IsSharedReadOnly { get; private set; }

    public static async Task<IReadOnlyList<CameraInfo>> ListCamerasAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();

        return groups
            .Where(g => g.SourceInfos.Any(IsColorVideo))
            .Select(g => new CameraInfo(g.Id, g.DisplayName))
            .ToList();
    }

    private static bool IsColorVideo(MediaFrameSourceInfo info) =>
        info.SourceKind == MediaFrameSourceKind.Color
        && info.MediaStreamType is MediaStreamType.VideoRecord or MediaStreamType.VideoPreview;

    public async Task StartAsync(string cameraId)
    {
        await StopAsync();

        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var group = groups.FirstOrDefault(g => g.Id == cameraId)
                    ?? throw new InvalidOperationException("A câmera não está mais conectada.");

        // Primeiro tenta controle exclusivo (pra poder escolher resolução/fps). Se outro app já
        // estiver usando a câmera — o Discord numa chamada de vídeo, por exemplo — cai pro modo
        // compartilhado: lê o que a câmera já está entregando, sem poder mudar o formato.
        try
        {
            _capture = await CreateCaptureAsync(group, MediaCaptureSharingMode.ExclusiveControl);
            IsSharedReadOnly = false;
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            _capture = await CreateCaptureAsync(group, MediaCaptureSharingMode.SharedReadOnly);
            IsSharedReadOnly = true;
        }

        _capture.Failed += (_, args) =>
        {
            Stop();
            Failed?.Invoke(args.Message);
        };

        var source = _capture.FrameSources.Values
            .Where(s => IsColorVideo(s.Info))
            .OrderBy(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord ? 0 : 1)
            .First();

        if (!IsSharedReadOnly)
        {
            var format = PickFormat(source.SupportedFormats);
            if (format is not null)
            {
                await source.SetFormatAsync(format);
            }
        }

        // Pedir BGRA8 aqui faz o próprio Windows converter (de NV12/YUY2/MJPG, o que a câmera
        // entregar) — e é o formato que o nosso encoder já consome da captura de tela.
        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;

        _running = true;
        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            Stop();
            throw new InvalidOperationException(status == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                ? "Outro programa está usando a câmera."
                : $"A câmera não iniciou ({status}).");
        }
    }

    private static async Task<MediaCapture> CreateCaptureAsync(MediaFrameSourceGroup group, MediaCaptureSharingMode mode)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = mode,
                StreamingCaptureMode = StreamingCaptureMode.Video,

                // Obrigatório: com o padrão (Auto) o frame pode vir só como superfície de GPU e o
                // SoftwareBitmap chega null — sem bytes pra mandar pro encoder.
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>A maior resolução até 720p, preferindo 30 fps ou mais.</summary>
    private static MediaFrameFormat? PickFormat(IReadOnlyList<MediaFrameFormat> formats)
    {
        static double Fps(MediaFrameFormat f) =>
            f.FrameRate.Denominator == 0 ? 0 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator;

        return formats
            .Where(f => f.VideoFormat.Height <= MaxHeight && f.VideoFormat.Height >= 240)
            .OrderByDescending(f => Fps(f) >= PreferredFps - 1)
            .ThenByDescending(f => f.VideoFormat.Height)
            .ThenBy(f => Math.Abs(Fps(f) - PreferredFps))
            .FirstOrDefault();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // Mesmo lock do Stop(): sem isso o Stop podia descartar o buffer no meio desta cópia
        // (o evento chega numa thread do Windows, o Stop vem da UI).
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            using var reference = sender.TryAcquireLatestFrame();
            var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null)
            {
                return;
            }

            var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? null
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

            try
            {
                var frame = converted ?? bitmap;
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;
                var size = width * height * 4;

                if (_buffer is null || _buffer.Length != size)
                {
                    _buffer = new byte[size];
                }

                frame.CopyToBuffer(_buffer.AsBuffer());

                FrameCaptured?.Invoke(new CapturedFrame { Data = _buffer, Width = width, Height = height });
            }
            finally
            {
                converted?.Dispose();
                bitmap.Dispose();
            }
        }
    }

    /// <summary>
    /// Desliga e ESPERA a câmera ser liberada — quem vai religar (trocar de câmera) precisa disso,
    /// senão a câmera ainda está presa na sessão anterior e o controle exclusivo falha.
    /// </summary>
    public async Task StopAsync()
    {
        MediaFrameReader? reader;
        MediaCapture? capture;

        lock (_sync)
        {
            _running = false;
            reader = _reader;
            capture = _capture;
            _reader = null;
            _capture = null;
            _buffer = null;
        }

        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try
            {
                await reader.StopAsync();
            }
            catch
            {
            }
            reader.Dispose();
        }

        capture?.Dispose();
    }

    /// <summary>Desliga sem esperar (pra UI). Frames que ainda cheguem são ignorados.</summary>
    public void Stop() => _ = StopAsync();

    public void Dispose() => Stop();
}
