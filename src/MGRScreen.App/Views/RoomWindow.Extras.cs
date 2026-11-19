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
            // "Sair" na bandeja é fechar de vez, sem perguntar de novo.
            _leaving = true;
            Close();
        };

        _appliedVideoProcessing = _settings.VideoProcessing;
        _hotkeys = new GlobalHotkeys(this);
        ApplyHotkeys();
        AppSettings.Changed += OnSettingsChanged;

        InitializeChat();
        InitializeVoice();
        OnRoomFeaturesChanged();

        if (_settings.CheckUpdates)
        {
            _ = RemindAboutUpdateAsync();
        }
    }

    /// <summary>Dentro da sala a atualização só é lembrada: instalar agora fecharia o app e
    /// derrubaria a transmissão. O botão de atualizar fica no menu inicial.</summary>
    private async Task RemindAboutUpdateAsync()
    {
        var update = await UpdateChecker.CheckAsync(CancellationToken.None);
        if (update is not null && !_shuttingDown)
        {
            ShowToast($"Saiu a versão {update.Version}. Dá pra atualizar pelo menu inicial, ao sair da sala.", "");
        }
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
        ApplyVideoProcessing();
    }

    // ---------------------------------------------------------------- placa de vídeo x processador

    private int _appliedVideoProcessing;

    /// <summary>Preferência das configurações: codificar e decodificar no processador, mesmo
    /// havendo placa de vídeo. A conversão de cor da captura continua na placa de qualquer jeito
    /// (no processador ela custaria mais do que tudo o que esta opção economiza).</summary>
    private bool PreferCpu => _settings.VideoProcessing == 1;

    /// <summary>A opção mudou com a sala aberta: vale na hora, reiniciando o que estiver rodando.</summary>
    private void ApplyVideoProcessing()
    {
        if (_appliedVideoProcessing == _settings.VideoProcessing)
        {
            return;
        }

        _appliedVideoProcessing = _settings.VideoProcessing;

        if (_isSharing)
        {
            RestartStreamingWithCurrentQuality();
        }

        if (_cameraActive && _cameraWidth > 0)
        {
            RestartCameraStreaming(_cameraWidth, _cameraHeight);
        }

        if (!_backgroundMode)
        {
            foreach (var tile in _tiles.Values.Where(t => !t.IsLocal && t.Watching && t.Decoder is not null))
            {
                PauseVideo(tile);
                StartWatching(tile);
            }
        }

        ShowToast(PreferCpu
            ? "Vídeo agora é processado no processador (CPU)."
            : "Vídeo agora é processado na placa de vídeo, quando disponível.", "");
    }

    // ---------------------------------------------------------------- fechar x segundo plano

    private enum CloseChoice { Cancel, Background, Close }

    private bool _backgroundMode;

    /// <summary>
    /// O X da janela não some mais com o app sem avisar, nem fecha tudo sem perguntar: a pessoa
    /// escolhe entre fechar de vez e deixar em segundo plano (e pode mandar lembrar a escolha).
    /// </summary>
    private CloseChoice AskCloseChoice()
    {
        // Ainda nem entrou na sala (sem bandeja): não há o que deixar em segundo plano.
        if (_tray is null)
        {
            return CloseChoice.Close;
        }

        var warning = _isSharing
            ? "Você está compartilhando a tela: fechar de vez interrompe a transmissão pra todo mundo."
            : _ownedServer is not null && _client.Participants.Count > 1
                ? "Você é o anfitrião: fechar de vez encerra a sala pra todo mundo."
                : null;

        switch (_settings.CloseBehavior)
        {
            case 1:
                return CloseChoice.Background;

            case 2 when warning is null:
                return CloseChoice.Close;

            case 2:
                // Mesmo com "sempre fechar", derrubar a sala dos outros merece uma confirmação.
                return MessageBox.Show(this, warning + " Fechar mesmo assim?", "Fechar o MGRScreen",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
                    ? CloseChoice.Close
                    : CloseChoice.Cancel;
        }

        var (choice, remember) = CloseChoiceDialog.Ask(this, warning);
        if (remember && choice != CloseChoice.Cancel)
        {
            _settings.CloseBehavior = choice == CloseChoice.Background ? 1 : 2;
            _settings.Save();
        }

        return choice;
    }

    /// <summary>
    /// Some da barra de tarefas e fica só no ícone perto do relógio, gastando o mínimo: para de
    /// decodificar e desenhar o vídeo dos outros, que é o que mais pesa em quem assiste. O que
    /// os outros dependem de você continua: a sua transmissão, o áudio, a voz e o chat.
    /// </summary>
    private void EnterBackground()
    {
        if (_backgroundMode)
        {
            return;
        }

        _backgroundMode = true;

        if (_fullscreenTile is not null)
        {
            ExitFullscreen();
        }

        foreach (var tile in _tiles.Values)
        {
            ClosePopOut(tile);

            if (!tile.IsLocal && tile.Decoder is not null)
            {
                PauseVideo(tile);
            }
            else if (tile.IsLocal)
            {
                tile.ClearQueuedFrames(); // o preview da própria tela também para
            }
        }

        CompositionTarget.Rendering -= OnCompositionRendering;
        Hide();

        if (!_trayNoticeShown)
        {
            _trayNoticeShown = true;
            _tray?.ShowBalloon("MGRScreen em segundo plano",
                "A sala continua aberta no ícone perto do relógio. Clique duas vezes nele pra voltar, ou com o botão direito pra sair.");
        }
    }

    /// <summary>Para o decoder daquela tela sem "parar de assistir": o áudio dela continua e o
    /// vídeo volta sozinho ao reabrir a janela.</summary>
    private static void PauseVideo(StreamTile tile)
    {
        tile.StopPlayback();
        tile.ClearQueuedFrames();
    }

    /// <summary>Traz a sala de volta pra frente; se estava em segundo plano, religa o vídeo.</summary>
    private void RestoreFromTray()
    {
        if (_backgroundMode)
        {
            _backgroundMode = false;
            CompositionTarget.Rendering += OnCompositionRendering;

            foreach (var tile in _tiles.Values.Where(t => !t.IsLocal && t.Watching && t.Decoder is null))
            {
                StartWatching(tile);
            }
        }

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
