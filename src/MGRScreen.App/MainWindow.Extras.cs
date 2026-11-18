using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MGRScreen.App.Services;
using MGRScreen.App.Theme;
using MGRScreen.App.Views;

namespace MGRScreen.App;

/// <summary>O que o menu faz além de listar salas: convite por link, aviso de versão nova,
/// explicação de primeiro uso e a porta das configurações.</summary>
public partial class MainWindow
{
    private UpdateChecker.UpdateInfo? _update;
    private int _onboardingStep;

    private static readonly (string Glyph, string Title, string Body)[] OnboardingPages =
    {
        ("", "Na mesma rede, é automático",
            "Quem está no mesmo Wi-Fi ou cabo que você vê a sua sala aparecer sozinha na lista. É só clicar pra entrar."),
        ("", "Em casas diferentes, use o Tailscale",
            "O Tailscale é um programa gratuito que faz os computadores de vocês se comportarem como se estivessem na mesma rede. Com ele ligado nos dois lados, a sala também aparece na lista. Sem ele, dá pra entrar digitando o IP do anfitrião."),
    };

    /// <summary>Roda uma vez quando o menu abre. Devolve true se já trocou de janela.</summary>
    private bool OnOpened()
    {
        InviteLink.RegisterProtocol();

        // Aberto por um link de convite: entra direto, sem a pessoa precisar clicar em nada.
        if (App.PendingInvite is { } invite)
        {
            App.PendingInvite = null;
            _settings.LastManualAddress = invite.Address;
            OpenRoom(new RoomWindow(GetNickname(), invite.Address, ownedServer: null, roomLabel: invite.Address));
            return true;
        }

        if (!_settings.OnboardingDone)
        {
            ShowOnboardingPage(0);
        }

        if (_settings.CheckUpdates)
        {
            _ = CheckForUpdateAsync();
        }

        return false;
    }

    private async Task CheckForUpdateAsync()
    {
        var update = await UpdateChecker.CheckAsync(_scanCts.Token);
        if (update is null || !IsLoaded)
        {
            return;
        }

        _update = update;
        UpdateBannerText.Text = $"Saiu a versão {update.Version} do MGRScreen (você está na {UpdateChecker.CurrentVersion}).";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_update is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_update.Url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"Abra no navegador: {_update.Url}";
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
    }

    /// <summary>O avatar acompanha o que está sendo digitado: mesma cor e inicial que os outros
    /// vão ver na sala.</summary>
    private void OnNicknameChanged(object sender, TextChangedEventArgs e)
    {
        if (AvatarCircle is null)
        {
            return; // ainda carregando o XAML
        }

        var nickname = NicknameBox.Text.Trim();
        AvatarInitial.Text = nickname.Length > 0 ? nickname[..1].ToUpperInvariant() : "?";
        AvatarCircle.Fill = new SolidColorBrush(ThemeManager.ColorFor(nickname.Length > 0 ? nickname : "Amigo"));
    }

    private void OnRoomPasswordChanged(object sender, RoutedEventArgs e)
    {
        RoomPasswordHint.Visibility = RoomPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- primeiro uso

    private void ShowOnboardingPage(int index)
    {
        _onboardingStep = index;
        var (glyph, title, body) = OnboardingPages[index];
        OnboardingGlyph.Text = glyph;
        OnboardingTitle.Text = title;
        OnboardingBody.Text = body;
        OnboardingStep.Text = $"{index + 1} de {OnboardingPages.Length}";
        OnboardingNextButton.Content = index == OnboardingPages.Length - 1 ? "Entendi" : "Próximo";
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private void OnOnboardingNextClick(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep < OnboardingPages.Length - 1)
        {
            ShowOnboardingPage(_onboardingStep + 1);
            return;
        }

        OnboardingOverlay.Visibility = Visibility.Collapsed;
        _settings.OnboardingDone = true;
        _settings.Save();
    }
}
