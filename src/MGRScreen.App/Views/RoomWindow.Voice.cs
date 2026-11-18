using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MGRScreen.App.Services;
using MGRScreen.Core.Audio;
using MGRScreen.Core.Networking;

namespace MGRScreen.App.Views;

/// <summary>
/// Voz na sala. O microfone vira PCM mono de 16 kHz e vai por um canal próprio, separado do áudio
/// da tela. Não há cancelamento de eco: por isso o padrão é "apertar pra falar", que não manda o
/// som das caixas de volta pra sala.
/// </summary>
public partial class RoomWindow
{
    /// <summary>O mixer de áudio é indexado por participante e já usa o id puro pro áudio da
    /// tela; a voz da mesma pessoa entra com este deslocamento pra não colidir.</summary>
    private const int VoiceMixerOffset = 1_000_000;

    /// <summary>Valor de <see cref="AppSettings.VoiceMode"/> pra "não usar o microfone".</summary>
    private const int VoiceModeDisabled = 3;

    private MicrophoneCapture? _mic;
    private bool _voiceOn;
    private bool _echoWarningShown;
    private readonly DispatcherTimer _voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly ConcurrentDictionary<int, long> _lastVoiceAt = new();
    private readonly ConcurrentDictionary<int, bool> _voiceInMixer = new();
    private long _lastOwnVoiceAt;

    private void InitializeVoice()
    {
        _voiceTimer.Tick += OnVoiceTick;
        _voiceTimer.Start();
        UpdateMicButton();
    }

    private void ShutdownVoice()
    {
        _voiceTimer.Stop();
        StopVoice();
    }

    private void OnMicClick(object sender, RoutedEventArgs e)
    {
        if (_settings.VoiceMode == VoiceModeDisabled)
        {
            return; // também vale pro atalho global
        }

        if (_voiceOn)
        {
            StopVoice();
            return;
        }

        if (!_client.SupportsRoomFeatures)
        {
            ShowToast("A voz precisa que o anfitrião esteja na versão nova do MGRScreen.", "");
            return;
        }

        try
        {
            _mic = new MicrophoneCapture();
            _mic.VoiceCaptured += OnVoiceCaptured;
            _mic.Start(string.IsNullOrEmpty(_settings.MicDeviceId) ? null : _settings.MicDeviceId);
            _voiceOn = true;
            ApplyVoiceSettings();

            if (_settings.VoiceMode != 0 && !_echoWarningShown)
            {
                _echoWarningShown = true;
                ShowToast("Microfone aberto: use fone de ouvido, senão a sala ouve o próprio eco.", "");
            }
        }
        catch (Exception ex)
        {
            _mic?.Dispose();
            _mic = null;
            _voiceOn = false;
            ShowToast($"Não consegui abrir o microfone: {ex.Message}", "");
        }

        UpdateMicButton();
    }

    private void StopVoice()
    {
        if (!_voiceOn && _mic is null)
        {
            return;
        }

        _voiceOn = false;
        var mic = _mic;
        _mic = null;
        if (mic is not null)
        {
            mic.VoiceCaptured -= OnVoiceCaptured;
            mic.Dispose();
        }

        UpdateMicButton();
    }

    /// <summary>Aplica o modo escolhido nas configurações (vale na hora, com o microfone ligado).</summary>
    private void ApplyVoiceSettings()
    {
        if (_settings.VoiceMode == VoiceModeDisabled)
        {
            StopVoice();
        }

        if (_mic is null)
        {
            UpdateMicButton();
            return;
        }

        // 0 = apertar pra falar (o timer liga/desliga o envio conforme a tecla) · 1 = ativação
        // por voz · 2 = sempre aberto.
        _mic.VoiceActivation = _settings.VoiceMode == 1;
        _mic.Transmit = _settings.VoiceMode != 0;
        UpdateMicButton();
    }

    private void OnVoiceCaptured(byte[] pcm16)
    {
        Volatile.Write(ref _lastOwnVoiceAt, Environment.TickCount64);
        var ptsUs = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000d / System.Diagnostics.Stopwatch.Frequency));
        _client.SendVoiceChunk(pcm16, ptsUs);
    }

    /// <summary>Voz de alguém chegando (thread de rede).</summary>
    private void OnVoiceChunkReceived(int participantId, byte[] pcm16)
    {
        var key = VoiceMixerOffset + participantId;
        if (_voiceInMixer.TryAdd(participantId, true))
        {
            _audioMixer.AddParticipant(key, VoiceFormat.SampleRate, VoiceFormat.Channels);
        }

        _audioMixer.Push(key, pcm16);
        _lastVoiceAt[participantId] = Environment.TickCount64;
    }

    private void OnVoiceParticipantLeft(int participantId)
    {
        if (_voiceInMixer.TryRemove(participantId, out _))
        {
            _audioMixer.RemoveParticipant(VoiceMixerOffset + participantId);
        }

        _lastVoiceAt.TryRemove(participantId, out _);
    }

    /// <summary>25 vezes por segundo: lê a tecla de apertar-pra-falar e acende o anel de quem
    /// está falando.</summary>
    private void OnVoiceTick(object? sender, EventArgs e)
    {
        if (_mic is not null && _settings.VoiceMode == 0)
        {
            var talking = GlobalHotkeys.IsKeyDown(_settings.PushToTalkKey);
            if (_mic.Transmit != talking)
            {
                _mic.Transmit = talking;
            }
        }

        UpdateMicButton();

        var now = Environment.TickCount64;
        var speaking = (Brush)FindResource("SuccessBrush");

        foreach (var (id, ring) in _speakingRings)
        {
            var last = id == _client.ParticipantId
                ? Volatile.Read(ref _lastOwnVoiceAt)
                : _lastVoiceAt.GetValueOrDefault(id);
            var active = last != 0 && now - last < 350;
            ring.BorderBrush = active ? speaking : Brushes.Transparent;
        }
    }

    private const string GlyphMic = "";
    private const string GlyphMicOff = "";

    /// <summary>Último estado desenhado no botão, pra só mexer nele quando muda (o timer de voz
    /// passa aqui 25 vezes por segundo).</summary>
    private string _micButtonState = "";

    /// <summary>
    /// O botão diz sempre uma de três coisas, com cor: desligado (cinza), ligado mas mudo
    /// (vermelho — ninguém te ouve agora) ou falando (verde — a sala está te ouvindo).
    /// </summary>
    private void UpdateMicButton()
    {
        // Microfone desativado nas configurações: o botão some e nada é capturado.
        if (_settings.VoiceMode == VoiceModeDisabled)
        {
            MicButton.Visibility = Visibility.Collapsed;
            _micButtonState = "";
            return;
        }

        MicButton.Visibility = Visibility.Visible;

        var talking = _voiceOn && Environment.TickCount64 - Volatile.Read(ref _lastOwnVoiceAt) < 350;
        var state = !_voiceOn ? "off" : talking ? "talking" : $"muted:{_settings.VoiceMode}:{_settings.PushToTalkKey}";
        if (state == _micButtonState)
        {
            return;
        }

        _micButtonState = state;

        if (!_voiceOn)
        {
            MicButton.Content = "Mic desligado";
            MicButton.Tag = GlyphMicOff;
            MicButton.Background = (Brush)FindResource("Surface2Brush");
            MicButton.Foreground = (Brush)FindResource("TextSecondaryBrush");
            MicButton.ToolTip = "Seu microfone está desligado. Clique pra ligar e poder falar na sala.";
        }
        else if (talking)
        {
            MicButton.Content = "Falando";
            MicButton.Tag = GlyphMic;
            MicButton.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x3D, 0x26));
            MicButton.Foreground = (Brush)FindResource("SuccessBrush");
            MicButton.ToolTip = "A sala está te ouvindo. Clique pra desligar o microfone.";
        }
        else
        {
            // Ligado, mas ninguém está ouvindo neste instante.
            MicButton.Content = _settings.VoiceMode switch
            {
                0 => $"Mudo · segure {_settings.PushToTalkKey}",
                1 => "Mudo · fale pra abrir",
                _ => "Mic aberto",
            };
            MicButton.Tag = _settings.VoiceMode == 2 ? GlyphMic : GlyphMicOff;
            MicButton.Background = (Brush)FindResource(_settings.VoiceMode == 2 ? "AccentSoftBrush" : "DangerSoftBrush");
            MicButton.Foreground = (Brush)FindResource(_settings.VoiceMode == 2 ? "TextPrimaryBrush" : "DangerBrush");
            MicButton.ToolTip = _settings.VoiceMode switch
            {
                0 => $"Microfone ligado, mas mudo. Segure {_settings.PushToTalkKey} enquanto fala. Clique pra desligar.",
                1 => "Microfone ligado: abre sozinho quando você fala. Clique pra desligar.",
                _ => "Microfone aberto o tempo todo. Clique pra desligar.",
            };
        }
    }
}
