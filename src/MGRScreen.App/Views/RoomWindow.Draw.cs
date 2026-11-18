using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MGRScreen.App.Theme;
using MGRScreen.Core.Networking;

namespace MGRScreen.App.Views;

/// <summary>
/// Desenhar por cima da tela de alguém, pra apontar alguma coisa. O traço aparece pra todo mundo
/// que está assistindo aquela tela e some sozinho depois de alguns segundos — funciona como um
/// ponteiro que deixa rastro, sem precisar de borracha.
///
/// As coordenadas viajam em pixels do vídeo (não da janela), então o traço cai no mesmo ponto da
/// imagem em qualquer tamanho de tile, com zoom ou em tela cheia.
/// </summary>
public partial class RoomWindow
{
    private static readonly TimeSpan StrokeLifetime = TimeSpan.FromSeconds(7);

    /// <summary>Traços dos outros em andamento, por "autor:id do traço".</summary>
    private readonly Dictionary<string, RemoteStroke> _remoteStrokes = new();

    private sealed class RemoteStroke
    {
        public required Polyline Line { get; init; }
        public required StreamTile Tile { get; init; }
        public required DispatcherTimer Expiry { get; init; }
    }

    private sealed class StrokePayload
    {
        public string T { get; set; } = "s";
        public int Id { get; set; }
        public string C { get; set; } = "#FFFFFF";
        public double W { get; set; }
        public double H { get; set; }
        public double[] P { get; set; } = [];
    }

    private void ToggleDrawMode(StreamTile tile)
    {
        tile.DrawMode = !tile.DrawMode;
        tile.DrawLayer.IsHitTestVisible = tile.DrawMode;
        tile.DrawLayer.Background = tile.DrawMode ? Brushes.Transparent : null;
        tile.DrawLayer.Cursor = tile.DrawMode ? Cursors.Pen : null;
        tile.DrawButton.Background = tile.DrawMode
            ? (Brush)FindResource("AccentBrush")
            : new SolidColorBrush(Color.FromArgb(0xB3, 0x10, 0x12, 0x16));
        tile.DrawButton.ToolTip = tile.DrawMode ? "Parar de desenhar" : "Desenhar nesta tela (todos veem)";

        if (!tile.DrawHandlersAttached)
        {
            tile.DrawHandlersAttached = true;
            tile.DrawLayer.MouseLeftButtonDown += (_, e) => BeginStroke(tile, e);
            tile.DrawLayer.MouseMove += (_, e) => ContinueStroke(tile, e);
            tile.DrawLayer.MouseLeftButtonUp += (_, e) => EndStroke(tile, e);
        }

        if (tile.DrawMode && !_client.SupportsRoomFeatures)
        {
            ShowToast("O anfitrião está numa versão antiga: o desenho aparece só pra você.", "");
        }
    }

    private Polyline NewLine(StreamTile tile, Color color) => new()
    {
        Stroke = new SolidColorBrush(color),
        // Espessura proporcional ao vídeo: fica com a mesma cara em 720p e em 1080p.
        StrokeThickness = Math.Max(3, tile.PictureGrid.Width / 320),
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black },
    };

    private void BeginStroke(StreamTile tile, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(tile.DrawLayer);
        var line = NewLine(tile, ThemeManager.ColorFor(_nickname));
        line.Points.Add(point);
        tile.DrawLayer.Children.Add(line);

        tile.ActiveStroke = line;
        tile.ActiveStrokeId++;
        tile.UnsentPoints.Clear();
        tile.UnsentPoints.Add(point);
        tile.LastStrokeSend = Environment.TickCount64;
        tile.DrawLayer.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueStroke(StreamTile tile, MouseEventArgs e)
    {
        if (tile.ActiveStroke is not { } line || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(tile.DrawLayer);
        var last = line.Points[^1];
        if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) < 2)
        {
            return;
        }

        line.Points.Add(point);
        tile.UnsentPoints.Add(point);

        // Manda em pacotes a cada ~50 ms em vez de um por movimento do mouse.
        if (Environment.TickCount64 - tile.LastStrokeSend >= 50)
        {
            FlushStroke(tile);
        }
    }

    private void EndStroke(StreamTile tile, MouseButtonEventArgs e)
    {
        if (tile.ActiveStroke is not { } line)
        {
            return;
        }

        tile.DrawLayer.ReleaseMouseCapture();
        FlushStroke(tile);
        tile.ActiveStroke = null;
        FadeOutLater(tile, line);
        e.Handled = true;
    }

    private void FlushStroke(StreamTile tile)
    {
        tile.LastStrokeSend = Environment.TickCount64;
        if (tile.UnsentPoints.Count == 0)
        {
            return;
        }

        var points = new double[tile.UnsentPoints.Count * 2];
        for (var i = 0; i < tile.UnsentPoints.Count; i++)
        {
            points[i * 2] = Math.Round(tile.UnsentPoints[i].X, 1);
            points[(i * 2) + 1] = Math.Round(tile.UnsentPoints[i].Y, 1);
        }
        tile.UnsentPoints.Clear();

        var color = ThemeManager.ColorFor(_nickname);
        var payload = JsonSerializer.Serialize(new StrokePayload
        {
            Id = tile.ActiveStrokeId,
            C = string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}"),
            W = tile.PictureGrid.Width,
            H = tile.PictureGrid.Height,
            P = points,
        });

        // O tile de preview da minha própria tela desenha no MEU stream; os outros, no do dono.
        var target = tile.IsLocal ? _client.ParticipantId : tile.ParticipantId;
        _ = _client.SendAnnotationAsync(target, tile.Kind, payload);
    }

    private void OnAnnotationReceived(AnnotationMessage message)
    {
        StrokePayload? stroke;
        try
        {
            stroke = JsonSerializer.Deserialize<StrokePayload>(message.Payload);
        }
        catch (JsonException)
        {
            return;
        }

        if (stroke is not { T: "s" } || stroke.P.Length < 2 || stroke.P.Length % 2 != 0 || stroke.W <= 0 || stroke.H <= 0)
        {
            return;
        }

        // Um desenho na minha tela aparece no meu preview; na de outra pessoa, no tile dela.
        var key = message.TargetParticipantId == _client.ParticipantId
            ? (message.TargetKind == StreamKind.Camera ? LocalCameraTileId : LocalTileId)
            : TileKey(message.TargetParticipantId, message.TargetKind);
        if (!_tiles.TryGetValue(key, out var tile))
        {
            return;
        }

        var id = $"{message.FromParticipantId}:{key}:{stroke.Id}";
        if (!_remoteStrokes.TryGetValue(id, out var remote))
        {
            var color = ThemeManager.Parse(stroke.C) ?? ThemeManager.ColorFor(message.FromNickname);
            var line = NewLine(tile, color);
            tile.DrawLayer.Children.Add(line);

            // Quem desenha não avisa "terminei": o traço expira um tempo depois do último pedaço.
            var expiry = new DispatcherTimer { Interval = StrokeLifetime };
            remote = new RemoteStroke { Line = line, Tile = tile, Expiry = expiry };
            expiry.Tick += (_, _) =>
            {
                expiry.Stop();
                _remoteStrokes.Remove(id);
                FadeOut(tile, line);
            };
            _remoteStrokes[id] = remote;
        }

        // Quem desenhou pode estar vendo o vídeo em outra resolução: converte pra a daqui.
        var scaleX = tile.PictureGrid.Width / stroke.W;
        var scaleY = tile.PictureGrid.Height / stroke.H;
        for (var i = 0; i < stroke.P.Length && remote.Line.Points.Count < 4000; i += 2)
        {
            remote.Line.Points.Add(new Point(stroke.P[i] * scaleX, stroke.P[i + 1] * scaleY));
        }

        remote.Expiry.Stop();
        remote.Expiry.Start();
    }

    private static void FadeOutLater(StreamTile tile, Polyline line)
    {
        var timer = new DispatcherTimer { Interval = StrokeLifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FadeOut(tile, line);
        };
        timer.Start();
    }

    private static void FadeOut(StreamTile tile, Polyline line)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(600));
        fade.Completed += (_, _) => tile.DrawLayer.Children.Remove(line);
        line.BeginAnimation(OpacityProperty, fade);
    }

    private sealed partial class StreamTile
    {
        public bool DrawHandlersAttached;
        public Polyline? ActiveStroke;
        public int ActiveStrokeId;
        public long LastStrokeSend;
        public readonly List<Point> UnsentPoints = [];
    }
}
