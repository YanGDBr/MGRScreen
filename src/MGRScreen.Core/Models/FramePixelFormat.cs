namespace MGRScreen.Core.Models;

/// <summary>Como os pixels de um frame cru estão organizados no array de bytes.</summary>
public enum FramePixelFormat
{
    /// <summary>4 bytes por pixel. É o que a câmera entrega.</summary>
    Bgra,

    /// <summary>Plano Y em resolução cheia seguido de um plano UV intercalado em meia resolução
    /// (1,5 byte por pixel). É o que a captura de tela entrega depois de converter na GPU.</summary>
    Nv12,

    /// <summary>Planos Y, U e V separados, U e V em meia resolução (1,5 byte por pixel). É o que
    /// o decoder entrega.</summary>
    Yuv420p,
}

public static class FrameLayout
{
    public static int ChromaWidth(int width) => (width + 1) / 2;
    public static int ChromaHeight(int height) => (height + 1) / 2;

    public static int ByteCount(FramePixelFormat format, int width, int height) => format == FramePixelFormat.Bgra
        ? width * height * 4
        : (width * height) + (2 * ChromaWidth(width) * ChromaHeight(height));

    public static string FfmpegName(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Nv12 => "nv12",
        FramePixelFormat.Yuv420p => "yuv420p",
        _ => "bgra",
    };
}
