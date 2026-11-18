using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MGRScreen.App.Theme;
using MGRScreen.Core.Networking;

namespace MGRScreen.App.Views;

/// <summary>Quem está na sala: o chip de cada pessoa na barra de cima.</summary>
public partial class RoomWindow
{
    /// <summary>Anel em volta do avatar de cada participante, pra acender enquanto a pessoa fala.</summary>
    private readonly Dictionary<int, Border> _speakingRings = new();

    private Border BuildParticipantChip(ParticipantInfo participant, bool isMe, bool isSharing)
    {
        var color = ThemeManager.ColorFor(participant.Nickname);
        var initial = participant.Nickname.Trim().Length > 0
            ? participant.Nickname.Trim()[..1].ToUpperInvariant()
            : "?";

        var avatar = new Grid
        {
            Width = 20,
            Height = 20,
            Children =
            {
                new Ellipse { Fill = new SolidColorBrush(color) },
                new TextBlock
                {
                    Text = initial,
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        // O anel fica transparente até a pessoa falar (ver RoomWindow.Voice.cs).
        var ring = new Border
        {
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(1),
            Child = avatar,
        };
        _speakingRings[participant.Id] = ring;

        var name = new TextBlock
        {
            Text = isMe ? $"{participant.Nickname} (você)" : participant.Nickname,
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = (Brush)FindResource(isSharing ? "TextPrimaryBrush" : "TextSecondaryBrush"),
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { ring, name } };

        if (isSharing)
        {
            content.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Margin = new Thickness(6, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("SuccessBrush"),
            });
        }

        return new Border
        {
            Background = (Brush)FindResource("Surface2Brush"),
            BorderBrush = (Brush)FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(3, 2, 10, 2),
            Margin = new Thickness(0, 0, 6, 4),
            ToolTip = isSharing ? "Compartilhando a tela" : "Na sala",
            Child = content,
        };
    }
}
