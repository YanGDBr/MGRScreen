namespace MGRScreen.Core.Audio;

/// <summary>
/// Captura o áudio de <b>todos</b> os apps que estão tocando algo, <b>menos</b> os que você pedir
/// pra ficar de fora (tipicamente o próprio MGRScreen e o Discord), e entrega um único stream
/// PCM 16-bit já misturado.
///
/// Por que isto existe: a API do Windows aceita só UMA árvore de processos por captura (incluir
/// ou excluir), então não dá pra pedir "tudo menos A e menos B" numa captura só. A saída é abrir
/// uma captura em modo <i>incluir</i> para cada app que interessa e somar os streams aqui.
///
/// Os apps são redescobertos periodicamente, então algo que começou a tocar depois entra sozinho
/// (com alguns segundos de atraso).
/// </summary>
public sealed class MixedProcessLoopbackCapture : IDisposable
{
    /// <summary>Blocos de PCM 16-bit (48 kHz, estéreo) já misturados.</summary>
    public event Action<byte[]>? DataAvailable;
    public event Action<string>? Notice;

    public float PeakLevel { get; private set; }

    /// <summary>Quantos apps estão sendo capturados agora.</summary>
    public int SourceCount
    {
        get
        {
            lock (_sourcesSync)
            {
                return _sources.Count;
            }
        }
    }

    private const int BytesPerFrame = ProcessLoopbackCapture.ChannelCount * 2;
    private const int BlockMilliseconds = 10;
    private static readonly int BlockBytes = ProcessLoopbackCapture.SampleRateHz / (1000 / BlockMilliseconds) * BytesPerFrame;

    private readonly object _sourcesSync = new();
    private readonly Dictionary<uint, Source> _sources = new();
    private IReadOnlySet<uint> _excludedPids = new HashSet<uint>();

    private CancellationTokenSource? _cts;
    private Task? _mixTask;
    private Task? _rescanTask;

    public void Start(IReadOnlySet<uint> excludedPids)
    {
        _excludedPids = excludedPids;
        _cts = new CancellationTokenSource();

        RefreshSources();

        if (SourceCount == 0)
        {
            Notice?.Invoke("Nenhum app tocando áudio no momento — o som entra assim que algum começar.");
        }

        _mixTask = Task.Run(() => MixLoopAsync(_cts.Token));
        _rescanTask = Task.Run(() => RescanLoopAsync(_cts.Token));
    }

    /// <summary>Descobre quais apps devem ser capturados agora e ajusta as capturas ativas.</summary>
    private void RefreshSources()
    {
        var parentMap = ProcessTreeHelper.BuildParentMap();
        var candidates = AudioDeviceHelper.GetAppsWithAudio().Select(a => a.ProcessId).ToList();

        // Descarta quem é (ou descende de) alguém excluído — assim o ffmpeg, que é filho nosso,
        // também fica de fora junto com o MGRScreen.
        var wanted = candidates
            .Where(pid => !ProcessTreeHelper.HasAncestorIn(pid, _excludedPids, parentMap))
            .ToList();

        // Descarta quem já está coberto pela árvore de outro candidato, pra não somar o mesmo
        // áudio duas vezes (navegadores/Electron têm vários processos).
        var wantedSet = wanted.ToHashSet();
        var deduped = wanted
            .Where(pid =>
            {
                var others = wantedSet.Where(p => p != pid).ToHashSet();
                return !ProcessTreeHelper.HasAncestorIn(pid, others, parentMap);
            })
            .ToHashSet();

        lock (_sourcesSync)
        {
            foreach (var pid in _sources.Keys.Where(p => !deduped.Contains(p)).ToList())
            {
                _sources[pid].Capture.Dispose();
                _sources.Remove(pid);
            }

            foreach (var pid in deduped.Where(p => !_sources.ContainsKey(p)))
            {
                try
                {
                    var source = new Source();
                    source.Capture = new ProcessLoopbackCapture();
                    source.Capture.DataAvailable += data => source.Append(data);
                    source.Capture.Start(pid, exclude: false);
                    _sources[pid] = source;
                }
                catch
                {
                    // App pode ter fechado entre a listagem e a captura — só ignora.
                }
            }
        }
    }

    private async Task RescanLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                RefreshSources();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MixLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(BlockMilliseconds));

        var accumulator = new int[BlockBytes / 2];
        var sourceBlock = new byte[BlockBytes];
        var output = new byte[BlockBytes];

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                Array.Clear(accumulator);

                List<Source> snapshot;
                lock (_sourcesSync)
                {
                    snapshot = _sources.Values.ToList();
                }

                foreach (var source in snapshot)
                {
                    var read = source.Take(sourceBlock, BlockBytes);
                    for (var i = 0; i + 1 < read; i += 2)
                    {
                        accumulator[i / 2] += BitConverter.ToInt16(sourceBlock, i);
                    }
                }

                var peak = 0f;
                for (var i = 0; i < accumulator.Length; i++)
                {
                    var value = Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
                    output[i * 2] = (byte)(value & 0xFF);
                    output[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);

                    var magnitude = Math.Abs(value) / (float)short.MaxValue;
                    if (magnitude > peak)
                    {
                        peak = magnitude;
                    }
                }

                PeakLevel = peak;

                var block = new byte[BlockBytes];
                Buffer.BlockCopy(output, 0, block, 0, BlockBytes);
                DataAvailable?.Invoke(block);
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
            Task.WaitAll(new[] { _mixTask, _rescanTask }.Where(t => t is not null).Select(t => t!).ToArray(), 2000);
        }
        catch
        {
        }

        lock (_sourcesSync)
        {
            foreach (var source in _sources.Values)
            {
                source.Capture.Dispose();
            }
            _sources.Clear();
        }

        _cts?.Dispose();
        _cts = null;
        PeakLevel = 0;
    }

    public void Dispose() => Stop();

    /// <summary>Um app sendo capturado + o buffer do que ele já entregou e ainda não foi misturado.</summary>
    private sealed class Source
    {
        public ProcessLoopbackCapture Capture = null!;

        private readonly object _sync = new();
        private byte[] _buffer = new byte[ProcessLoopbackCapture.SampleRateHz * BytesPerFrame]; // ~1s
        private int _count;

        public void Append(byte[] data)
        {
            lock (_sync)
            {
                if (_count + data.Length > _buffer.Length)
                {
                    // Não acompanhou o consumo: joga fora o mais antigo em vez de acumular atraso.
                    var keep = _buffer.Length / 2;
                    var start = Math.Max(0, _count - keep);
                    Array.Copy(_buffer, start, _buffer, 0, _count - start);
                    _count -= start;

                    if (_count + data.Length > _buffer.Length)
                    {
                        _count = 0;
                    }
                }

                Buffer.BlockCopy(data, 0, _buffer, _count, Math.Min(data.Length, _buffer.Length - _count));
                _count = Math.Min(_count + data.Length, _buffer.Length);
            }
        }

        public int Take(byte[] destination, int wanted)
        {
            lock (_sync)
            {
                var take = Math.Min(wanted, _count);
                if (take <= 0)
                {
                    return 0;
                }

                Buffer.BlockCopy(_buffer, 0, destination, 0, take);
                Array.Copy(_buffer, take, _buffer, 0, _count - take);
                _count -= take;
                return take;
            }
        }
    }
}
