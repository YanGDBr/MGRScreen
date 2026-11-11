using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using MGRScreen.Core.Models;
using MGRScreen.Core.Networking;

namespace MGRScreen.Core.Encoding;

/// <summary>
/// Envolve um processo `ffmpeg` que recebe vídeo cru (BGRA) via stdin e devolve um stream H.264
/// elementary (Annex-B) já encodado via stdout.
///
/// Só vídeo — o áudio vai por um canal de rede separado, sem passar pelo ffmpeg (H.264 cru não
/// carrega áudio; ver docs/ARCHITECTURE.md).
///
/// Detalhe importante de performance: os frames NÃO são escritos direto pelo thread de captura.
/// Eles entram num buffer duplo e um único "writer task" os escreve no ritmo exato do
/// <see cref="SessionSettings.Fps"/>. Isso resolve dois problemas reais da versão anterior:
/// (1) várias escritas assíncronas concorrentes de ~8 MB no mesmo pipe podiam se intercalar e
/// corromper o stream cru; (2) alimentar o ffmpeg mais rápido que o `-r` configurado fazia o
/// stream "correr na frente" e o espectador ficar cada vez mais atrasado.
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    public event Action<byte[], long>? EncodedChunkReady;
    public event Action<string>? FfmpegLogLine;

    /// <summary>Disparado se o processo do ffmpeg morrer sozinho (encoder de hardware que a
    /// máquina não suporta de verdade, argumento inválido, etc). Quem chama deve reagir caindo
    /// pra um encoder mais seguro — sem isso o sintoma pro espectador é só "tela preta".</summary>
    public event Action<int>? Exited;

    /// <summary>Fica true no primeiro byte que sai do encoder. Se continuar false depois de
    /// alguns segundos transmitindo, alguma coisa está errada no encode.</summary>
    public bool HasProducedOutput { get; private set; }

    /// <summary>Resolução com que este encoder foi criado — a captura precisa continuar
    /// entregando exatamente esse tamanho, senão o stream cru sai desalinhado.</summary>
    public int InputWidth => _settings.Width;
    public int InputHeight => _settings.Height;

    private readonly SessionSettings _settings;
    private readonly string _ffmpegPath;

    private Process? _process;
    private CancellationTokenSource? _cts;

    // Buffer duplo: um slot guarda o frame mais novo ainda não escrito, o outro guarda o último
    // frame já escrito (pra poder repetir quando a captura não entregou nada novo no tick).
    private readonly object _sync = new();
    private readonly byte[]?[] _buffers = new byte[2][];
    private int _pendingIndex = -1;
    private int _writingIndex = -1;
    private byte[]? _lastWritten;

    private long _framesSubmitted;
    private long _framesEncoded;
    private Channel<long>? _frameTimes;

    /// <summary>Frames que a captura entregou (pode ser maior que o alvo — o excedente é descartado).</summary>
    public long FramesSubmitted => Interlocked.Read(ref _framesSubmitted);

    /// <summary>Frames realmente escritos no ffmpeg. É ESTE o número que corresponde ao que os
    /// espectadores recebem — se estiver bem abaixo do alvo, a máquina não está dando conta.</summary>
    public long FramesEncoded => Interlocked.Read(ref _framesEncoded);

    public FfmpegEncoder(SessionSettings settings, string ffmpegPath)
    {
        _settings = settings;
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>Grava num arquivo local em vez de expor o stream via stdout — útil pra validar
    /// qualidade/fps sem envolver rede.</summary>
    public void StartToFile(string outputPath) => StartInternal(outputPath, toFile: true);

    public void StartToStream() => StartInternal(null, toFile: false);

    private void StartInternal(string? outputPath, bool toFile)
    {
        _cts = new CancellationTokenSource();
        _frameTimes = toFile ? null : Channel.CreateBounded<long>(240);

        var psi = new ProcessStartInfo(_ffmpegPath, BuildArguments(outputPath, toFile))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = !toFile,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao iniciar ffmpeg.exe");

        var process = _process;
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            if (!token.IsCancellationRequested)
            {
                Exited?.Invoke(process.ExitCode);
            }
        });

        Task.Run(() => PumpStdErrAsync(token));
        Task.Run(() => WriteFramesLoopAsync(token));

        if (!toFile)
        {
            Task.Run(() => PumpStdOutAsync(token));
        }
    }

    private string BuildArguments(string? outputPath, bool toFile)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning ");
        sb.Append($"-f rawvideo -pix_fmt bgra -s {_settings.Width}x{_settings.Height} -r {_settings.Fps} -i pipe:0 ");

        if (_settings.NeedsScaling)
        {
            // bicubic em vez de fast_bilinear: quando a tela é reduzida (ex: 1440p -> 1080p), o
            // fast_bilinear borra texto a ponto de atrapalhar a leitura, e o custo extra do
            // bicubic é pequeno perto do resto do pipeline.
            sb.Append($"-vf scale={_settings.OutputWidth}:{_settings.OutputHeight} -sws_flags bicubic ");
        }

        sb.Append(BuildVideoEncoderArgs());
        sb.Append("-map 0:v -an ");

        if (!toFile)
        {
            // ESSENCIAL pra quem entra na sala DEPOIS que a transmissão já começou: repete os
            // cabeçalhos SPS/PPS antes de cada keyframe. Sem isso, esses cabeçalhos só existem
            // no comecinho do stream e quem chega depois recebe "non-existing PPS 0 referenced"
            // — na prática, tela preta (permanente em encoders que não repetem por conta própria).
            sb.Append("-bsf:v dump_extra=freq=keyframe,h264_metadata=aud=insert ");

            // Não segurar pacote esperando encher o buffer de saída (mais ~9 ms de latência).
            sb.Append("-fps_mode passthrough -flush_packets 1 ");
        }

        sb.Append(toFile ? $"\"{outputPath}\"" : "-f h264 pipe:1");
        return sb.ToString();
    }

    private string BuildVideoEncoderArgs()
    {
        // GOP de ~1s: é o tempo máximo que alguém que acabou de entrar (ou que voltou a assistir)
        // espera até a imagem aparecer. Curto demais desperdiça banda, longo demais dá a sensação
        // de "travou".
        var gop = _settings.Fps;
        var bitrate = _settings.VideoBitrateKbps;

        return _settings.VideoEncoder switch
        {
            // NVENC: preset p4 em vez de p1. O p1 é o mais rápido e o mais pobre em qualidade;
            // como o NVENC roda em hardware dedicado, subir pra p4 melhora bastante a imagem
            // (principalmente texto) praticamente de graça. `-rc vbr` com teto no bitrate evita o
            // "filler data" que o CBR injeta em tela parada só pra manter a taxa — banda jogada
            // fora que agora fica disponível pros momentos de movimento.
            // `-delay 0 -zerolatency 1`: medido, tirou ~88 ms de latência. Por padrão o NVENC
            // segura alguns frames na fila antes de entregar o primeiro.
            "h264_nvenc" =>
                $"-c:v h264_nvenc -preset p4 -tune ll -rc cbr -b:v {bitrate}k " +
                $"-g {gop} -bf 0 -no-scenecut 1 -forced-idr 1 -rc-lookahead 0 -delay 0 -zerolatency 1 ",

            // Equivalente AMD AMF: ultralowlatency, sem B-frames, forçando IDR a cada keyframe
            // e async_depth 1 para latência mínima em transmissão ao vivo.
            "h264_amf" =>
                $"-c:v h264_amf -usage ultralowlatency -quality balanced -rc cbr -b:v {bitrate}k " +
                $"-g {gop} -bf 0 -forced_idr 1 -async_depth 1 ",

            // -async_depth 1 é o botão de baixa latência do QSV: o padrão (4) enfileira 4 frames.
            "h264_qsv" =>
                $"-c:v h264_qsv -preset faster -b:v {bitrate}k -maxrate {bitrate}k -g {gop} -bf 0 -async_depth 1 ",

            // libx264 continua em ultrafast: quem cai aqui não tem encoder de hardware, e subir o
            // preset custaria FPS justamente em quem já está com CPU no limite.
            // `-pix_fmt yuv420p` garante compatibilidade total com aceleração de hardware (d3d11va) dos espectadores.
            _ => $"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -b:v {bitrate}k -g {gop} -bf 0 -x264-params repeat-headers=1 ",
        };
    }

    /// <summary>
    /// Entrega um frame pra codificação. Não bloqueia e nunca lança: se o encoder ainda não deu
    /// conta do frame anterior, o anterior é simplesmente descartado (melhor perder frame do que
    /// acumular atraso). O array de origem pode ser reutilizado pelo chamador logo em seguida —
    /// os bytes são copiados aqui.
    /// </summary>
    public void SubmitFrame(byte[] bgra)
    {
        lock (_sync)
        {
            var index = _writingIndex == 0 ? 1 : 0;

            var buffer = _buffers[index];
            if (buffer is null || buffer.Length != bgra.Length)
            {
                buffer = new byte[bgra.Length];
                _buffers[index] = buffer;
            }

            Buffer.BlockCopy(bgra, 0, buffer, 0, bgra.Length);
            _pendingIndex = index;
        }

        Interlocked.Increment(ref _framesSubmitted);
    }

    private async Task WriteFramesLoopAsync(CancellationToken token)
    {
        var stream = _process!.StandardInput.BaseStream;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Max(1, _settings.Fps)));

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                byte[]? toWrite;

                lock (_sync)
                {
                    if (_pendingIndex >= 0)
                    {
                        _writingIndex = _pendingIndex;
                        _pendingIndex = -1;
                        _lastWritten = _buffers[_writingIndex];
                    }

                    // Sem frame novo: repete o último. Mantém o stream em taxa constante (o que
                    // o `-r` do ffmpeg espera) e custa quase nada em H.264 numa tela parada.
                    toWrite = _lastWritten;
                }

                if (toWrite is null)
                {
                    continue; // ainda não chegou o primeiro frame
                }

                // All encoder presets use no B-frames and raw video provides one output AU per
                // input frame. Record the actual pacing time, including repeats and stalls.
                if (_frameTimes is not null && !_frameTimes.Writer.TryWrite(TimedMedia.NowUs))
                {
                    FfmpegLogLine?.Invoke("Encoder não está produzindo frames.");
                    _process?.Kill();
                    return;
                }
                await stream.WriteAsync(toWrite, token);
                await stream.FlushAsync(token);
                Interlocked.Increment(ref _framesEncoded);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // ffmpeg encerrou/pipe quebrou (inclusive durante o Stop()).
        }
    }

    private async Task PumpStdOutAsync(CancellationToken token)
    {
        var stream = _process!.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        var accessUnits = new H264AccessUnits();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token);
                if (read == 0)
                {
                    break;
                }

                HasProducedOutput = true;

                accessUnits.Append(buffer.AsSpan(0, read), frame =>
                {
                    if (!_frameTimes!.Reader.TryRead(out var pts))
                        throw new InvalidDataException("Encoder produziu um frame sem timestamp.");
                    EncodedChunkReady?.Invoke(frame, pts);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidDataException ex)
        {
            FfmpegLogLine?.Invoke(ex.Message);
            try { _process?.Kill(); }
            catch (InvalidOperationException) { }
        }
    }

    private async Task PumpStdErrAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await _process!.StandardError.ReadLineAsync(token);
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
    }

    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _process?.StandardInput.BaseStream.Close();
        }
        catch
        {
        }

        if (_process is { HasExited: false })
        {
            _process.WaitForExit(2000);
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }

        _process?.Dispose();
        _process = null;

        lock (_sync)
        {
            _buffers[0] = null;
            _buffers[1] = null;
            _lastWritten = null;
            _pendingIndex = -1;
            _writingIndex = -1;
        }
    }

    public void Dispose() => Stop();
}
