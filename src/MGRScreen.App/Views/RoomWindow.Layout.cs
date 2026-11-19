using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MGRScreen.Core.Networking;

namespace MGRScreen.App.Views;

/// <summary>
/// Onde cada tela fica na sala. Três modos:
///  - automático: grade calculada pra aproveitar a área;
///  - foco: uma tela grande e as outras em miniatura ao lado;
///  - livre: cada tela onde a pessoa deixou, com o tamanho que ela quis.
/// Também cuida de tela cheia e de destacar uma tela em outra janela.
/// </summary>
public partial class RoomWindow
{
    private enum LayoutMode { Auto, Focus, Free }

    private const double TileCornerRadius = 12;
    private const double TileGap = 8;
    private const double MinTileWidth = 200;
    private const double MinTileHeight = 120;
    private const double SnapDistance = 10;

    private LayoutMode _layoutMode = LayoutMode.Auto;

    /// <summary>Qual tela é a grande no modo foco (chave do tile). Se sumir, vale a primeira.</summary>
    private int _focusKey = int.MinValue;
    private int _topZ;

    // Arraste em andamento (mover uma tela, ou mover a imagem ampliada dentro dela).
    private StreamTile? _dragTile;
    private Point _dragStart;
    private Rect _dragStartRect;
    private Point _dragStartPan;
    private bool _dragMoved;
    private bool _dragIsPan;

    private void InitializeLayout()
    {
        _layoutMode = _settings.LayoutMode switch
        {
            "focus" => LayoutMode.Focus,
            "free" => LayoutMode.Free,
            _ => LayoutMode.Auto,
        };

        TilesArea.SizeChanged += (_, _) => RelayoutTiles();
        UpdateLayoutMenu();
    }

    private IEnumerable<StreamTile> StageTiles =>
        _tiles.Values.Where(t => t.PopOut is null && t != _fullscreenTile).OrderBy(t => t.Key < 0 ? int.MaxValue + (long)t.Key : t.Key);

    /// <summary>Recalcula posição e tamanho de todas as telas que estão no palco.</summary>
    private void RelayoutTiles()
    {
        EmptyStatePanel.Visibility = _tiles.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

        var stage = new Size(TilesArea.ActualWidth, TilesArea.ActualHeight);
        if (stage.Width < 40 || stage.Height < 40)
        {
            return;
        }

        var tiles = StageTiles.ToList();
        if (tiles.Count == 0)
        {
            return;
        }

        switch (_layoutMode)
        {
            case LayoutMode.Focus:
                LayoutFocus(tiles, stage);
                break;

            case LayoutMode.Free:
                LayoutFree(tiles, stage);
                break;

            default:
                var rects = GridRects(tiles.Count, stage);
                for (var i = 0; i < tiles.Count; i++)
                {
                    PlaceTile(tiles[i], rects[i]);
                }
                break;
        }
    }

    /// <summary>
    /// Grade que dá o maior tamanho possível pras telas em 16:9: testa de 1 a N colunas e fica
    /// com a que rende o tile mais largo. A última linha, se incompleta, fica centralizada.
    /// </summary>
    private static Rect[] GridRects(int count, Size stage)
    {
        var best = (Columns: 1, Width: 0.0);
        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (int)Math.Ceiling((double)count / columns);
            var cellWidth = (stage.Width - (TileGap * (columns - 1))) / columns;
            var cellHeight = (stage.Height - (TileGap * (rows - 1))) / rows;
            var width = Math.Min(cellWidth, cellHeight * 16 / 9);
            if (width > best.Width)
            {
                best = (columns, width);
            }
        }

        var tileWidth = Math.Max(MinTileWidth, best.Width);
        var tileHeight = tileWidth * 9 / 16;
        var cols = best.Columns;
        var rowCount = (int)Math.Ceiling((double)count / cols);
        var totalHeight = (rowCount * tileHeight) + (TileGap * (rowCount - 1));
        var top = Math.Max(0, (stage.Height - totalHeight) / 2);

        var rects = new Rect[count];
        for (var i = 0; i < count; i++)
        {
            var row = i / cols;
            var inRow = Math.Min(cols, count - (row * cols));
            var rowWidth = (inRow * tileWidth) + (TileGap * (inRow - 1));
            var left = Math.Max(0, (stage.Width - rowWidth) / 2);
            rects[i] = new Rect(
                left + ((i % cols) * (tileWidth + TileGap)),
                top + (row * (tileHeight + TileGap)),
                tileWidth,
                tileHeight);
        }

        return rects;
    }

    private void LayoutFocus(List<StreamTile> tiles, Size stage)
    {
        var focus = tiles.FirstOrDefault(t => t.Key == _focusKey)
                    ?? tiles.FirstOrDefault(t => !t.IsLocal && t.Kind == StreamKind.Screen)
                    ?? tiles[0];
        var others = tiles.Where(t => t != focus).ToList();

        if (others.Count == 0)
        {
            PlaceTile(focus, new Rect(0, 0, stage.Width, stage.Height));
            return;
        }

        var stripWidth = Math.Clamp(stage.Width * 0.22, 180, 320);
        PlaceTile(focus, new Rect(0, 0, stage.Width - stripWidth - TileGap, stage.Height));

        // Miniaturas empilhadas à direita; se não couberem em 16:9, encolhem na altura.
        var height = Math.Min(stripWidth * 9 / 16, (stage.Height - (TileGap * (others.Count - 1))) / others.Count);
        for (var i = 0; i < others.Count; i++)
        {
            PlaceTile(others[i], new Rect(stage.Width - stripWidth, i * (height + TileGap), stripWidth, height));
        }
    }

    private void LayoutFree(List<StreamTile> tiles, Size stage)
    {
        // Quem ainda não tem lugar definido (acabou de entrar) recebe o da grade automática.
        Rect[]? fallback = null;

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];

            if (tile.FreeRect is null && _settings.FreeLayout.TryGetValue(tile.LayoutKey, out var saved) && saved.Length == 4)
            {
                tile.FreeRect = new Rect(saved[0], saved[1], saved[2], saved[3]);
            }

            if (tile.FreeRect is null)
            {
                fallback ??= GridRects(tiles.Count, stage);
                tile.FreeRect = Normalize(fallback[i], stage);
            }

            var n = tile.FreeRect.Value;
            var rect = new Rect(n.X * stage.Width, n.Y * stage.Height, n.Width * stage.Width, n.Height * stage.Height);
            PlaceTile(tile, ClampToStage(rect, stage));
        }
    }

    private static Rect Normalize(Rect rect, Size stage) =>
        new(rect.X / stage.Width, rect.Y / stage.Height, rect.Width / stage.Width, rect.Height / stage.Height);

    private static Rect ClampToStage(Rect rect, Size stage)
    {
        var width = Math.Clamp(rect.Width, Math.Min(MinTileWidth, stage.Width), stage.Width);
        var height = Math.Clamp(rect.Height, Math.Min(MinTileHeight, stage.Height), stage.Height);
        var x = Math.Clamp(rect.X, 0, stage.Width - width);
        var y = Math.Clamp(rect.Y, 0, stage.Height - height);
        return new Rect(x, y, width, height);
    }

    private static void PlaceTile(StreamTile tile, Rect rect)
    {
        tile.StageRect = rect;
        Canvas.SetLeft(tile.Root, rect.X);
        Canvas.SetTop(tile.Root, rect.Y);
        tile.Root.Width = rect.Width;
        tile.Root.Height = rect.Height;
    }

    // ---------------------------------------------------------------- troca de modo

    private void SetLayoutMode(LayoutMode mode, bool keepCurrentPositions = false)
    {
        if (mode == LayoutMode.Free && keepCurrentPositions)
        {
            // Passando pro modo livre por ter arrastado algo: todo mundo fica exatamente onde
            // está na tela agora, senão as outras telas pulariam de lugar no meio do arraste.
            var stage = new Size(TilesArea.ActualWidth, TilesArea.ActualHeight);
            foreach (var tile in StageTiles)
            {
                tile.FreeRect = Normalize(tile.StageRect, stage);
            }
        }

        _layoutMode = mode;
        _settings.LayoutMode = mode switch
        {
            LayoutMode.Focus => "focus",
            LayoutMode.Free => "free",
            _ => "auto",
        };
        _settings.Save();

        UpdateLayoutMenu();
        RelayoutTiles();
    }

    private void UpdateLayoutMenu()
    {
        void Mark(Button item, bool selected) =>
            item.Foreground = (Brush)FindResource(selected ? "AccentBrush" : "TextPrimaryBrush");

        Mark(LayoutAutoItem, _layoutMode == LayoutMode.Auto);
        Mark(LayoutFocusItem, _layoutMode == LayoutMode.Focus);
        Mark(LayoutFreeItem, _layoutMode == LayoutMode.Free);
    }

    private void SaveFreeLayout()
    {
        foreach (var tile in _tiles.Values)
        {
            if (tile.FreeRect is { } rect)
            {
                _settings.FreeLayout[tile.LayoutKey] = [rect.X, rect.Y, rect.Width, rect.Height];
            }
        }

        _settings.Save();
    }

    private void OnLayoutButtonClick(object sender, RoutedEventArgs e) => LayoutPopup.IsOpen = true;

    private void OnLayoutAutoClick(object sender, RoutedEventArgs e)
    {
        LayoutPopup.IsOpen = false;
        SetLayoutMode(LayoutMode.Auto);
    }

    private void OnLayoutFocusClick(object sender, RoutedEventArgs e)
    {
        LayoutPopup.IsOpen = false;
        SetLayoutMode(LayoutMode.Focus);
    }

    private void OnLayoutFreeClick(object sender, RoutedEventArgs e)
    {
        LayoutPopup.IsOpen = false;
        SetLayoutMode(LayoutMode.Free);
    }

    // ---------------------------------------------------------------- arrastar e redimensionar

    /// <summary>Liga mouse (mover / ampliar / duplo clique) e as oito alças de redimensionar.</summary>
    private void AttachLayoutBehavior(StreamTile tile, Grid tileGrid)
    {
        tile.Root.MouseLeftButtonDown += (_, e) => OnTileMouseDown(tile, e);
        tile.Root.MouseMove += (_, e) => OnTileMouseMove(tile, e);
        tile.Root.MouseLeftButtonUp += (_, e) => OnTileMouseUp(tile, e);

        // (horizontal, vertical): -1 = borda esquerda/superior, +1 = direita/inferior, 0 = não mexe.
        (int H, int V, Cursor Cursor)[] handles =
        {
            (-1, -1, Cursors.SizeNWSE), (0, -1, Cursors.SizeNS), (1, -1, Cursors.SizeNESW),
            (-1, 0, Cursors.SizeWE), (1, 0, Cursors.SizeWE),
            (-1, 1, Cursors.SizeNESW), (0, 1, Cursors.SizeNS), (1, 1, Cursors.SizeNWSE),
        };

        var grips = new List<Thumb>();
        foreach (var (h, v, cursor) in handles)
        {
            var grip = new Thumb
            {
                Cursor = cursor,
                Opacity = 0, // invisível: só a área de clique e o cursor
                IsHitTestVisible = false,
                HorizontalAlignment = h < 0 ? HorizontalAlignment.Left : h > 0 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                VerticalAlignment = v < 0 ? VerticalAlignment.Top : v > 0 ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
                Width = h == 0 ? double.NaN : (v == 0 ? 6 : 14),
                Height = v == 0 ? double.NaN : (h == 0 ? 6 : 14),
                Margin = new Thickness(h == 0 ? 14 : 0, v == 0 ? 14 : 0, h == 0 ? 14 : 0, v == 0 ? 14 : 0),
            };
            Panel.SetZIndex(grip, 10);

            Rect startRect = default;
            Point startMouse = default;

            grip.DragStarted += (_, _) =>
            {
                if (!IsOnStage(tile))
                {
                    return;
                }

                BringToFront(tile);
                if (_layoutMode != LayoutMode.Free)
                {
                    SetLayoutMode(LayoutMode.Free, keepCurrentPositions: true);
                }

                startRect = tile.StageRect;
                startMouse = Mouse.GetPosition(TilesGrid);
            };

            grip.DragDelta += (_, _) =>
            {
                if (!IsOnStage(tile) || startRect.IsEmpty || startRect.Width == 0)
                {
                    return;
                }

                // Posição absoluta do mouse em vez do delta que o Thumb informa: o Thumb se move
                // junto com a borda, e com o encaixe o delta relativo fica preso num vai-e-vem.
                var mouse = Mouse.GetPosition(TilesGrid);
                ResizeTile(tile, startRect, mouse.X - startMouse.X, mouse.Y - startMouse.Y, h, v);
            };

            grip.DragCompleted += (_, _) =>
            {
                if (IsOnStage(tile))
                {
                    CommitTileRect(tile);
                }

                startRect = default;
            };

            tileGrid.Children.Add(grip);
            grips.Add(grip);
        }

        tile.Grips = grips.ToArray();
    }

    private bool IsOnStage(StreamTile tile) => tile.PopOut is null && tile != _fullscreenTile;

    private void BringToFront(StreamTile tile) => Panel.SetZIndex(tile.Root, ++_topZ);

    private void OnTileMouseDown(StreamTile tile, MouseButtonEventArgs e)
    {
        if (tile.DrawMode)
        {
            return; // o modo de desenho trata o mouse sozinho
        }

        // Duplo clique alterna tela cheia (é o gesto que todo mundo tenta).
        if (e.ClickCount == 2)
        {
            _dragTile = null;
            tile.Root.ReleaseMouseCapture();
            ToggleFullscreen(tile);
            e.Handled = true;
            return;
        }

        _dragTile = tile;
        _dragMoved = false;
        _dragIsPan = tile.Zoom.ScaleX > 1;
        _dragStartPan = new Point(tile.Pan.X, tile.Pan.Y);

        if (_dragIsPan)
        {
            _dragStart = e.GetPosition(tile.PictureClip);
        }
        else if (tile.PopOut is not null)
        {
            // Janela destacada: arrastar a imagem move a janela (ela não tem barra de título).
            _dragTile = null;
            if (tile.PopOut.WindowState == WindowState.Normal)
            {
                tile.PopOut.DragMove();
            }
            return;
        }
        else if (IsOnStage(tile))
        {
            _dragStart = e.GetPosition(TilesGrid);
            _dragStartRect = tile.StageRect;
            BringToFront(tile);
        }
        else
        {
            _dragTile = null;
            return;
        }

        tile.Root.CaptureMouse();
    }

    private void OnTileMouseMove(StreamTile tile, MouseEventArgs e)
    {
        if (_dragTile != tile || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (_dragIsPan)
        {
            var position = e.GetPosition(tile.PictureClip);
            tile.Pan.X = _dragStartPan.X + (position.X - _dragStart.X);
            tile.Pan.Y = _dragStartPan.Y + (position.Y - _dragStart.Y);
            ClampPan(tile);
            _dragMoved = true;
            return;
        }

        var mouse = e.GetPosition(TilesGrid);
        var dx = mouse.X - _dragStart.X;
        var dy = mouse.Y - _dragStart.Y;

        // Só vira arraste depois de alguns pixels: um clique com a mão tremendo não deve tirar
        // a sala do modo automático.
        if (!_dragMoved && Math.Abs(dx) < 5 && Math.Abs(dy) < 5)
        {
            return;
        }

        if (!_dragMoved)
        {
            _dragMoved = true;
            if (_layoutMode != LayoutMode.Free)
            {
                SetLayoutMode(LayoutMode.Free, keepCurrentPositions: true);
            }
        }

        var stage = new Size(TilesArea.ActualWidth, TilesArea.ActualHeight);
        var rect = new Rect(_dragStartRect.X + dx, _dragStartRect.Y + dy, _dragStartRect.Width, _dragStartRect.Height);

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            var (xs, ys) = SnapLines(tile, stage);
            rect.X += BestSnap(rect.Left, rect.Right, xs);
            rect.Y += BestSnap(rect.Top, rect.Bottom, ys);
        }

        PlaceTile(tile, ClampToStage(rect, stage));
    }

    private void OnTileMouseUp(StreamTile tile, MouseButtonEventArgs e)
    {
        if (_dragTile != tile)
        {
            return;
        }

        _dragTile = null;
        tile.Root.ReleaseMouseCapture();

        if (_dragIsPan)
        {
            return;
        }

        if (_dragMoved)
        {
            CommitTileRect(tile);
        }
        else if (_layoutMode == LayoutMode.Focus && IsOnStage(tile) && tile.Key != _focusKey)
        {
            // Clique simples numa miniatura do modo foco: ela vira a tela grande.
            _focusKey = tile.Key;
            RelayoutTiles();
        }
    }

    private void ResizeTile(StreamTile tile, Rect start, double dx, double dy, int h, int v)
    {
        var stage = new Size(TilesArea.ActualWidth, TilesArea.ActualHeight);
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;

        if (h < 0) left += dx;
        if (h > 0) right += dx;
        if (v < 0) top += dy;
        if (v > 0) bottom += dy;

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            var (xs, ys) = SnapLines(tile, stage);
            if (h < 0) left += NearestSnap(left, xs);
            if (h > 0) right += NearestSnap(right, xs);
            if (v < 0) top += NearestSnap(top, ys);
            if (v > 0) bottom += NearestSnap(bottom, ys);
        }

        // Limites: dentro do palco e sem ficar menor que o mínimo (a borda que se move é que para).
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(stage.Width, right);
        bottom = Math.Min(stage.Height, bottom);
        if (right - left < MinTileWidth)
        {
            if (h < 0) left = right - MinTileWidth; else right = left + MinTileWidth;
        }
        if (bottom - top < MinTileHeight)
        {
            if (v < 0) top = bottom - MinTileHeight; else bottom = top + MinTileHeight;
        }

        // Shift mantém a proporção que a tela tinha ao começar a redimensionar.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && start.Height > 0)
        {
            var ratio = start.Width / start.Height;
            if (h != 0)
            {
                var height = (right - left) / ratio;
                if (v < 0) top = bottom - height; else bottom = top + height;
            }
            else
            {
                var width = (bottom - top) * ratio;
                right = left + width;
            }
        }

        PlaceTile(tile, ClampToStage(new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)), stage));
    }

    private void CommitTileRect(StreamTile tile)
    {
        var stage = new Size(TilesArea.ActualWidth, TilesArea.ActualHeight);
        if (stage.Width < 1 || stage.Height < 1)
        {
            return;
        }

        tile.FreeRect = Normalize(tile.StageRect, stage);
        SaveFreeLayout();
    }

    /// <summary>Linhas em que uma borda pode grudar: as bordas do palco e as das outras telas
    /// (encostando, ou deixando o espaçamento padrão entre elas).</summary>
    private (List<double> Xs, List<double> Ys) SnapLines(StreamTile moving, Size stage)
    {
        var xs = new List<double> { 0, stage.Width };
        var ys = new List<double> { 0, stage.Height };

        foreach (var other in StageTiles)
        {
            if (other == moving)
            {
                continue;
            }

            var r = other.StageRect;
            xs.AddRange([r.Left, r.Right, r.Left - TileGap, r.Right + TileGap]);
            ys.AddRange([r.Top, r.Bottom, r.Top - TileGap, r.Bottom + TileGap]);
        }

        return (xs, ys);
    }

    /// <summary>Quanto somar a uma borda pra ela cair na linha mais próxima (0 se nenhuma perto).</summary>
    private static double NearestSnap(double edge, List<double> lines)
    {
        var best = 0.0;
        var bestDistance = SnapDistance;
        foreach (var line in lines)
        {
            var distance = Math.Abs(line - edge);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line - edge;
            }
        }

        return best;
    }

    /// <summary>Ao mover, tanto a borda de início quanto a de fim podem grudar: vale a mais próxima.</summary>
    private static double BestSnap(double start, double end, List<double> lines)
    {
        var a = NearestSnap(start, lines);
        var b = NearestSnap(end, lines);
        if (a == 0) return b;
        if (b == 0) return a;
        return Math.Abs(a) <= Math.Abs(b) ? a : b;
    }

    // ---------------------------------------------------------------- tela cheia

    private void ToggleFullscreen(StreamTile tile)
    {
        // Tela destacada: a "tela cheia" é da janela dela, não da sala.
        if (tile.PopOut is not null)
        {
            tile.PopOut.ToggleFullscreen();
            tile.FullscreenButton.Content = tile.PopOut.IsFullscreen ? GlyphExitFullscreen : GlyphFullscreen;
            return;
        }

        if (_fullscreenTile == tile)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen(tile);
        }
    }

    private void EnterFullscreen(StreamTile tile)
    {
        if (_fullscreenTile is not null)
        {
            ExitFullscreen();
        }

        // Move o tile pra camada que cobre a janela inteira.
        TilesGrid.Children.Remove(tile.Root);
        SetTileChrome(tile, framed: false);
        FullscreenHost.Children.Add(tile.Root);
        FullscreenHost.Visibility = Visibility.Visible;

        _previousWindowStyle = WindowStyle;
        _previousWindowState = WindowState;
        _previousResizeMode = ResizeMode;

        // Marca ANTES de mexer na janela: maximizar dispara SizeChanged -> RelayoutTiles(), que
        // reaplicaria o tamanho do palco justamente neste tile e ele não preencheria a tela.
        _fullscreenTile = tile;

        // Tela cheia de verdade (cobre a barra de tarefas).
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal; // garante a transição quando já está maximizada
        WindowState = WindowState.Maximized;
        tile.FullscreenButton.Content = GlyphExitFullscreen;
        tile.FullscreenButton.ToolTip = "Sair da tela cheia (Esc)";
        RelayoutTiles();
    }

    private void ExitFullscreen()
    {
        var tile = _fullscreenTile;
        if (tile is null)
        {
            return;
        }

        _fullscreenTile = null;

        FullscreenHost.Children.Remove(tile.Root);
        FullscreenHost.Visibility = Visibility.Collapsed;

        SetTileChrome(tile, framed: true);
        tile.FullscreenButton.Content = GlyphFullscreen;
        tile.FullscreenButton.ToolTip = "Tela cheia";

        // Só volta pro palco se o participante ainda estiver na sala.
        if (_tiles.ContainsKey(tile.Key))
        {
            TilesGrid.Children.Add(tile.Root);
        }

        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        WindowState = _previousWindowState;

        RelayoutTiles();
    }

    /// <summary>Dentro do palco o tile tem borda e cantos arredondados; em tela cheia ou em
    /// janela própria ele ocupa tudo, sem moldura e sem tamanho fixo.</summary>
    private static void SetTileChrome(StreamTile tile, bool framed)
    {
        tile.Root.CornerRadius = new CornerRadius(framed ? TileCornerRadius : 0);
        tile.Root.BorderThickness = new Thickness(framed ? 1 : 0);

        if (!framed)
        {
            tile.Root.Width = double.NaN;
            tile.Root.Height = double.NaN;
            Canvas.SetLeft(tile.Root, 0);
            Canvas.SetTop(tile.Root, 0);
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullscreenTile is not null)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- janela destacada

    /// <summary>Tira a tela da sala e põe numa janela própria (outro monitor, ou por cima do
    /// jogo com "manter por cima"). Clicar de novo, ou fechar a janela, devolve pra sala.</summary>
    private void TogglePopOut(StreamTile tile)
    {
        if (tile.PopOut is not null)
        {
            ClosePopOut(tile);
            return;
        }

        if (_fullscreenTile == tile)
        {
            ExitFullscreen();
        }

        TilesGrid.Children.Remove(tile.Root);
        SetTileChrome(tile, framed: false);

        var window = new TileWindow(tile.Kind == StreamKind.Camera ? $"{tile.Nickname} · câmera" : tile.Nickname, tile.Root);
        window.Closed += (_, _) => OnPopOutClosed(tile, window);
        tile.PopOut = window;

        tile.PopOutButton.Content = GlyphReturn;
        tile.PopOutButton.ToolTip = "Devolver pra sala";
        tile.PinButton.Visibility = Visibility.Visible;
        UpdatePinButton(tile);

        // A janela nova abre ao lado, sem cobrir a sala: quem tem várias telas vai destacando
        // uma por uma, e a sala precisa continuar ali à mão.
        var offset = 32 * (_tiles.Values.Count(t => t.PopOut is not null) - 1);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Max(SystemParameters.VirtualScreenLeft, Left + 48 + offset);
        window.Top = Math.Max(SystemParameters.VirtualScreenTop, Top + 48 + offset);

        window.Show();
        RelayoutTiles();
    }

    private void TogglePinned(StreamTile tile)
    {
        if (tile.PopOut is null)
        {
            return;
        }

        tile.PopOut.Topmost = !tile.PopOut.Topmost;
        UpdatePinButton(tile);
    }

    private static void UpdatePinButton(StreamTile tile)
    {
        var pinned = tile.PopOut?.Topmost == true;
        tile.PinButton.Content = pinned ? GlyphPinned : GlyphPin;
        tile.PinButton.ToolTip = pinned ? "Parar de manter por cima" : "Manter por cima das outras janelas";
    }

    private static void ClosePopOut(StreamTile tile) => tile.PopOut?.Close();

    private void OnPopOutClosed(StreamTile tile, TileWindow window)
    {
        window.Content = null; // solta o tile pra ele poder voltar pro palco
        if (tile.PopOut != window)
        {
            return;
        }

        tile.PopOut = null;
        tile.PopOutButton.Content = GlyphPopOut;
        tile.PopOutButton.ToolTip = "Destacar em outra janela";
        tile.PinButton.Visibility = Visibility.Collapsed;
        tile.FullscreenButton.Content = GlyphFullscreen;

        if (_shuttingDown || !_tiles.ContainsKey(tile.Key))
        {
            return;
        }

        SetTileChrome(tile, framed: true);
        TilesGrid.Children.Add(tile.Root);
        RelayoutTiles();

        // A tela voltou pra sala, então a sala vem pra frente. Sem isso ela ficava atrás das
        // outras janelas e parecia que a tela tinha sumido. (Indo pra segundo plano as janelas
        // destacadas também fecham, e aí a sala NÃO deve reaparecer.)
        if (!_backgroundMode)
        {
            RestoreFromTray();
        }
    }

    private sealed partial class StreamTile
    {
        /// <summary>Onde o tile está no palco agora, em unidades da janela.</summary>
        public Rect StageRect;

        /// <summary>Posição no modo livre, em fração do palco (0 a 1), pra acompanhar o
        /// redimensionamento da janela. Null = ainda não foi posicionado à mão.</summary>
        public Rect? FreeRect;

        /// <summary>Janela própria, quando a tela foi destacada da sala.</summary>
        public TileWindow? PopOut;

        public bool DrawMode;
    }
}
