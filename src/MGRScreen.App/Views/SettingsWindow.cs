using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MGRScreen.App.Services;
using MGRScreen.App.Theme;
using MGRScreen.Core.Audio;

namespace MGRScreen.App.Views;

/// <summary>
/// Configurações do app, num lugar só. Tudo vale na hora: cada mudança é salva e avisada a quem
/// estiver aberto (<see cref="AppSettings.Changed"/>), sem botão de "aplicar".
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly StackPanel _swatches = new() { Orientation = Orientation.Horizontal };
    private TextBox _hexBox = null!;

    public SettingsWindow()
    {
        // O estilo implícito de Window não vale pra classes derivadas: aplica à mão.
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Configurações — MGRScreen";
        Width = 520;
        Height = 680;
        MinWidth = 440;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        content.Children.Add(new TextBlock { Text = "Configurações", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });

        content.Children.Add(Card("APARÊNCIA", BuildAppearance()));
        content.Children.Add(Card("GERAL", BuildGeneral()));
        content.Children.Add(Card("VOZ", BuildVoice()));
        content.Children.Add(Card("ATALHOS (funcionam com o app em segundo plano)", BuildHotkeys()));
        content.Children.Add(Card("PASTAS", BuildFolders()));
        content.Children.Add(new TextBlock
        {
            Text = $"MGRScreen {UpdateChecker.CurrentVersion}",
            Style = Res<Style>("CaptionTextStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
        };
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private void Commit()
    {
        _settings.Save();
        AppSettings.NotifyChanged();
    }

    private static Border Card(string title, UIElement body) => new()
    {
        Style = Res<Style>("CardStyle"),
        Margin = new Thickness(0, 0, 0, 12),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, Style = Res<Style>("SectionHeaderTextStyle"), Margin = new Thickness(0, 0, 0, 12) },
                body,
            },
        },
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Style = Res<Style>("LabelTextStyle"),
        Margin = new Thickness(0, 10, 0, 4),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Style = Res<Style>("CaptionTextStyle"),
        Margin = new Thickness(0, 6, 0, 0),
    };

    // ---------------------------------------------------------------- aparência

    private UIElement BuildAppearance()
    {
        _hexBox = new TextBox { Width = 110, Text = _settings.AccentColor, MaxLength = 7, FontFamily = new FontFamily("Consolas") };
        _hexBox.TextChanged += (_, _) =>
        {
            // Só aplica quando o que foi digitado já é uma cor completa.
            if (_hexBox.Text.Length == 7 && ThemeManager.Parse(_hexBox.Text) is not null)
            {
                SetAccent(_hexBox.Text, updateBox: false);
            }
        };

        RebuildSwatches();

        return new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Cor de destaque", Style = Res<Style>("LabelTextStyle") },
                _swatches,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = "Outra cor (#RRGGBB)", Style = Res<Style>("LabelTextStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) },
                        _hexBox,
                    },
                },
                Hint("O fundo escuro do app é sempre o mesmo; só a cor dos botões e destaques muda."),
            },
        };
    }

    private void RebuildSwatches()
    {
        _swatches.Children.Clear();
        foreach (var (name, hex) in ThemeManager.Presets)
        {
            var selected = string.Equals(hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(3),
                BorderThickness = new Thickness(2),
                BorderBrush = selected ? Res<Brush>("TextPrimaryBrush") : Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = name,
                Child = new Ellipse { Fill = new SolidColorBrush(ThemeManager.Parse(hex)!.Value) },
            };
            swatch.MouseLeftButtonUp += (_, _) => SetAccent(hex, updateBox: true);
            _swatches.Children.Add(swatch);
        }
    }

    private void SetAccent(string hex, bool updateBox)
    {
        _settings.AccentColor = hex.ToUpperInvariant();
        ThemeManager.Apply(hex);
        if (updateBox)
        {
            _hexBox.Text = _settings.AccentColor;
        }
        RebuildSwatches();
        Commit();
    }

    // ---------------------------------------------------------------- geral

    private UIElement BuildGeneral()
    {
        CheckBox Option(string text, bool value, Action<bool> set)
        {
            var box = new CheckBox { Content = text, IsChecked = value, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
            box.Click += (_, _) =>
            {
                set(box.IsChecked == true);
                Commit();
            };
            return box;
        }

        return new StackPanel
        {
            Children =
            {
                Option("Ao minimizar, ficar só no ícone perto do relógio", _settings.MinimizeToTray, v => _settings.MinimizeToTray = v),
                Option("Avisar quando alguém entra, compartilha ou manda mensagem", _settings.Notifications, v => _settings.Notifications = v),
                Option("Avisar quando houver versão nova", _settings.CheckUpdates, v => _settings.CheckUpdates = v),
            },
        };
    }

    // ---------------------------------------------------------------- voz

    private UIElement BuildVoice()
    {
        var mode = new ComboBox();
        mode.Items.Add(new ComboBoxItem { Content = "Apertar pra falar (recomendado)" });
        mode.Items.Add(new ComboBoxItem { Content = "Ativar quando eu falar" });
        mode.Items.Add(new ComboBoxItem { Content = "Microfone sempre aberto" });
        mode.Items.Add(new ComboBoxItem { Content = "Não usar o microfone" });
        mode.SelectedIndex = Math.Clamp(_settings.VoiceMode, 0, 3);
        mode.SelectionChanged += (_, _) =>
        {
            _settings.VoiceMode = mode.SelectedIndex;
            Commit();
        };

        var devices = MicrophoneCapture.ListDevices();
        var device = new ComboBox();
        device.Items.Add(new ComboBoxItem { Content = "Microfone padrão do Windows", Tag = "" });
        foreach (var (id, name) in devices)
        {
            device.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        }
        device.SelectedIndex = Math.Max(0, device.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == _settings.MicDeviceId));
        device.SelectionChanged += (_, _) =>
        {
            _settings.MicDeviceId = (string)((ComboBoxItem)device.SelectedItem).Tag;
            Commit();
        };

        return new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Como o microfone transmite", Style = Res<Style>("LabelTextStyle") },
                mode,
                Label("Tecla de apertar pra falar"),
                HotkeyBox(() => _settings.PushToTalkKey, v => _settings.PushToTalkKey = v, allowSingleKey: true),
                Label("Microfone"),
                device,
                Hint("Sem fone de ouvido, o microfone aberto devolve pra sala o som das suas caixas. Trocar o microfone vale da próxima vez que ele for ligado."),
            },
        };
    }

    // ---------------------------------------------------------------- atalhos

    private UIElement BuildHotkeys() => new StackPanel
    {
        Children =
        {
            new TextBlock { Text = "Começar / parar de compartilhar", Style = Res<Style>("LabelTextStyle") },
            HotkeyBox(() => _settings.HotkeyToggleShare, v => _settings.HotkeyToggleShare = v, allowSingleKey: false),
            Label("Ligar / desligar o microfone"),
            HotkeyBox(() => _settings.HotkeyToggleMic, v => _settings.HotkeyToggleMic = v, allowSingleKey: false),
            Hint("Clique no campo e aperte a combinação. Backspace deixa sem atalho. O atalho de compartilhar recomeça no mesmo monitor da última vez."),
        },
    };

    /// <summary>Campo que captura a combinação apertada em vez de texto digitado.</summary>
    private TextBox HotkeyBox(Func<string> get, Action<string> set, bool allowSingleKey)
    {
        var box = new TextBox
        {
            Text = get(),
            IsReadOnly = true,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
        };

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key is Key.Back or Key.Delete)
            {
                if (allowSingleKey)
                {
                    return; // apertar-pra-falar sempre precisa de uma tecla
                }

                set("");
                box.Text = "";
                Commit();
                return;
            }

            // Ignora o momento em que só o modificador está apertado.
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
                or Key.RightShift or Key.LWin or Key.RWin or Key.Tab or Key.Escape)
            {
                return;
            }

            var modifiers = Keyboard.Modifiers;
            if (!allowSingleKey && modifiers == ModifierKeys.None && key is < Key.F1 or > Key.F24)
            {
                return; // atalho global de uma letra só atrapalharia a digitação em qualquer app
            }

            var gesture = GlobalHotkeys.Format(modifiers, key);
            if (!GlobalHotkeys.TryParse(gesture, out ModifierKeys _, out Key _))
            {
                return;
            }

            set(gesture);
            box.Text = gesture;
            Commit();
        };

        return box;
    }

    // ---------------------------------------------------------------- pastas

    private UIElement BuildFolders()
    {
        UIElement Row(string title, Func<string> effective, Action<string> set)
        {
            var path = new TextBlock
            {
                Text = effective(),
                Style = Res<Style>("CaptionTextStyle"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var change = new Button { Content = "Trocar", Style = Res<Style>("SecondaryButtonStyle"), Padding = new Thickness(12, 6, 12, 6), FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
            change.Click += (_, _) =>
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = effective(), Description = title };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    set(dialog.SelectedPath);
                    path.Text = effective();
                    Commit();
                }
            };

            var open = new Button { Content = "Abrir", Style = Res<Style>("SecondaryButtonStyle"), Padding = new Thickness(12, 6, 12, 6), FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
            open.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(effective());
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{effective()}\"");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                }
            };

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel
            {
                Children = { new TextBlock { Text = title, Style = Res<Style>("LabelTextStyle") }, path },
            };
            Grid.SetColumn(change, 1);
            Grid.SetColumn(open, 2);
            grid.Children.Add(text);
            grid.Children.Add(change);
            grid.Children.Add(open);
            return grid;
        }

        return new StackPanel
        {
            Children =
            {
                Row("Gravações", () => _settings.EffectiveRecordingsFolder, v => _settings.RecordingsFolder = v),
                Row("Imagens salvas", () => _settings.EffectiveScreenshotsFolder, v => _settings.ScreenshotsFolder = v),
            },
        };
    }
}
