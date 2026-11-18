using System.Windows;
using System.Windows.Controls;
using MGRScreen.App.Views;
using MGRScreen.Core.Networking;

namespace MGRScreen.App;

public partial class MainWindow : Window
{
    /// <summary>Portas da sala. Fixas em 51900/51901 pra todo mundo se encontrar; a variável de
    /// ambiente MGRSCREEN_PORT_BASE só existe pra rodar testes ao lado de uma sala já aberta.</summary>
    public static readonly int ControlPort = PortBase();
    public static readonly int MediaPort = ControlPort + 1;

    private static int PortBase() =>
        int.TryParse(Environment.GetEnvironmentVariable("MGRSCREEN_PORT_BASE"), out var port) && port is > 1024 and < 65000
            ? port
            : 51900;

    private readonly AppSettings _settings = AppSettings.Current;
    private readonly CancellationTokenSource _scanCts = new();

    /// <summary>Assinatura do que está desenhado na lista. Só redesenho quando muda de verdade —
    /// senão a lista pisca a cada busca, mesmo estando tudo igual.</summary>
    private string _renderedSignature = "";

    private bool _scanning;

    public MainWindow()
    {
        InitializeComponent();

        NicknameBox.Text = string.IsNullOrWhiteSpace(_settings.Nickname) ? "Amigo" : _settings.Nickname;
        HostAddressBox.Text = _settings.LastManualAddress;

        // Atalho pra sala de ontem: na prática é sempre a mesma turma.
        if (!string.IsNullOrWhiteSpace(_settings.LastRoomAddress))
        {
            LastRoomButton.Visibility = Visibility.Visible;
            LastRoomText.Text = $"Voltar pra {_settings.LastRoomHost}";
            LastRoomAddressText.Text = _settings.LastRoomAddress;
        }

        Loaded += async (_, _) =>
        {
            if (OnOpened())
            {
                return; // já saiu daqui direto pra uma sala (link de convite)
            }

            await ScanLoopAsync(_scanCts.Token);
        };
    }

    /// <summary>
    /// Fica procurando sala enquanto o menu está aberto. Repete sozinho porque o amigo pode criar
    /// a sala depois de você já ter aberto o app — sem isso, todo mundo ia ficar clicando em
    /// "procurar de novo".
    /// </summary>
    private async Task ScanLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await ScanOnceAsync(token);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ScanOnceAsync(CancellationToken token)
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;

        try
        {
            // Os endereços que a gente já conhece entram na busca: se você já digitou o IP do seu
            // amigo alguma vez, a sala dele passa a aparecer sozinha nas próximas.
            var known = new[] { _settings.LastRoomAddress, _settings.LastManualAddress };

            var sessions = await SessionBrowser.ScanAsync(
                TimeSpan.FromSeconds(2.5), token, ControlPort, MediaPort, known);

            if (token.IsCancellationRequested)
            {
                return;
            }

            ShowSessions(sessions);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _scanning = false;
        }
    }

    private void ShowSessions(IReadOnlyList<DiscoveredSession> sessions)
    {
        var signature = string.Join("|", sessions.Select(s => $"{s.Key}:{s.HostNickname}:{s.ParticipantCount}"));
        if (signature != _renderedSignature)
        {
            SessionsList.ItemsSource = sessions;
            _renderedSignature = signature;
        }

        NoSessionsText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ScanStatusText.Text = sessions.Count switch
        {
            0 => "Nenhuma sala encontrada.",
            1 => "1 sala encontrada.",
            _ => $"{sessions.Count} salas encontradas.",
        };
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ScanStatusText.Text = "Procurando...";
        await ScanOnceAsync(_scanCts.Token);
    }

    /// <summary>Mostra o que a busca está enxergando, pra não virar advinhação quando nada aparece.</summary>
    private async void OnDiagnoseClick(object sender, RoutedEventArgs e)
    {
        DiagnoseButton.IsEnabled = false;
        ScanStatusText.Text = "Checando...";

        try
        {
            var known = new[] { _settings.LastRoomAddress, _settings.LastManualAddress };
            var report = await SessionBrowser.DiagnoseAsync(ControlPort, MediaPort, known, _scanCts.Token);

            MessageBox.Show(
                report + Environment.NewLine
                       + "Dica: os dois lados precisam estar na mesma versão do MGRScreen, e a sala "
                       + "precisa estar aberta na máquina do seu amigo." + Environment.NewLine
                       + "(Ctrl+C copia este texto.)",
                "Por que a sala não aparece",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            DiagnoseButton.IsEnabled = true;
        }
    }

    private void OnCreateRoomClick(object sender, RoutedEventArgs e)
    {
        var nickname = GetNickname();
        var password = RoomPasswordBox.Password.Length > 0 ? RoomPasswordBox.Password : null;

        RoomServer server;
        try
        {
            server = new RoomServer(nickname, ControlPort, MediaPort, password);
            server.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não consegui abrir as portas {ControlPort}/{MediaPort}: {ex.Message}";
            return;
        }

        // Quem cria a sala entra nela como um participante comum, conectando no próprio servidor.
        OpenRoom(new RoomWindow(nickname, "127.0.0.1", server, password: password));
    }

    private void OnJoinDiscoveredClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not DiscoveredSession session)
        {
            return;
        }

        OpenRoom(new RoomWindow(GetNickname(), session.Address, ownedServer: null,
            roomLabel: $"sala de {session.HostNickname}"));
    }

    private void OnLastRoomClick(object sender, RoutedEventArgs e)
    {
        OpenRoom(new RoomWindow(GetNickname(), _settings.LastRoomAddress, ownedServer: null,
            roomLabel: _settings.LastRoomHost));
    }

    private void OnJoinByAddressClick(object sender, RoutedEventArgs e)
    {
        // Colaram um link de convite em vez do IP: vale o endereço de dentro dele.
        var address = HostAddressBox.Text.Trim();
        address = InviteLink.TryParse(address)?.Address ?? address;
        if (string.IsNullOrWhiteSpace(address))
        {
            StatusText.Text = "Digite o IP do anfitrião.";
            return;
        }

        _settings.LastManualAddress = address;
        OpenRoom(new RoomWindow(GetNickname(), address, ownedServer: null, roomLabel: address));
    }

    private string GetNickname()
    {
        var nickname = NicknameBox.Text.Trim();
        nickname = string.IsNullOrWhiteSpace(nickname) ? "Amigo" : nickname;

        _settings.Nickname = nickname;
        _settings.Save();

        return nickname;
    }

    private void OpenRoom(RoomWindow room)
    {
        _scanCts.Cancel();
        room.Show();
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e) => _scanCts.Cancel();
}
