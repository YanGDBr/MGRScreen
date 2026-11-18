using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace MGRScreen.App.Views;

/// <summary>
/// Janela que recebe uma tela destacada da sala. Sem barra de título, pra sobrar só a imagem:
/// arrasta-se pela própria imagem e redimensiona-se pelas bordas. Com "manter por cima" ligado
/// vira um mini player que fica visível por cima de um jogo em janela sem bordas.
/// </summary>
internal sealed class TileWindow : Window
{
    private WindowState _previousState;

    public bool IsFullscreen { get; private set; }

    public TileWindow(string title, UIElement content)
    {
        Title = $"{title} — MGRScreen";
        Width = 800;
        Height = 450;
        MinWidth = 240;
        MinHeight = 135;
        Background = Brushes.Black;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/MGRScreen;component/Assets/mgrscreen.png"));

        // Remove a moldura do Windows mas mantém a borda de redimensionar.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        Content = content;

        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && IsFullscreen)
            {
                ToggleFullscreen();
            }
        };
    }

    public void ToggleFullscreen()
    {
        if (IsFullscreen)
        {
            WindowState = _previousState;
            IsFullscreen = false;
        }
        else
        {
            _previousState = WindowState;
            WindowState = WindowState.Normal; // garante a transição quando já está maximizada
            WindowState = WindowState.Maximized;
            IsFullscreen = true;
        }
    }
}
