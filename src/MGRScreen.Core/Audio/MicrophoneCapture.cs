using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MGRScreen.Core.Audio;

/// <summary>
/// Captura o microfone pra conversa por voz na sala e entrega sempre no mesmo formato, seja qual
/// for o do dispositivo: <b>16 kHz, mono, PCM 16-bit</b>, em blocos de 20 ms. É o formato
/// clássico de voz — 32 KB/s sem compressão, pouco o bastante pra ir cru pela rede local — e o
/// bloco fixo é o que deixa o outro lado misturar e tocar sem adivinhar tamanhos.
///
/// O microfone fica aberto mesmo sem transmitir (<see cref="Transmit"/> = false), pra o medidor
/// de nível da UI funcionar e pra "apertar pra falar" não perder o começo da frase esperando o
/// dispositivo abrir.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    /// <summary>Mono, PCM 16-bit little-endian.</summary>
    public const int SampleRate = 16000;

    private const int BlockSamples = SampleRate / 50; // 20 ms
    // Voz não é contínua: sem segurar o portão aberto depois que o nível cai, ele cortaria o
    // fim das palavras e as pausas entre elas. 15 blocos = 300 ms.
    private const int HangBlocks = 15;

    /// <summary>
    /// Blocos de 20 ms (640 bytes); array novo a cada chamada. Dispara na thread de captura:
    /// quem assina não deve demorar nem chamar <see cref="Stop"/> de dentro do handler.
    /// </summary>
    public event Action<byte[]>? VoiceCaptured;

    private readonly object _lifecycleLock = new();
    private WasapiCapture? _capture;
    private volatile bool _running;

    private volatile bool _transmit;
    private volatile bool _voiceActivation;
    private volatile float _activationThreshold = 0.02f;
    private volatile float _peakLevel;

    // Tudo daqui pra baixo só é tocado pela thread de captura (ou pelo Start, antes de ela existir).
    private Func<byte[], int, float>? _readSample;
    private int _bytesPerSample;
    private int _channels;
    private float[] _mono = Array.Empty<float>();
    private MonoSource? _source;
    private WdlResamplingSampleProvider? _resampler;
    private readonly float[] _resampled = new float[BlockSamples];
    private readonly float[] _block = new float[BlockSamples];
    private int _blockFill;
    private int _hangRemaining;

    /// <summary>0..1, do último bloco, pra medidor (atualiza mesmo com <see cref="Transmit"/> = false).</summary>
    public float PeakLevel => _peakLevel;

    /// <summary>false = captura (e mede o nível) mas não dispara <see cref="VoiceCaptured"/>.</summary>
    public bool Transmit
    {
        get => _transmit;
        set => _transmit = value;
    }

    /// <summary>true = só dispara quando o nível passa do limiar, com ~300 ms de sustentação depois que cai.</summary>
    public bool VoiceActivation
    {
        get => _voiceActivation;
        set => _voiceActivation = value;
    }

    public float ActivationThreshold
    {
        get => _activationThreshold;
        set => _activationThreshold = value;
    }

    public bool IsRunning => _running;

    /// <summary>Dispositivos de captura ativos. Lista vazia se não houver nenhum.</summary>
    public static IReadOnlyList<(string Id, string Name)> ListDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var result = new List<(string Id, string Name)>();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    result.Add((device.ID, device.FriendlyName));
                }
            }

            return result;
        }
        catch (COMException)
        {
            return Array.Empty<(string, string)>(); // serviço de áudio do Windows parado
        }
    }

    /// <summary>
    /// Abre o microfone. <paramref name="deviceId"/> null = o microfone padrão de comunicação (o
    /// mesmo que Discord/Teams usam, que pode ser o headset mesmo quando o padrão "geral" é
    /// outro). Lança se não houver microfone ou se ele não puder ser aberto.
    /// </summary>
    public void Start(string? deviceId = null)
    {
        lock (_lifecycleLock)
        {
            StopLocked();

            var device = OpenDevice(deviceId);
            WasapiCapture? capture = null;

            try
            {
                capture = new WasapiCapture(device);
                var format = capture.WaveFormat;

                _readSample = (format.Encoding, format.BitsPerSample) switch
                {
                    (WaveFormatEncoding.IeeeFloat, 32) => static (buffer, offset) => BitConverter.ToSingle(buffer, offset),
                    (WaveFormatEncoding.Pcm, 16) => static (buffer, offset) => BitConverter.ToInt16(buffer, offset) / 32768f,
                    (WaveFormatEncoding.Pcm, 24) => static (buffer, offset) =>
                        ((buffer[offset] << 8) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 24)) / 2147483648f,
                    (WaveFormatEncoding.Pcm, 32) => static (buffer, offset) => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                    _ => throw new NotSupportedException(
                        $"Formato de microfone não suportado: {format.Encoding}, {format.BitsPerSample} bits."),
                };

                _channels = format.Channels;
                _bytesPerSample = format.BitsPerSample / 8;

                // O resampler do NAudio (WDL) já filtra antes de reduzir a taxa; descartar 2 de
                // cada 3 amostras "na mão" dobraria os agudos pra dentro da voz como chiado.
                _source = new MonoSource(format.SampleRate);
                _resampler = new WdlResamplingSampleProvider(_source, SampleRate);
                _blockFill = 0;
                _hangRemaining = 0;

                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();

                _capture = capture;
                _running = true;
            }
            catch
            {
                capture?.Dispose();
                device.Dispose();
                throw;
            }
        }
    }

    private static MMDevice OpenDevice(string? deviceId)
    {
        // O enumerator não é descartado de propósito, pelo mesmo motivo do AudioDeviceHelper: o
        // MMDevice continua em uso pela captura depois que este método retorna.
        var enumerator = new MMDeviceEnumerator();

        try
        {
            return deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : enumerator.GetDevice(deviceId);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                deviceId is null ? "Nenhum microfone encontrado." : "O microfone escolhido não está mais disponível.", ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var frameBytes = _bytesPerSample * _channels;
        var frames = e.BytesRecorded / frameBytes;
        if (frames == 0)
        {
            return;
        }

        if (_mono.Length < frames)
        {
            _mono = new float[frames];
        }

        // Mono pela média dos canais: microfone estéreo costuma ter a mesma voz nos dois (ou
        // voz num e silêncio no outro), e a média serve pros dois casos.
        var read = _readSample!;
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            var offset = i * frameBytes;
            for (var c = 0; c < _channels; c++)
            {
                sum += read(e.Buffer, offset + (c * _bytesPerSample));
            }
            _mono[i] = sum / _channels;
        }

        _source!.Load(_mono, frames);

        // Puxa do resampler até ele esgotar o que acabou de entrar; o resto (fração de amostra,
        // estado do filtro) fica guardado nele pro próximo pacote.
        int produced;
        while ((produced = _resampler!.Read(_resampled, 0, _resampled.Length)) > 0)
        {
            for (var i = 0; i < produced; i++)
            {
                _block[_blockFill++] = _resampled[i];
                if (_blockFill == BlockSamples)
                {
                    _blockFill = 0;
                    EmitBlock();
                }
            }
        }
    }

    private void EmitBlock()
    {
        var peak = 0f;
        for (var i = 0; i < BlockSamples; i++)
        {
            var abs = Math.Abs(_block[i]);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        _peakLevel = Math.Min(peak, 1f);

        // O portão é avaliado mesmo sem transmitir, pra soltar a tecla de falar no meio de uma
        // frase e apertar de novo não começar com ele fechado.
        var gateOpen = true;
        if (peak >= _activationThreshold)
        {
            _hangRemaining = HangBlocks;
        }
        else if (_hangRemaining > 0)
        {
            _hangRemaining--;
        }
        else
        {
            gateOpen = false;
        }

        if (!_transmit || (_voiceActivation && !gateOpen))
        {
            return;
        }

        var handler = VoiceCaptured;
        if (handler is null)
        {
            return;
        }

        var pcm = new byte[BlockSamples * 2];
        for (var i = 0; i < BlockSamples; i++)
        {
            var value = (short)(Math.Clamp(_block[i], -1f, 1f) * short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        handler(pcm);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Chega aqui também quando o microfone é desconectado no meio do uso; sem isto a UI
        // continuaria mostrando "capturando" com o medidor congelado no último valor.
        if (ReferenceEquals(sender, _capture))
        {
            _running = false;
            _peakLevel = 0;
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        var capture = _capture;
        _capture = null;
        _running = false;
        _peakLevel = 0;

        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;

        try
        {
            capture.StopRecording();
        }
        catch
        {
        }

        // O Dispose espera a thread de captura terminar, então depois daqui nenhum bloco sai mais.
        capture.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Fonte "de empurrar" pro resampler, que é "de puxar": recebe o pacote que acabou de chegar
    /// do microfone e o entrega até acabar. Devolver 0 no fim só faz o resampler parar de
    /// produzir até o próximo pacote.
    /// </summary>
    private sealed class MonoSource : ISampleProvider
    {
        private float[] _data = Array.Empty<float>();
        private int _position;
        private int _count;

        public MonoSource(int sampleRate)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public void Load(float[] data, int count)
        {
            _data = data;
            _position = 0;
            _count = count;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _count - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
