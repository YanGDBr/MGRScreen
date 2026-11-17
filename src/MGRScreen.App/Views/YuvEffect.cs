using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MGRScreen.Core.Capture;

namespace MGRScreen.App.Views;

/// <summary>
/// Converte YUV 4:2:0 em RGB na placa de vídeo, na hora de desenhar o tile. A imagem em si é o
/// plano Y (um bitmap em tons de cinza); os planos U e V entram como texturas extras em meia
/// resolução. Assim o frame decodificado nunca é convertido pra BGRA na CPU.
///
/// BT.601 faixa limitada, o inverso exato do que a captura e o ffmpeg aplicam na ida.
/// ps_2_0 de propósito: é o único perfil que o WPF também roda sem aceleração (área de trabalho
/// remota, driver de vídeo básico).
/// </summary>
public sealed class YuvEffect : ShaderEffect
{
    private const string Source = """
        sampler2D ySampler : register(s0);
        sampler2D uSampler : register(s1);
        sampler2D vSampler : register(s2);

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float y = (tex2D(ySampler, uv).r - 0.0627451) * 1.1643836;
            float u = tex2D(uSampler, uv).r - 0.5019608;
            float v = tex2D(vSampler, uv).r - 0.5019608;
            float3 rgb = float3(y + 1.5960268 * v, y - 0.3917623 * u - 0.8129676 * v, y + 2.0172321 * u);
            return float4(saturate(rgb), 1);
        }
        """;

    private static readonly Lazy<PixelShader?> Shader = new(() =>
    {
        try
        {
            var shader = new PixelShader();
            shader.SetStreamSource(new MemoryStream(HlslCompiler.Compile(Source, "main", "ps_2_0")));
            shader.Freeze();
            return shader;
        }
        catch (Exception)
        {
            return null; // sem compilador de shader: IsAvailable fica false e o tile avisa
        }
    });

    /// <summary>False se o shader não pôde ser compilado nesta máquina.</summary>
    public static bool IsAvailable => Shader.Value is not null;

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(YuvEffect), 0);

    public static readonly DependencyProperty UProperty =
        RegisterPixelShaderSamplerProperty("U", typeof(YuvEffect), 1);

    public static readonly DependencyProperty VProperty =
        RegisterPixelShaderSamplerProperty("V", typeof(YuvEffect), 2);

    public YuvEffect()
    {
        PixelShader = Shader.Value;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(UProperty);
        UpdateShaderValue(VProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Brush U
    {
        get => (Brush)GetValue(UProperty);
        set => SetValue(UProperty, value);
    }

    public Brush V
    {
        get => (Brush)GetValue(VProperty);
        set => SetValue(VProperty, value);
    }
}
