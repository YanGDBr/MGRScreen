using System.Diagnostics;
using MGRScreen.Core.Networking;

namespace MGRScreen.Core.Encoding;

/// <summary>
/// Um frame decodificado. ATENÇÃO: <see cref="Data"/> é um buffer REUTILIZADO entre frames — só
/// vale durante a execução do handler. Quem precisa guardar deve copiar. (Alocar 8 MB por frame
/// jogava tudo no Large Object Heap dezenas de vezes por segundo e derrubava o FPS real.)
/// </summary>
public readonly struct DecodedFrame
{
    public required byte[] Data { get; init; } // BGRA32
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>
/// Lado do Viewer: alimenta um processo `ffmpeg` com o stream H.264 elementary (Annex-B) bruto
/// recebido pela rede (via stdin) e recebe de volta frames de vídeo decodificados em BGRA32
/// (via stdout). Só vídeo — o áudio viaja num canal de rede separado, sem passar pelo ffmpeg
/// (ver <see cref="FfmpegEncoder"/> e docs/ARCHITECTURE.md sobre o porquê).
/// </summary>
public sealed class FfmpegDecoder : IDisposable
{
    public event Action<DecodedFrame>? FrameDecoded;
    public event Action<string>? FfmpegLogLine;

    private readonly int _width;
    private readonly int _height;
    private readonly string _ffmpegPath;
    private readonly bool _useHardwareDecode;

    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private readonly object _stopLock = new();
    private Task? _stopTask;

    // Fila de entrada com um único escritor e limite por tempo: se o ffmpeg não consumir em
    // 500 ms, o certo é descartar e voltar limpo no próximo keyframe, e não mostrar imagem de
    // 5 segundos atrás.
    private readonly MediaQueue _input = new(TimeSpan.FromMilliseconds(300), maxBytes: 8 * 1024 * 1024);

    private bool _awaitingKeyframe;
    private long _resyncStartedAt;
    private int _droppedChunks;

    public FfmpegDecoder(int width, int height, string ffmpegPath, bool useHardwareDecode = true)
    {
        _width = width;
        _height = height;
        _ffmpegPath = ffmpegPath;
        _useHardwareDecode = useHardwareDecode;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // Container: H.264 elementary stream cru (Annex-B), não MPEG-TS. Validado com testes de
        // ponta a ponta: o demuxer mpegts, alimentado por um pipe "ao vivo" (em vez de um arquivo
        // completo), tem probing bem mais instável que o elementary stream puro.
        //
        // Os flags abaixo derrubaram a latência do pipeline de ~540 ms para ~70 ms (medido com o
        // método do "flash": manda frame escuro, dispara um branco e cronometra até ele sair
        // decodificado). O que cada um faz:
        //   -analyzeduration 0   antes estava em 500000 (meio segundo!) e sozinho respondia por
        //                        ~370 ms da latência.
        //   -flags low_delay     desliga o buffer de reordenação do decoder.
        //   -flush_packets 1     não segura pacote esperando encher buffer de saída.
        //
        // ATENÇÃO: NÃO adicionar `-fflags nobuffer` aqui. Foi medido: ele não melhora a latência
        // (~70 ms com ou sem) e faz quem entra no meio da transmissão esperar ~2 GOPs pela
        // primeira imagem (1,8 s em vez de ~70 ms).
        // -hwaccel d3d11va manda a decodificação pra GPU. É best-effort de propósito: se a máquina
        // não suportar, o ffmpeg avisa no stderr e decodifica em software do mesmo jeito — por isso
        // não precisa de detecção prévia nem de caminho de fallback nosso. Com 4 pessoas
        // compartilhando, são 4 decoders rodando ao mesmo tempo, e aí isso pesa de verdade.
        var hardware = _useHardwareDecode ? "-hwaccel d3d11va " : "";

        var args = $"-hide_banner -loglevel warning {hardware}" +
                   $"-probesize 32 -analyzeduration 0 -flags low_delay -flush_packets 1 " +
                   $"-f h264 -i pipe:0 -map 0:v -pix_fmt bgra -f rawvideo pipe:1 ";

        var psi = new ProcessStartInfo(_ffmpegPath, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao iniciar ffmpeg.exe");

        Task.Run(() => PumpStdErrAsync(_cts.Token));
        _stdoutTask = Task.Run(() => PumpVideoAsync(_cts.Token));
        Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Alimenta o decoder com bytes brutos recebidos do media channel (TCP). Não bloqueia e não
    /// acumula: se o ffmpeg não estiver dando conta (a máquina está ocupada com um jogo, por
    /// exemplo), os pedaços são descartados até o próximo keyframe.
    ///
    /// Antes isto era um `await stream.WriteAsync` chamado como fire-and-forget a cada chunk que
    /// chegava da rede. Dois problemas: (1) fila infinita na frente de um decoder lento — segundos
    /// de atraso que nunca voltavam; (2) várias escritas simultâneas no MESMO stdin, que podiam se
    /// intercalar e corromper o stream H.264.
    /// </summary>
    public void SubmitChunk(byte[] data)
    {
        if (_awaitingKeyframe)
        {
            var waitedTooLong = Environment.TickCount64 - _resyncStartedAt > 3000;

            if (!H264Inspector.ContainsKeyframeStart(data, 0) && !waitedTooLong)
            {
                return;
            }

            _awaitingKeyframe = false;
        }

        if (!_input.TryEnqueue(data))
        {
            _awaitingKeyframe = true;
            _resyncStartedAt = Environment.TickCount64;
            Interlocked.Increment(ref _droppedChunks);
        }
    }

    /// <summary>Quantos pedaços foram descartados por o decoder não estar acompanhando.</summary>
    public int DroppedChunks => Volatile.Read(ref _droppedChunks);

    private async Task WriteLoopAsync(CancellationToken token)
    {
        try
        {
            while (await _input.DequeueAsync(token) is { } chunk)
            {
                if (_process is not { HasExited: false })
                {
                    return;
                }

                var stream = _process.StandardInput.BaseStream;
                await stream.WriteAsync(chunk, token);
                await stream.FlushAsync(token);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                   or InvalidOperationException or OperationCanceledException)
        {
            // Corrida normal: chunks que ainda estavam a caminho quando o decoder foi parado
            // (é o que acontece toda vez que alguém clica em "parar de assistir").
        }
    }

    private async Task PumpVideoAsync(CancellationToken token)
    {
        var frameSize = _width * _height * 4;
        var frameBuffer = new byte[frameSize];
        var stream = _process!.StandardOutput.BaseStream;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, frameBuffer, frameSize, token))
                {
                    break; // ffmpeg encerrou / fechou o stdout
                }

                // Entrega o próprio buffer (reutilizado) — ver comentário em DecodedFrame.
                FrameDecoded?.Invoke(new DecodedFrame { Data = frameBuffer, Width = _width, Height = _height });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FfmpegLogLine?.Invoke($"[erro inesperado lendo vídeo decodificado] {ex.Message}");
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private async Task PumpStdErrAsync(CancellationToken token)
    {
        var reader = _process!.StandardError;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                FfmpegLogLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FfmpegLogLine?.Invoke($"[erro inesperado lendo stderr] {ex.Message}");
        }
    }

    public Task StopAsync()
    {
        lock (_stopLock)
            return _stopTask ??= Task.Run(StopCoreAsync);
    }

    private async Task StopCoreAsync()
    {
        _input.Complete();
        var process = _process;
        if (process is null)
            return;

        try
        {
            // EOF lets FFmpeg deliver the last decoded frames before cancellation.
            process.StandardInput.BaseStream.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(deadline.Token);
            if (_stdoutTask is not null)
                await _stdoutTask.WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
        }
        finally
        {
            _cts?.Cancel();
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) { }
            process.Dispose();
            _process = null;
        }
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();
    public void Dispose() => Stop();
}
