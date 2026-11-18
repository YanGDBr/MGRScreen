using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MGRScreen.App.Views;

/// <summary>Avisos rápidos no canto da sala: somem sozinhos e não roubam o foco.</summary>
public partial class RoomWindow
{
    private const int MaxToasts = 4;

    /// <summary>Mostra um aviso por alguns segundos. Clicar nele executa <paramref name="onClick"/>
    /// (abrir a pasta da gravação, por exemplo) e o fecha.</summary>
    private void ShowToast(string text, string glyph = "", Action? onClick = null)
    {
        while (ToastHost.Children.Count >= MaxToasts)
        {
            ToastHost.Children.RemoveAt(0);
        }

        var toast = new Border
        {
            Background = (Brush)FindResource("Surface2Brush"),
            BorderBrush = (Brush)FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = 0,
            Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = glyph,
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 15,
                        Foreground = (Brush)FindResource("AccentBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0),
                    },
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 12,
                        MaxWidth = 250,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(onClick is null ? 4.5 : 7) };

        void Dismiss()
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => ToastHost.Children.Remove(toast);
            toast.BeginAnimation(OpacityProperty, fade);
        }

        timer.Tick += (_, _) => Dismiss();
        toast.MouseLeftButtonUp += (_, _) =>
        {
            onClick?.Invoke();
            Dismiss();
        };

        ToastHost.Children.Add(toast);
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        timer.Start();
    }

    /// <summary>
    /// Aviso de algo que aconteceu na sala (alguém entrou, começou a compartilhar...). Respeita a
    /// opção de notificações; com a janela minimizada ou em segundo plano, vai pro balão da
    /// bandeja em vez de aparecer onde ninguém está olhando.
    /// </summary>
    private void NotifyEvent(string text, string glyph = "")
    {
        if (!_settings.Notifications)
        {
            return;
        }

        if (!IsActive || WindowState == WindowState.Minimized || !IsVisible)
        {
            ShowTrayBalloon(text);
        }

        ShowToast(text, glyph);
    }
}
