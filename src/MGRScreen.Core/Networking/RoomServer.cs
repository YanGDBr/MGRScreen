using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace MGRScreen.Core.Networking;

/// <summary>
/// O "dono da sala" roda isto. Diferente da versão anterior (1 Host transmitindo pra N Viewers),
/// aqui QUALQUER participante pode compartilhar a tela: o servidor é um repassador burro que
/// recebe os bytes de quem está publicando e manda pra todos os outros, carimbando o id de quem
/// originou pra cada um saber de quem é aquele stream.
///
/// Quem cria a sala também entra nela como participante comum (via <see cref="RoomClient"/>
/// apontando pra 127.0.0.1), então não existe caminho de código especial pro "dono".
/// </summary>
public sealed class RoomServer : IDisposable
{
    public event Action<string>? Log;

    private readonly string _hostNickname;
    private readonly int _controlPort;
    private readonly int _mediaPort;

    private SessionBeacon? _beacon;
    private TcpListener? _controlListener;
    private TcpListener? _mediaListener;
    private CancellationTokenSource? _cts;

    private readonly ConcurrentDictionary<string, int> _pendingMediaTokens = new(); // token -> participantId
    private readonly ConcurrentDictionary<int, Participant> _participants = new();
    private int _nextParticipantId;

    public RoomServer(string hostNickname, int controlPort, int mediaPort)
    {
        _hostNickname = hostNickname;
        _controlPort = controlPort;
        _mediaPort = mediaPort;
    }

    public int ParticipantCount => _participants.Count;

    private long _bytesRelayed;

    /// <summary>
    /// Quantos bytes o relay mandou pra fora desde a última consulta. É o UPLOAD do anfitrião:
    /// cada stream sai daqui uma vez pra cada outro participante. Com 3 amigos assistindo uma
    /// transmissão de 8 Mbps, são 24 Mbps saindo da casa do anfitrião — e se os amigos também
    /// compartilham, os streams deles também passam por aqui e multiplicam.
    /// </summary>
    public long TakeBytesRelayed() => Interlocked.Exchange(ref _bytesRelayed, 0);

    public void Start(bool announceOnTailscale = true)
    {
        _cts = new CancellationTokenSource();

        _controlListener = new TcpListener(IPAddress.Any, _controlPort);
        _controlListener.Start();
        _mediaListener = new TcpListener(IPAddress.Any, _mediaPort);
        _mediaListener.Start();

        _ = Task.Run(() => AcceptLoopAsync(_controlListener, HandleControlClientAsync, _cts.Token));
        _ = Task.Run(() => AcceptLoopAsync(_mediaListener, HandleMediaClientAsync, _cts.Token));
        _ = Task.Run(() => MonitorCongestionLoopAsync(_cts.Token));
        if (announceOnTailscale)
        {
            var token = _cts.Token;
            _ = Task.Run(() => SessionDiscovery.AnnounceToTailscaleAsync(
                _hostNickname, _controlPort, _mediaPort, () => ParticipantCount, token));
        }

        // Responde a quem estiver procurando sala na rede, pra ninguém precisar digitar IP.
        //
        // Opcional DE PROPÓSITO: o Windows reserva faixas de porta pra Hyper-V/WSL/Docker, e elas
        // mudam a cada reinicialização — já aconteceu da 51902 cair dentro de uma (51808–51907) e o
        // bind falhar com "acesso proibido". Antes essa exceção subia daqui e a sala inteira não
        // abria, com uma mensagem culpando as portas 51900/51901. Sem o beacon, a sala só deixa de
        // aparecer na busca por broadcast; a busca por TCP (Discover no canal de controle) continua
        // achando ela, e entrar pelo IP funciona igual.
        try
        {
            _beacon = new SessionBeacon(_hostNickname, _controlPort, _mediaPort, () => ParticipantCount);
            _beacon.Start();
        }
        catch (SocketException ex)
        {
            _beacon?.Dispose();
            _beacon = null;
            Log?.Invoke($"Busca automática por broadcast indisponível nesta máquina ({ex.SocketErrorCode}). "
                        + "A sala funciona normalmente e ainda aparece pela busca direta.");
        }
    }

    private const int CongestionWindowSeconds = 4;

    /// <summary>Quantos frames perdidos numa janela já contam como "não está acompanhando".
    /// A 60 fps a janela tem ~240 frames, então 10 é ~4% de perda.</summary>
    private const int DroppedFramesPerWindowThreshold = 10;

    /// <summary>
    /// Avisa quem está transmitindo quando algum espectador não está dando conta da banda. Sem
    /// isso, o sintoma pro espectador é imagem congelando de vez em quando e ninguém entende por
    /// quê — agora quem transmite vê o aviso e pode baixar a qualidade.
    /// </summary>
    private async Task MonitorCongestionLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CongestionWindowSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                foreach (var participant in _participants.Values)
                {
                    // Um engasgo isolado (uma janela ruim) é normal e não merece aviso; o que
                    // incomoda de verdade é a perda que se repete. Por isso só avisamos depois
                    // de duas janelas seguidas ruins, e no máximo uma vez a cada 30 s.
                    if (participant.TakeDropCount() < DroppedFramesPerWindowThreshold)
                    {
                        participant.CongestedWindows = 0;
                        continue;
                    }

                    participant.CongestedWindows++;

                    if (participant.CongestedWindows < 2 ||
                        Environment.TickCount64 - participant.LastCongestionWarningAt < 30_000)
                    {
                        continue;
                    }

                    participant.LastCongestionWarningAt = Environment.TickCount64;

                    Log?.Invoke($"{participant.Nickname} não está acompanhando a banda.");

                    var warning = new RoomEnvelope
                    {
                        Kind = RoomMessageKind.ViewerCongested,
                        ParticipantId = participant.Id,
                        Nickname = participant.Nickname,
                    };

                    // Vai pra quem publica (pra baixar a qualidade) E pro próprio espectador
                    // afetado — sem isso ele vê a imagem congelar sem saber que o problema é a
                    // conexão dele, e reclama que "o app está travando".
                    var bytes = warning.ToJsonBytes();

                    foreach (var other in _participants.Values.Where(p => p.IsPublishing || p.Id == participant.Id))
                    {
                        _ = SafeSendControlAsync(other, bytes);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener, Func<TcpClient, CancellationToken, Task> handler, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => handler(client, token));
        }
    }

    private async Task HandleControlClientAsync(TcpClient client, CancellationToken token)
    {
        Participant? participant = null;

        try
        {
            var stream = client.GetStream();

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(8));
            var joinBytes = await MessageFraming.ReadMessageAsync(stream, handshake.Token);
            if (joinBytes is null)
            {
                client.Dispose();
                return;
            }

            var join = RoomEnvelope.FromJsonBytes(joinBytes);

            // Descoberta pelo canal de controle: responde quem é e vai embora, sem virar
            // participante. É o mesmo caminho TCP que a entrada normal usa — de propósito: se dá
            // pra entrar digitando o IP, então dá pra descobrir, sem depender de UDP passar.
            if (join.Kind == RoomMessageKind.Discover)
            {
                var reply = new RoomEnvelope
                {
                    Kind = RoomMessageKind.DiscoverReply,
                    Nickname = _hostNickname,
                    ParticipantCount = ParticipantCount,
                    MediaPort = _mediaPort,
                };

                await MessageFraming.WriteMessageAsync(stream, reply.ToJsonBytes(), token);
                client.Dispose();
                return;
            }

            if (join.Kind != RoomMessageKind.Join)
            {
                await MessageFraming.WriteMessageAsync(stream, RoomEnvelope.Rejected("Handshake inválido.").ToJsonBytes(), token);
                client.Dispose();
                return;
            }

            if (join.ViewerOnly && string.IsNullOrWhiteSpace(join.Nickname))
            {
                await MessageFraming.WriteMessageAsync(stream,
                    RoomEnvelope.Rejected("Informe seu apelido para entrar na sala.").ToJsonBytes(), handshake.Token);
                return;
            }

            var id = Interlocked.Increment(ref _nextParticipantId);
            var nickname = string.IsNullOrWhiteSpace(join.Nickname) ? $"Amigo {id}" : join.Nickname!.Trim();
            var mediaToken = Guid.NewGuid().ToString("N");

            if (join.ViewerOnly && join.ProtocolVersion != 2)
            {
                await MessageFraming.WriteMessageAsync(stream, RoomEnvelope.Rejected("Versão do visualizador incompatível.").ToJsonBytes(), token);
                return;
            }

            participant = new Participant(id, nickname, client, stream, join.ProtocolVersion == 2, join.ViewerOnly,
                n => Interlocked.Add(ref _bytesRelayed, n));
            await participant.ControlWriteLock.WaitAsync(handshake.Token);
            _participants[id] = participant;
            _pendingMediaTokens[mediaToken] = id;

            try
            {
                var accepted = RoomEnvelope.Accepted(id, mediaToken, SnapshotParticipants(), SnapshotStreams());
                accepted.ProtocolVersion = participant.SupportsTimedMedia ? 2 : 0;
                await MessageFraming.WriteMessageAsync(stream, accepted.ToJsonBytes(), handshake.Token);
            }
            finally
            {
                participant.ControlWriteLock.Release();
            }

            Log?.Invoke($"{nickname} entrou na sala.");
            BroadcastControl(new RoomEnvelope
            {
                Kind = RoomMessageKind.ParticipantJoined,
                ParticipantId = id,
                Nickname = nickname,
            }, exceptParticipantId: id);

            await ReadControlMessagesAsync(participant, token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
        }
        finally
        {
            if (participant is not null)
            {
                foreach (var entry in _pendingMediaTokens)
                    if (entry.Value == participant.Id)
                        _pendingMediaTokens.TryRemove(entry.Key, out _);
                RemoveParticipant(participant);
            }
            client.Dispose();
        }
    }

    private async Task ReadControlMessagesAsync(Participant participant, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var bytes = await MessageFraming.ReadMessageAsync(participant.ControlStream, token);
            if (bytes is null)
            {
                break;
            }

            var message = RoomEnvelope.FromJsonBytes(bytes);

            switch (message.Kind)
            {
                case RoomMessageKind.Ping when participant.SupportsTimedMedia:
                    await SafeSendControlAsync(participant, new RoomEnvelope { Kind = RoomMessageKind.Pong }.ToJsonBytes());
                    break;

                case RoomMessageKind.Subscribe when participant.ViewerOnly && Enum.IsDefined(message.StreamKind):
                    participant.Subscribe(message.ParticipantId, message.StreamKind, message.IncludeAudio);
                    break;

                case RoomMessageKind.Unsubscribe when participant.ViewerOnly:
                    participant.Subscribe(null);
                    break;

                case RoomMessageKind.StartStream when !participant.ViewerOnly && message.Stream is not null:
                    var info = message.Stream;
                    if (!Enum.IsDefined(info.Kind) || info.Width is < 2 or > 8192 || info.Height is < 2 or > 4320 ||
                        info.Fps is < 1 or > 120 || (info.HasAudio &&
                        (info.AudioSampleRate is < 8_000 or > 192_000 || info.AudioChannels is < 1 or > 8)))
                        throw new InvalidDataException("Formato de transmissão inválido.");
                    info.ParticipantId = participant.Id;
                    info.Nickname = participant.Nickname;
                    participant.SetStream(info.Kind, info);

                    Log?.Invoke(info.Kind == StreamKind.Camera
                        ? $"{participant.Nickname} ligou a câmera ({info.Width}x{info.Height})."
                        : $"{participant.Nickname} começou a compartilhar ({info.Width}x{info.Height}@{info.Fps}).");
                    BroadcastControl(new RoomEnvelope
                    {
                        Kind = RoomMessageKind.StreamStarted,
                        ParticipantId = participant.Id,
                        Stream = info,
                    }, exceptParticipantId: participant.Id);
                    break;

                case RoomMessageKind.StopStream when !participant.ViewerOnly && Enum.IsDefined(message.StreamKind):
                    participant.SetStream(message.StreamKind, null);
                    Log?.Invoke(message.StreamKind == StreamKind.Camera
                        ? $"{participant.Nickname} desligou a câmera."
                        : $"{participant.Nickname} parou de compartilhar.");
                    BroadcastControl(new RoomEnvelope
                    {
                        Kind = RoomMessageKind.StreamStopped,
                        ParticipantId = participant.Id,
                        StreamKind = message.StreamKind,
                    }, exceptParticipantId: participant.Id);
                    break;
            }
        }
    }

    private async Task HandleMediaClientAsync(TcpClient client, CancellationToken token)
    {
        using var socket = client;
        var stream = client.GetStream();

        // Primeiro contato: 32 bytes ASCII com o token entregue no handshake de controle.
        var tokenBuffer = new byte[32];
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (!await ReadExactAsync(stream, tokenBuffer, 32, handshake.Token))
                return;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return; }

        var token32 = System.Text.Encoding.ASCII.GetString(tokenBuffer);
        if (!_pendingMediaTokens.TryRemove(token32, out var participantId)
            || !_participants.TryGetValue(participantId, out var participant))
        {
            client.Dispose();
            return;
        }

        // Buffer de envio pequeno DE PROPÓSITO. Sem isso o Windows cresce esse buffer sozinho até
        // vários MB: quando o link do espectador não dá conta, o atraso vai todo pra dentro do
        // kernel, a nossa fila nunca enche e a lógica de descarte nunca roda. Medido: o atraso
        // chegava a 4,6 s no loopback com só 2,5x de falta de banda — na internet, os 10 s que o
        // usuário relatou. Com 64 KB, o kernel segura ~65 ms a 8 Mbps e o resto vira pressão na
        // nossa fila, que descarta e ressincroniza no keyframe.
        client.NoDelay = true;
        client.SendBufferSize = 32 * 1024;

        if (!participant.AttachMedia(client, stream))
            return;

        // Mesmo socket nos dois sentidos: uma task escreve o que os outros publicam, e este
        // loop lê o que ESTE participante está publicando pra repassar aos demais.
        try
        {
            var header = new byte[MediaFraming.HeaderSize];

            while (!token.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, header.Length, token))
                {
                    break;
                }

                var length = MediaFraming.ReadLength(header);
                var timed = TimedMedia.IsTimed(header);
                var type = TimedMedia.Type(header);
                if (participant.ViewerOnly || !Enum.IsDefined(type) ||
                    length is <= 0 or > TimedMedia.MaxPayload + TimedMedia.PrefixSize ||
                    (timed && (!participant.SupportsTimedMedia || length <= TimedMedia.PrefixSize)))
                {
                    break;
                }

                var framed = new byte[MediaFraming.HeaderSize + length];
                Buffer.BlockCopy(header, 0, framed, 0, header.Length);
                if (!await ReadExactAsync(stream, framed.AsMemory(MediaFraming.HeaderSize, length), token))
                {
                    break;
                }

                // Carimba quem originou (o cliente manda 0) e repassa pros outros.
                MediaFraming.StampPublisher(framed, participant.Id);
                if (timed)
                {
                    var info = type == MediaChunkType.CameraVideo ? participant.CameraStream : participant.ScreenStream;
                    if (info is null || info.Generation != TimedMedia.Generation(framed))
                        continue;
                }
                RelayToOthers(framed, participant.Id, timed);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
        finally
        {
            client.Dispose();
            RemoveParticipant(participant);
        }
    }

    private void RelayToOthers(byte[] framed, int senderId, bool timed)
    {
        byte[]? legacy = timed ? null : framed;
        foreach (var (id, participant) in _participants)
        {
            if (id == senderId)
                continue;
            if (participant.ViewerOnly)
            {
                if (timed)
                    participant.TryEnqueueMedia(framed);
            }
            else
                participant.TryEnqueueMedia(legacy ??= TimedMedia.ToLegacy(framed));
        }
    }

    private void BroadcastControl(RoomEnvelope message, int exceptParticipantId = 0)
    {
        var bytes = message.ToJsonBytes();

        foreach (var (id, participant) in _participants)
        {
            if (id == exceptParticipantId)
            {
                continue;
            }

            _ = SafeSendControlAsync(participant, bytes);
        }
    }

    private static async Task SafeSendControlAsync(Participant participant, byte[] bytes)
    {
        try
        {
            await participant.ControlWriteLock.WaitAsync();
            try
            {
                await MessageFraming.WriteMessageAsync(participant.ControlStream, bytes);
            }
            finally
            {
                participant.ControlWriteLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void RemoveParticipant(Participant participant)
    {
        if (!_participants.TryRemove(participant.Id, out _))
        {
            return;
        }

        Log?.Invoke($"{participant.Nickname} saiu da sala.");

        foreach (var stream in participant.ActiveStreams)
        {
            BroadcastControl(new RoomEnvelope
            {
                Kind = RoomMessageKind.StreamStopped,
                ParticipantId = participant.Id,
                StreamKind = stream.Kind,
            });
        }

        BroadcastControl(new RoomEnvelope
        {
            Kind = RoomMessageKind.ParticipantLeft,
            ParticipantId = participant.Id,
            Nickname = participant.Nickname,
        });

        participant.Dispose();
    }

    private List<ParticipantInfo> SnapshotParticipants() =>
        _participants.Values
            .Select(p => new ParticipantInfo { Id = p.Id, Nickname = p.Nickname, IsSharing = p.ScreenStream is not null })
            .ToList();

    private List<StreamInfo> SnapshotStreams() =>
        _participants.Values
            .SelectMany(p => p.ActiveStreams)
            .ToList();

    private static Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token) =>
        ReadExactAsync(stream, buffer.AsMemory(0, count), token);

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _beacon?.Stop();
        _controlListener?.Stop();
        _mediaListener?.Stop();

        foreach (var participant in _participants.Values)
        {
            participant.Dispose();
        }
        _participants.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>Um participante conectado: canal de controle + canal de mídia com fila de envio
    /// própria, pra que alguém com internet ruim não segure os outros.</summary>
    private sealed class Participant : IDisposable
    {
        public int Id { get; }
        public string Nickname { get; }
        public NetworkStream ControlStream { get; }
        public bool SupportsTimedMedia { get; }
        public bool ViewerOnly { get; }
        public SemaphoreSlim ControlWriteLock { get; } = new(1, 1);

        // Tela e câmera são independentes: dá pra ter os dois ligados, só um, ou nenhum.
        private volatile StreamInfo? _screenStream;
        private volatile StreamInfo? _cameraStream;
        public StreamInfo? ScreenStream => _screenStream;
        public StreamInfo? CameraStream => _cameraStream;

        public bool IsPublishing => ScreenStream is not null || CameraStream is not null;

        public IEnumerable<StreamInfo> ActiveStreams =>
            new[] { ScreenStream, CameraStream }.Where(s => s is not null).Select(s => s!);

        public void SetStream(StreamKind kind, StreamInfo? info)
        {
            if (kind == StreamKind.Camera)
            {
                _cameraStream = info;
            }
            else
            {
                _screenStream = info;
            }
        }

        private readonly TcpClient _controlClient;
        private TcpClient? _mediaClient;
        private NetworkStream? _mediaStream;
        private bool _disposed;

        // Limite por TEMPO, não por quantidade (ver MediaQueue): descarta quando o pedaço mais
        // velho está esperando há mais de 500 ms. Fila cheia aqui significa o participante
        // assistindo atrasado, e o certo é descartar e ressincronizar no próximo keyframe. Só
        // funciona junto com o SendBufferSize limitado do socket — senão o atraso se acumula no
        // kernel, fora daqui, e esta fila nunca fica velha.
        private readonly MediaQueue _mediaQueue = new(TimeSpan.FromMilliseconds(300), maxBytes: 8 * 1024 * 1024);

        private readonly Action<int> _countRelayed;

        public Participant(int id, string nickname, TcpClient controlClient, NetworkStream controlStream,
            bool supportsTimedMedia, bool viewerOnly, Action<int> countRelayed)
        {
            Id = id;
            Nickname = nickname;
            _countRelayed = countRelayed;
            _controlClient = controlClient;
            ControlStream = controlStream;
            SupportsTimedMedia = supportsTimedMedia;
            ViewerOnly = viewerOnly;
        }

        public bool AttachMedia(TcpClient client, NetworkStream stream)
        {
            lock (_mediaSync)
            {
                if (_disposed)
                    return false;
                _mediaClient = client;
                _mediaStream = stream;
            }
            _ = Task.Run(SendLoopAsync);
            return true;
        }

        /// <summary>
        /// Enfileira um pedaço de mídia pra este participante. Se a fila encher (internet dele
        /// não está acompanhando), entra em modo "resync": o vídeo é segurado até aparecer um
        /// keyframe e só então volta a fluir.
        ///
        /// Sem isso, continuávamos mandando bytes do meio de um GOP e ele ficava com a imagem
        /// embaralhada até o próximo keyframe (até 1 s). Assim a imagem congela e volta limpa.
        /// O áudio nunca é segurado — cortar o som é pior que um congelamento de vídeo.
        /// </summary>
        private readonly object _mediaSync = new();
        private (int Publisher, StreamKind Kind, bool Audio)? _subscription;
        private readonly HashSet<(int Publisher, MediaChunkType Type)> _awaitingKeyframes = new();

        public void Subscribe(int? publisher, StreamKind kind = StreamKind.Screen, bool audio = false)
        {
            lock (_mediaSync)
            {
                _subscription = publisher.HasValue ? (publisher.Value, kind, audio) : null;
                _mediaQueue.Clear();
                _awaitingKeyframes.Clear();
                if (publisher.HasValue)
                    _awaitingKeyframes.Add((publisher.Value, MediaFraming.VideoTypeFor(kind)));
            }
        }

        public void TryEnqueueMedia(byte[] framed)
        {
            var type = TimedMedia.Type(framed);
            var key = (MediaFraming.ReadPublisherId(framed), type);
            var isVideo = MediaFraming.IsVideo(type);
            lock (_mediaSync)
            {
                if (_disposed)
                    return;
                if (ViewerOnly && (_subscription is not { } sub || sub.Publisher != key.Item1 ||
                    (isVideo ? type != MediaFraming.VideoTypeFor(sub.Kind) : !sub.Audio)))
                    return;

                if (isVideo && _awaitingKeyframes.Contains(key) &&
                    !(ViewerOnly ? TimedMedia.IsKeyframe(framed) : H264Inspector.ContainsKeyframeStart(framed, MediaFraming.HeaderSize)))
                {
                    Interlocked.Increment(ref _dropCount);
                    return;
                }
                if (_mediaQueue.TryEnqueue(framed))
                    _awaitingKeyframes.Remove(key);
                else if (isVideo)
                {
                    _awaitingKeyframes.Add(key);
                    Interlocked.Increment(ref _dropCount);
                }
            }
        }

        private int _dropCount;

        /// <summary>Quantos frames de vídeo este participante perdeu desde a última consulta.</summary>
        public int TakeDropCount() => Interlocked.Exchange(ref _dropCount, 0);

        /// <summary>Janelas de medição seguidas em que ele apareceu congestionado.</summary>
        public int CongestedWindows { get; set; }

        /// <summary>Quando avisamos sobre ele pela última vez (pra não repetir o aviso sem parar).</summary>
        public long LastCongestionWarningAt { get; set; }

        private async Task SendLoopAsync()
        {
            try
            {
                while (await _mediaQueue.DequeueAsync(CancellationToken.None) is { } chunk)
                {
                    if (_mediaStream is null)
                    {
                        break;
                    }

                    await _mediaStream.WriteAsync(chunk);
                    _countRelayed(chunk.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
            finally
            {
                _mediaClient?.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_mediaSync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _mediaQueue.Clear();
                _mediaQueue.Complete();
                _mediaClient?.Dispose();
                _controlClient.Dispose();
            }
        }
    }
}
