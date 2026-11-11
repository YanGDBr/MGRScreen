using System.Text.Json;

namespace MGRScreen.Core.Networking;

public enum RoomMessageKind
{
    Join,               // cliente -> servidor: quero entrar (só o nickname)
    JoinAccepted,       // servidor -> cliente: entrou; segue seu id, token de mídia e o estado atual da sala
    JoinRejected,       // servidor -> cliente: handshake inválido
    StartStream,        // cliente -> servidor: comecei a compartilhar (formato do meu stream)
    StopStream,         // cliente -> servidor: parei de compartilhar
    ParticipantJoined,  // servidor -> clientes
    ParticipantLeft,    // servidor -> clientes
    StreamStarted,      // servidor -> clientes: fulano começou a compartilhar
    StreamStopped,      // servidor -> clientes: fulano parou
    ViewerCongested,    // servidor -> quem transmite: fulano não está acompanhando a banda
    Discover,           // cliente -> servidor: tem sala aí? (não entra, só pergunta)
    DiscoverReply,      // servidor -> cliente: tem, e o nome é esse
    Ping,
    Pong,
    Subscribe,
    Unsubscribe,
}

public sealed class ParticipantInfo
{
    public int Id { get; set; }
    public string Nickname { get; set; } = "";
    public bool IsSharing { get; set; }
}

/// <summary>
/// De onde vem um stream. Cada participante pode publicar os dois ao mesmo tempo (o caso típico:
/// o jogo na tela + o rosto na câmera), então tudo que identifica um stream é o par
/// (participante, tipo) — e não só o participante, como era quando só existia a tela.
/// </summary>
public enum StreamKind
{
    Screen = 0,
    Camera = 1,
}

/// <summary>Formato de um stream sendo compartilhado — o receptor precisa da resolução pra criar
/// o decoder (lemos frames crus de tamanho fixo) e do formato de áudio pra tocar.</summary>
public sealed class StreamInfo
{
    public int ParticipantId { get; set; }
    public string Nickname { get; set; } = "";

    /// <summary>Tela ou câmera. Versões anteriores não mandam isto, e o padrão (0) é tela.</summary>
    public StreamKind Kind { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public bool HasAudio { get; set; }
    public int AudioSampleRate { get; set; } = 48_000;
    public int AudioChannels { get; set; } = 2;
    public long Generation { get; set; }
}

/// <summary>
/// Envelope único e "achatado" pra todas as mensagens de controle — o volume é baixíssimo
/// (entrar/sair da sala, começar/parar de compartilhar), então não vale a complexidade de uma
/// hierarquia polimórfica de tipos.
/// </summary>
public sealed class RoomEnvelope
{
    public RoomMessageKind Kind { get; set; }

    public string? Nickname { get; set; }
    public string? Reason { get; set; }
    public int ParticipantId { get; set; }
    public string? MediaToken { get; set; }

    /// <summary>Qual stream parou (StopStream / StreamStopped).</summary>
    public StreamKind StreamKind { get; set; }

    /// <summary>Quanta gente está na sala (usado na resposta da descoberta).</summary>
    public int ParticipantCount { get; set; }
    public int MediaPort { get; set; }

    public List<ParticipantInfo>? Participants { get; set; }
    public List<StreamInfo>? Streams { get; set; }
    public StreamInfo? Stream { get; set; }
    public int ProtocolVersion { get; set; }
    public bool ViewerOnly { get; set; }
    public bool IncludeAudio { get; set; }

    public byte[] ToJsonBytes() => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));

    public static RoomEnvelope FromJsonBytes(byte[] bytes) =>
        JsonSerializer.Deserialize<RoomEnvelope>(bytes)
        ?? throw new InvalidDataException("Mensagem de controle vazia/inválida.");

    public static RoomEnvelope Join(string nickname) => new()
    {
        Kind = RoomMessageKind.Join,
        Nickname = nickname,
    };

    public static RoomEnvelope Accepted(int participantId, string mediaToken, List<ParticipantInfo> participants, List<StreamInfo> streams) => new()
    {
        Kind = RoomMessageKind.JoinAccepted,
        ParticipantId = participantId,
        MediaToken = mediaToken,
        Participants = participants,
        Streams = streams,
    };

    public static RoomEnvelope Rejected(string reason) => new()
    {
        Kind = RoomMessageKind.JoinRejected,
        Reason = reason,
    };
}
