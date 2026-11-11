namespace MGRScreen.Core.Networking;

public enum MediaChunkType : byte
{
    Video = 1,       // vídeo da tela
    Audio = 2,
    CameraVideo = 3, // vídeo da câmera (sem áudio próprio: a voz de todo mundo já vai pelo Discord)
}

/// <summary>
/// Enquadramento do media channel: <c>[1 byte tipo][4 bytes id de quem publicou][4 bytes tamanho][payload]</c>.
///
/// O mesmo socket carrega vídeo (H.264 cru) e áudio (PCM 16-bit) de VÁRIOS participantes ao
/// mesmo tempo — daí a necessidade do id: o cliente precisa saber pra qual "tile" (e pra qual
/// decoder) mandar cada pedaço de bytes.
///
/// No sentido cliente -> servidor o id é ignorado (o servidor sabe quem está mandando pelo
/// socket); no sentido servidor -> cliente ele é preenchido com o id de quem originou.
/// </summary>
public static class MediaFraming
{
    public const int HeaderSize = 9;

    public static byte[] Frame(MediaChunkType type, int publisherId, byte[] payload)
    {
        var framed = new byte[HeaderSize + payload.Length];
        framed[0] = (byte)type;
        BitConverter.TryWriteBytes(framed.AsSpan(1, 4), publisherId);
        BitConverter.TryWriteBytes(framed.AsSpan(5, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, framed, HeaderSize, payload.Length);
        return framed;
    }

    /// <summary>Reescreve o id no cabeçalho de um frame já montado — o servidor usa isso pra
    /// carimbar quem originou antes de repassar, sem precisar remontar o array.</summary>
    public static void StampPublisher(byte[] framed, int publisherId)
    {
        BitConverter.TryWriteBytes(framed.AsSpan(1, 4), publisherId);
    }

    public static MediaChunkType ReadType(byte[] header) => (MediaChunkType)header[0];

    public static bool IsVideo(MediaChunkType type) => type is MediaChunkType.Video or MediaChunkType.CameraVideo;

    public static MediaChunkType VideoTypeFor(StreamKind kind) =>
        kind == StreamKind.Camera ? MediaChunkType.CameraVideo : MediaChunkType.Video;

    public static int ReadPublisherId(byte[] header) => BitConverter.ToInt32(header, 1);

    public static int ReadLength(byte[] header) => BitConverter.ToInt32(header, 5);
}
