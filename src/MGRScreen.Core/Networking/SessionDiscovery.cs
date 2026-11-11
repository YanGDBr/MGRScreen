using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MGRScreen.Core.Networking;

/// <summary>Uma sala encontrada na rede, pronta pra entrar.</summary>
public sealed class DiscoveredSession
{
    public string HostNickname { get; set; } = "";
    public string Address { get; set; } = "";
    public int ControlPort { get; set; }
    public int MediaPort { get; set; }
    public int ParticipantCount { get; set; }

    /// <summary>Por onde ela apareceu: "Rede local", "Tailscale", "Radmin VPN"...</summary>
    public string NetworkLabel { get; set; } = "";

    public string Key => $"{Address}:{ControlPort}";
}

/// <summary>O que o anfitrião responde quando alguém pergunta se tem sala ali.</summary>
internal sealed class SessionAnnouncement
{
    public string Magic { get; set; } = SessionDiscovery.ReplyMagic;
    public string HostNickname { get; set; } = "";
    public int ControlPort { get; set; }
    public int MediaPort { get; set; }
    public int ParticipantCount { get; set; }
}

internal static class SessionDiscovery
{
    public const int DiscoveryPort = 51902;
    public const string ProbeMagic = "MGRSCREEN-PROBE-1";
    public const string ReplyMagic = "MGRSCREEN-ROOM-1";
    public const string TailscaleMagic = "MGRSCREEN-ROOM-PUSH-1";

    public static async Task AnnounceToTailscaleAsync(string nickname, int controlPort, int mediaPort,
        Func<int> participantCount, CancellationToken token)
    {
        IReadOnlyList<IPAddress> peers = Array.Empty<IPAddress>();
        IPAddress? previousLocal = null;
        var nextRefresh = 0L;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = Environment.TickCount64;
                var local = LocalNetworks.Enumerate().FirstOrDefault(n =>
                    n.InterfaceName.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) &&
                    LocalNetworks.IsTailscaleRange(n.Address))?.Address;
                if (local is not null && (!local.Equals(previousLocal) || now >= nextRefresh))
                {
                    peers = await SessionBrowser.GetTailscalePeersAsync(token);
                    nextRefresh = now + 30_000;
                }
                previousLocal = local;

                if (local is not null && peers.Count > 0)
                {
                    var announcement = JsonSerializer.SerializeToUtf8Bytes(new SessionAnnouncement
                    {
                        Magic = TailscaleMagic,
                        HostNickname = nickname,
                        ControlPort = controlPort,
                        MediaPort = mediaPort,
                        ParticipantCount = participantCount(),
                    });
                    try
                    {
                        // Bind to the tunnel address so room announcements cannot fall back
                        // to an unrelated CGNAT network when the VPN disconnects.
                        using var udp = new UdpClient(new IPEndPoint(local, 0));
                        foreach (var peer in peers.Where(LocalNetworks.IsTailscaleRange).Distinct())
                        {
                            try { await udp.SendAsync(announcement, new IPEndPoint(peer, DiscoveryPort), token); }
                            catch (SocketException) { }
                        }
                    }
                    catch (SocketException) { }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

/// <summary>
/// Fica escutando na porta de descoberta e responde a quem pergunta se tem sala aqui. É
/// pergunta-e-resposta, não anúncio periódico: só gera tráfego quando alguém está de fato
/// procurando sala, e o anfitrião não fica falando sozinho na rede o tempo todo.
/// </summary>
public sealed class SessionBeacon : IDisposable
{
    private readonly string _hostNickname;
    private readonly int _controlPort;
    private readonly int _mediaPort;
    private readonly Func<int> _participantCount;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public SessionBeacon(string hostNickname, int controlPort, int mediaPort, Func<int> participantCount)
    {
        _hostNickname = hostNickname;
        _controlPort = controlPort;
        _mediaPort = mediaPort;
        _participantCount = participantCount;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // ReuseAddress pra não quebrar quando há duas instâncias do app na mesma máquina
        // (acontece o tempo todo em teste) — a segunda simplesmente não responde.
        _udp = new UdpClient { ExclusiveAddressUse = false };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, SessionDiscovery.DiscoveryPort));

        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var received = await _udp!.ReceiveAsync(token);
                var text = System.Text.Encoding.UTF8.GetString(received.Buffer);

                if (!text.StartsWith(SessionDiscovery.ProbeMagic, StringComparison.Ordinal))
                {
                    continue;
                }

                var reply = JsonSerializer.SerializeToUtf8Bytes(new SessionAnnouncement
                {
                    HostNickname = _hostNickname,
                    ControlPort = _controlPort,
                    MediaPort = _mediaPort,
                    ParticipantCount = _participantCount(),
                });

                await _udp.SendAsync(reply, received.RemoteEndPoint, token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Procura salas. Manda a pergunta por broadcast em cada placa de rede (pega rede local, Radmin,
/// Hamachi, ZeroTier) e, além disso, pergunta direto pra cada máquina da rede Tailscale — o
/// Tailscale é roteamento puro e NÃO entrega broadcast, então lá a única forma é perguntar de um
/// em um. As duas respostas caem na mesma lista.
/// </summary>
public static class SessionBrowser
{
    /// <param name="knownAddresses">Endereços pra perguntar direto, além dos que a gente descobre
    /// sozinho — típico: o IP do amigo que você já digitou antes.</param>
    public static async Task<IReadOnlyList<DiscoveredSession>> ScanAsync(
        TimeSpan timeout,
        CancellationToken token,
        int controlPort = 51900,
        int mediaPort = 51901,
        IEnumerable<string>? knownAddresses = null)
    {
        var found = new Dictionary<string, DiscoveredSession>();

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        scanCts.CancelAfter(timeout);

        // Dois caminhos ao mesmo tempo, porque nenhum cobre tudo sozinho:
        //   UDP em broadcast  -> acha quem está na mesma LAN/Radmin/Hamachi sem saber o IP dele.
        //   TCP em cada peer  -> único jeito no Tailscale (não tem broadcast), e passa em firewall
        //                        que só liberou a porta da sala.
        var udpScan = ScanByBroadcastAsync(found, scanCts.Token);
        var tcpScan = ScanByDirectProbeAsync(found, controlPort, mediaPort, knownAddresses, scanCts.Token);

        await Task.WhenAll(udpScan, tcpScan);

        lock (found)
        {
            return found.Values.OrderBy(s => s.NetworkLabel).ThenBy(s => s.HostNickname).ToList();
        }
    }

    /// <summary>
    /// Relatório de por que a busca está (ou não está) achando sala. Serve pra separar as três
    /// causas que dão exatamente o mesmo sintoma: o amigo não está com sala aberta, a porta está
    /// bloqueada, ou o app dele é de uma versão antiga que nem responde à pergunta.
    /// </summary>
    public static async Task<string> DiagnoseAsync(
        int controlPort, int mediaPort, IEnumerable<string>? knownAddresses, CancellationToken token)
    {
        var report = new System.Text.StringBuilder();

        report.AppendLine("== Suas redes ==");
        foreach (var nic in LocalNetworks.Enumerate())
        {
            report.AppendLine($"  {nic.Label,-12} {nic.Address}/{nic.Mask}  ({nic.InterfaceName})");
        }

        report.AppendLine();
        report.AppendLine("== Broadcast (rede local, Radmin, Hamachi, ZeroTier) ==");
        var broadcasts = LocalNetworks.BroadcastAddresses();
        report.AppendLine(broadcasts.Count == 0
            ? "  nenhum endereço de broadcast — só a busca por TCP vai funcionar"
            : "  perguntando em: " + string.Join(", ", broadcasts));

        var byBroadcast = new Dictionary<string, DiscoveredSession>();
        using (var udpCts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            udpCts.CancelAfter(TimeSpan.FromSeconds(2));
            await ScanByBroadcastAsync(byBroadcast, udpCts.Token);
        }

        report.AppendLine($"  responderam: {byBroadcast.Count}");
        foreach (var session in byBroadcast.Values)
        {
            report.AppendLine($"    {session.HostNickname} em {session.Address} [{session.NetworkLabel}]");
        }

        report.AppendLine();
        report.AppendLine("== Tailscale ==");
        var exe = FindTailscaleExecutable();
        if (exe is null)
        {
            report.AppendLine("  tailscale.exe não encontrado — salas via Tailscale não vão aparecer sozinhas");
        }
        else
        {
            var peers = await GetTailscalePeersAsync(token);
            report.AppendLine($"  {exe}");
            report.AppendLine(peers.Count == 0
                ? "  nenhuma máquina online na sua rede Tailscale"
                : "  máquinas online: " + string.Join(", ", peers.Select(p => p.ToString())));
        }

        report.AppendLine();
        report.AppendLine($"== Pergunta direta na porta {controlPort} (TCP) ==");

        var targets = new List<string>();
        foreach (var peer in await GetTailscalePeersAsync(token))
        {
            targets.Add(peer.ToString());
        }
        if (knownAddresses is not null)
        {
            targets.AddRange(knownAddresses.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        var mine = LocalNetworks.Enumerate().Select(n => n.Address.ToString()).ToHashSet();
        targets = targets.Distinct().Where(a => !mine.Contains(a)).ToList();

        if (targets.Count == 0)
        {
            report.AppendLine("  nenhum endereço pra perguntar (sem Tailscale e sem IP usado antes)");
        }

        foreach (var address in targets)
        {
            report.AppendLine($"  {address}: {await DescribeProbeAsync(address, controlPort, token)}");
        }

        return report.ToString();
    }

    private static async Task<string> DescribeProbeAsync(string address, int controlPort, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(address, controlPort, timeout.Token);

            var stream = client.GetStream();
            await MessageFraming.WriteMessageAsync(
                stream, new RoomEnvelope { Kind = RoomMessageKind.Discover }.ToJsonBytes(), timeout.Token);

            var bytes = await MessageFraming.ReadMessageAsync(stream, timeout.Token);
            if (bytes is null)
            {
                return "conectou, mas fechou sem responder (app antigo? atualize os dois lados)";
            }

            var reply = RoomEnvelope.FromJsonBytes(bytes);
            return reply.Kind == RoomMessageKind.DiscoverReply
                ? $"SALA DE {reply.Nickname} ({reply.ParticipantCount} na sala)"
                : $"respondeu {reply.Kind} — versão diferente do app dos dois lados";
        }
        catch (OperationCanceledException)
        {
            return "sem resposta (porta bloqueada por firewall, ou máquina fora do ar)";
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return "recusou a conexão — a máquina existe, mas não tem sala aberta";
        }
        catch (Exception ex)
        {
            return $"falhou: {ex.Message}";
        }
    }

    private static async Task ScanByBroadcastAsync(
        Dictionary<string, DiscoveredSession> found, CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var collecting = CollectRepliesAsync(udp, found, token);
            var probe = System.Text.Encoding.UTF8.GetBytes(SessionDiscovery.ProbeMagic);

            foreach (var target in LocalNetworks.BroadcastAddresses())
            {
                try
                {
                    await udp.SendAsync(probe, new IPEndPoint(target, SessionDiscovery.DiscoveryPort), token);
                }
                catch (SocketException)
                {
                    // Placa que não aceita broadcast, rota que não existe: segue pras outras.
                }
            }

            await collecting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
        }
    }

    /// <summary>
    /// Pergunta de um em um, por TCP, na porta de controle da sala. É o mesmo caminho que a
    /// entrada manual usa: se digitar o IP funciona, isto também funciona.
    /// </summary>
    private static async Task ScanByDirectProbeAsync(
        Dictionary<string, DiscoveredSession> found,
        int controlPort,
        int mediaPort,
        IEnumerable<string>? knownAddresses,
        CancellationToken token)
    {
        var targets = new List<string>();

        foreach (var peer in await GetTailscalePeersAsync(token))
        {
            targets.Add(peer.ToString());
        }

        if (knownAddresses is not null)
        {
            targets.AddRange(knownAddresses.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        var mine = LocalNetworks.Enumerate().Select(n => n.Address.ToString()).ToHashSet();

        var probes = targets
            .Distinct()
            .Where(a => !mine.Contains(a))
            .Select(address => ProbeAddressAsync(address, controlPort, mediaPort, found, token));

        await Task.WhenAll(probes);
    }

    private static async Task ProbeAddressAsync(
        string address,
        int controlPort,
        int mediaPort,
        Dictionary<string, DiscoveredSession> found,
        CancellationToken token)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(address, controlPort, token);

            var stream = client.GetStream();
            await MessageFraming.WriteMessageAsync(
                stream, new RoomEnvelope { Kind = RoomMessageKind.Discover }.ToJsonBytes(), token);

            var bytes = await MessageFraming.ReadMessageAsync(stream, token);
            if (bytes is null)
            {
                return;
            }

            var reply = RoomEnvelope.FromJsonBytes(bytes);

            // Uma versão mais antiga do app não conhece a pergunta e responde recusando. Mesmo
            // assim tem uma sala ali, e dá pra entrar — então mostra na lista, avisando.
            var isOlderVersion = reply.Kind == RoomMessageKind.JoinRejected;

            if (reply.Kind != RoomMessageKind.DiscoverReply && !isOlderVersion)
            {
                return;
            }

            var session = new DiscoveredSession
            {
                HostNickname = isOlderVersion
                    ? "Sala (app desatualizado)"
                    : string.IsNullOrWhiteSpace(reply.Nickname) ? "Sala sem nome" : reply.Nickname,
                Address = address,
                ControlPort = controlPort,
                MediaPort = mediaPort,
                ParticipantCount = reply.ParticipantCount,
                NetworkLabel = IPAddress.TryParse(address, out var parsed)
                    ? LocalNetworks.LabelFor(parsed)
                    : "Internet",
            };

            lock (found)
            {
                found[session.Key] = session;
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException
                                   or IOException or InvalidDataException or ObjectDisposedException)
        {
            // Sem sala nesse endereço, ou fora do ar: normal, nem todo peer tem o app aberto.
        }
    }

    private static async Task CollectRepliesAsync(
        UdpClient udp, Dictionary<string, DiscoveredSession> found, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(token);

                SessionAnnouncement? announcement;
                try
                {
                    announcement = JsonSerializer.Deserialize<SessionAnnouncement>(received.Buffer);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (announcement?.Magic != SessionDiscovery.ReplyMagic)
                {
                    continue;
                }

                var address = received.RemoteEndPoint.Address;
                var session = new DiscoveredSession
                {
                    HostNickname = string.IsNullOrWhiteSpace(announcement.HostNickname)
                        ? "Sala sem nome"
                        : announcement.HostNickname,
                    Address = address.ToString(),
                    ControlPort = announcement.ControlPort,
                    MediaPort = announcement.MediaPort,
                    ParticipantCount = announcement.ParticipantCount,
                    NetworkLabel = LocalNetworks.LabelFor(address),
                };

                lock (found)
                {
                    found[session.Key] = session;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Os IPs 100.x das outras máquinas da sua rede Tailscale, lidos do próprio Tailscale. Se ele
    /// não estiver instalado ou logado, a lista volta vazia e o resto da busca continua igual.
    /// </summary>
    internal static async Task<IReadOnlyList<IPAddress>> GetTailscalePeersAsync(CancellationToken token)
    {
        var exe = FindTailscaleExecutable();
        if (exe is null)
        {
            return Array.Empty<IPAddress>();
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "status --json")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return Array.Empty<IPAddress>();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            var json = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            return ParseTailscalePeers(json);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            return Array.Empty<IPAddress>();
        }
    }

    public static IReadOnlyList<IPAddress> ParseTailscalePeers(string json)
    {
        var peers = new List<IPAddress>();

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Peer", out var peerMap)
                || peerMap.ValueKind != JsonValueKind.Object)
            {
                return peers;
            }

            foreach (var peer in peerMap.EnumerateObject())
            {
                // Máquina offline no Tailscale não vai responder — não gasta pergunta com ela.
                if (peer.Value.TryGetProperty("Online", out var online)
                    && online.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                if (!peer.Value.TryGetProperty("TailscaleIPs", out var ips)
                    || ips.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var ip in ips.EnumerateArray())
                {
                    if (IPAddress.TryParse(ip.GetString(), out var parsed)
                        && parsed.AddressFamily == AddressFamily.InterNetwork)
                    {
                        peers.Add(parsed);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return peers;
    }

    private static string? FindTailscaleExecutable()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Tailscale\tailscale.exe",
            @"C:\Program Files (x86)\Tailscale\tailscale.exe",
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
