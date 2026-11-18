using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using MGRScreen.Core.Encoding;
using MGRScreen.Core.Models;
using MGRScreen.Core.Networking;

var serve = args.Contains("--serve");
var options = args.Where(arg => arg != "--serve").ToArray();
var ffmpeg = Path.GetFullPath(options.Length == 0 ? "tools/ffmpeg/ffmpeg.exe" : options[0]);
if (!File.Exists(ffmpeg))
    throw new FileNotFoundException("Informe o caminho do FFmpeg.", ffmpeg);

using var deadline = new CancellationTokenSource(serve ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(40));
var token = deadline.Token;
var controlPort = FreePort();
var mediaPort = FreePort();
while (mediaPort == controlPort)
    mediaPort = FreePort();
using var server = new RoomServer("host", controlPort, mediaPort);
server.Start(announceOnTailscale: false);
if (!serve)
{
    var (discovery, discoveryJson) = await Exchange(controlPort, new RoomEnvelope { Kind = RoomMessageKind.Discover });
    Check(discovery.Kind == RoomMessageKind.DiscoverReply && discovery.Nickname == "host" &&
        discovery.MediaPort == mediaPort && discovery.ParticipantCount == 0, "Descoberta TCP inválida.");
    Check(discovery.ProtocolVersion == 0 && !HasNewFields(discoveryJson),
        "A descoberta de um cliente antigo recebeu campos do protocolo 3.");
    var anonymous = RoomEnvelope.Join("   ");
    anonymous.ProtocolVersion = 2;
    anonymous.ViewerOnly = true;
    var (blankReply, _) = await Exchange(controlPort, anonymous);
    Check(blankReply.Kind == RoomMessageKind.JoinRejected && server.ParticipantCount == 0,
        "A sala aceitou um espectador sem apelido.");
    Check(!server.RequiresPassword, "Sala sem senha diz que exige senha.");
    await PasswordRoom();
    await OldServer();
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
legacy.Watch(token);
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

await RoomFeatures();

var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
publisher.Disconnected += _ => disconnected.TrySetResult();
server.Stop();
await disconnected.Task.WaitAsync(token);
Console.WriteLine($"{chosenEncoder}: assinatura seletiva, troca de stream, SPS/PPS, PTS, Pong, framing legado e desconexão OK.");
Console.WriteLine("Protocolo 3: senha, chat, anotações, voz, compatibilidade com clientes v2/legado e com servidor antigo OK.");

// Chat, anotações e voz na sala principal, com três tipos de cliente antigo olhando: o Android
// (espectador v2), o Windows 1.0.x (v2) e o legado (sem versão). Nenhum deles pode ver nada novo.
async Task RoomFeatures()
{
    viewer.Watch(token);
    using var oldDesktop = await Peer.Join(controlPort, mediaPort, viewer: false, token, version: 2);
    oldDesktop.Watch(token);
    var oldPeers = new[] { viewer, oldDesktop, legacy };

    using var alice = new RoomClient();
    using var bob = new RoomClient();
    var aliceSaw = Listen(alice);
    var bobSaw = Listen(bob);
    var publisherSaw = Listen(publisher);
    // Senha informada numa sala aberta não atrapalha.
    Check(await alice.ConnectAsync("127.0.0.1", controlPort, mediaPort, "alice") is null, "alice não entrou.");
    Check(await bob.ConnectAsync("127.0.0.1", controlPort, mediaPort, "bob", "qualquer") is null && !bob.RejectedForPassword,
        "Sala sem senha recusou quem informou uma.");
    Check(publisher.SupportsRoomFeatures && alice.SupportsRoomFeatures && bob.SupportsRoomFeatures,
        "Cliente novo em servidor novo sem as funções da sala.");
    using var modern = await Peer.Join(controlPort, mediaPort, viewer: false, token, version: 3);
    using var modernViewer = await Peer.Join(controlPort, mediaPort, viewer: true, token, version: 3);

    // Chat: eco ao remetente, entrega aos demais, apelido e horário do servidor.
    var before = DateTime.UtcNow.AddSeconds(-1);
    await alice.SendChatAsync("oi");
    await alice.SendChatAsync("   ");
    await alice.SendChatAsync(new string('x', 2500));
    await modern.Send(new RoomEnvelope
    {
        Kind = RoomMessageKind.Chat, Text = "forjada", Nickname = "impostor", ParticipantId = 999, SentAtUnixMs = 1,
    }, token);
    await bob.SendChatAsync("fim");
    var order = new List<string>();
    foreach (var saw in new[] { aliceSaw, bobSaw, publisherSaw })
    {
        var chats = new List<ChatMessage>();
        while (chats.Count < 4)
            if (await saw.Reader.ReadAsync(token) is ChatMessage chat)
                chats.Add(chat);
        Check(chats.All(c => c.SentAtUtc.Kind == DateTimeKind.Utc && c.SentAtUtc >= before &&
            c.SentAtUtc <= DateTime.UtcNow.AddSeconds(1)), "Horário do chat não veio do servidor.");
        var hello = chats.Single(c => c.Text == "oi");
        Check(hello.ParticipantId == alice.ParticipantId && hello.Nickname == "alice", "Chat com remetente errado.");
        Check(chats.Count(c => c.Text == new string('x', 2000)) == 1, "Chat não foi cortado em 2000 caracteres.");
        var forged = chats.Single(c => c.Text == "forjada");
        Check(forged.ParticipantId == modern.Id && forged.Nickname == "fixture", "O servidor confiou no remetente do chat.");
        Check(chats.Single(c => c.Text == "fim").ParticipantId == bob.ParticipantId, "Chat do bob com remetente errado.");
        Check(chats.FindIndex(c => c.Text == "oi") < chats.FindIndex(c => c.Text.StartsWith('x')), "Chat fora de ordem.");
        order.Add(string.Join("|", chats.Select(c => c.ParticipantId)));
    }
    Check(order.Distinct().Count() == 1, "Participantes viram o chat em ordens diferentes.");

    // Anotação: chega aos outros, não ao remetente. Grande demais é ignorada, no cliente e no servidor.
    const string stroke = "{\"x\":0.5,\"cor\":\"é\"}";
    await alice.SendAnnotationAsync(publisher.ParticipantId, StreamKind.Camera, stroke);
    await alice.SendAnnotationAsync(publisher.ParticipantId, StreamKind.Screen, new string('a', 16 * 1024 + 1));
    await modern.Send(new RoomEnvelope { Kind = RoomMessageKind.Annotation, Payload = new string('a', 16 * 1024 + 1) }, token);
    await modern.Send(new RoomEnvelope { Kind = RoomMessageKind.Chat, Text = "depois do traço" }, token);
    await alice.SendChatAsync("marca");
    foreach (var saw in new[] { bobSaw, publisherSaw })
    {
        var annotations = await Until(saw, "marca", "depois do traço");
        Check(annotations.Count == 1 && annotations[0] ==
            new AnnotationMessage(alice.ParticipantId, "alice", publisher.ParticipantId, StreamKind.Camera, stroke),
            "Anotação não chegou certa aos outros.");
    }
    Check((await Until(aliceSaw, "marca", "depois do traço")).Count == 0, "Anotação voltou ao remetente.");

    // Limite de taxa: de uma rajada de 200, só o que cabe na(s) janela(s) de 1 s passa.
    for (var index = 0; index < 200; index++)
        await alice.SendAnnotationAsync(publisher.ParticipantId, StreamKind.Screen, "{}");
    await alice.SendChatAsync("rajada");
    var burst = (await Until(bobSaw, "rajada")).Count;
    Check(burst is >= 59 and <= 120, $"Limite de taxa das anotações deixou passar {burst} de 200.");

    // Voz: chega aos outros v3 (inclusive a um espectador que não assinou stream nenhum), nunca a quem falou.
    var voice = Enumerable.Repeat((byte)7, 640).ToArray();
    var aliceHeardHerself = false;
    alice.VoiceChunkReceived += (_, _) => aliceHeardHerself = true;
    var bobHeard = Heard(bob);
    var publisherHeard = Heard(publisher);
    var viewerHeard = modernViewer.ReadMedia(token);
    while (!bobHeard.IsCompleted || !publisherHeard.IsCompleted || !viewerHeard.IsCompleted)
    {
        alice.SendVoiceChunk(voice, 20_000);
        await Task.Delay(20, token);
    }
    foreach (var heard in new[] { bobHeard, publisherHeard })
    {
        var (speaker, pcm) = await heard;
        Check(speaker == alice.ParticipantId && pcm.SequenceEqual(voice), "Voz chegou errada.");
    }
    var (voiceHeader, voicePayload) = await viewerHeard;
    Check(voiceHeader[0] == 0x84 && MediaFraming.ReadPublisherId(voiceHeader) == alice.ParticipantId &&
        voicePayload.AsSpan(25).SequenceEqual(voice), "Espectador v3 sem assinatura não recebeu a voz.");

    // Compatibilidade. Marca o ponto em que tudo acima já foi entregue aos v3 e espera cada
    // cliente antigo receber coisas POSTERIORES pelos mesmos canais (que são filas em ordem):
    // a entrada de mais um participante no controle e mais vídeo/áudio na mídia. Se algo novo
    // tivesse sido mandado a eles, teria chegado antes disso.
    var marks = oldPeers.Select(peer => peer.MediaSeen).ToArray();
    using var late = await Peer.Join(controlPort, mediaPort, viewer: false, token);
    for (var index = 0; index < oldPeers.Length; index++)
    {
        var peer = oldPeers[index];
        while (!peer.SawJoin(late.Id) || peer.MediaSeen < marks[index] + 20)
        {
            Check(peer.Failure is null, $"Cliente antigo perdeu a conexão: {peer.Failure}");
            await Task.Delay(20, token);
        }
    }
    await Task.Delay(300, token);
    Check(!aliceHeardHerself, "Voz voltou a quem falou.");
    Check(viewer.MediaTypes.All(type => type is 0x81 or 0x82 or 0x83), "Android (v2) recebeu um tipo de mídia novo.");
    Check(viewer.MediaTypes.Contains((byte)0x83), "Android (v2) parou de receber o stream assinado.");
    foreach (var desktop in new[] { oldDesktop, legacy })
    {
        Check(desktop.MediaTypes.All(type => type is 1 or 2 or 3), "Cliente antigo recebeu voz ou framing novo.");
        Check(new byte[] { 1, 2, 3 }.All(desktop.MediaTypes.Contains), "Cliente antigo parou de receber vídeo/áudio.");
    }
    foreach (var peer in oldPeers)
    {
        Check(peer.Failure is null, $"Cliente antigo perdeu a conexão: {peer.Failure}");
        Check(peer.Control.All(json => RoomEnvelope.FromJsonBytes(Encoding.UTF8.GetBytes(json)).Kind <= RoomMessageKind.Unsubscribe),
            "Cliente antigo recebeu um tipo de mensagem do protocolo 3.");
        Check(!peer.Control.Any(HasNewFields), "Cliente antigo recebeu campos do protocolo 3.");
    }
}

// Sala com senha, num servidor à parte.
async Task PasswordRoom()
{
    const string password = "s3nha çã";
    var lockedControl = FreePort();
    var lockedMedia = FreePort();
    using var locked = new RoomServer("cofre", lockedControl, lockedMedia, password);
    locked.Start(announceOnTailscale: false);
    Check(locked.RequiresPassword, "Sala com senha não diz que exige senha.");

    // Descoberta: só quem pergunta no protocolo 3 fica sabendo da senha.
    var (modernDiscovery, _) = await Exchange(lockedControl, new RoomEnvelope { Kind = RoomMessageKind.Discover, ProtocolVersion = 3 });
    Check(modernDiscovery.Kind == RoomMessageKind.DiscoverReply && modernDiscovery.RequiresPassword &&
        modernDiscovery.ProtocolVersion == 3, "Descoberta v3 não informou a senha.");
    var (oldDiscovery, oldDiscoveryJson) = await Exchange(lockedControl, new RoomEnvelope { Kind = RoomMessageKind.Discover });
    Check(oldDiscovery.Kind == RoomMessageKind.DiscoverReply && oldDiscovery.Nickname == "cofre" &&
        oldDiscovery.ProtocolVersion == 0 && !HasNewFields(oldDiscoveryJson), "Descoberta antiga mudou na sala com senha.");
    var (openDiscovery, _) = await Exchange(controlPort, new RoomEnvelope { Kind = RoomMessageKind.Discover, ProtocolVersion = 3 });
    Check(!openDiscovery.RequiresPassword && openDiscovery.ProtocolVersion == 3, "Descoberta v3 da sala aberta inválida.");

    using (var right = new RoomClient())
    {
        Check(await right.ConnectAsync("127.0.0.1", lockedControl, lockedMedia, "certa", password) is null &&
            !right.RejectedForPassword && right.SupportsRoomFeatures, "Senha certa foi recusada.");
        await WaitUntil(() => locked.ParticipantCount == 1);
    }
    await WaitUntil(() => locked.ParticipantCount == 0);

    // Errada e ausente são recusadas; a mesma instância entra depois que o usuário digita a certa.
    using (var retry = new RoomClient())
    {
        Check(await retry.ConnectAsync("127.0.0.1", lockedControl, lockedMedia, "tenta", "errada") is not null &&
            retry.RejectedForPassword, "Senha errada foi aceita.");
        Check(await retry.ConnectAsync("127.0.0.1", lockedControl, lockedMedia, "tenta") is not null &&
            retry.RejectedForPassword, "Entrou sem senha numa sala com senha.");
        Check(await retry.ConnectAsync("127.0.0.1", lockedControl, lockedMedia, "tenta", "") is not null &&
            retry.RejectedForPassword, "Entrou com senha vazia numa sala com senha.");
        Check(locked.ParticipantCount == 0, "Tentativa recusada virou participante.");
        Check(await retry.ConnectAsync("127.0.0.1", lockedControl, lockedMedia, "tenta", password) is null &&
            !retry.RejectedForPassword, "Não entrou ao tentar de novo com a senha certa.");
    }

    // Cliente antigo (Windows 1.0.x, Android, legado): recusa comum, sem nada do protocolo 3.
    foreach (var (version, viewerOnly) in new[] { (2, false), (2, true), (0, false) })
    {
        var oldJoin = RoomEnvelope.Join("antigo");
        oldJoin.ProtocolVersion = version;
        oldJoin.ViewerOnly = viewerOnly;
        var (oldReply, oldJson) = await Exchange(lockedControl, oldJoin);
        Check(oldReply.Kind == RoomMessageKind.JoinRejected && oldReply.Reason!.Contains("senha") && !HasNewFields(oldJson),
            "Cliente antigo não recebeu a recusa comum na sala com senha.");
    }

    // A resposta ao desafio de uma conexão não serve em outra (o nonce muda), e lixo não passa.
    var first = await Challenge(lockedControl);
    var second = await Challenge(lockedControl);
    Check(first.Nonce != second.Nonce && Convert.FromBase64String(first.Nonce).Length == 32, "Nonce do desafio repetido ou curto.");
    var replayed = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(first.Nonce)));
    foreach (var proof in new[] { replayed, "", "não é base64", Convert.ToBase64String(new byte[32]) })
    {
        await MessageFraming.WriteMessageAsync(second.Client.GetStream(),
            new RoomEnvelope { Kind = RoomMessageKind.JoinAuth, Proof = proof }.ToJsonBytes(), token);
        var verdict = RoomEnvelope.FromJsonBytes((await MessageFraming.ReadMessageAsync(second.Client.GetStream(), token))!);
        Check(verdict.Kind == RoomMessageKind.JoinRejected && verdict.RequiresPassword, "Prova de senha inválida foi aceita.");
        second.Client.Dispose();
        second = await Challenge(lockedControl);
    }
    first.Client.Dispose();
    second.Client.Dispose();
}

async Task<(TcpClient Client, string Nonce)> Challenge(int port)
{
    var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port, token);
    var join = RoomEnvelope.Join("desafio");
    join.ProtocolVersion = 2;
    join.MaxProtocolVersion = 3;
    await MessageFraming.WriteMessageAsync(client.GetStream(), join.ToJsonBytes(), token);
    var challenge = RoomEnvelope.FromJsonBytes((await MessageFraming.ReadMessageAsync(client.GetStream(), token))!);
    Check(challenge.Kind == RoomMessageKind.JoinChallenge && challenge.Nonce is not null, "Sala com senha não desafiou o cliente v3.");
    return (client, challenge.Nonce!);
}

// Cliente novo num anfitrião 1.0.x (imitado aqui: ele só aceita ProtocolVersion == 2 e responde 2).
// Tem que entrar como antes, e as funções novas não podem mandar nada que o servidor antigo não conheça.
async Task OldServer()
{
    var oldControl = new TcpListener(IPAddress.Loopback, 0);
    var oldMedia = new TcpListener(IPAddress.Loopback, 0);
    oldControl.Start();
    oldMedia.Start();
    try
    {
        using var client = new RoomClient();
        var connecting = client.ConnectAsync("127.0.0.1", ((IPEndPoint)oldControl.LocalEndpoint).Port,
            ((IPEndPoint)oldMedia.LocalEndpoint).Port, "novo", "senha que o antigo ignora");
        using var control = await oldControl.AcceptTcpClientAsync(token);
        var join = RoomEnvelope.FromJsonBytes((await MessageFraming.ReadMessageAsync(control.GetStream(), token))!);
        Check(join.Kind == RoomMessageKind.Join && join.ProtocolVersion == 2 && !join.ViewerOnly,
            "O Join do cliente novo não seria aceito como protocolo 2 por um servidor antigo.");
        var accepted = RoomEnvelope.Accepted(1, Guid.NewGuid().ToString("N"), new(), new());
        accepted.ProtocolVersion = 2;
        await MessageFraming.WriteMessageAsync(control.GetStream(), accepted.ToJsonBytes(), token);
        using var media = await oldMedia.AcceptTcpClientAsync(token);
        await media.GetStream().ReadExactlyAsync(new byte[32], token);
        Check(await connecting is null && !client.SupportsRoomFeatures && !client.RejectedForPassword,
            "Cliente novo não entrou direito num servidor antigo.");

        await client.SendChatAsync("oi");
        await client.SendAnnotationAsync(1, StreamKind.Screen, "{}");
        client.SendVoiceChunk(new byte[640], 1);
        // Marcadores conhecidos pelo servidor antigo: são a primeira coisa que pode chegar.
        await client.AnnounceStreamStoppedAsync();
        client.SendAudioChunk(new byte[4], 1, new StreamInfo { Generation = 1 });
        RoomEnvelope next;
        do
            next = RoomEnvelope.FromJsonBytes((await MessageFraming.ReadMessageAsync(control.GetStream(), token))!);
        while (next.Kind == RoomMessageKind.Ping);
        Check(next.Kind == RoomMessageKind.StopStream, "Cliente novo mandou chat/anotação a um servidor antigo.");
        var header = new byte[MediaFraming.HeaderSize];
        await media.GetStream().ReadExactlyAsync(header, token);
        Check(header[0] == 0x82, "Cliente novo mandou voz a um servidor antigo.");
    }
    finally
    {
        oldControl.Stop();
        oldMedia.Stop();
    }
}

// Tudo que um cliente recebe de chat e anotação, na ordem em que chegou.
static Channel<object> Listen(RoomClient client)
{
    var seen = Channel.CreateUnbounded<object>();
    client.ChatReceived += chat => seen.Writer.TryWrite(chat);
    client.AnnotationReceived += annotation => seen.Writer.TryWrite(annotation);
    return seen;
}

// Consome até aparecerem todos os chats-marcadores e devolve as anotações vistas no caminho.
async Task<List<AnnotationMessage>> Until(Channel<object> seen, params string[] markers)
{
    var annotations = new List<AnnotationMessage>();
    var pending = markers.ToHashSet();
    while (pending.Count > 0)
    {
        var item = await seen.Reader.ReadAsync(token);
        if (item is AnnotationMessage annotation)
            annotations.Add(annotation);
        else
            pending.Remove(((ChatMessage)item).Text);
    }
    return annotations;
}

static Task<(int, byte[])> Heard(RoomClient client)
{
    var heard = new TaskCompletionSource<(int, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.VoiceChunkReceived += (speaker, pcm) => heard.TrySetResult((speaker, pcm));
    return heard.Task;
}

async Task WaitUntil(Func<bool> condition)
{
    while (!condition())
        await Task.Delay(10, token);
}

// As chaves JSON que só existem no protocolo 3: nenhuma pode aparecer no que um cliente antigo recebe.
static bool HasNewFields(string json) => new[]
{
    "MaxProtocolVersion", "RequiresPassword", "Nonce", "Proof", "Text", "SentAtUnixMs", "TargetParticipantId", "Payload",
}.Any(field => json.Contains($"\"{field}\":"));

async Task<(RoomEnvelope Reply, string Json)> Exchange(int port, RoomEnvelope envelope)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port, token);
    var stream = client.GetStream();
    await MessageFraming.WriteMessageAsync(stream, envelope.ToJsonBytes(), token);
    var reply = await MessageFraming.ReadMessageAsync(stream, token) ?? throw new EndOfStreamException();
    return (RoomEnvelope.FromJsonBytes(reply), Encoding.UTF8.GetString(reply));
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

sealed class Peer(TcpClient control, TcpClient media, int id) : IDisposable
{
    public int AvailableMedia => media.Available;
    public int Id => id;

    // Preenchidos por Watch: tudo que este cliente recebeu, pra provar o que NÃO chegou.
    private readonly List<string> _control = new();
    private readonly HashSet<byte> _mediaTypes = new();
    private int _mediaSeen;
    public string[] Control { get { lock (_control) return _control.ToArray(); } }
    public byte[] MediaTypes { get { lock (_mediaTypes) return _mediaTypes.ToArray(); } }
    public int MediaSeen => Volatile.Read(ref _mediaSeen);
    public string? Failure { get; private set; }

    public bool SawJoin(int participantId) => Control.Select(json => RoomEnvelope.FromJsonBytes(Encoding.UTF8.GetBytes(json)))
        .Any(message => message.Kind == RoomMessageKind.ParticipantJoined && message.ParticipantId == participantId);

    /// <summary>Passa a ler os dois canais em segundo plano, guardando o JSON de controle cru e o
    /// byte de tipo de cada pedaço de mídia.</summary>
    public void Watch(CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await MessageFraming.ReadMessageAsync(control.GetStream(), token) is { } message)
                    lock (_control)
                        _control.Add(Encoding.UTF8.GetString(message));
                Failure ??= "controle fechado";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var (header, _) = await ReadMedia(token);
                    lock (_mediaTypes)
                        _mediaTypes.Add(header[0]);
                    Interlocked.Increment(ref _mediaSeen);
                }
            }
            catch (EndOfStreamException) { Failure ??= "mídia fechada"; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        });
    }

    /// <param name="version">Padrão: 2 pro espectador (Android), 0 pro legado. Com 3, entra como
    /// o cliente novo entra: ProtocolVersion 2 + MaxProtocolVersion 3.</param>
    public static async Task<Peer> Join(int controlPort, int mediaPort, bool viewer, CancellationToken token, int? version = null)
    {
        var control = new TcpClient();
        var media = new TcpClient();
        try
        {
            await control.ConnectAsync(IPAddress.Loopback, controlPort, token);
            var join = RoomEnvelope.Join("fixture");
            var expected = version ?? (viewer ? 2 : 0);
            join.ProtocolVersion = Math.Min(expected, 2);
            join.MaxProtocolVersion = expected > 2 ? expected : 0;
            join.ViewerOnly = viewer;
            await MessageFraming.WriteMessageAsync(control.GetStream(), join.ToJsonBytes(), token);
            var reply = RoomEnvelope.FromJsonBytes((await MessageFraming.ReadMessageAsync(control.GetStream(), token))!);
            if (reply.Kind != RoomMessageKind.JoinAccepted || reply.ProtocolVersion != expected)
                throw new Exception("Negociação inválida.");
            await media.ConnectAsync(IPAddress.Loopback, mediaPort, token);
            await media.GetStream().WriteAsync(Encoding.ASCII.GetBytes(reply.MediaToken!), token);
            return new Peer(control, media, reply.ParticipantId);
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
