using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MGRScreen.Core.Audio;
using MGRScreen.Core.Capture;
using MGRScreen.Core.Encoding;
using MGRScreen.Core.Models;
using MGRScreen.Core.Networking;
using NAudio.Wave;

namespace MGRScreen.App.Views;

public partial class RoomWindow : Window
{
    private readonly string _nickname;
    private readonly string _hostAddress;
    private readonly string _roomLabel;
    private readonly RoomServer? _ownedServer;

    private RoomClient _client = new();
    private readonly ScreenCaptureService _capture = new();
    private readonly SystemAudioCapture _audioCapture = new();

    /// <summary>Toca o áudio de todo mundo por uma saída só, com volume por pessoa.</summary>
    private readonly RemoteAudioMixer _audioMixer = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private StreamTile? _volumePopupTile;

    private readonly CameraCaptureService _camera = new();
    private FfmpegEncoder? _cameraEncoder;
    private bool _cameraActive;
    private volatile bool _cameraWaitingForFirstFrame;
    private bool _cameraEncoderFallbackUsed;
    private int _cameraRestartPending;
    private int _cameraWidth;
    private int _cameraHeight;

    /// <summary>O que eu anunciei da câmera, pra reanunciar depois de reconectar.</summary>
    private StreamInfo? _myCameraInfo;

    private CancellationTokenSource? _reconnectCts;
    private bool _reconnecting;
    private bool _shuttingDown;

    /// <summary>Já confirmou a saída pelo botão — não perguntar de novo no Closing.</summary>
    private bool _leaving;

    /// <summary>O que eu anunciei estar transmitindo, pra reanunciar depois de reconectar.</summary>
    private StreamInfo? _myStreamInfo;

    /// <summary>Tamanho do que estou capturando, pra conseguir reiniciar o encoder na hora.</summary>
    private int _captureWidth;
    private int _captureHeight;
    private FfmpegEncoder? _encoder;

    // ConcurrentDictionary e não Dictionary: a thread de rede lê isto a cada chunk recebido
    // enquanto a thread de UI adiciona/remove tiles. Com Dictionary comum essa corrida pode
    // fazer a busca falhar (ou lançar) e o vídeo simplesmente nunca chegar ao decoder.
    private readonly ConcurrentDictionary<int, StreamTile> _tiles = new();
    private const int LocalTileId = -1;        // preview da minha tela
    private const int LocalCameraTileId = -2;  // preview da minha câmera

    /// <summary>
    /// Chave de um tile. Cada pessoa pode ter até dois (tela e câmera), então o tile é
    /// identificado pelo par (participante, tipo) — não só pelo participante, como era quando só
    /// existia a tela. Ids de participante começam em 1, então as chaves remotas são ≥ 2 e nunca
    /// colidem com as locais (negativas).
    /// </summary>
    private static int TileKey(int participantId, StreamKind kind) => (participantId * 2) + (int)kind;

    private string _ffmpegPath = "ffmpeg.exe";
    private string _bestEncoder = "libx264";
    private bool _encoderFallbackUsed;
    private bool _isSharing;
    private bool _waitingForFirstFrame;
    private int _blackFrameSeconds;
    private long _lastBlackCheckTick;
    private volatile bool _lastCapturedFrameWasBlack;

    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastFramesEncoded;

    private StreamTile? _fullscreenTile;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;
    private ResizeMode _previousResizeMode;

    /// <param name="roomLabel">Como chamar esta sala no menu depois ("sala do Gui"). Quando vem
    /// da busca automática é o nickname do anfitrião; quando é IP digitado à mão, o próprio IP.</param>
    public RoomWindow(string nickname, string hostAddress, RoomServer? ownedServer, string? roomLabel = null)
    {
        InitializeComponent();

        _nickname = nickname;
        _hostAddress = hostAddress;
        _ownedServer = ownedServer;
        _roomLabel = roomLabel ?? hostAddress;

        // O anfitrião vê o endereço que os amigos precisam usar; quem entrou vê onde se conectou.
        RoomAddressText.Text = ownedServer is not null
            ? LocalNetworks.PreferredInviteAddress().Address
            : hostAddress;

        Loaded += OnLoaded;
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;
        SizeChanged += (_, _) => RelayoutTiles();
        Closed += (_, _) => Shutdown();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_leaving && !ConfirmLeavingWhileSharing())
        {
            e.Cancel = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _statsTimer.Tick += OnStatsTick;
        _statsTimer.Start();

        RestorePreferences();
        _audioMixer.Start();

        _camera.FrameCaptured += OnCameraFrame;
        _camera.Failed += message => Dispatcher.BeginInvoke(() =>
        {
            StopCamera();
            StatusText.Text = $"A câmera parou: {message}";
        });

        WireClientEvents(_client);

        if (_ownedServer is not null)
        {
            _ownedServer.Log += line => Dispatcher.BeginInvoke(() => StatusText.Text = line);
        }

        StatusText.Text = "Conectando na sala...";
        string? rejection;
        try
        {
            rejection = await _client.ConnectAsync(
                _hostAddress, MainWindow.ControlPort, MainWindow.MediaPort, _nickname);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não consegui conectar em {_hostAddress}: {ex.Message}";
            ShareButton.IsEnabled = false;
            return;
        }

        if (rejection is not null)
        {
            StatusText.Text = rejection;
            ShareButton.IsEnabled = false;
            return;
        }

        UpdateParticipantsLabel();
        if (_ownedServer is not null)
        {
            ConnectionInfoText.Text = "Você é o anfitrião · a sala aparece sozinha pra quem estiver nas suas redes";
        }
        else
        {
            ConnectionInfoText.Text = "Conectado na sala.";
        }
        StatusText.Text = "Conectado.";

        _settings.LastRoomAddress = _hostAddress;
        _settings.LastRoomHost = _ownedServer is not null ? "sua sala" : _roomLabel;
        _settings.Save();

        _ffmpegPath = FfmpegLocator.FindFfmpegExecutable();
        _bestEncoder = await FfmpegLocator.DetectBestVideoEncoderAsync(_ffmpegPath);
        StatusText.Text = $"Conectado. Encoder de vídeo: {_bestEncoder}.";
        CameraButton.IsEnabled = true;
    }

    /// <summary>Liga os eventos da conexão. Fica separado porque a reconexão cria um cliente
    /// novo do zero e precisa ligar tudo de novo nele.</summary>
    private void WireClientEvents(RoomClient client)
    {
        client.StreamStarted += info => Dispatcher.BeginInvoke(() => OnRemoteStreamStarted(info));
        client.StreamStopped += (id, kind) => Dispatcher.BeginInvoke(() => RemoveTile(TileKey(id, kind)));
        client.ParticipantJoined += p => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"{p.Nickname} entrou na sala.";
            UpdateParticipantsLabel();
        });
        client.ParticipantLeft += _ => Dispatcher.BeginInvoke(UpdateParticipantsLabel);
        client.Disconnected += reason => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(client, _client) || _shuttingDown)
                return;
            StatusText.Text = reason;
            StartReconnecting(reason);
        });
        client.ViewerCongested += (nickname, isMe) => Dispatcher.BeginInvoke(() =>
        {
            if (isMe)
            {
                // O aviso mais importante é este: sem ele a pessoa acha que o app trava.
                StatusText.Text = "⚠ A sua conexão não está acompanhando o que estão transmitindo. " +
                                  "A imagem vai congelar de vez em quando — peça pra quem está compartilhando " +
                                  "baixar a qualidade para 720p.";
            }
            else if (_isSharing)
            {
                StatusText.Text = $"⚠ {nickname} não está conseguindo acompanhar a sua transmissão. " +
                                  "A imagem dele vai congelar de vez em quando — considere trocar a qualidade para 720p.";
            }
        });
        client.VideoChunkReceived += OnRemoteVideoChunk;
        client.AudioChunkReceived += OnRemoteAudioChunk;
    }

    /// <summary>
    /// Um clique no X derrubava a sua transmissão sem perguntar nada. Agora pergunta.
    /// </summary>
    private bool ConfirmLeavingWhileSharing()
    {
        if (!_isSharing)
        {
            return true;
        }

        var answer = MessageBox.Show(
            "Você está compartilhando a sua tela agora. Sair vai interromper a transmissão pra todo mundo. Sair mesmo assim?",
            "Sair da sala",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return answer == MessageBoxResult.Yes;
    }

    // ---------------------------------------------------------------- reconexão

    /// <summary>
    /// Cai a internet por 2 segundos e antes disso a sala inteira estava perdida: as telas
    /// congelavam e a única saída era fechar e abrir o app. Agora tenta voltar sozinho.
    /// </summary>
    private void StartReconnecting(string reason)
    {
        if (_reconnecting || _shuttingDown)
        {
            return;
        }

        _reconnecting = true;
        _reconnectCts = new CancellationTokenSource();
        _ = ReconnectLoopAsync(reason, _reconnectCts.Token);
    }

    private async Task ReconnectLoopAsync(string reason, CancellationToken token)
    {
        var attempt = 0;

        while (!token.IsCancellationRequested)
        {
            attempt++;
            ReconnectBanner.Visibility = Visibility.Visible;
            ReconnectBannerText.Text = $"{reason} Reconectando... (tentativa {attempt})";

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var client = new RoomClient();
            WireClientEvents(client);

            // As telas antigas são de uma conexão que morreu — os ids de participante mudam a cada
            // conexão, então elas nunca mais receberiam vídeo. O ConnectAsync avisa de novo quem
            // está compartilhando e elas são recriadas.
            ClearRemoteTiles();

            string? rejection;
            try
            {
                rejection = await client.ConnectAsync(
                    _hostAddress, MainWindow.ControlPort, MainWindow.MediaPort, _nickname);
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException)
            {
                client.Disconnect();
                continue;
            }

            if (rejection is not null)
            {
                client.Disconnect();
                continue;
            }

            _client.Disconnect();
            _client = client;

            if (_isSharing && _myStreamInfo is not null)
            {
                await _client.AnnounceStreamStartedAsync(_myStreamInfo);
            }

            if (_cameraActive && _myCameraInfo is not null)
            {
                await _client.AnnounceStreamStartedAsync(_myCameraInfo);
            }

            ReconnectBanner.Visibility = Visibility.Collapsed;
            StatusText.Text = "Reconectado.";
            UpdateParticipantsLabel();
            _reconnecting = false;
            return;
        }
    }

    private void ClearRemoteTiles()
    {
        foreach (var key in _tiles.Keys.Where(key => key >= 0).ToList())
        {
            RemoveTile(key);
        }
    }

    private List<AudioAppInfo> _audioApps = new();

    private void OnAudioModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AudioAppCombo is null)
        {
            return; // ainda carregando o XAML
        }

        var isSingleApp = AudioModeCombo.SelectedIndex == 3;
        AudioAppCombo.Visibility = isSingleApp ? Visibility.Visible : Visibility.Collapsed;

        if (isSingleApp)
        {
            RefreshAudioApps();
        }
    }

    private void RefreshAudioApps()
    {
        var previous = AudioAppCombo.SelectedItem as string;

        _audioApps = AudioDeviceHelper.GetAppsWithAudio().ToList();
        AudioAppCombo.Items.Clear();
        foreach (var app in _audioApps)
        {
            AudioAppCombo.Items.Add(app.Name);
        }

        if (AudioAppCombo.Items.Count == 0)
        {
            StatusText.Text = "Nenhum app com áudio ativo no momento — abra o jogo/vídeo e escolha de novo.";
            return;
        }

        var index = previous is null ? 0 : Math.Max(0, AudioAppCombo.Items.IndexOf(previous));
        AudioAppCombo.SelectedIndex = index;
    }

    private AudioShareMode SelectedAudioMode => AudioModeCombo.SelectedIndex switch
    {
        1 => AudioShareMode.SystemExceptSelf,
        2 => AudioShareMode.SystemExceptDiscord,
        3 => AudioShareMode.SingleApp,
        4 => AudioShareMode.None,
        _ => AudioShareMode.SystemExceptSelfAndDiscord,
    };

    // ---------------------------------------------------------------- compartilhar minha tela

    private async void OnShareClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Escolha o monitor/janela na janela do Windows que vai abrir...";
        await StartCaptureAsync(() => _capture.StartAsync(new WindowInteropHelper(this).Handle));
    }

    /// <summary>Compartilha direto o mesmo monitor da última vez, sem abrir o seletor.</summary>
    private async void OnShareAgainClick(object sender, RoutedEventArgs e)
    {
        var monitor = _settings.LastMonitorDeviceName;
        StatusText.Text = "Compartilhando o mesmo monitor da última vez...";

        var started = await StartCaptureAsync(() => _capture.StartForMonitorAsync(monitor));
        if (!started)
        {
            StatusText.Text = "Aquele monitor não está mais conectado — escolha outra tela.";
            UpdateShareAgainButton();
        }
    }

    private async Task<bool> StartCaptureAsync(Func<Task<bool>> start)
    {
        ShareButton.IsEnabled = false;
        ShareAgainButton.IsEnabled = false;

        try
        {
            _capture.TargetFps = QualityCombo.SelectedIndex is 1 or 2 ? 60 : 30;
            _capture.FrameCaptured += OnLocalFrameCaptured;
            _waitingForFirstFrame = true;

            var started = await start();
            if (!started)
            {
                _capture.FrameCaptured -= OnLocalFrameCaptured;
                _waitingForFirstFrame = false;
                StatusText.Text = "Nenhuma tela selecionada.";
                ShareButton.IsEnabled = true;
                ShareAgainButton.IsEnabled = true;
                return false;
            }

            // Só dá pra oferecer "de novo" quando foi um monitor identificável.
            _settings.LastMonitorDeviceName = _capture.LastMonitorDeviceName ?? "";
            _settings.AudioModeIndex = AudioModeCombo.SelectedIndex;
            _settings.Save();

            StatusText.Text = "Iniciando transmissão...";
            return true;
        }
        catch (Exception ex)
        {
            _capture.FrameCaptured -= OnLocalFrameCaptured;
            _waitingForFirstFrame = false;
            StatusText.Text = $"Erro ao iniciar captura: {ex.Message}";
            ShareButton.IsEnabled = true;
            ShareAgainButton.IsEnabled = true;
            return false;
        }
    }

    private void OnLocalFrameCaptured(CapturedFrame frame)
    {
        if (_waitingForFirstFrame)
        {
            _waitingForFirstFrame = false;
            Dispatcher.BeginInvoke(() => BeginStreaming(frame.Width, frame.Height));
        }

        var encoder = _encoder;
        if (encoder is not null)
        {
            // A resolução da tela pode mudar no meio da transmissão — é literalmente o que
            // acontece quando um jogo entra em tela cheia e troca o modo de vídeo. O ffmpeg foi
            // iniciado com `-s LARGURAxALTURA` fixo, então continuar enviando frames de outro
            // tamanho produz um stream desalinhado: quem assiste vê tela preta/lixo, enquanto o
            // preview local de quem compartilha continua perfeito (ele usa o tamanho do frame).
            if (frame.Width != encoder.InputWidth || frame.Height != encoder.InputHeight)
            {
                Dispatcher.BeginInvoke(() => RestartStreamingForNewSize(frame.Width, frame.Height));
                return;
            }

            encoder.SubmitFrame(frame.Data);
        }

        // Uma vez por segundo, checa se a captura está vindo totalmente preta. É o sintoma
        // clássico de jogo em tela cheia EXCLUSIVA: o WGC entrega frames, mas pretos — e aí
        // quem assiste vê tela preta sem nenhuma pista do motivo.
        var now = Environment.TickCount64;
        if (_isSharing && now - _lastBlackCheckTick >= 1000)
        {
            _lastBlackCheckTick = now;
            _lastCapturedFrameWasBlack = IsMostlyBlack(frame.Data);
        }

        if (_tiles.TryGetValue(LocalTileId, out var tile) && tile.Watching)
        {
            PushFrameToTile(tile, frame.Data, frame.Width, frame.Height);
        }
    }

    /// <summary>Amostra ~400 pixels espalhados em vez de varrer os 8 MB do frame.</summary>
    private static bool IsMostlyBlack(byte[] bgra)
    {
        const int samples = 400;
        var step = Math.Max(4, bgra.Length / samples / 4 * 4);

        for (var i = 0; i + 2 < bgra.Length; i += step)
        {
            if (bgra[i] > 12 || bgra[i + 1] > 12 || bgra[i + 2] > 12)
            {
                return false;
            }
        }

        return true;
    }

    private void BeginStreaming(int width, int height)
    {
        _captureWidth = width;
        _captureHeight = height;

        // 0 = 1080p30 · 1 = 1080p60 · 2 = 720p60 · 3 = 720p30
        var quality = QualityCombo.SelectedIndex;
        var fps = quality is 1 or 2 ? 60 : 30;
        var maxHeight = quality >= 2 ? 720 : 1080;
        var bitrate = quality switch
        {
            1 => 12_000, // 1080p60
            2 => 6_000,  // 720p60 — metade dos pixels do 1080p, então rende bem com menos banda
            3 => 4_000,  // 720p30
            _ => 8_000,  // 1080p30
        };

        var (outWidth, outHeight) = SessionSettings.ScaleToHeight(width, height, maxHeight);

        var settings = new SessionSettings
        {
            Width = width,
            Height = height,
            OutputWidth = outWidth,
            OutputHeight = outHeight,
            Fps = fps,
            VideoBitrateKbps = bitrate,
            VideoEncoder = _encoderFallbackUsed ? "libx264" : _bestEncoder,
        };

        var hasAudio = _isSharing || StartAudioCapture();

        _encoder = new FfmpegEncoder(settings, _ffmpegPath);
        _encoder.FfmpegLogLine += line => Dispatcher.BeginInvoke(() => StatusText.Text = $"[ffmpeg] {line}");
        _encoder.Exited += code => Dispatcher.BeginInvoke(() => OnEncoderExited(code, width, height));
        _encoder.StartToStream();

        _myStreamInfo = new StreamInfo
        {
            Generation = System.Diagnostics.Stopwatch.GetTimestamp(),
            Width = outWidth,
            Height = outHeight,
            Fps = fps,
            HasAudio = hasAudio,
            AudioSampleRate = _audioCapture.SampleRate,
            AudioChannels = _audioCapture.Channels,
        };

        var screenInfo = _myStreamInfo;
        _encoder.EncodedChunkReady += (frame, pts) => _client.SendVideoChunk(frame, pts, screenInfo);
        _ = _client.AnnounceStreamStartedAsync(_myStreamInfo);

        _isSharing = true;
        _blackFrameSeconds = 0;
        _lastFramesEncoded = 0;
        ShareButton.Visibility = Visibility.Collapsed;
        ShareAgainButton.Visibility = Visibility.Collapsed;
        StopShareButton.Visibility = Visibility.Visible;
        StopShareButton.IsEnabled = true;

        if (!_tiles.ContainsKey(LocalTileId))
        {
            AddTile(LocalTileId, LocalTileId, StreamKind.Screen, $"{_nickname} (você)", width, height, info: null);
        }

        UpdateParticipantsLabel();
        StatusText.Text = outWidth == width
            ? $"Compartilhando {width}x{height} a {fps} fps."
            : $"Compartilhando {width}x{height} reduzido para {outWidth}x{outHeight} a {fps} fps.";
    }

    /// <summary>A tela mudou de tamanho no meio da transmissão: recria o encoder no novo tamanho
    /// e reavisa a sala pra que todo mundo recrie o decoder na resolução certa.</summary>
    /// <summary>Reinicia o encoder com o preset escolhido agora, sem parar a captura.</summary>
    private void RestartStreamingWithCurrentQuality()
    {
        if (!_isSharing || _captureWidth == 0)
        {
            return;
        }

        StatusText.Text = "Trocando a qualidade da transmissão...";

        // A captura é lida a cada frame, então mudar aqui vale imediatamente.
        _capture.TargetFps = QualityCombo.SelectedIndex is 1 or 2 ? 60 : 30;

        _encoder?.Stop();
        _encoder = null;

        RemoveTile(LocalTileId);
        BeginStreaming(_captureWidth, _captureHeight);
    }

    private void RestartStreamingForNewSize(int width, int height)
    {
        if (!_isSharing)
        {
            return;
        }

        StatusText.Text = $"A resolução mudou para {width}x{height}, reiniciando a transmissão...";

        _encoder?.Stop();
        _encoder = null;

        RemoveTile(LocalTileId);
        BeginStreaming(width, height);
    }

    private void OnEncoderExited(int exitCode, int width, int height)
    {
        if (!_isSharing)
        {
            return;
        }

        if (!_encoderFallbackUsed && _bestEncoder != "libx264")
        {
            // O encoder de hardware aparecia na lista do ffmpeg mas não funcionou de verdade
            // (driver, placa ocupada, etc). Sem este fallback o sintoma pro resto da sala é
            // apenas "tela preta", sem nenhuma pista do motivo.
            _encoderFallbackUsed = true;
            StatusText.Text = $"O encoder {_bestEncoder} falhou (código {exitCode}). Tentando de novo com libx264 (CPU)...";
            _encoder?.Stop();
            _encoder = null;
            RemoveTile(LocalTileId);
            BeginStreaming(width, height);
            return;
        }

        StatusText.Text = $"O ffmpeg encerrou inesperadamente (código {exitCode}). Transmissão interrompida.";
        StopSharing();
    }

    private bool StartAudioCapture()
    {
        var mode = SelectedAudioMode;
        if (mode == AudioShareMode.None)
        {
            return false;
        }

        uint? targetPid = null;
        if (mode == AudioShareMode.SingleApp)
        {
            var index = AudioAppCombo.SelectedIndex;
            if (index < 0 || index >= _audioApps.Count)
            {
                StatusText.Text = "Escolha qual app terá o áudio compartilhado.";
                return false;
            }
            targetPid = _audioApps[index].ProcessId;
        }

        try
        {
            _audioCapture.AudioCaptured += OnLocalAudioCaptured;
            _audioCapture.Notice += message => Dispatcher.BeginInvoke(() => StatusText.Text = message);
            _audioCapture.Start(mode, targetPid);

            AudioModeCombo.IsEnabled = false;
            AudioAppCombo.IsEnabled = false;
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Áudio não pôde ser capturado: {ex.Message}";
            return false;
        }
    }

    private readonly object _audioClockLock = new();
    private long _audioClockGeneration;
    private long _nextAudioPts;

    private void OnLocalAudioCaptured(byte[] pcm16)
    {
        var info = _myStreamInfo;
        if (info is not { HasAudio: true } || info.AudioSampleRate <= 0 || info.AudioChannels <= 0)
            return;

        var nowUs = (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
            (1_000_000d / System.Diagnostics.Stopwatch.Frequency));
        var durationUs = pcm16.Length * 1_000_000L / (info.AudioSampleRate * info.AudioChannels * 2);
        lock (_audioClockLock)
        {
            var estimate = Math.Max(0, nowUs - durationUs);
            if (_audioClockGeneration != info.Generation || Math.Abs(_nextAudioPts - estimate) > 100_000)
            {
                _audioClockGeneration = info.Generation;
                _nextAudioPts = estimate;
            }
            _client.SendAudioChunk(pcm16, _nextAudioPts, info);
            _nextAudioPts += durationUs;
        }
    }

    private void OnStopShareClick(object sender, RoutedEventArgs e) => StopSharing();

    private void StopSharing()
    {
        if (!_isSharing)
        {
            return;
        }

        _isSharing = false;
        _capture.FrameCaptured -= OnLocalFrameCaptured;
        _capture.Stop();

        _audioCapture.AudioCaptured -= OnLocalAudioCaptured;
        _audioCapture.Stop();
        AudioModeCombo.IsEnabled = true;
        AudioAppCombo.IsEnabled = true;

        _encoder?.Stop();
        _encoder = null;

        _ = _client.AnnounceStreamStoppedAsync();

        RemoveTile(LocalTileId);

        ShareButton.Visibility = Visibility.Visible;
        ShareButton.IsEnabled = true;
        ShareAgainButton.IsEnabled = true;
        StopShareButton.Visibility = Visibility.Collapsed;
        UpdateShareAgainButton();
        UpdateParticipantsLabel();
        StatusText.Text = "Você parou de compartilhar.";
    }

    // ---------------------------------------------------------------- minha câmera

    private async void OnCameraClick(object sender, RoutedEventArgs e)
    {
        if (_cameraActive)
        {
            StopCamera();
            StatusText.Text = "Câmera desligada.";
            return;
        }

        CameraButton.IsEnabled = false;
        try
        {
            var cameras = await CameraCaptureService.ListCamerasAsync();

            switch (cameras.Count)
            {
                case 0:
                    StatusText.Text = "Nenhuma câmera encontrada neste computador.";
                    break;
                case 1:
                    await StartCameraAsync(cameras[0]);
                    break;
                default:
                    ShowCameraPicker(cameras);
                    break;
            }
        }
        finally
        {
            CameraButton.IsEnabled = true;
        }
    }

    /// <summary>Lista as câmeras pra escolher, com a última usada no topo.</summary>
    private void ShowCameraPicker(IReadOnlyList<CameraInfo> cameras)
    {
        CameraPickerList.Children.Clear();

        foreach (var camera in cameras.OrderByDescending(c => c.Id == _settings.LastCameraId))
        {
            var button = new Button
            {
                Content = camera.Name,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
            };
            button.Click += async (_, _) =>
            {
                CameraPickerPopup.IsOpen = false;
                await StartCameraAsync(camera);
            };
            CameraPickerList.Children.Add(button);
        }

        CameraPickerPopup.IsOpen = true;
    }

    private async Task StartCameraAsync(CameraInfo camera)
    {
        CameraButton.IsEnabled = false;
        StatusText.Text = $"Ligando {camera.Name}...";

        // Marcado ANTES de ligar: o primeiro frame pode chegar antes do StartAsync terminar, e é
        // ele que dispara a criação do encoder.
        _cameraActive = true;
        _cameraWaitingForFirstFrame = true;

        try
        {
            await _camera.StartAsync(camera.Id);

            _settings.LastCameraId = camera.Id;
            _settings.Save();

            CameraButton.Content = "Desligar câmera";
            StatusText.Text = _camera.IsSharedReadOnly
                ? "Câmera ligada. Ela já estava aberta em outro programa (Discord?), então fica na resolução que ele escolheu."
                : "Câmera ligada.";
        }
        catch (UnauthorizedAccessException)
        {
            StopCamera();
            StatusText.Text = "O Windows bloqueou a câmera. Libere em Configurações → Privacidade e segurança → "
                            + "Câmera → \"Permitir que aplicativos da área de trabalho acessem a câmera\".";
        }
        catch (Exception ex)
        {
            StopCamera();
            StatusText.Text = $"Não consegui ligar a câmera: {ex.Message}";
        }
        finally
        {
            CameraButton.IsEnabled = true;
        }
    }

    /// <summary>Frame da câmera (chega numa thread do Windows).</summary>
    private void OnCameraFrame(CapturedFrame frame)
    {
        if (!_cameraActive)
        {
            return;
        }

        if (_cameraWaitingForFirstFrame)
        {
            _cameraWaitingForFirstFrame = false;
            Dispatcher.BeginInvoke(() => BeginCameraStreaming(frame.Width, frame.Height));
        }

        var encoder = _cameraEncoder;
        if (encoder is not null)
        {
            // Resolução mudou (outro app que controla a câmera trocou o formato, por exemplo): o
            // encoder foi aberto com tamanho fixo, então precisa recomeçar. A flag evita enfileirar
            // um reinício por frame até o primeiro ser processado.
            if (frame.Width != encoder.InputWidth || frame.Height != encoder.InputHeight)
            {
                if (Interlocked.Exchange(ref _cameraRestartPending, 1) == 0)
                {
                    Dispatcher.BeginInvoke(() => RestartCameraStreaming(frame.Width, frame.Height));
                }
                return;
            }

            encoder.SubmitFrame(frame.Data);
        }

        if (_tiles.TryGetValue(LocalCameraTileId, out var tile) && tile.Watching)
        {
            PushFrameToTile(tile, frame.Data, frame.Width, frame.Height);
        }
    }

    private void BeginCameraStreaming(int width, int height)
    {
        if (!_cameraActive)
        {
            return; // desligaram antes do primeiro frame ser processado
        }

        // Câmera não precisa da banda da tela: 720p é o teto, 30 fps, e bitrate bem menor — rosto
        // se mexe pouco e o H.264 comprime muito bem. É o que deixa sobrar banda pra tela.
        var (outWidth, outHeight) = SessionSettings.ScaleToHeight(width, height, 720);

        var settings = new SessionSettings
        {
            Width = width,
            Height = height,
            OutputWidth = outWidth,
            OutputHeight = outHeight,
            Fps = 30,
            VideoBitrateKbps = outHeight >= 720 ? 2_000 : 1_200,
            VideoEncoder = _cameraEncoderFallbackUsed ? "libx264" : _bestEncoder,
        };

        _cameraWidth = width;
        _cameraHeight = height;

        _cameraEncoder = new FfmpegEncoder(settings, _ffmpegPath);
        _cameraEncoder.Exited += code => Dispatcher.BeginInvoke(() => OnCameraEncoderExited(code));
        _cameraEncoder.StartToStream();

        _myCameraInfo = new StreamInfo
        {
            Generation = System.Diagnostics.Stopwatch.GetTimestamp(),
            Kind = StreamKind.Camera,
            Width = outWidth,
            Height = outHeight,
            Fps = 30,
            HasAudio = false,
        };
        var cameraInfo = _myCameraInfo;
        _cameraEncoder.EncodedChunkReady += (frame, pts) => _client.SendVideoChunk(frame, pts, cameraInfo);
        _ = _client.AnnounceStreamStartedAsync(_myCameraInfo);

        if (!_tiles.ContainsKey(LocalCameraTileId))
        {
            AddTile(LocalCameraTileId, LocalCameraTileId, StreamKind.Camera, $"{_nickname} (você)", width, height, info: null);
        }
    }

    private void RestartCameraStreaming(int width, int height)
    {
        Interlocked.Exchange(ref _cameraRestartPending, 0);

        if (!_cameraActive)
        {
            return;
        }

        _cameraEncoder?.Stop();
        _cameraEncoder = null;
        RemoveTile(LocalCameraTileId);
        BeginCameraStreaming(width, height);
    }

    private void OnCameraEncoderExited(int exitCode)
    {
        if (!_cameraActive)
        {
            return;
        }

        // Mesmo raciocínio da tela: o encoder de hardware aparece na lista mas pode falhar de
        // verdade (driver, limite de sessões da placa). Tenta uma vez no processador.
        if (!_cameraEncoderFallbackUsed && _bestEncoder != "libx264")
        {
            _cameraEncoderFallbackUsed = true;
            StatusText.Text = $"O encoder {_bestEncoder} falhou na câmera (código {exitCode}). Tentando com libx264...";
            _cameraEncoder?.Stop();
            _cameraEncoder = null;
            BeginCameraStreaming(_cameraWidth, _cameraHeight);
            return;
        }

        StopCamera();
        StatusText.Text = $"O ffmpeg da câmera encerrou (código {exitCode}). Câmera desligada.";
    }

    private void StopCamera()
    {
        if (!_cameraActive)
        {
            return;
        }

        _cameraActive = false;
        _cameraWaitingForFirstFrame = false;

        _camera.Stop();

        _cameraEncoder?.Stop();
        _cameraEncoder = null;

        if (_myCameraInfo is not null)
        {
            _ = _client.AnnounceStreamStoppedAsync(StreamKind.Camera);
            _myCameraInfo = null;
        }

        RemoveTile(LocalCameraTileId);
        CameraButton.Content = "Ligar câmera";
    }

    // ---------------------------------------------------------------- streams dos outros

    private void OnRemoteStreamStarted(StreamInfo info)
    {
        var key = TileKey(info.ParticipantId, info.Kind);
        RemoveTile(key);

        var tile = AddTile(key, info.ParticipantId, info.Kind, info.Nickname, info.Width, info.Height, info: info);
        StartWatching(tile);

        StatusText.Text = info.Kind == StreamKind.Camera
            ? $"{info.Nickname} ligou a câmera."
            : $"{info.Nickname} começou a compartilhar.";
        UpdateParticipantsLabel();
    }

    /// <summary>Cria (ou recria) o decoder e o áudio daquele participante.</summary>
    private void StartWatching(StreamTile tile)
    {
        if (tile.Info is null || tile.Decoder is not null)
        {
            tile.Watching = true;
            tile.UpdateToggleLabel();
            return;
        }

        var info = tile.Info;
        var decoder = new FfmpegDecoder(info.Width, info.Height, _ffmpegPath);
        decoder.FrameDecoded += frame =>
        {
            if (ReferenceEquals(tile.Decoder, decoder))
                PushFrameToTile(tile, frame.Data, frame.Width, frame.Height);
        };
        decoder.Start();
        tile.Decoder = decoder;

        if (info.HasAudio && tile.Kind == StreamKind.Screen)
        {
            _audioMixer.AddParticipant(tile.ParticipantId, info.AudioSampleRate, info.AudioChannels);
            _audioMixer.SetVolume(tile.ParticipantId, tile.Volume);
            _audioMixer.SetMuted(tile.ParticipantId, tile.Muted);
        }

        tile.Watching = true;
        tile.Placeholder.Text = "Aguardando vídeo...";
        tile.Placeholder.Visibility = Visibility.Visible;
        tile.UpdateToggleLabel();
    }

    /// <summary>Para de decodificar e de tocar o áudio daquele participante — ele continua
    /// compartilhando normalmente pros outros, só que aqui não gastamos mais CPU com isso.</summary>
    private void StopWatching(StreamTile tile)
    {
        tile.Watching = false;
        tile.StopPlayback();

        // Só o tile da TELA tem áudio. Sem esta checagem, parar de assistir a câmera de alguém
        // tiraria do mixer o som da tela dessa mesma pessoa (o mixer é indexado pelo participante).
        if (tile.Kind == StreamKind.Screen)
        {
            _audioMixer.RemoveParticipant(tile.ParticipantId);
        }
        tile.Image.Source = null;
        tile.Bitmap = null;
        tile.Placeholder.Text = tile.IsLocal
            ? "Preview desligado (você continua compartilhando)."
            : $"Você parou de assistir {tile.Nickname}.";
        tile.Placeholder.Visibility = Visibility.Visible;
        tile.UpdateToggleLabel();
    }

    private void OnToggleWatchClick(StreamTile tile)
    {
        if (tile.Watching)
        {
            StopWatching(tile);
            StatusText.Text = tile.IsLocal
                ? "Preview local desligado."
                : $"Você parou de assistir {tile.Nickname}.";
        }
        else
        {
            StartWatching(tile);
            StatusText.Text = tile.IsLocal
                ? "Preview local ligado."
                : $"Voltando a assistir {tile.Nickname}...";
        }
    }

    private void OnRemoteVideoChunk(int participantId, StreamKind kind, byte[] chunk)
    {
        if (_tiles.TryGetValue(TileKey(participantId, kind), out var tile))
        {
            Interlocked.Add(ref tile.BytesReceived, chunk.Length);

            var decoder = tile.Decoder;
            if (tile.Watching && decoder is not null)
            {
                decoder.SubmitChunk(chunk);
            }
        }
    }

    private void OnRemoteAudioChunk(int participantId, byte[] pcm16)
    {
        if (_tiles.TryGetValue(TileKey(participantId, StreamKind.Screen), out var tile) && tile.Watching)
        {
            _audioMixer.Push(participantId, pcm16);
        }
    }

    // ---------------------------------------------------------------- tiles

    private StreamTile AddTile(int key, int participantId, StreamKind kind, string nickname, int width, int height, StreamInfo? info)
    {
        var image = new Image { Stretch = Stretch.Uniform };

        // Preview da minha própria câmera espelhado, como todo app de vídeo faz (senão mexer a
        // mão direita parece mexer a esquerda). Só aqui: os outros recebem a imagem normal.
        if (participantId == LocalCameraTileId)
        {
            image.RenderTransformOrigin = new Point(0.5, 0.5);
            image.RenderTransform = new ScaleTransform(-1, 1);
        }

        var placeholder = new TextBlock
        {
            Text = "Aguardando vídeo...",
            Foreground = (Brush)FindResource("TextMutedBrush"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10),
            Child = new TextBlock
            {
                Text = kind == StreamKind.Camera ? $"{nickname} · câmera" : nickname,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            },
        };

        Button MakeTileButton(string content) => new()
        {
            Style = (Style)FindResource("SecondaryButtonStyle"),
            Padding = new Thickness(9, 3, 9, 3),
            FontSize = 11,
            Content = content,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var toggleButton = MakeTileButton("Parar de assistir");
        var muteButton = MakeTileButton("Silenciar");
        var fullscreenButton = MakeTileButton("Tela cheia");

        // Preview local não toca áudio meu; câmera não tem áudio nenhum. Nos dois casos não há o
        // que silenciar.
        muteButton.Visibility = participantId < 0 || kind == StreamKind.Camera
            ? Visibility.Collapsed
            : Visibility.Visible;

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Children = { toggleButton, muteButton, fullscreenButton },
        };

        var fpsText = new TextBlock
        {
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
        };

        var root = new Border
        {
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(10),
            BorderBrush = (Brush)FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6),
            ClipToBounds = true,
            Child = new Grid { Children = { image, placeholder, label, buttonsPanel, fpsText } },
        };

        var tile = new StreamTile
        {
            Key = key,
            Kind = kind,
            ParticipantId = participantId,
            Nickname = nickname,
            Root = root,
            Image = image,
            Placeholder = placeholder,
            FpsText = fpsText,
            ToggleButton = toggleButton,
            MuteButton = muteButton,
            FullscreenButton = fullscreenButton,
            Info = info,
            Width = width,
            Height = height,
            Watching = true,
        };
        tile.UpdateToggleLabel();
        tile.UpdateMuteLabel();

        ApplySavedAudioPreference(tile);

        toggleButton.Click += (_, _) => OnToggleWatchClick(tile);
        muteButton.Click += (_, _) => OnToggleMuteClick(tile);
        fullscreenButton.Click += (_, _) => ToggleFullscreen(tile);

        // Botão direito na tela da pessoa abre o volume dela (câmera não tem áudio).
        if (participantId >= 0 && kind == StreamKind.Screen)
        {
            root.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenVolumePopup(tile);
            };
        }

        // Duplo clique na imagem também alterna tela cheia (é o gesto que todo mundo tenta).
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ToggleFullscreen(tile);
            }
        };

        _tiles[key] = tile;
        TilesGrid.Children.Add(root);
        RelayoutTiles();

        return tile;
    }

    private void RemoveTile(int key)
    {
        if (!_tiles.TryRemove(key, out var tile))
        {
            return;
        }

        if (_fullscreenTile == tile)
        {
            ExitFullscreen();
        }

        TilesGrid.Children.Remove(tile.Root);
        if (tile.Kind == StreamKind.Screen && !tile.IsLocal)
        {
            _audioMixer.RemoveParticipant(tile.ParticipantId);
        }
        tile.Dispose();
        RelayoutTiles();
        UpdateParticipantsLabel();
    }

    // ---------------------------------------------------------------- tela cheia

    private void ToggleFullscreen(StreamTile tile)
    {
        if (_fullscreenTile == tile)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen(tile);
        }
    }

    private void EnterFullscreen(StreamTile tile)
    {
        if (_fullscreenTile is not null)
        {
            ExitFullscreen();
        }

        // Move o tile pra camada que cobre a janela inteira. Guardamos as aparências originais
        // pra devolver tudo no lugar depois.
        TilesGrid.Children.Remove(tile.Root);
        tile.Root.Margin = new Thickness(0);
        tile.Root.CornerRadius = new CornerRadius(0);
        tile.Root.BorderThickness = new Thickness(0);
        tile.Root.Width = double.NaN; // ocupa tudo em vez do tamanho calculado da grade
        tile.Root.Height = double.NaN;
        FullscreenHost.Children.Add(tile.Root);
        FullscreenHost.Visibility = Visibility.Visible;

        _previousWindowStyle = WindowStyle;
        _previousWindowState = WindowState;
        _previousResizeMode = ResizeMode;

        // Marca ANTES de mexer na janela: maximizar dispara SizeChanged -> RelayoutTiles(), que
        // reaplicaria o tamanho de grade justamente neste tile e ele não preencheria a tela.
        _fullscreenTile = tile;

        // Tela cheia de verdade (cobre a barra de tarefas).
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal; // garante a transição quando já está maximizada
        WindowState = WindowState.Maximized;
        tile.FullscreenButton.Content = "Sair (Esc)";
        StatusText.Text = $"Tela cheia: {tile.Nickname}. Pressione Esc ou dê duplo clique pra sair.";
    }

    private void ExitFullscreen()
    {
        var tile = _fullscreenTile;
        if (tile is null)
        {
            return;
        }

        _fullscreenTile = null;

        FullscreenHost.Children.Remove(tile.Root);
        FullscreenHost.Visibility = Visibility.Collapsed;

        tile.Root.Margin = new Thickness(6);
        tile.Root.CornerRadius = new CornerRadius(10);
        tile.Root.BorderThickness = new Thickness(1);
        tile.FullscreenButton.Content = "Tela cheia";

        // Só volta pra grade se o participante ainda estiver na sala.
        if (_tiles.ContainsKey(tile.Key))
        {
            TilesGrid.Children.Add(tile.Root);
        }

        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        WindowState = _previousWindowState;

        RelayoutTiles();
        StatusText.Text = $"Saiu da tela cheia de {tile.Nickname}.";
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _fullscreenTile is not null)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Calcula um tamanho "médio" pros tiles em vez de deixá-los esticar. Antes, com uma pessoa
    /// só compartilhando, o tile ocupava a área inteira e a imagem ficava desproporcional, com
    /// tarjas pretas enormes. Agora os tiles mantêm 16:9, cabem todos na tela e têm um teto.
    /// </summary>
    private void RelayoutTiles()
    {
        var count = TilesGrid.Children.Count;
        EmptyStateText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (count == 0)
        {
            return;
        }

        var availableWidth = TilesScroll.ViewportWidth > 10 ? TilesScroll.ViewportWidth : Math.Max(400, ActualWidth - 40);
        var availableHeight = TilesScroll.ViewportHeight > 10 ? TilesScroll.ViewportHeight : Math.Max(300, ActualHeight - 220);

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
        var rows = (int)Math.Ceiling((double)count / columns);

        const double tileMargin = 12; // 6 de cada lado
        var widthLimit = (availableWidth / columns) - tileMargin;
        var heightLimit = ((availableHeight / rows) - tileMargin) * 16.0 / 9.0;

        var tileWidth = Math.Clamp(Math.Min(widthLimit, heightLimit), 320, 760);
        var tileHeight = tileWidth * 9.0 / 16.0;

        foreach (var tile in _tiles.Values)
        {
            if (tile == _fullscreenTile)
            {
                continue; // em tela cheia o tile ocupa tudo
            }

            tile.Root.Width = tileWidth;
            tile.Root.Height = tileHeight;
        }
    }

    private void OnToggleMuteClick(StreamTile tile)
    {
        tile.Muted = !tile.Muted;
        _audioMixer.SetMuted(tile.ParticipantId, tile.Muted);
        SaveTileAudioPreference(tile);

        tile.UpdateMuteLabel();
        StatusText.Text = tile.Muted
            ? $"Áudio de {tile.Nickname} silenciado (a imagem continua)."
            : $"Áudio de {tile.Nickname} reativado.";
    }

    // ---------------------------------------------------------------- preferências salvas

    /// <summary>Devolve a sala do jeito que a pessoa deixou da última vez.</summary>
    private void RestorePreferences()
    {
        MasterVolumeSlider.Value = Math.Clamp(_settings.MasterVolume, 0, 100);
        _audioMixer.MasterVolume = MasterVolumeSlider.Value / 100.0;

        if (_settings.QualityIndex >= 0 && _settings.QualityIndex < QualityCombo.Items.Count)
        {
            QualityCombo.SelectedIndex = _settings.QualityIndex;
        }

        if (_settings.AudioModeIndex >= 0 && _settings.AudioModeIndex < AudioModeCombo.Items.Count)
        {
            AudioModeCombo.SelectedIndex = _settings.AudioModeIndex;
        }

        QualityCombo.SelectionChanged += (_, _) =>
        {
            _settings.QualityIndex = QualityCombo.SelectedIndex;
            _settings.Save();

            // Antes, trocar a qualidade no meio da transmissão não fazia nada até parar e começar
            // de novo — e o aviso de congestionamento manda justamente trocar pra 720p. Agora vale
            // na hora (o encoder reinicia, dá um engasgo de ~1 s e volta).
            if (_isSharing)
            {
                RestartStreamingWithCurrentQuality();
            }
        };

        UpdateShareAgainButton();
    }

    /// <summary>
    /// Mostra "Compartilhar de novo" quando dá pra saber exatamente qual monitor foi o último.
    /// Só vale pra monitor: janela não tem identificador estável entre execuções do app, e
    /// compartilhar a janela errada seria pior que abrir o seletor.
    /// </summary>
    private void UpdateShareAgainButton()
    {
        var saved = _settings.LastMonitorDeviceName;

        var stillConnected = !string.IsNullOrEmpty(saved)
                             && MonitorEnumerator.GetMonitors().Any(m => m.DeviceName == saved);

        ShareAgainButton.Visibility = stillConnected && !_isSharing ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- volume

    /// <summary>Abre o controle de volume daquela tela (botão direito em cima dela).</summary>
    private void OpenVolumePopup(StreamTile tile)
    {
        _volumePopupTile = tile;

        VolumePopupTitle.Text = $"Volume de {tile.Nickname}";
        VolumeSlider.Value = tile.Volume * 100;
        VolumeValueText.Text = $"{tile.Volume * 100:0}%";
        VolumePopupMuteButton.Content = tile.Muted ? "Ativar som" : "Silenciar";
        VolumePopup.IsOpen = true;
    }

    private void OnTileVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumePopupTile is null || VolumeValueText is null)
        {
            return;
        }

        var tile = _volumePopupTile;
        tile.Volume = e.NewValue / 100.0;
        VolumeValueText.Text = $"{e.NewValue:0}%";

        // Mexer no volume tira do mudo: é o que a pessoa quer dizer ao arrastar pra cima.
        if (tile.Muted && e.NewValue > 0)
        {
            tile.Muted = false;
            tile.UpdateMuteLabel();
            _audioMixer.SetMuted(tile.ParticipantId, false);
            VolumePopupMuteButton.Content = "Silenciar";
        }

        _audioMixer.SetVolume(tile.ParticipantId, tile.Volume);
        SaveTileAudioPreference(tile);
    }

    private void OnVolumePopupMuteClick(object sender, RoutedEventArgs e)
    {
        if (_volumePopupTile is null)
        {
            return;
        }

        OnToggleMuteClick(_volumePopupTile);
        VolumePopupMuteButton.Content = _volumePopupTile.Muted ? "Ativar som" : "Silenciar";
    }

    private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MasterVolumeText is null)
        {
            return; // ainda carregando o XAML
        }

        MasterVolumeText.Text = $"{e.NewValue:0}%";
        _audioMixer.MasterVolume = e.NewValue / 100.0;

        _settings.MasterVolume = (int)e.NewValue;
        _settings.Save();
    }

    /// <summary>
    /// Guarda volume e mudo pelo NICKNAME, não pelo id do participante: o id muda toda vez que a
    /// pessoa reconecta, então guardar por id perderia a preferência no primeiro reconnect.
    /// </summary>
    private void SaveTileAudioPreference(StreamTile tile)
    {
        if (tile.IsLocal || tile.Kind == StreamKind.Camera)
        {
            return;
        }

        _settings.Volumes[tile.Nickname] = (int)Math.Round(tile.Volume * 100);
        _settings.MutedNicknames.Remove(tile.Nickname);

        if (tile.Muted)
        {
            _settings.MutedNicknames.Add(tile.Nickname);
        }

        _settings.Save();
    }

    private void ApplySavedAudioPreference(StreamTile tile)
    {
        if (tile.IsLocal || tile.Kind == StreamKind.Camera)
        {
            return;
        }

        if (_settings.Volumes.TryGetValue(tile.Nickname, out var saved))
        {
            tile.Volume = Math.Clamp(saved, 0, 200) / 100.0;
        }

        tile.Muted = _settings.MutedNicknames.Contains(tile.Nickname);
        tile.UpdateMuteLabel();
    }

    /// <summary>Lista os participantes com um pontinho verde em quem está compartilhando.</summary>
    private void UpdateParticipantsPanel()
    {
        ParticipantsPanel.Items.Clear();

        foreach (var participant in _client.Participants)
        {
            var isSharing = _tiles.ContainsKey(TileKey(participant.Id, StreamKind.Screen))
                            || (participant.Id == _client.ParticipantId && _isSharing);
            var isMe = participant.Id == _client.ParticipantId;

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = isSharing
                    ? (Brush)FindResource("SuccessBrush")
                    : (Brush)FindResource("TextMutedBrush"),
            };

            var name = new TextBlock
            {
                Text = isMe ? $"{participant.Nickname} (você)" : participant.Nickname,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(isSharing ? "TextPrimaryBrush" : "TextSecondaryBrush"),
            };

            ParticipantsPanel.Items.Add(new Border
            {
                Background = (Brush)FindResource("Surface2Brush"),
                BorderBrush = (Brush)FindResource("BorderBrush2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 4),
                ToolTip = isSharing ? "Compartilhando a tela" : "Na sala",
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, name } },
            });
        }
    }

    private void OnLeaveRoomClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeavingWhileSharing())
        {
            return;
        }

        if (_ownedServer is not null && _client.Participants.Count > 1)
        {
            var answer = MessageBox.Show(
                "Você é o anfitrião: se sair, a sala fecha para todo mundo. Sair mesmo assim?",
                "Sair da sala",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _leaving = true; // já confirmei aqui; o Closing não precisa perguntar de novo

        // Abre o menu ANTES de fechar esta janela: se fechasse primeiro, o WPF encerraria o app
        // por não haver mais nenhuma janela aberta.
        new MainWindow().Show();
        Close();
    }

    private void PushFrameToTile(StreamTile tile, byte[] data, int width, int height)
    {
        lock (tile.Sync)
        {
            if (!tile.Watching)
            {
                return;
            }

            if (tile.PendingFrame is null || tile.PendingFrame.Length != data.Length)
            {
                tile.PendingFrame = new byte[data.Length];
            }

            Buffer.BlockCopy(data, 0, tile.PendingFrame, 0, data.Length);
            tile.Width = width;
            tile.Height = height;

            if (tile.RenderPending)
            {
                return;
            }

            tile.RenderPending = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => RenderTile(tile));
    }

    private void RenderTile(StreamTile tile)
    {
        lock (tile.Sync)
        {
            tile.RenderPending = false;

            if (tile.PendingFrame is null || !tile.Watching)
            {
                return;
            }

            if (tile.Bitmap is null || tile.Bitmap.PixelWidth != tile.Width || tile.Bitmap.PixelHeight != tile.Height)
            {
                tile.Bitmap = new WriteableBitmap(tile.Width, tile.Height, 96, 96, PixelFormats.Bgra32, null);
                tile.Image.Source = tile.Bitmap;
            }

            tile.Bitmap.WritePixels(
                new Int32Rect(0, 0, tile.Width, tile.Height),
                tile.PendingFrame,
                tile.Width * 4,
                0);

            tile.FramesRendered++;
        }

        if (tile.Placeholder.Visibility == Visibility.Visible)
        {
            tile.Placeholder.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- status

    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (_ownedServer is not null)
        {
            // O anfitrião precisa VER isso: a sala inteira sai pela internet dele. É o motivo mais
            // comum de "o dono da sala perde fps" — o upload dele está no limite e ninguém sabe.
            var mbps = _ownedServer.TakeBytesRelayed() * 8 / 1_000_000.0;
            RelayUploadText.Text = mbps < 0.05
                ? ""
                : $"Saída da sala pela sua internet: {mbps:0.0} Mbps";
        }

        foreach (var tile in _tiles.Values)
        {
            var frames = Interlocked.Exchange(ref tile.FramesRendered, 0);
            var bytes = Interlocked.Exchange(ref tile.BytesReceived, 0);

            if (tile.IsLocal)
            {
                tile.FpsText.Text = tile.Watching ? $"{frames} fps (preview)" : "";
                continue;
            }

            // Mostrar os KB/s recebidos separa "não está chegando nada pela rede" de "está
            // chegando mas não decodifica" — sem isso, os dois casos parecem só "tela preta".
            var kbps = bytes / 1024.0;
            tile.FpsText.Text = tile.Watching
                ? $"{frames} fps · {kbps:0} KB/s"
                : "não assistindo";

            if (tile.Watching && frames == 0 && bytes > 0)
            {
                tile.Placeholder.Text = "Recebendo dados, mas não consegui decodificar ainda...";
                tile.Placeholder.Visibility = Visibility.Visible;
            }
            else if (tile.Watching && bytes == 0)
            {
                tile.Placeholder.Text = $"{tile.Nickname} não está enviando vídeo no momento.";
                tile.Placeholder.Visibility = Visibility.Visible;
            }
        }

        if (_isSharing && _encoder is not null)
        {
            var encoded = _encoder.FramesEncoded;
            var delta = encoded - _lastFramesEncoded;
            _lastFramesEncoded = encoded;

            var warning = !_encoder.HasProducedOutput && encoded > 30
                ? " · ⚠ o encoder não está produzindo vídeo"
                : string.Empty;

            ConnectionInfoText.Text = $"Enviando {delta} fps{warning}";

            _blackFrameSeconds = _lastCapturedFrameWasBlack ? _blackFrameSeconds + 1 : 0;
            if (_blackFrameSeconds == 3)
            {
                StatusText.Text = "⚠ A tela capturada está totalmente preta. Se você está num jogo, "
                                + "troque de \"tela cheia exclusiva\" para \"tela cheia sem bordas\" — "
                                + "o Windows não deixa capturar jogos em tela cheia exclusiva.";
            }
        }

        var peak = _audioCapture.PeakLevel;
        AudioLevelBar.Width = Math.Clamp(peak, 0, 1) * AudioLevelTrack.ActualWidth;
    }

    private void UpdateParticipantsLabel()
    {
        var count = _client.Participants.Count;
        var sharing = _tiles.Values.Count(t => !t.IsLocal && t.Kind == StreamKind.Screen) + (_isSharing ? 1 : 0);

        ParticipantsText.Text = sharing == 0
            ? $"PARTICIPANTES ({count})"
            : $"PARTICIPANTES ({count}) · {sharing} COMPARTILHANDO";

        UpdateParticipantsPanel();
    }

    private void OnCopyRoomInfoClick(object sender, RoutedEventArgs e)
    {
        var invite = "Entre na minha sala do MGRScreen!" + Environment.NewLine
                     + "Abra o app: a sala deve aparecer sozinha na lista. Se não aparecer, "
                     + "use o endereço da rede que você usa pra falar comigo:" + Environment.NewLine;

        if (_ownedServer is not null)
        {
            foreach (var (address, network) in LocalNetworks.AllInviteAddresses())
            {
                invite += $"  {network}: {address}" + Environment.NewLine;
            }
        }
        else
        {
            invite += $"  {_hostAddress}" + Environment.NewLine;
        }

        Clipboard.SetText(invite);
        StatusText.Text = "Convite copiado para a área de transferência.";
    }


    private void Shutdown()
    {
        _shuttingDown = true;
        _statsTimer.Stop();
        StopSharing();

        foreach (var tile in _tiles.Values)
        {
            tile.Dispose();
        }
        _tiles.Clear();

        StopCamera();
        _camera.Dispose();
        _reconnectCts?.Cancel();
        _client.Disconnect();
        _capture.Dispose();
        _audioCapture.Dispose();
        _audioMixer.Dispose();
        _ownedServer?.Stop();
    }

    /// <summary>Um "quadradinho" da grade: a imagem, o decoder daquele participante e o áudio dele.</summary>
    private sealed class StreamTile : IDisposable
    {
        /// <summary>Chave no dicionário de tiles: (participante, tela/câmera). Ver TileKey.</summary>
        public int Key { get; init; }
        public StreamKind Kind { get; init; }

        /// <summary>Id real do participante, ou negativo pros previews locais.</summary>
        public int ParticipantId { get; init; }

        public bool IsLocal => ParticipantId < 0;
        public string Nickname { get; init; } = "";
        public required Border Root { get; init; }
        public required Image Image { get; init; }
        public required TextBlock Placeholder { get; init; }
        public required TextBlock FpsText { get; init; }
        public required Button ToggleButton { get; init; }
        public required Button MuteButton { get; init; }
        public required Button FullscreenButton { get; init; }
        public bool Muted;

        /// <summary>1 = volume original. Vai até 2 (200%).</summary>
        public double Volume = 1.0;

        public StreamInfo? Info { get; init; }

        public FfmpegDecoder? Decoder;
        public WriteableBitmap? Bitmap;
        public byte[]? PendingFrame;
        public bool RenderPending;
        public bool Watching;
        public int Width;
        public int Height;
        public int FramesRendered;
        public long BytesReceived;
        public readonly object Sync = new();

        public void UpdateToggleLabel()
        {
            ToggleButton.Content = Watching
                ? (IsLocal ? "Ocultar preview" : "Parar de assistir")
                : (IsLocal ? "Mostrar preview" : "Assistir");
        }

        public void UpdateMuteLabel() => MuteButton.Content = Muted ? "Ativar som" : "Silenciar";

        /// <summary>Para o vídeo. O áudio é do mixer, quem tira de lá é a janela.</summary>
        public void StopPlayback()
        {
            var decoder = Decoder;
            Decoder = null;
            if (decoder is not null)
                _ = decoder.StopAsync();
        }

        public void Dispose() => StopPlayback();
    }
}
