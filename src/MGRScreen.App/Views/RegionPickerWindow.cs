using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MGRScreen.App.Views;

/// <summary>
/// Deixa o usuário escolher um pedaço do monitor pra compartilhar: um véu escuro cobre a tela e
/// ele arrasta um retângulo. Janela feita só em código (sem XAML) pra ser uma peça solta.
///
/// O cuidado aqui é com unidade: o WPF mede em DIPs e o resultado tem que sair em pixels físicos
/// do monitor, que pode ter escala diferente da do monitor onde o app está. Por isso (1) a janela
/// é colocada com SetWindowPos em pixels físicos e (2) a conversão usa a proporção entre o
/// tamanho físico do monitor e o tamanho que o WPF enxerga pra janela que o cobre — que é a
/// escala real, valha o que o app declarar de DPI.
/// </summary>
public sealed class RegionPickerWindow : Window
{
    private const int MinSize = 64;
    // Abaixo disso, soltar o mouse foi um clique sem querer, não uma seleção.
    private const int MinDragPixels = 8;

    private readonly int _monitorLeft;
    private readonly int _monitorTop;
    private readonly int _monitorWidth;
    private readonly int _monitorHeight;

    private readonly Canvas _canvas = new();
    private readonly RectangleGeometry _veilArea = new();
    private readonly RectangleGeometry _veilHole = new();
    private readonly Rectangle _outline;
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeText;

    private bool _dragging;
    private Point _dragStart;
    private Rect _selection;
    private Int32Rect? _result;

    /// <summary>
    /// Modal. Cobre o monitor indicado com um véu escuro; o usuário arrasta um retângulo;
    /// Enter/soltar confirma, Esc cancela. Devolve o retângulo em PIXELS FÍSICOS relativos ao
    /// canto superior esquerdo do monitor, com largura e altura pares e no mínimo 64x64; null se
    /// cancelou.
    /// </summary>
    public static Int32Rect? Pick(IntPtr monitorHandle)
    {
        if (!TryGetMonitorBounds(monitorHandle, out var bounds))
        {
            return null; // monitor desconectado entre a escolha e a chamada
        }

        var window = new RegionPickerWindow(bounds);
        window.ShowDialog();
        return window._result;
    }

    private RegionPickerWindow(RECT bounds)
    {
        _monitorLeft = bounds.Left;
        _monitorTop = bounds.Top;
        _monitorWidth = bounds.Right - bounds.Left;
        _monitorHeight = bounds.Bottom - bounds.Top;

        Title = "Escolher região";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        // Quase transparente, e não Transparent: numa janela em camadas, pixel com alfa zero
        // deixa o clique passar pra janela de baixo — e o buraco da seleção é exatamente isso.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;

        // Tamanho provisório; o de verdade é aplicado em pixels físicos assim que existe handle.
        Left = 0;
        Top = 0;
        Width = 200;
        Height = 200;

        // O véu é "a tela toda menos a seleção", pra região escolhida aparecer com a cor real.
        var veil = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, _veilArea, _veilHole),
        };

        _outline = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true,
        };

        _sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold };
        _sizeLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x14, 0x18)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 4),
            Child = _sizeText,
            Visibility = Visibility.Collapsed,
        };

        _canvas.Children.Add(veil);
        _canvas.Children.Add(_outline);
        _canvas.Children.Add(_sizeLabel);

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x14, 0x18)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 8, 16, 9),
            Margin = new Thickness(0, 28, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Arraste para escolher a região · Esc cancela",
                Foreground = Brushes.White,
                FontSize = 15,
            },
        };

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(hint);
        Content = root;

        _canvas.SizeChanged += (_, e) => _veilArea.Rect = new Rect(e.NewSize);

        // Em três momentos porque o WPF pode desfazer a posição: ao mudar pra um monitor de outra
        // escala o Windows manda WM_DPICHANGED com um tamanho "sugerido" e o WPF o aplica por
        // cima do nosso; e ao mostrar a janela ele reaplica o que acha que é o Left/Top.
        SourceInitialized += (_, _) => CoverMonitor();
        Loaded += (_, _) =>
        {
            CoverMonitor();
            Activate();
            Focus();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(CoverMonitor));
    }

    private void CoverMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        RunWithPhysicalCoordinates(() => SetWindowPos(
            hwnd, HWND_TOPMOST, _monitorLeft, _monitorTop, _monitorWidth, _monitorHeight, SWP_NOACTIVATE));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        _dragStart = ClampToCanvas(e.GetPosition(_canvas));
        _dragging = true;
        CaptureMouse();
        UpdateSelection(_dragStart);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            UpdateSelection(ClampToCanvas(e.GetPosition(_canvas)));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragging)
        {
            UpdateSelection(ClampToCanvas(e.GetPosition(_canvas)));
            ConfirmOrReset();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Cancel();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Alt+Tab no meio do arrasto: sem isto a seleção ficaria "grudada" no mouse solto.
        if (_dragging)
        {
            ClearSelection();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
        else if (e.Key == Key.Enter && _dragging)
        {
            e.Handled = true;
            ConfirmOrReset();
        }
    }

    private void ConfirmOrReset()
    {
        var (scaleX, scaleY) = GetScale();

        if (_selection.Width * scaleX < MinDragPixels || _selection.Height * scaleY < MinDragPixels)
        {
            ClearSelection();
            return;
        }

        _result = ToMonitorPixels(_selection);
        _dragging = false;
        ReleaseMouseCapture();
        Close();
    }

    private void Cancel()
    {
        _result = null;
        _dragging = false;
        ReleaseMouseCapture();
        Close();
    }

    private void ClearSelection()
    {
        _dragging = false;
        ReleaseMouseCapture();
        _selection = default;
        _veilHole.Rect = default;
        _outline.Visibility = Visibility.Collapsed;
        _sizeLabel.Visibility = Visibility.Collapsed;
    }

    private void UpdateSelection(Point current)
    {
        _selection = new Rect(_dragStart, current);
        _veilHole.Rect = _selection;

        Canvas.SetLeft(_outline, _selection.X);
        Canvas.SetTop(_outline, _selection.Y);
        _outline.Width = _selection.Width;
        _outline.Height = _selection.Height;
        _outline.Visibility = Visibility.Visible;

        // Mostra o que vai ser devolvido de fato (já par e com o mínimo), não a conta crua.
        var pixels = ToMonitorPixels(_selection);
        _sizeText.Text = $"{pixels.Width} × {pixels.Height}";
        _sizeLabel.Visibility = Visibility.Visible;
        _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var label = _sizeLabel.DesiredSize;

        // Logo abaixo do canto inferior direito; se não couber na tela, vai pra dentro.
        var left = _selection.Right - label.Width;
        var top = _selection.Bottom + 6;
        if (top + label.Height > _canvas.ActualHeight)
        {
            top = _selection.Bottom - label.Height - 6;
        }

        Canvas.SetLeft(_sizeLabel, Math.Clamp(left, 0, Math.Max(0, _canvas.ActualWidth - label.Width)));
        Canvas.SetTop(_sizeLabel, Math.Max(0, top));
    }

    private Point ClampToCanvas(Point point) => new(
        Math.Clamp(point.X, 0, _canvas.ActualWidth),
        Math.Clamp(point.Y, 0, _canvas.ActualHeight));

    /// <summary>Pixels físicos do monitor por DIP desta janela, em cada eixo.</summary>
    private (double X, double Y) GetScale()
    {
        if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            // Ainda sem layout: cai na escala que o WPF informa pra janela.
            var dpi = VisualTreeHelper.GetDpi(this);
            return (dpi.DpiScaleX, dpi.DpiScaleY);
        }

        // A janela cobre o monitor inteiro, então a largura dela em DIPs equivale à largura do
        // monitor em pixels. Dá o mesmo que VisualTreeHelper.GetDpi quando o app acompanha a
        // escala de cada monitor, e continua certo quando não acompanha (aí o Windows estica a
        // janela e o DPI informado pelo WPF não é o do monitor).
        return (_monitorWidth / _canvas.ActualWidth, _monitorHeight / _canvas.ActualHeight);
    }

    private Int32Rect ToMonitorPixels(Rect selection)
    {
        var (scaleX, scaleY) = GetScale();

        var (x, width) = FitAxis(selection.X * scaleX, selection.Width * scaleX, _monitorWidth);
        var (y, height) = FitAxis(selection.Y * scaleY, selection.Height * scaleY, _monitorHeight);

        return new Int32Rect(x, y, width, height);
    }

    // Encoders H.264 querem dimensões pares, e abaixo de 64 vários encoders de GPU nem iniciam.
    private static (int Start, int Length) FitAxis(double start, double length, int monitorLength)
    {
        var max = monitorLength & ~1;
        var size = Math.Clamp((int)Math.Round(length) & ~1, Math.Min(MinSize, max), max);
        // Se o mínimo fez o retângulo crescer pra fora do monitor, ele é empurrado de volta.
        var position = Math.Clamp((int)Math.Round(start), 0, monitorLength - size);
        return (position, size);
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    private static bool TryGetMonitorBounds(IntPtr monitorHandle, out RECT bounds)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        var ok = false;
        RunWithPhysicalCoordinates(() => ok = GetMonitorInfo(monitorHandle, ref info));

        bounds = info.Monitor;
        return ok && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    /// <summary>
    /// Se o processo não acompanha a escala de cada monitor, o Windows "mente" as coordenadas dos
    /// monitores de escala diferente (devolve e aceita valores já escalados). Trocar o contexto
    /// de DPI da thread durante a chamada faz as duas funções falarem em pixels físicos de
    /// verdade, seja qual for o modo do app.
    /// </summary>
    private static void RunWithPhysicalCoordinates(Action action)
    {
        var previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            action();
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }
}
