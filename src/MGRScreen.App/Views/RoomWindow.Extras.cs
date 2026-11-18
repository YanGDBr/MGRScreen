using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MGRScreen.App.Services;
using MGRScreen.Core.Capture;

namespace MGRScreen.App.Views;

/// <summary>
/// O que orbita a sala sem ser vídeo: senha, bandeja do sistema, atalhos globais, gravação,
/// compartilhar só uma região e a porta de entrada das configurações.
/// </summary>
public partial class RoomWindow
{
    private string? _password;
    private TrayIcon? _tray;
    private GlobalHotkeys? _hotkeys;
    private bool _trayNoticeShown;

    // ---------------------------------------------------------------- senha

    /// <summary>Conecta; se a sala pedir senha (ou a senha estiver errada), pergunta e tenta de
    /// novo até entrar ou a pessoa desistir.</summary>
    private async Task<string?> ConnectAskingPasswordAsync()
    {
        while (true)
        {
            var rejection = await _client.ConnectAsync(
                _hostAddress, MainWindow.ControlPort, MainWindow.MediaPort, _nickname, _password);

            if (rejection is null || !_client.RejectedForPassword)
            {
                return rejection;
            }

            var typed = PasswordPrompt.Ask(this, rejection);
            if (typed is null)
            {
                return rejection;
            }

            _password = typed;
        }
    }

    // ---------------------------------------------------------------- ligar / desligar

    /// <summary>Chamado uma vez, depois de entrar na sala.</summary>
    private void InitializeExtras()
    {
        RoomLockIcon.Visibility = string.IsNullOrEmpty(_password) ? Visibility.Collapsed : Visibility.Visible;

        _tray = new TrayIcon("MGRScreen");
        _tray.OpenRequested += RestoreFromTray;
        _tray.StopSharingRequested += StopSharing;
        _tray.ExitRequested += () =>
        {
            RestoreFromTray();
            Close();
        };

        StateChanged += (_, _) =>
        {
            // Minimizar esconde a janela e deixa só o ícone perto do relógio. A sala (e a
            // transmissão) continua rodando normalmente.
            if (WindowState == WindowState.Minimized && _settings.MinimizeToTray && _fullscreenTile is null)
            {
                Hide();
                if (!_trayNoticeShown)
                {
                    _trayNoticeShown = true;
                    _tray?.ShowBalloon("MGRScreen continua aberto", "A sala está na bandeja, perto do relógio. Clique duas vezes no ícone pra voltar.");
                }
            }
        };

        _hotkeys = new GlobalHotkeys(this);
        ApplyHotkeys();
        AppSettings.Changed += OnSettingsChanged;

        InitializeChat();
        InitializeVoice();
        OnRoomFeaturesChanged();
    }

    private void ShutdownExtras()
    {
        AppSettings.Changed -= OnSettingsChanged;
        ShutdownVoice();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;
    }

    private void OnSettingsChanged()
    {
        ApplyHotkeys();
        ApplyVoiceSettings();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ShowTrayBalloon(string text) => _tray?.ShowBalloon("MGRScreen", text);

    // ---------------------------------------------------------------- atalhos globais

    private void ApplyHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        Register("share", _settings.HotkeyToggleShare, ToggleShareFromHotkey);
        Register("mic", _settings.HotkeyToggleMic, () => OnMicClick(this, new RoutedEventArgs()));

        void Register(string id, string gesture, Action action)
        {
            if (string.IsNullOrWhiteSpace(gesture))
            {
                _hotkeys.Unregister(id);
            }
            else if (!_hotkeys.Register(id, gesture, action))
            {
                ShowToast($"O atalho {gesture} já está em uso por outro programa.", "");
            }
        }
    }

    /// <summary>Atalho de compartilhar: para se estiver compartilhando; senão recomeça no mesmo
    /// monitor da última vez, sem abrir o seletor (a pessoa pode estar dentro de um jogo).</summary>
    private async void ToggleShareFromHotkey()
    {
        if (_isSharing)
        {
            StopSharing();
            return;
        }

        var monitor = _settings.LastMonitorDeviceName;
        if (string.IsNullOrEmpty(monitor) || !ShareButton.IsEnabled)
        {
            RestoreFromTray();
            return;
        }

        _capture.CropRegion = null;
        await StartCaptureAsync(() => _capture.StartForMonitorAsync(monitor));
    }

    // ---------------------------------------------------------------- configurações

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
    }

    // ---------------------------------------------------------------- compartilhar uma região

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private async void OnShareRegionClick(object sender, RoutedEventArgs e)
    {
        if (_isSharing)
        {
            ShowToast("Pare a transmissão atual antes de escolher uma região.", "");
            return;
        }

        // A região é escolhida no monitor em que esta janela está.
        var handle = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var monitor = MonitorEnumerator.GetMonitors().FirstOrDefault(m => m.Handle == handle)
                      ?? MonitorEnumerator.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (monitor is null)
        {
            return;
        }

        var region = RegionPickerWindow.Pick(monitor.Handle);
        if (region is not { } rect)
        {
            StatusText.Text = "Nenhuma região escolhida.";
            return;
        }

        _capture.CropRegion = (rect.X, rect.Y, rect.Width, rect.Height);
        StatusText.Text = $"Compartilhando uma região de {rect.Width}x{rect.Height}...";
        var started = await StartCaptureAsync(() => _capture.StartForMonitorAsync(monitor.DeviceName));
        if (!started)
        {
            _capture.CropRegion = null;
        }
    }

    // ---------------------------------------------------------------- gravação

    /// <summary>
    /// Grava o vídeo daquela tela exatamente como ele chega (sem recodificar, então não pesa).
    /// Só a imagem: o ffmpeg que acompanha o app não tem como juntar o áudio no arquivo.
    /// </summary>
    private void ToggleRecording(StreamTile tile)
    {
        if (tile.Recorder is not null)
        {
            StopRecording(tile, announce: true);
            return;
        }

        try
        {
            var folder = _settings.EffectiveRecordingsFolder;
            var path = Path.Combine(folder, $"{SafeFileName(tile.Nickname)} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
            var fps = tile.Info?.Fps ?? (int)Math.Round(1 / tile.FrameSeconds);

            tile.Recorder = new StreamRecorder(_ffmpegPath, path, Math.Max(1, fps));
            tile.RecordButton.Content = GlyphStop;
            tile.RecordButton.ToolTip = "Parar a gravação";
            tile.RecText.Text = "REC";
            tile.RecBadge.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowToast($"Não consegui começar a gravar: {ex.Message}", "");
        }
    }

    private async void StopRecording(StreamTile tile, bool announce)
    {
        var recorder = tile.Recorder;
        if (recorder is null)
        {
            return;
        }

        tile.Recorder = null;
        tile.RecordButton.Content = GlyphRecord;
        tile.RecordButton.ToolTip = "Gravar esta tela";
        tile.RecBadge.Visibility = Visibility.Collapsed;

        var path = await recorder.StopAsync();
        if (!announce || _shuttingDown)
        {
            return;
        }

        if (path is null)
        {
            ShowToast("Nada foi gravado (o vídeo ainda não tinha começado).", "");
        }
        else
        {
            ShowToast($"Gravação salva: {Path.GetFileName(path)}", GlyphRecord, () => RevealInExplorer(path));
        }
    }

    private static void UpdateRecordingBadge(StreamTile tile)
    {
        if (tile.Recorder is { HasStarted: true } recorder)
        {
            tile.RecText.Text = $"REC {recorder.Elapsed:mm\\:ss}";
        }
    }

    /// <summary>O meu próprio stream também pode ser gravado (pelo tile de preview).</summary>
    private void RecordLocalChunk(byte[] chunk)
    {
        if (_tiles.TryGetValue(LocalTileId, out var tile))
        {
            tile.Recorder?.Write(chunk);
        }
    }

    private sealed partial class StreamTile
    {
        public volatile StreamRecorder? Recorder;
    }
}
