using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MGRScreen.Core.Audio;

/// <summary>
/// Toca o áudio de todo mundo da sala por UMA saída só, com volume independente por pessoa.
///
/// Antes era um <c>WaveOutEvent</c> por participante: 4 pessoas compartilhando = 4 saídas de
/// áudio abertas, cada uma com sua latência, e sem controle de volume nenhum (só dava pra jogar
/// as amostras fora na chegada, que era como o "silenciar" funcionava). Com um mixer só, volume
/// por pessoa é um <see cref="VolumeSampleProvider"/> em cada entrada, silenciar é volume 0, e a
/// correção de atraso acontece num lugar só.
/// </summary>
public sealed class RemoteAudioMixer : IDisposable
{
    /// <summary>Formato interno do mixer. Todo mundo é convertido pra cá antes de somar.</summary>
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    /// <summary>
    /// Acima disso o áudio de alguém está atrasado o bastante pra incomodar, e a gente descarta o
    /// excesso pra alcançar. Sem isso o atraso só cresce: um engasgo de rede empurrava o áudio pra
    /// trás e ele NUNCA voltava ao lugar (o buffer era de 2 s e só descartava quando estourava),
    /// então o som ficava permanentemente atrás da imagem até silenciar e reativar.
    /// </summary>
    private static readonly TimeSpan MaxLag = TimeSpan.FromMilliseconds(400);

    private sealed class Source
    {
        public required BufferedWaveProvider Buffer { get; init; }
        public required VolumeSampleProvider Volume { get; init; }
        public required ISampleProvider MixerInput { get; init; }
        public float DesiredVolume { get; set; } = 1f;
        public bool Muted { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<int, Source> _sources = new();
    private readonly MixingSampleProvider _mixer = new(MixFormat) { ReadFully = true };
    private readonly VolumeSampleProvider _master;

    private WaveOutEvent? _output;

    public RemoteAudioMixer()
    {
        _master = new VolumeSampleProvider(_mixer) { Volume = 1f };
    }

    /// <summary>
    /// A mistura final (48 kHz, estéreo, float). Normalmente vai direto pra placa de som; fica
    /// exposta pra dar pra conferir a conta do mixer sem depender de ouvido humano.
    /// </summary>
    public ISampleProvider MixedOutput => _master;

    /// <summary>Volume geral, 0 a 1.</summary>
    public double MasterVolume
    {
        get => _master.Volume;
        set => _master.Volume = (float)Math.Clamp(value, 0, 1);
    }

    public void Start()
    {
        if (_output is not null)
        {
            return;
        }

        // Latência menor que o padrão (300 ms): aqui o áudio precisa acompanhar a imagem.
        _output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
        _output.Init(_master);
        _output.Play();
    }

    public void AddParticipant(int participantId, int sampleRate, int channels)
    {
        lock (_sync)
        {
            if (_sources.ContainsKey(participantId))
            {
                return;
            }

            var buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
            };

            // O mixer só aceita entradas no formato dele, então cada um é adaptado aqui.
            ISampleProvider chain = buffer.ToSampleProvider();

            if (channels == 1)
            {
                chain = new MonoToStereoSampleProvider(chain);
            }

            if (sampleRate != MixFormat.SampleRate)
            {
                chain = new WdlResamplingSampleProvider(chain, MixFormat.SampleRate);
            }

            var volume = new VolumeSampleProvider(chain) { Volume = 1f };

            var source = new Source { Buffer = buffer, Volume = volume, MixerInput = volume };
            _sources[participantId] = source;
            _mixer.AddMixerInput(volume);
        }
    }

    public void RemoveParticipant(int participantId)
    {
        lock (_sync)
        {
            if (!_sources.Remove(participantId, out var source))
            {
                return;
            }

            _mixer.RemoveMixerInput(source.MixerInput);
        }
    }

    /// <summary>Volume de uma pessoa. 1 = original, 2 = dobro (pode distorcer se a fonte já for alta).</summary>
    public void SetVolume(int participantId, double volume)
    {
        lock (_sync)
        {
            if (!_sources.TryGetValue(participantId, out var source))
            {
                return;
            }

            source.DesiredVolume = (float)Math.Clamp(volume, 0, 2);
            source.Volume.Volume = source.Muted ? 0f : source.DesiredVolume;
        }
    }

    public void SetMuted(int participantId, bool muted)
    {
        lock (_sync)
        {
            if (!_sources.TryGetValue(participantId, out var source))
            {
                return;
            }

            source.Muted = muted;
            source.Volume.Volume = muted ? 0f : source.DesiredVolume;

            if (muted)
            {
                source.Buffer.ClearBuffer();
            }
        }
    }

    /// <summary>Entrega um pedaço de PCM 16 bits de alguém da sala.</summary>
    public void Push(int participantId, byte[] pcm16)
    {
        lock (_sync)
        {
            if (!_sources.TryGetValue(participantId, out var source))
            {
                return;
            }

            // Silenciado: nem enfileira. Assim reativar não solta um bolo de áudio velho.
            if (source.Muted)
            {
                return;
            }

            try
            {
                source.Buffer.AddSamples(pcm16, 0, pcm16.Length);
            }
            catch (InvalidOperationException)
            {
                // Buffer cheio mesmo com DiscardOnBufferOverflow: perder áudio é melhor que travar.
            }

            // Corta DEPOIS de enfileirar, não antes: cortando antes, o pedaço recém-chegado ficava
            // por cima do limite e o atraso real acabava sendo MaxLag + um pedaço.
            TrimLag(source.Buffer);
        }
    }

    /// <summary>Quanto áudio dessa pessoa está esperando pra tocar (ou seja, o atraso dela).</summary>
    public TimeSpan BufferedDurationOf(int participantId)
    {
        lock (_sync)
        {
            return _sources.TryGetValue(participantId, out var source)
                ? source.Buffer.BufferedDuration
                : TimeSpan.Zero;
        }
    }

    /// <summary>Descarta o excesso acumulado, mantendo só o fim (o mais recente).</summary>
    private static void TrimLag(BufferedWaveProvider buffer)
    {
        if (buffer.BufferedDuration <= MaxLag)
        {
            return;
        }

        var keepBytes = (int)(MaxLag.TotalSeconds * buffer.WaveFormat.AverageBytesPerSecond);
        var excess = buffer.BufferedBytes - keepBytes;

        if (excess > 0)
        {
            buffer.Read(new byte[excess], 0, excess);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            foreach (var source in _sources.Values)
            {
                _mixer.RemoveMixerInput(source.MixerInput);
            }
            _sources.Clear();
        }

        if (_output is null)
        {
            return;
        }

        try
        {
            _output.Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException)
        {
        }

        _output.Dispose();
        _output = null;
    }

    public void Dispose() => Stop();
}
