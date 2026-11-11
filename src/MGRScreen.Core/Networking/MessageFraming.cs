using System.Net.Sockets;

namespace MGRScreen.Core.Networking;

/// <summary>Enquadramento simples "prefixo de tamanho (4 bytes, little-endian) + payload" para
/// mensagens de controle (JSON pequeno). O media channel NÃO usa isso — é um relay binário puro
/// do stream MPEG-TS (ver docs/ARCHITECTURE.md seção 1).</summary>
public static class MessageFraming
{
    private const int MaxMessageSize = 1024 * 1024; // 1 MB é mais que suficiente para JSON de controle

    public static async Task WriteMessageAsync(NetworkStream stream, byte[] payload, CancellationToken token = default)
    {
        var lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, token);
        await stream.WriteAsync(payload, token);
    }

    /// <summary>Retorna null se a conexão foi fechada de forma limpa antes de uma nova mensagem.</summary>
    public static async Task<byte[]?> ReadMessageAsync(NetworkStream stream, CancellationToken token = default)
    {
        var lengthPrefix = new byte[4];
        if (!await ReadExactAsync(stream, lengthPrefix, 4, token))
        {
            return null;
        }

        var length = BitConverter.ToInt32(lengthPrefix);
        if (length is < 0 or > MaxMessageSize)
        {
            throw new InvalidDataException($"Tamanho de mensagem inválido: {length}");
        }

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, length, token))
        {
            return null;
        }

        return payload;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
