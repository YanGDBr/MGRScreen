using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MGRScreen.App.Views;

public partial class RoomWindow
{
    /// <summary>
    /// Pergunta o que fazer quando a pessoa clica no X da sala: fechar de vez, deixar em segundo
    /// plano, ou desistir. "Lembrar" grava a escolha nas configurações.
    /// </summary>
    private static class CloseChoiceDialog
    {
        public static (CloseChoice Choice, bool Remember) Ask(Window owner, string? warning)
        {
            var choice = CloseChoice.Cancel;
            T Res<T>(string key) => (T)Application.Current.FindResource(key);

            var remember = new CheckBox { Content = "Lembrar minha escolha", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

            var background = new Button { Content = "Manter em segundo plano", Tag = "", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
            var close = new Button { Content = "Fechar de vez", Tag = "", Style = Res<Style>("DangerButtonStyle"), Margin = new Thickness(8, 0, 0, 0) };
            var cancel = new Button { Content = "Cancelar", IsCancel = true, Style = Res<Style>("SecondaryButtonStyle") };

            var content = new StackPanel { Margin = new Thickness(24) };
            content.Children.Add(new TextBlock { Text = "Fechar o MGRScreen?", FontSize = 18, FontWeight = FontWeights.SemiBold });

            if (warning is not null)
            {
                content.Children.Add(new Border
                {
                    Background = Res<Brush>("DangerSoftBrush"),
                    BorderBrush = Res<Brush>("DangerBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 14, 0, 0),
                    Child = new TextBlock { Text = warning, FontSize = 12 },
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = "Em segundo plano o app sai da barra de tarefas e fica só no ícone perto do relógio, "
                     + "gastando bem menos: ele para de decodificar e desenhar o vídeo dos outros. A sua "
                     + "transmissão, o áudio, a voz e o chat continuam funcionando.",
                Style = Res<Style>("SubtitleTextStyle"),
                Margin = new Thickness(0, 14, 0, 0),
            });

            // Em linha própria: ao lado dos três botões o texto não cabia e ficava cortado.
            remember.Margin = new Thickness(0, 18, 0, 0);
            content.Children.Add(remember);
            content.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
                Children = { cancel, close, background },
            });

            var window = new Window
            {
                Title = "Fechar o MGRScreen",
                Width = 600,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Content = content,
            };
            if (owner.IsVisible)
            {
                window.Owner = owner;
            }

            background.Click += (_, _) =>
            {
                choice = CloseChoice.Background;
                window.DialogResult = true;
            };
            close.Click += (_, _) =>
            {
                choice = CloseChoice.Close;
                window.DialogResult = true;
            };

            window.ShowDialog();
            return (choice, remember.IsChecked == true);
        }
    }
}
