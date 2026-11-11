using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MGRScreen.Core.Audio;

/// <summary>
/// Captura o áudio que vai ser compartilhado e entrega sempre como <b>PCM 16-bit</b>.
///
/// O modo (<see cref="AudioShareMode"/>) decide o que entra:
/// - <b>SystemExceptSelf</b>: tudo, menos o próprio MGRScreen. É o que elimina o <b>eco</b> — sem
///   isso, o áudio das telas que EU estou assistindo é recapturado e devolvido pra quem
///   compartilhou, que se ouve de volta.
/// - <b>SystemExceptDiscord</b>: tudo, menos a call do Discord.
/// - <b>SingleApp</b>: só o áudio de um app (o jogo). Resolve eco e Discord ao mesmo tempo,
///   porque nada além daquele app entra.
///
/// A limitação de "ou um, ou outro" vem da própria API do Windows: cada captura aceita uma única
/// árvore de processos.
/// </summary>
public sealed class SystemAudioCapture : IDisposable
{
    /// <summary>Blocos de PCM 16-bit prontos pra enviar pela rede.</summary>
    public event Action<byte[]>? AudioCaptured;

    /// <summary>Avisos pra UI (fallback aplicado, app não encontrado, etc).</summary>
    public event Action<string>? Notice;

    private WasapiLoopbackCapture? _wasapiCapture;
    private ProcessLoopbackCapture? _processCapture;
    private MixedProcessLoopbackCapture? _mixedCapture;
    private float _wasapiPeak;

    public float PeakLevel => _mixedCapture?.PeakLevel ?? _processCapture?.PeakLevel ?? _wasapiPeak;

    /// <summary>No modo misturado, quantos apps estão sendo capturados.</summary>
    public int MixedSourceCount => _mixedCapture?.SourceCount ?? 0;

    public int SampleRate { get; private set; } = 48_000;
    public int Channels { get; private set; } = 2;

    /// <summary>O que efetivamente está valendo (pode diferir do pedido, se houve fallback).</summary>
    public AudioShareMode ActiveMode { get; private set; } = AudioShareMode.None;

    public void Start(AudioShareMode mode, uint? targetAppProcessId)
    {
        ActiveMode = AudioShareMode.None;

        switch (mode)
        {
            case AudioShareMode.None:
                return;

            case AudioShareMode.SystemExceptSelfAndDiscord:
                if (TryStartMixed())
                {
                    return;
                }
                break;

            case AudioShareMode.SingleApp when targetAppProcessId is not null:
                if (TryStartProcessLoopback(targetAppProcessId.Value, exclude: false, AudioShareMode.SingleApp))
                {
                    return;
                }
                break;

            case AudioShareMode.SystemExceptDiscord:
                var discordPid = FindDiscordProcessId();
                if (discordPid is null)
                {
                    Notice?.Invoke("Discord não está aberto — compartilhando tudo, menos o MGRScreen.");
                    if (TryStartProcessLoopback((uint)Environment.ProcessId, exclude: true, AudioShareMode.SystemExceptSelf))
                    {
                        return;
                    }
                }
                else if (TryStartProcessLoopback(discordPid.Value, exclude: true, AudioShareMode.SystemExceptDiscord))
                {
                    return;
                }
                break;

            case AudioShareMode.SystemExceptSelf:
            default:
                if (TryStartProcessLoopback((uint)Environment.ProcessId, exclude: true, AudioShareMode.SystemExceptSelf))
                {
                    return;
                }
                break;
        }

        // Último recurso: loopback do dispositivo padrão. Funciona em qualquer Windows, mas
        // captura TUDO — inclusive o que o próprio MGRScreen está tocando, então volta o eco.
        StartWasapiLoopback();
    }

    private bool TryStartMixed()
    {
        try
        {
            var excluded = new HashSet<uint> { (uint)Environment.ProcessId };

            foreach (var pid in FindDiscordProcessIds())
            {
                excluded.Add(pid);
            }

            _mixedCapture = new MixedProcessLoopbackCapture();
            _mixedCapture.DataAvailable += pcm16 => AudioCaptured?.Invoke(pcm16);
            _mixedCapture.Notice += message => Notice?.Invoke(message);
            _mixedCapture.Start(excluded);

            SampleRate = ProcessLoopbackCapture.SampleRateHz;
            Channels = ProcessLoopbackCapture.ChannelCount;
            ActiveMode = AudioShareMode.SystemExceptSelfAndDiscord;
            return true;
        }
        catch (Exception ex)
        {
            _mixedCapture?.Dispose();
            _mixedCapture = null;
            Notice?.Invoke($"Modo sem eco e sem Discord indisponível ({ex.Message}).");
            return false;
        }
    }

    private bool TryStartProcessLoopback(uint pid, bool exclude, AudioShareMode resultingMode)
    {
        try
        {
            _processCapture = new ProcessLoopbackCapture();
            _processCapture.DataAvailable += pcm16 => AudioCaptured?.Invoke(pcm16);
            _processCapture.Failed += ex => Notice?.Invoke($"A captura de áudio parou: {ex.Message}");
            _processCapture.Start(pid, exclude);

            SampleRate = ProcessLoopbackCapture.SampleRateHz;
            Channels = ProcessLoopbackCapture.ChannelCount;
            ActiveMode = resultingMode;
            return true;
        }
        catch (Exception ex)
        {
            // Windows 10 antigo (a API é do build 20348+) ou outra falha: melhor ter áudio com
            // eco do que não ter áudio nenhum.
            _processCapture?.Dispose();
            _processCapture = null;
            Notice?.Invoke($"Modo de áudio indisponível ({ex.Message}). Usando captura simples.");
            return false;
        }
    }

    private void StartWasapiLoopback()
    {
        var device = AudioDeviceHelper.GetDefaultRenderDevice();
        _wasapiCapture = new WasapiLoopbackCapture(device);
        SampleRate = _wasapiCapture.WaveFormat.SampleRate;
        Channels = _wasapiCapture.WaveFormat.Channels;

        _wasapiCapture.DataAvailable += OnWasapiDataAvailable;
        _wasapiCapture.StartRecording();
        ActiveMode = AudioShareMode.SystemExceptSelf; // "pretendido", mas sem exclusão real
    }

    /// <summary>Todos os processos do Discord (ele roda como vários, sendo Electron).</summary>
    private static List<uint> FindDiscordProcessIds()
    {
        return Process.GetProcesses()
            .Where(p => p.ProcessName.Contains("Discord", StringComparison.OrdinalIgnoreCase))
            .Select(p => (uint)p.Id)
            .ToList();
    }

    private static uint? FindDiscordProcessId()
    {
        var candidates = Process.GetProcesses()
            .Where(p => p.ProcessName.Contains("Discord", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // A exclusão vale pra árvore inteira, e o Discord roda como vários processos (Electron);
        // o de maior uso de memória é, na prática, o principal.
        var main = candidates.OrderByDescending(p =>
        {
            try
            {
                return p.WorkingSet64;
            }
            catch
            {
                return 0L;
            }
        }).First();

        return (uint)main.Id;
    }

    private void OnWasapiDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        var format = _wasapiCapture!.WaveFormat;
        byte[] pcm16;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            pcm16 = ConvertFloatToPcm16(e.Buffer, e.BytesRecorded, out var peak);
            _wasapiPeak = peak;
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            pcm16 = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, pcm16, 0, e.BytesRecorded);
            _wasapiPeak = Pcm16Peak(pcm16);
        }
        else
        {
            _wasapiPeak = 0;
            return;
        }

        AudioCaptured?.Invoke(pcm16);
    }

    private static byte[] ConvertFloatToPcm16(byte[] buffer, int bytesRecorded, out float peak)
    {
        var sampleCount = bytesRecorded / 4;
        var output = new byte[sampleCount * 2];
        peak = 0f;

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToSingle(buffer, i * 4);

            var abs = Math.Abs(sample);
            if (abs > peak)
            {
                peak = abs;
            }

            var value = (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
            output[i * 2] = (byte)(value & 0xFF);
            output[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return output;
    }

    private static float Pcm16Peak(byte[] pcm16)
    {
        var peak = 0f;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var value = Math.Abs(BitConverter.ToInt16(pcm16, i) / (float)short.MaxValue);
            if (value > peak)
            {
                peak = value;
            }
        }
        return peak;
    }

    public void Stop()
    {
        if (_wasapiCapture is not null)
        {
            _wasapiCapture.DataAvailable -= OnWasapiDataAvailable;
            try
            {
                _wasapiCapture.StopRecording();
            }
            catch
            {
            }
            _wasapiCapture.Dispose();
            _wasapiCapture = null;
        }

        _processCapture?.Dispose();
        _processCapture = null;

        _mixedCapture?.Dispose();
        _mixedCapture = null;

        _wasapiPeak = 0;
        ActiveMode = AudioShareMode.None;
    }

    public void Dispose() => Stop();
}
