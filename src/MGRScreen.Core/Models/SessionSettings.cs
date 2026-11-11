namespace MGRScreen.Core.Models;

/// <summary>Parâmetros de um stream que este participante vai publicar.</summary>
public sealed class SessionSettings
{
    /// <summary>Resolução do que a captura entrega (tamanho nativo do monitor/janela).</summary>
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;

    /// <summary>Resolução que sai do encoder e trafega na rede. Se for menor que a de entrada,
    /// o ffmpeg reduz a imagem — é isso que faz a opção "720p" realmente economizar banda em vez
    /// de só baixar o bitrate. 0 = igual à entrada.</summary>
    public int OutputWidth { get; set; }
    public int OutputHeight { get; set; }

    public int Fps { get; set; } = 60;
    public int VideoBitrateKbps { get; set; } = 10_000;

    /// <summary>Nome do encoder ffmpeg escolhido (h264_nvenc/h264_amf/h264_qsv/libx264).</summary>
    public string VideoEncoder { get; set; } = "libx264";

    public bool NeedsScaling => OutputWidth > 0 && OutputHeight > 0
                                && (OutputWidth != Width || OutputHeight != Height);

    public int EffectiveWidth => OutputWidth > 0 ? OutputWidth : Width;
    public int EffectiveHeight => OutputHeight > 0 ? OutputHeight : Height;

    /// <summary>Reduz mantendo proporção até caber em <paramref name="maxHeight"/>, sempre com
    /// dimensões pares (exigência do yuv420p). Nunca aumenta.</summary>
    public static (int Width, int Height) ScaleToHeight(int width, int height, int maxHeight)
    {
        if (height <= maxHeight)
        {
            return (MakeEven(width), MakeEven(height));
        }

        var scale = (double)maxHeight / height;
        return (MakeEven((int)Math.Round(width * scale)), MakeEven(maxHeight));
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value + 1;
}
