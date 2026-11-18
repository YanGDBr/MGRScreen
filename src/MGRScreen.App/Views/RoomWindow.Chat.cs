using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MGRScreen.App.Theme;
using MGRScreen.Core.Networking;

namespace MGRScreen.App.Views;

/// <summary>Chat de texto da sala (painel lateral) e o que depende do protocolo novo.</summary>
public partial class RoomWindow
{
    private const int MaxChatMessages = 300;
    private int _lastChatAuthor = int.MinValue;

    private void InitializeChat()
    {
        SetChatOpen(_settings.ChatPanelOpen, save: false);
    }

    /// <summary>Eventos das funções novas da sala. Chegam na thread de rede.</summary>
    private void WireRoomFeatureEvents(RoomClient client)
    {
        client.ChatReceived += message => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(client, _client))
            {
                AddChatMessage(message);
            }
        });
        client.AnnotationReceived += message => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(client, _client))
            {
                OnAnnotationReceived(message);
            }
        });
        client.VoiceChunkReceived += OnVoiceChunkReceived;
        client.ParticipantLeft += id => OnVoiceParticipantLeft(id);
    }

    /// <summary>
    /// Chat, voz e desenho só existem quando o anfitrião está na versão nova. Numa sala antiga o
    /// resto funciona igual, e estes controles avisam em vez de falhar em silêncio.
    /// </summary>
    private void OnRoomFeaturesChanged()
    {
        var supported = _client.SupportsRoomFeatures;
        ChatUnavailableText.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        ChatInput.IsEnabled = supported;

        if (!supported)
        {
            StopVoice();
        }
    }

    private void OnChatToggleClick(object sender, RoutedEventArgs e) =>
        SetChatOpen(ChatPanel.Visibility != Visibility.Visible, save: true);

    private void SetChatOpen(bool open, bool save)
    {
        ChatPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChatToggleButton.Foreground = (Brush)FindResource(open ? "AccentBrush" : "TextSecondaryBrush");

        if (open)
        {
            ChatScroll.ScrollToEnd();
            Keyboard.Focus(ChatInput);
        }

        if (save)
        {
            _settings.ChatPanelOpen = open;
            _settings.Save();
        }
    }

    private void OnChatSendClick(object sender, RoutedEventArgs e) => SendChat();

    private void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendChat();
            e.Handled = true;
        }
    }

    private void SendChat()
    {
        var text = ChatInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // A mensagem só aparece quando o servidor devolve o eco: assim todo mundo vê na mesma ordem.
        _ = _client.SendChatAsync(text);
        ChatInput.Clear();
    }

    private void AddChatMessage(ChatMessage message)
    {
        var mine = message.ParticipantId == _client.ParticipantId;

        // Mensagens seguidas da mesma pessoa ficam agrupadas, sem repetir o nome.
        if (message.ParticipantId != _lastChatAuthor)
        {
            _lastChatAuthor = message.ParticipantId;
            ChatMessages.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, ChatMessages.Children.Count == 0 ? 2 : 12, 0, 2),
                Children =
                {
                    new TextBlock
                    {
                        Text = mine ? $"{message.Nickname} (você)" : message.Nickname,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(ThemeManager.ColorFor(message.Nickname)),
                    },
                    new TextBlock
                    {
                        Text = message.SentAtUtc.ToLocalTime().ToString("HH:mm"),
                        Style = (Style)FindResource("CaptionTextStyle"),
                        FontSize = 10,
                        Margin = new Thickness(8, 2, 0, 0),
                    },
                },
            });
        }

        // TextBox somente leitura em vez de TextBlock: dá pra selecionar e copiar um link.
        ChatMessages.Children.Add(new TextBox
        {
            Text = message.Text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 1),
            FontSize = 13,
            Template = PlainTextTemplate,
        });

        while (ChatMessages.Children.Count > MaxChatMessages)
        {
            ChatMessages.Children.RemoveAt(0);
        }

        ChatScroll.ScrollToEnd();

        if (mine)
        {
            return;
        }

        if (ChatPanel.Visibility != Visibility.Visible)
        {
            // Chat fechado: o botão acende e a mensagem aparece como aviso.
            ChatToggleButton.Foreground = (Brush)FindResource("WarningBrush");
            NotifyEvent($"{message.Nickname}: {Shorten(message.Text, 90)}", "");
        }
        else if (!IsActive)
        {
            ShowTrayBalloon($"{message.Nickname}: {Shorten(message.Text, 90)}");
        }
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";

    /// <summary>TextBox sem a moldura do tema (só o texto), pras mensagens do chat.</summary>
    private static readonly ControlTemplate PlainTextTemplate = CreatePlainTextTemplate();

    private static ControlTemplate CreatePlainTextTemplate()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        template.Seal();
        return template;
    }
}
