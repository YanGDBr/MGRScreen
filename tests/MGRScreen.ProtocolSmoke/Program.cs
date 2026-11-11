using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MGRScreen.Core.Encoding;
using MGRScreen.Core.Models;
using MGRScreen.Core.Networking;

var serve = args.Contains("--serve");
var options = args.Where(arg => arg != "--serve").ToArray();
var ffmpeg = Path.GetFullPath(options.Length == 0 ? "tools/ffmpeg/ffmpeg.exe" : options[0]);
if (!File.Exists(ffmpeg))
    throw new FileNotFoundException("Informe o caminho do FFmpeg.", ffmpeg);

using var deadline = new CancellationTokenSource(serve ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(25));
var token = deadline.Token;
var controlPort = FreePort();
var mediaPort = FreePort();
while (mediaPort == controlPort)
    mediaPort = FreePort();
using var server = new RoomServer("host", controlPort, mediaPort);
server.Start(announceOnTailscale: false);
if (!serve)
{
    var discovery = await SendFirstMessage(new RoomEnvelope { Kind = RoomMessageKind.Discover });
    Check(discovery.Kind == RoomMessageKind.DiscoverReply && discovery.Nickname == "host" &&
        discovery.MediaPort == mediaPort && discovery.ParticipantCount == 0, "Descoberta TCP inválida.");
    var anonymous = RoomEnvelope.Join("   ");
    anonymous.ProtocolVersion = 2;
    anonymous.ViewerOnly = true;
    var blankReply = await SendFirstMessage(anonymous);
    Check(blankReply.Kind == RoomMessageKind.JoinRejected && server.ParticipantCount == 0,
        "A sala aceitou um espectador sem apelido.");
}
using var publisher = new RoomClient();
if (await publisher.ConnectAsync("127.0.0.1", controlPort, mediaPort, "publisher") is { } rejection)
    throw new Exception(rejection);

var screen = new StreamInfo { Width = 320, Height = 180, Fps = 30, HasAudio = true, Generation = 123 };
var camera = new StreamInfo { Kind = StreamKind.Camera, Width = 320, Height = 180, Fps = 30, Generation = 456 };
await publisher.AnnounceStreamStartedAsync(screen);
await publisher.AnnounceStreamStartedAsync(camera);
var chosenEncoder = options.Length > 1 ? options[1] : "libx264";
using var encoder = new FfmpegEncoder(new SessionSettings
{
    Width = screen.Width, Height = screen.Height, Fps = screen.Fps,
    VideoBitrateKbps = 600, VideoEncoder = chosenEncoder,
}, ffmpeg);
encoder.FfmpegLogLine += Console.WriteLine;
encoder.EncodedChunkReady += (frame, pts) =>
{
    publisher.SendVideoChunk(frame, pts, screen);
    publisher.SendVideoChunk(frame, pts, camera);
    publisher.SendAudioChunk(new byte[6400], pts, screen);
};
encoder.StartToStream();
var pixels = new byte[screen.Width * screen.Height * 4];
encoder.SubmitFrame(pixels);
if (serve)
{
    Console.WriteLine($"Sala sintética disponível. Controle: {controlPort}. Mídia: {mediaPort}. Ctrl+C para encerrar.");
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
    var phase = 0;
    while (await timer.WaitForNextTickAsync() && !token.IsCancellationRequested)
    {
        for (var y = 0; y < screen.Height; y++)
        for (var x = 0; x < screen.Width; x++)
        {
            var offset = (y * screen.Width + x) * 4;
            pixels[offset] = (byte)(x + phase);
            pixels[offset + 1] = (byte)(y + phase);
            pixels[offset + 2] = (byte)(phase + (x + phase) / 24 % 2 * 100);
            pixels[offset + 3] = 255;
        }
        encoder.SubmitFrame(pixels);
        phase += 3;
    }
    return;
}

using var viewer = await Peer.Join(controlPort, mediaPort, viewer: true, token);
using var legacy = await Peer.Join(controlPort, mediaPort, viewer: false, token);
await viewer.Send(new RoomEnvelope { Kind = RoomMessageKind.Ping }, token);
await viewer.WaitFor(RoomMessageKind.Pong, token);

var firstLegacy = await legacy.ReadMedia(token);
Check(firstLegacy.Header[0] is 1 or 2 or 3, "Cliente antigo recebeu o novo framing.");
await Task.Delay(350, token);
Check(viewer.AvailableMedia == 0, "Espectador recebeu mídia antes de assinar.");

await viewer.Send(new RoomEnvelope
{
    Kind = RoomMessageKind.Subscribe, ParticipantId = publisher.ParticipantId, IncludeAudio = true,
}, token);

var latestPts = 0L;
var audioSeen = false;
var videoSeen = 0;
while (videoSeen < 12 || !audioSeen)
{
    var (header, data) = await viewer.ReadMedia(token);
    Check(header[0] is 0x81 or 0x82, "Assinatura recebeu câmera ou framing legado.");
    Check(MediaFraming.ReadPublisherId(header) == publisher.ParticipantId, "Publicador incorreto.");
    Check(BinaryPrimitives.ReadInt64LittleEndian(data) == screen.Generation, "Geração incorreta.");
    if (header[0] == 0x82)
    {
        audioSeen = true;
        continue;
    }
    var pts = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));
    Check(pts > latestPts, "Timestamp de vídeo não é crescente.");
    latestPts = pts;
    if (videoSeen++ == 0)
    {
        Check(data[24] == 1, "Assinatura começou sem um IDR.");
        var nals = NalTypes(data, 25);
        Check(nals.Contains(5) && nals.Contains(7) && nals.Contains(8) && nals.Contains(9),
            "O primeiro IDR não trouxe SPS, PPS e AUD.");
    }
}

await viewer.Send(new RoomEnvelope
{
    Kind = RoomMessageKind.Subscribe, ParticipantId = publisher.ParticipantId, StreamKind = StreamKind.Camera,
}, token);
while (true)
{
    var (header, data) = await viewer.ReadMedia(token);
    if (header[0] != 0x83)
        continue;
    Check(data[24] == 1 && BinaryPrimitives.ReadInt64LittleEndian(data) == camera.Generation,
        "Troca para câmera não começou com um IDR da geração correta.");
    break;
}

var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
publisher.Disconnected += _ => disconnected.TrySetResult();
server.Stop();
await disconnected.Task.WaitAsync(token);
Console.WriteLine($"{chosenEncoder}: assinatura seletiva, troca de stream, SPS/PPS, PTS, Pong, framing legado e desconexão OK.");

async Task<RoomEnvelope> SendFirstMessage(RoomEnvelope envelope)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, controlPort, token);
    var stream = client.GetStream();
    await MessageFraming.WriteMessageAsync(stream, envelope.ToJsonBytes(), token);
    var reply = await MessageFraming.ReadMessageAsync(stream, token) ?? throw new EndOfStreamException();
    return RoomEnvelope.FromJsonBytes(reply);
}

static void Check(bool result, string message)
{
    if (!result)
        throw new Exception(message);
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
    finally { listener.Stop(); }
}

static HashSet<int> NalTypes(byte[] data, int start)
{
    var types = new HashSet<int>();
    for (var index = start; index + 3 < data.Length; index++)
        if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
            types.Add(data[index + 3] & 31);
    return types;
}

sealed class Peer(TcpClient control, TcpClient media) : IDisposable
{
    public int AvailableMedia => media.Available;

    public static async Task<Peer> Join(int controlPort, int mediaPort, bool viewer, CancellationToken token)
    {
        var control = new TcpClient();
        var media = new TcpClient();
        try
        {
            await control.ConnectAsync(IPAddress.Loopback, controlPort, token);
            var join = RoomEnvelope.Join("fixture");
            join.ProtocolVersion = viewer ? 2 : 0;
            join.ViewerOnly = viewer;
            await MessageFraming.WriteMessageAsync(control.GetStream(), join.ToJsonBytes(), token);
            var reply = RoomEnvelope.FromJsonBytes((await MessageFraming.ReadMessageAsync(control.GetStream(), token))!);
            if (reply.Kind != RoomMessageKind.JoinAccepted || reply.ProtocolVersion != join.ProtocolVersion)
                throw new Exception("Negociação inválida.");
            await media.ConnectAsync(IPAddress.Loopback, mediaPort, token);
            await media.GetStream().WriteAsync(Encoding.ASCII.GetBytes(reply.MediaToken!), token);
            return new Peer(control, media);
        }
        catch
        {
            control.Dispose();
            media.Dispose();
            throw;
        }
    }

    public Task Send(RoomEnvelope message, CancellationToken token) =>
        MessageFraming.WriteMessageAsync(control.GetStream(), message.ToJsonBytes(), token);

    public async Task WaitFor(RoomMessageKind kind, CancellationToken token)
    {
        while (true)
        {
            var message = await MessageFraming.ReadMessageAsync(control.GetStream(), token)
                ?? throw new EndOfStreamException();
            if (RoomEnvelope.FromJsonBytes(message).Kind == kind)
                return;
        }
    }

    public async Task<(byte[] Header, byte[] Payload)> ReadMedia(CancellationToken token)
    {
        var header = new byte[MediaFraming.HeaderSize];
        await media.GetStream().ReadExactlyAsync(header, token);
        var payload = new byte[MediaFraming.ReadLength(header)];
        await media.GetStream().ReadExactlyAsync(payload, token);
        return (header, payload);
    }

    public void Dispose()
    {
        control.Dispose();
        media.Dispose();
    }
}
