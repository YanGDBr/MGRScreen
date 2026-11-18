using System.Text.Json;
using System.Text.Json.Serialization;

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

    // Protocolo 3. O servidor só manda estes tipos a quem entrou anunciando a versão 3: os
    // clientes 1.0.x e o visualizador Android não os conhecem. Os valores numéricos anteriores
    // não mudam (o enum vai como número no JSON), por isso os novos entram sempre no fim.
    JoinChallenge,      // servidor -> cliente: a sala tem senha; segue o nonce do desafio
    JoinAuth,           // cliente -> servidor: HMAC-SHA256(senha, nonce)
    Chat,               // cliente -> servidor: texto; servidor -> todos: texto carimbado
    Annotation,         // cliente -> servidor -> os outros: desenho/ponteiro sobre a tela de alguém
}

/// <summary>Uma mensagem do chat da sala. Apelido e horário são carimbados pelo servidor.</summary>
public sealed record ChatMessage(int ParticipantId, string Nickname, string Text, DateTime SentAtUtc);

/// <summary>Um desenho (ou ponteiro laser) de alguém sobre o stream de outra pessoa. O
/// <paramref name="Payload"/> é opaco pra rede: quem define o formato é a interface.</summary>
public sealed record AnnotationMessage(
    int FromParticipantId, string FromNickname, int TargetParticipantId, StreamKind TargetKind, string Payload);

/// <summary>Versões e limites do protocolo da sala, num lugar só pra cliente e servidor
/// concordarem.</summary>
internal static class RoomFeatures
{
    /// <summary>Mídia com timestamp, Ping/Pong e assinatura seletiva (ver docs/PROTOCOL_V2.md).</summary>
    public const int TimedMediaVersion = 2;

    /// <summary>Senha, chat, anotações e voz.</summary>
    public const int Version = 3;

    public const int MaxChatLength = 2000;
    public const int MaxAnnotationBytes = 16 * 1024;
    public const int MaxAnnotationsPerSecond = 60;

    /// <summary>2 s de voz. Um pedaço normal tem 20–40 ms; acima disto é cliente com defeito.</summary>
    public const int MaxVoiceChunkBytes = 2 * VoiceFormat.SampleRate * VoiceFormat.Channels * 2;

    /// <summary>
    /// A senha nunca trafega: o servidor sorteia um nonce por tentativa de entrada e o cliente
    /// devolve HMAC-SHA256(chave = senha em UTF-8, mensagem = nonce). Como o nonce muda a cada
    /// conexão, capturar uma resposta não serve pra entrar depois. Não é proteção contra quem
    /// grampeia a rede e tenta adivinhar a senha offline (o resto do tráfego também não é
    /// cifrado) — o objetivo é só não deixar a senha passar em claro.
    /// </summary>
    public static byte[] PasswordProof(string password, byte[] nonce) =>
        System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password), nonce);

    /// <summary>Corta no limite do chat sem partir um par substituto (emoji) ao meio.</summary>
    public static string ClampChat(string text)
    {
        if (text.Length <= MaxChatLength)
            return text;
        var length = char.IsHighSurrogate(text[MaxChatLength - 1]) ? MaxChatLength - 1 : MaxChatLength;
        return text[..length];
    }

    public static bool AnnotationFits(string? payload) =>
        !string.IsNullOrEmpty(payload) && System.Text.Encoding.UTF8.GetByteCount(payload) <= MaxAnnotationBytes;
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

    // Campos do protocolo 3. Todos somem do JSON quando estão no valor padrão: assim as
    // mensagens que os clientes antigos recebem continuam byte a byte iguais às de antes, sem
    // depender de como cada um trata uma chave desconhecida.

    /// <summary>
    /// A maior versão que quem está entrando fala (só no Join). O cliente novo continua mandando
    /// <see cref="ProtocolVersion"/> = 2 e anuncia a 3 aqui, porque um servidor 1.0.x compara
    /// ProtocolVersion == 2: mandar 3 lá faria ele nos tratar como cliente legado (sem timestamp,
    /// sem Ping/Pong e invisível pro Android). Assim o servidor antigo ignora este campo e responde
    /// 2 como sempre; o novo responde 3.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxProtocolVersion { get; set; }

    /// <summary>Na descoberta: a sala tem senha. No JoinRejected: a recusa foi por causa da senha.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresPassword { get; set; }

    /// <summary>Nonce do desafio de senha, em Base64 (JoinChallenge).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Nonce { get; set; }

    /// <summary>Resposta ao desafio, em Base64 (JoinAuth).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Proof { get; set; }

    /// <summary>Texto do chat.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Text { get; set; }

    /// <summary>Horário em que o servidor recebeu a mensagem de chat (ms desde 1970, UTC).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long SentAtUnixMs { get; set; }

    /// <summary>Anotação: de quem é o stream anotado (o tipo dele vai em <see cref="StreamKind"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TargetParticipantId { get; set; }

    /// <summary>Conteúdo da anotação, opaco pra rede.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Payload { get; set; }

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
