using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MGRScreen.App.Views;

/// <summary>Caixinha que pede a senha da sala. Devolve null se a pessoa cancelar.</summary>
internal static class PasswordPrompt
{
    public static string? Ask(Window? owner, string message)
    {
        string? result = null;

        var box = new PasswordBox
        {
            FontSize = 14,
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Brush)Application.Current.FindResource("Surface2Brush"),
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush2"),
            CaretBrush = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
        };

        var ok = new Button { Content = "Entrar", IsDefault = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 96 };
        var cancel = new Button
        {
            Content = "Cancelar",
            IsCancel = true,
            MinWidth = 96,
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
        };

        var window = new Window
        {
            Title = "Senha da sala",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new TextBlock { Text = "Esta sala tem senha", FontSize = 16, FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = message,
                        Style = (Style)Application.Current.FindResource("CaptionTextStyle"),
                        Margin = new Thickness(0, 6, 0, 14),
                    },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 18, 0, 0),
                        Children = { cancel, ok },
                    },
                },
            },
        };

        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }

        ok.Click += (_, _) =>
        {
            result = box.Password;
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => Keyboard.Focus(box);

        return window.ShowDialog() == true && !string.IsNullOrEmpty(result) ? result : null;
    }
}
