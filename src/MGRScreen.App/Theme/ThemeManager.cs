using System.Windows;
using System.Windows.Media;

namespace MGRScreen.App.Theme;

/// <summary>
/// Troca a cor de destaque do app em tempo de execução. A paleta escura é fixa (Theme.xaml); só o
/// destaque e as três variações dele (hover, pressionado, fundo suave) são recalculados a partir
/// da cor escolhida. Os estilos referenciam esses pincéis como DynamicResource, então a troca
/// aparece na hora em todas as janelas abertas.
/// </summary>
public static class ThemeManager
{
    public const string DefaultAccent = "#7C5CFF";

    public static readonly (string Name, string Hex)[] Presets =
    {
        ("Roxo", DefaultAccent),
        ("Azul", "#3B82F6"),
        ("Ciano", "#06B6D4"),
        ("Verde", "#22C55E"),
        ("Amarelo", "#EAB308"),
        ("Laranja", "#F97316"),
        ("Vermelho", "#EF4444"),
        ("Rosa", "#EC4899"),
    };

    private static readonly Color Background = Color.FromRgb(0x0E, 0x10, 0x13);

    public static Color Accent { get; private set; } = Parse(DefaultAccent)!.Value;

    public static Color? Parse(string? hex)
    {
        try
        {
            return string.IsNullOrWhiteSpace(hex) ? null : (Color)ColorConverter.ConvertFromString(hex.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static void Apply(string? hex)
    {
        var accent = Parse(hex) ?? Parse(DefaultAccent)!.Value;
        accent.A = 255;
        Accent = accent;

        var resources = Application.Current.Resources;
        resources["AccentBrush"] = Frozen(accent);
        resources["AccentHoverBrush"] = Frozen(Mix(accent, Colors.White, 0.14));
        resources["AccentPressedBrush"] = Frozen(Mix(accent, Colors.Black, 0.16));
        resources["AccentSoftBrush"] = Frozen(Mix(Background, accent, 0.20));
    }

    /// <summary>Cor estável por pessoa (avatar, borda do desenho), derivada do nickname.</summary>
    public static Color ColorFor(string nickname)
    {
        // Hash próprio: string.GetHashCode muda a cada execução do processo.
        var hash = 17;
        foreach (var c in nickname)
        {
            hash = unchecked((hash * 31) + char.ToLowerInvariant(c));
        }

        var palette = AvatarPalette;
        return palette[(int)((uint)hash % (uint)palette.Length)];
    }

    private static readonly Color[] AvatarPalette =
    {
        Color.FromRgb(0x7C, 0x5C, 0xFF), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x06, 0xB6, 0xD4),
        Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xEA, 0xB3, 0x08), Color.FromRgb(0xF9, 0x73, 0x16),
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0xA8, 0x55, 0xF7),
        Color.FromRgb(0x84, 0xCC, 0x16),
    };

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + ((to.R - from.R) * amount)),
        (byte)(from.G + ((to.G - from.G) * amount)),
        (byte)(from.B + ((to.B - from.B) * amount)));
}
