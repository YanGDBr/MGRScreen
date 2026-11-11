using System.Buffers.Binary;
using System.Diagnostics;

namespace MGRScreen.Core.Networking;

internal static class TimedMedia
{
    // The outer framing stays identical to v1. The high bit marks the timed payload.
    public const int PrefixSize = 25;
    public const int Offset = MediaFraming.HeaderSize + PrefixSize;
    public const int MaxPayload = 4 * 1024 * 1024;
    public static long NowUs => (long)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));

    public static bool IsTimed(byte[] frame) => (frame[0] & 0x80) != 0;
    public static MediaChunkType Type(byte[] frame) => (MediaChunkType)(frame[0] & 0x7f);
    public static long Generation(byte[] frame) => BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(9));
    public static bool IsKeyframe(byte[] frame) => frame[33] == 1;

    public static byte[] Frame(MediaChunkType type, long generation, long sequence, long pts, bool keyframe, byte[] payload)
    {
        var frame = new byte[Offset + payload.Length];
        frame[0] = (byte)((byte)type | 0x80);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5), PrefixSize + payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(9), generation);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(17), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(25), pts);
        frame[33] = keyframe ? (byte)1 : (byte)0;
        payload.CopyTo(frame, Offset);
        return frame;
    }

    public static byte[] ToLegacy(byte[] frame) =>
        MediaFraming.Frame(Type(frame), MediaFraming.ReadPublisherId(frame), frame.AsSpan(Offset).ToArray());
}
