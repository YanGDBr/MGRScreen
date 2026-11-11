using System.Net.Sockets;

namespace MGRScreen.Core.Networking;

/// <summary>
/// Um participante da sala. Todo mundo usa esta mesma classe — inclusive quem criou a sala (que
/// conecta em 127.0.0.1 no próprio <see cref="RoomServer"/>). Publica o próprio stream e recebe
/// os dos outros pelo mesmo socket de mídia.
/// </summary>
public sealed class RoomClient : IDisposable
{
    public event Action<StreamInfo>? StreamStarted;
    public event Action<int, StreamKind>? StreamStopped; // (participantId, qual stream)
    public event Action<ParticipantInfo>? ParticipantJoined;
    public event Action<int>? ParticipantLeft;

    /// <summary>Alguém não está acompanhando a banda: (nickname, é sobre mim mesmo?).</summary>
    public event Action<string, bool>? ViewerCongested;
    public event Action<int, StreamKind, byte[]>? VideoChunkReceived; // (participantId, tela/câmera, chunk)
    public event Action<int, byte[]>? AudioChunkReceived; // (participantId, pcm16)
    public event Action<string>? Disconnected;

    public int ParticipantId { get; private set; }

    /// <summary>Cópia da lista atual. É cópia de propósito: a lista é alterada pela thread de
    /// rede enquanto a UI a percorre pra desenhar os participantes.</summary>
    public IReadOnlyList<ParticipantInfo> Participants
    {
        get
        {
            lock (_participantsSync)
            {
                return _participants.ToList();
            }
        }
    }

    private readonly object _participantsSync = new();
    private readonly List<ParticipantInfo> _participants = new();
    private TcpClient? _controlClient;
    private TcpClient? _mediaClient;
    private NetworkStream? _controlStream;
    private NetworkStream? _mediaStream;
    private CancellationTokenSource? _cts;
    private bool _timedMedia;
    private int _ended;
    private long _lastPongAt;
    private long _sequence;

    private readonly SemaphoreSlim _controlWriteLock = new(1, 1);

    // Fila de saída própria: a captura/áudio nunca escrevem direto no socket (evita escritas
    // concorrentes se intercalando e corrompendo o stream).
    //
    // Limite por tempo (ver MediaQueue): se o upload daqui não dá conta e o pedaço mais velho
    // está esperando há mais de 300 ms, descarta e ressincroniza no próximo keyframe. Um keyframe
    // grande passa numa boa, desde que o link o drene a tempo.
    private readonly MediaQueue _outgoing = new(TimeSpan.FromMilliseconds(300), maxBytes: 8 * 1024 * 1024);

    // Estado de ressincronização separado por stream (índice = StreamKind): um keyframe da
    // câmera não pode destravar a tela, que está num GOP completamente diferente.
    private readonly bool[] _awaitingKeyframe = new bool[2];

    /// <summary>Conecta e faz o handshake. Retorna null em caso de sucesso, ou a mensagem de
    /// rejeição em caso de falha.</summary>
    public async Task<string?> ConnectAsync(string hostAddress, int controlPort, int mediaPort, string nickname)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(8));

        _controlClient = new TcpClient();
        await _controlClient.ConnectAsync(hostAddress, controlPort, handshake.Token);
        _controlStream = _controlClient.GetStream();

        var join = RoomEnvelope.Join(nickname);
        join.ProtocolVersion = 2;
        await MessageFraming.WriteMessageAsync(_controlStream, join.ToJsonBytes(), handshake.Token);

        var responseBytes = await MessageFraming.ReadMessageAsync(_controlStream, handshake.Token);
        if (responseBytes is null)
        {
            return "A sala fechou a conexão sem responder.";
        }

        var response = RoomEnvelope.FromJsonBytes(responseBytes);
        if (response.Kind == RoomMessageKind.JoinRejected)
        {
            return response.Reason ?? "Conexão rejeitada.";
        }

        if (response.Kind != RoomMessageKind.JoinAccepted)
            throw new InvalidDataException("Resposta da sala inválida.");
        _timedMedia = response.ProtocolVersion == 2;
        ParticipantId = response.ParticipantId;
        lock (_participantsSync)
        {
            _participants.Clear();
            if (response.Participants is not null)
            {
                _participants.AddRange(response.Participants);
            }
        }

        // Mesma razão do lado do servidor: buffers curtos pra o atraso não se esconder no kernel.
        // O de recepção é maior porque limita a banda em links com latência (128 KB aguentam
        // ~10 Mbps a 100 ms de ida e volta, acima do que a gente transmite).
        _mediaClient = new TcpClient
        {
            NoDelay = true,
            SendBufferSize = 32 * 1024,
            ReceiveBufferSize = 128 * 1024,
        };
        await _mediaClient.ConnectAsync(hostAddress, mediaPort, handshake.Token);
        _mediaStream = _mediaClient.GetStream();

        var mediaToken = response.MediaToken ?? throw new InvalidDataException("Sala não enviou o token de mídia.");
        await _mediaStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(mediaToken), handshake.Token);

        // Streams que já estavam rolando quando entramos.
        if (response.Streams is not null)
        {
            foreach (var stream in response.Streams)
            {
                StreamStarted?.Invoke(stream);
            }
        }

        _ = Task.Run(() => ReceiveControlLoopAsync(token));
        _ = Task.Run(() => ReceiveMediaLoopAsync(token));
        _ = Task.Run(() => SendMediaLoopAsync(token));
        if (_timedMedia)
        {
            _lastPongAt = Environment.TickCount64;
            _ = Task.Run(() => HeartbeatAsync(token));
        }
        return null;
    }

    public async Task AnnounceStreamStartedAsync(StreamInfo info)
    {
        await SendControlAsync(new RoomEnvelope { Kind = RoomMessageKind.StartStream, Stream = info });
    }

    public async Task AnnounceStreamStoppedAsync(StreamKind kind = StreamKind.Screen)
    {
        await SendControlAsync(new RoomEnvelope { Kind = RoomMessageKind.StopStream, StreamKind = kind });
    }

    /// <summary>
    /// Enfileira vídeo pra enviar. Se o upload daqui não estiver dando conta, a fila enche e
    /// entramos em "resync": paramos de mandar até o próximo keyframe, senão os outros recebem
    /// pedaços do meio de um GOP e ficam com a imagem embaralhada até o keyframe seguinte.
    /// </summary>
    public void SendVideoChunk(byte[] chunk, long pts, StreamInfo info)
    {
        var k = (int)info.Kind;
        var keyframe = H264Inspector.ContainsIdr(chunk);
        if (_awaitingKeyframe[k] && !keyframe)
            return;
        var type = MediaFraming.VideoTypeFor(info.Kind);
        var frame = _timedMedia
            ? TimedMedia.Frame(type, info.Generation, Interlocked.Increment(ref _sequence), pts, keyframe, chunk)
            : MediaFraming.Frame(type, 0, chunk);
        _awaitingKeyframe[k] = !_outgoing.TryEnqueue(frame);
    }

    /// <summary>Áudio nunca é segurado: cortar o som incomoda mais que travar a imagem.</summary>
    public void SendAudioChunk(byte[] pcm16, long pts, StreamInfo info) =>
        _outgoing.TryEnqueue(_timedMedia
            ? TimedMedia.Frame(MediaChunkType.Audio, info.Generation, Interlocked.Increment(ref _sequence), pts, false, pcm16)
            : MediaFraming.Frame(MediaChunkType.Audio, 0, pcm16));

    private async Task SendControlAsync(RoomEnvelope message)
    {
        if (_controlStream is null)
        {
            return;
        }

        try
        {
            var token = _cts?.Token ?? CancellationToken.None;
            await _controlWriteLock.WaitAsync(token);
            try
            {
                await MessageFraming.WriteMessageAsync(_controlStream, message.ToJsonBytes(), token);
            }
            finally
            {
                _controlWriteLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            ConnectionLost();
        }
    }

    private async Task ReceiveControlLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytes = await MessageFraming.ReadMessageAsync(_controlStream!, token);
                if (bytes is null)
                {
                    break;
                }

                var message = RoomEnvelope.FromJsonBytes(bytes);

                switch (message.Kind)
                {
                    case RoomMessageKind.Pong:
                        Interlocked.Exchange(ref _lastPongAt, Environment.TickCount64);
                        break;
                    case RoomMessageKind.ParticipantJoined:
                        var joined = new ParticipantInfo { Id = message.ParticipantId, Nickname = message.Nickname ?? "Amigo" };
                        lock (_participantsSync)
                        {
                            _participants.Add(joined);
                        }
                        ParticipantJoined?.Invoke(joined);
                        break;

                    case RoomMessageKind.ParticipantLeft:
                        lock (_participantsSync)
                        {
                            _participants.RemoveAll(p => p.Id == message.ParticipantId);
                        }
                        ParticipantLeft?.Invoke(message.ParticipantId);
                        break;

                    case RoomMessageKind.StreamStarted when message.Stream is not null:
                        StreamStarted?.Invoke(message.Stream);
                        break;

                    case RoomMessageKind.StreamStopped:
                        StreamStopped?.Invoke(message.ParticipantId, message.StreamKind);
                        break;

                    case RoomMessageKind.ViewerCongested:
                        ViewerCongested?.Invoke(
                            message.Nickname ?? "Um espectador",
                            message.ParticipantId == ParticipantId);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException
            or ObjectDisposedException or System.Text.Json.JsonException)
        {
        }

        ConnectionLost();
    }

    private async Task ReceiveMediaLoopAsync(CancellationToken token)
    {
        var header = new byte[MediaFraming.HeaderSize];

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await ReadExactAsync(_mediaStream!, header.AsMemory(), token))
                {
                    break;
                }

                var type = MediaFraming.ReadType(header);
                var publisherId = MediaFraming.ReadPublisherId(header);
                var length = MediaFraming.ReadLength(header);

                if (length is < 0 or > 32 * 1024 * 1024)
                {
                    break;
                }

                var payload = new byte[length];
                if (!await ReadExactAsync(_mediaStream!, payload.AsMemory(), token))
                {
                    break;
                }

                switch (type)
                {
                    case MediaChunkType.Video:
                        VideoChunkReceived?.Invoke(publisherId, StreamKind.Screen, payload);
                        break;
                    case MediaChunkType.CameraVideo:
                        VideoChunkReceived?.Invoke(publisherId, StreamKind.Camera, payload);
                        break;
                    case MediaChunkType.Audio:
                        AudioChunkReceived?.Invoke(publisherId, payload);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        ConnectionLost();
    }

    private async Task SendMediaLoopAsync(CancellationToken token)
    {
        try
        {
            while (await _outgoing.DequeueAsync(token) is { } framed)
            {
                if (_mediaStream is null)
                {
                    break;
                }

                await _mediaStream.WriteAsync(framed, token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        ConnectionLost();
    }

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

    private async Task HeartbeatAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastPongAt) > 8000)
                {
                    ConnectionLost();
                    return;
                }
                _ = SendControlAsync(new RoomEnvelope { Kind = RoomMessageKind.Ping });
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ConnectionLost()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;
        CloseSockets();
        Disconnected?.Invoke("Conexão com a sala foi perdida.");
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            CloseSockets();
    }

    private void CloseSockets()
    {
        _cts?.Cancel();
        _outgoing.Complete();
        _controlClient?.Dispose();
        _mediaClient?.Dispose();
    }

    public void Dispose() => Disconnect();
}
