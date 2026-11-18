using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MGRScreen.App.Theme;
using MGRScreen.Core.Models;
using MGRScreen.Core.Networking;

namespace MGRScreen.App.Views;

/// <summary>
/// A parte visual de cada tela da sala: a árvore de elementos do tile, os controles que aparecem
/// ao passar o mouse, zoom, indicador de conexão e captura de imagem.
/// </summary>
public partial class RoomWindow
{
    // Glifos do Segoe MDL2 Assets usados nos tiles.
    private const string GlyphFullscreen = "";
    private const string GlyphExitFullscreen = "";
    private const string GlyphPopOut = "";
    private const string GlyphReturn = "";
    private const string GlyphPin = "";
    private const string GlyphPinned = "";
    private const string GlyphCamera = "";
    private const string GlyphRecord = "";
    private const string GlyphStop = "";
    private const string GlyphPen = "";

    private StreamTile AddTile(int key, int participantId, StreamKind kind, string nickname, int width, int height, StreamInfo? info)
    {
        var image = new Image { Stretch = Stretch.Fill };

        // Preview da minha própria câmera espelhado, como todo app de vídeo faz (senão mexer a
        // mão direita parece mexer a esquerda). Só aqui: os outros recebem a imagem normal.
        if (participantId == LocalCameraTileId)
        {
            image.RenderTransformOrigin = new Point(0.5, 0.5);
            image.RenderTransform = new ScaleTransform(-1, 1);
        }

        // Imagem e camada de desenho moram numa grade do tamanho exato do frame, dentro de um
        // Viewbox. Assim as duas escalam juntas e o desenho usa coordenadas em pixels do vídeo,
        // não da tela — o traço cai no mesmo lugar em qualquer tamanho de tile.
        var drawLayer = new Canvas { IsHitTestVisible = false };
        var pictureGrid = new Grid
        {
            Width = Math.Max(2, width),
            Height = Math.Max(2, height),
            Children = { image, drawLayer },
        };
        var zoom = new ScaleTransform(1, 1);
        var pan = new TranslateTransform();
        var pictureBox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = pictureGrid,
            RenderTransform = new TransformGroup { Children = { zoom, pan } },
        };
        var pictureClip = new Border { ClipToBounds = true, Background = Brushes.Transparent, Child = pictureBox };

        var placeholder = new TextBlock
        {
            Text = "Aguardando vídeo...",
            Foreground = (Brush)FindResource("TextMutedBrush"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(16),
            IsHitTestVisible = false,
        };

        // ---- etiqueta com o nome (sempre visível)
        var overlayBrush = new SolidColorBrush(Color.FromArgb(0xB3, 0x10, 0x12, 0x16));
        var qualityDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)FindResource("TextMutedBrush"),
            Visibility = participantId < 0 ? Visibility.Collapsed : Visibility.Visible,
        };
        var label = new Border
        {
            Background = overlayBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10),
            IsHitTestVisible = true,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Ellipse
                    {
                        Width = 9,
                        Height = 9,
                        Margin = new Thickness(0, 0, 7, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Fill = new SolidColorBrush(ThemeManager.ColorFor(nickname)),
                    },
                    new TextBlock
                    {
                        Text = kind == StreamKind.Camera ? $"{nickname} · câmera" : nickname,
                        Foreground = Brushes.White,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.NoWrap,
                    },
                    qualityDot,
                },
            },
        };

        var fpsText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
        };
        var fpsPill = new Border
        {
            Background = overlayBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = fpsText,
        };

        // ---- barra de ferramentas (só com o mouse em cima)
        Button Tool(string glyph, string tip) => new()
        {
            Style = (Style)FindResource("TileToolButtonStyle"),
            Content = glyph,
            ToolTip = tip,
        };

        var toggleButton = Tool("", "Parar de assistir");
        var muteButton = Tool("", "Silenciar");
        var drawButton = Tool(GlyphPen, "Desenhar nesta tela (todos veem)");
        var shotButton = Tool(GlyphCamera, "Salvar uma imagem desta tela");
        var recordButton = Tool(GlyphRecord, "Gravar esta tela");
        var pinButton = Tool(GlyphPin, "Manter por cima das outras janelas");
        var popOutButton = Tool(GlyphPopOut, "Destacar em outra janela");
        var fullscreenButton = Tool(GlyphFullscreen, "Tela cheia");

        // Preview local não toca áudio meu; câmera não tem áudio nenhum. Nos dois casos não há o
        // que silenciar.
        muteButton.Visibility = participantId < 0 || kind == StreamKind.Camera
            ? Visibility.Collapsed
            : Visibility.Visible;
        pinButton.Visibility = Visibility.Collapsed; // só faz sentido na janela destacada

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Opacity = 0,
            Children =
            {
                toggleButton, muteButton, drawButton, shotButton, recordButton, pinButton, popOutButton, fullscreenButton,
            },
        };

        // ---- selos do canto superior esquerdo: gravando e zoom
        var recText = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
        var recBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xEF, 0x44, 0x44)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 9, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Ellipse { Width = 7, Height = 7, Fill = Brushes.White, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center },
                    recText,
                },
            },
        };
        var zoomText = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
        var zoomBadge = new Border
        {
            Background = overlayBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 9, 4),
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            ToolTip = "Clique pra voltar ao tamanho normal",
            Child = zoomText,
        };
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10),
            Children = { recBadge, zoomBadge },
        };

        var grid = new Grid { Children = { pictureClip, placeholder, label, fpsPill, badges, toolbar } };

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0B)),
            CornerRadius = new CornerRadius(TileCornerRadius),
            BorderBrush = (Brush)FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            Child = grid,
        };

        var tile = new StreamTile
        {
            Key = key,
            Kind = kind,
            ParticipantId = participantId,
            Nickname = nickname,
            Root = root,
            Image = image,
            Placeholder = placeholder,
            FpsText = fpsText,
            ToggleButton = toggleButton,
            MuteButton = muteButton,
            FullscreenButton = fullscreenButton,
            Info = info,
            Width = width,
            Height = height,
            Watching = true,
            FrameSeconds = 1.0 / Math.Max(1, info?.Fps ?? (kind == StreamKind.Camera ? 30 : _capture.TargetFps)),
            PictureGrid = pictureGrid,
            PictureClip = pictureClip,
            DrawLayer = drawLayer,
            Zoom = zoom,
            Pan = pan,
            QualityDot = qualityDot,
            Toolbar = toolbar,
            FpsPill = fpsPill,
            RecBadge = recBadge,
            RecText = recText,
            ZoomBadge = zoomBadge,
            ZoomText = zoomText,
            PinButton = pinButton,
            PopOutButton = popOutButton,
            RecordButton = recordButton,
            DrawButton = drawButton,
        };
        tile.UpdateToggleLabel();
        tile.UpdateMuteLabel();

        ApplySavedAudioPreference(tile);

        toggleButton.Click += (_, _) => OnToggleWatchClick(tile);
        // O alto-falante abre o volume só desta tela (com o botão de silenciar dentro). O botão
        // direito em cima da imagem continua abrindo o mesmo painel.
        muteButton.Click += (_, _) => OpenVolumePopup(tile);
        fullscreenButton.Click += (_, _) => ToggleFullscreen(tile);
        popOutButton.Click += (_, _) => TogglePopOut(tile);
        pinButton.Click += (_, _) => TogglePinned(tile);
        shotButton.Click += (_, _) => SaveScreenshot(tile);
        recordButton.Click += (_, _) => ToggleRecording(tile);
        drawButton.Click += (_, _) => ToggleDrawMode(tile);
        zoomBadge.MouseLeftButtonDown += (_, e) =>
        {
            ResetZoom(tile);
            e.Handled = true;
        };

        // Botão direito na tela da pessoa abre o volume dela (câmera não tem áudio).
        if (participantId >= 0 && kind == StreamKind.Screen)
        {
            root.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenVolumePopup(tile);
            };
        }

        // Controles aparecem com o mouse em cima e somem quando ele sai: a imagem fica limpa.
        root.MouseEnter += (_, _) => FadeTileControls(tile, 1);
        root.MouseLeave += (_, _) => FadeTileControls(tile, 0);

        pictureClip.MouseWheel += (_, e) => OnTileWheel(tile, e);
        pictureClip.SizeChanged += (_, _) => ClampPan(tile);

        AttachLayoutBehavior(tile, grid);

        _tiles[key] = tile;
        TilesGrid.Children.Add(root);
        RelayoutTiles();

        return tile;
    }

    private void RemoveTile(int key)
    {
        if (!_tiles.TryRemove(key, out var tile))
        {
            return;
        }

        if (_fullscreenTile == tile)
        {
            ExitFullscreen();
        }

        StopRecording(tile, announce: true);
        ClosePopOut(tile);

        TilesGrid.Children.Remove(tile.Root);
        if (tile.Kind == StreamKind.Screen && !tile.IsLocal)
        {
            _audioMixer.RemoveParticipant(tile.ParticipantId);
        }
        tile.Dispose();
        RelayoutTiles();
        UpdateParticipantsLabel();
    }

    private static void FadeTileControls(StreamTile tile, double opacity)
    {
        var animation = new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(opacity > 0 ? 90 : 220));
        tile.Toolbar.BeginAnimation(OpacityProperty, animation);
        tile.FpsPill.BeginAnimation(OpacityProperty, animation);
        foreach (var grip in tile.Grips)
        {
            grip.IsHitTestVisible = opacity > 0;
        }
    }

    // ---------------------------------------------------------------- zoom

    /// <summary>Rodinha do mouse amplia a imagem em volta do ponteiro (pra ler texto pequeno).
    /// Com zoom, arrastar move a imagem em vez de mover a tela na sala.</summary>
    private static void OnTileWheel(StreamTile tile, MouseWheelEventArgs e)
    {
        var position = e.GetPosition(tile.PictureClip);
        var old = tile.Zoom.ScaleX;
        var scale = Math.Clamp(old * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1, 8);
        if (scale < 1.03)
        {
            scale = 1;
        }

        // Mantém parado o ponto da imagem que está embaixo do ponteiro.
        tile.Pan.X = position.X - ((position.X - tile.Pan.X) * scale / old);
        tile.Pan.Y = position.Y - ((position.Y - tile.Pan.Y) * scale / old);
        tile.Zoom.ScaleX = tile.Zoom.ScaleY = scale;
        ClampPan(tile);
        e.Handled = true;
    }

    private static void ResetZoom(StreamTile tile)
    {
        tile.Zoom.ScaleX = tile.Zoom.ScaleY = 1;
        ClampPan(tile);
    }

    /// <summary>Não deixa a imagem ampliada sair de dentro do tile, e atualiza o selo de zoom.</summary>
    private static void ClampPan(StreamTile tile)
    {
        var scale = tile.Zoom.ScaleX;
        if (scale <= 1)
        {
            tile.Pan.X = tile.Pan.Y = 0;
        }
        else
        {
            tile.Pan.X = Math.Clamp(tile.Pan.X, tile.PictureClip.ActualWidth * (1 - scale), 0);
            tile.Pan.Y = Math.Clamp(tile.Pan.Y, tile.PictureClip.ActualHeight * (1 - scale), 0);
        }

        tile.ZoomBadge.Visibility = scale > 1 ? Visibility.Visible : Visibility.Collapsed;
        tile.ZoomText.Text = $"{scale:0.0}×  ✕";
    }

    // ---------------------------------------------------------------- indicador de conexão

    /// <summary>
    /// Resume a saúde do stream num ponto colorido ao lado do nome: verde, amarelo ou vermelho.
    /// Os números (fps, KB/s) continuam existindo, mas só aparecem ao passar o mouse.
    /// </summary>
    private void UpdateTileQuality(StreamTile tile, int frames, long bytes)
    {
        if (tile.IsLocal)
        {
            return;
        }

        var drops = tile.Decoder?.DroppedChunks ?? 0;
        var newDrops = drops - tile.DropsSeen;
        tile.DropsSeen = drops;

        var expected = Math.Max(1, tile.Info?.Fps ?? 30);
        string key, tip;

        if (!tile.Watching)
        {
            (key, tip) = ("TextMutedBrush", "Você não está assistindo esta tela.");
        }
        else if (bytes == 0 || frames < expected * 0.5)
        {
            (key, tip) = ("DangerBrush", "Conexão ruim: a imagem está travando.");
        }
        else if (newDrops > 0 || frames < expected * 0.85)
        {
            (key, tip) = ("WarningBrush", "Conexão instável: alguns frames estão se perdendo.");
        }
        else
        {
            (key, tip) = ("SuccessBrush", "Conexão boa.");
        }

        tile.QualityDot.Fill = (Brush)FindResource(key);
        tile.QualityDot.ToolTip = tip;
    }

    // ---------------------------------------------------------------- imagem da tela

    /// <summary>Salva o frame atual em PNG, na resolução original do vídeo.</summary>
    private void SaveScreenshot(StreamTile tile)
    {
        try
        {
            ImageSource? source = tile.LumaPlane is not null ? tile.LumaPlane : tile.Bitmap;
            if (source is not BitmapSource bitmap)
            {
                ShowToast("Ainda não há imagem pra salvar.", "");
                return;
            }

            // Monta a imagem fora da tela, no tamanho real do frame, com o mesmo shader de cor do
            // tile. Capturar o tile em si daria o tamanho em que ele está sendo exibido.
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            var offscreen = new Image { Source = bitmap, Stretch = Stretch.Fill, Width = width, Height = height };
            if (tile.LumaPlane is not null && YuvEffect.IsAvailable)
            {
                offscreen.Effect = new YuvEffect { U = new ImageBrush(tile.UPlane), V = new ImageBrush(tile.VPlane) };
            }
            offscreen.Measure(new Size(width, height));
            offscreen.Arrange(new Rect(0, 0, width, height));
            offscreen.UpdateLayout();

            var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            render.Render(offscreen);

            var folder = _settings.EffectiveScreenshotsFolder;
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, $"{SafeFileName(tile.Nickname)} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(render));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            ShowToast($"Imagem salva em {System.IO.Path.GetFileName(folder)}.", GlyphCamera, () => RevealInExplorer(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowToast($"Não consegui salvar a imagem: {ex.Message}", "");
        }
    }

    private static string SafeFileName(string text)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(text.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "MGRScreen" : clean;
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
        }
    }

    private sealed partial class StreamTile
    {
        public Grid PictureGrid = null!;
        public Border PictureClip = null!;
        public Canvas DrawLayer = null!;
        public ScaleTransform Zoom = null!;
        public TranslateTransform Pan = null!;
        public Ellipse QualityDot = null!;
        public StackPanel Toolbar = null!;
        public Border FpsPill = null!;
        public Border RecBadge = null!;
        public TextBlock RecText = null!;
        public Border ZoomBadge = null!;
        public TextBlock ZoomText = null!;
        public Button PinButton = null!;
        public Button PopOutButton = null!;
        public Button RecordButton = null!;
        public Button DrawButton = null!;

        /// <summary>Alças de redimensionar nas bordas (só clicáveis com o mouse em cima).</summary>
        public Thumb[] Grips = [];

        public int DropsSeen;

        /// <summary>Chave pra guardar a posição no modo livre: sobrevive a reconexões.</summary>
        public string LayoutKey => $"{Nickname}|{(int)Kind}";

        /// <summary>O frame mudou de tamanho: a grade que segura imagem + desenho acompanha.</summary>
        public void SetPictureSize(int width, int height)
        {
            if (PictureGrid.Width != width || PictureGrid.Height != height)
            {
                PictureGrid.Width = width;
                PictureGrid.Height = height;
            }
        }
    }
}
