namespace MGRScreen.App.Services;

/// <summary>
/// Ícone na bandeja do Windows, pra o app continuar acessível (e dar pra parar de compartilhar)
/// com a janela escondida. O WPF não tem isso, então é o NotifyIcon do WinForms — que funciona
/// sem nenhuma janela WinForms, desde que seja criado na thread de UI: ele se pendura no loop de
/// mensagens dessa thread (o do Dispatcher), e é por isso que os eventos abaixo já chegam nela.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Duplo clique no ícone ou item "Abrir".</summary>
    public event Action? OpenRequested;

    /// <summary>Item "Parar de compartilhar".</summary>
    public event Action? StopSharingRequested;

    /// <summary>Item "Sair".</summary>
    public event Action? ExitRequested;

    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _stopSharingItem;

    // Só o ícone extraído do .exe é nosso pra descartar; o SystemIcons.Application é compartilhado.
    private readonly System.Drawing.Icon? _ownedIcon;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _ownedIcon = LoadExecutableIcon();

        var openItem = new System.Windows.Forms.ToolStripMenuItem("Abrir");
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold); // ação padrão (duplo clique)
        openItem.Click += (_, _) => OpenRequested?.Invoke();

        _stopSharingItem = new System.Windows.Forms.ToolStripMenuItem("Parar de compartilhar") { Enabled = false };
        _stopSharingItem.Click += (_, _) => StopSharingRequested?.Invoke();

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Sair");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu = new System.Windows.Forms.ContextMenuStrip();
        _menu.Items.Add(openItem);
        _menu.Items.Add(_stopSharingItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application,
            // O Windows recusa (com exceção) texto de dica acima de 127 caracteres.
            Text = tooltip.Length > 127 ? tooltip[..127] : tooltip,
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke();
            }
        };
    }

    /// <summary>Habilita/desabilita o item "Parar de compartilhar".</summary>
    public bool StopSharingEnabled
    {
        get => _stopSharingItem.Enabled;
        set => _stopSharingItem.Enabled = value;
    }

    /// <summary>Notificação do Windows saindo do ícone (ex.: "continua compartilhando em segundo plano").</summary>
    public void ShowBalloon(string title, string text)
    {
        if (_disposed || string.IsNullOrEmpty(text))
        {
            return; // o NotifyIcon lança se o texto do balão vier vazio
        }

        // O tempo é ignorado desde o Windows Vista (vale a configuração do sistema), mas o
        // parâmetro é obrigatório.
        _notifyIcon.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.None);
    }

    private static System.Drawing.Icon? LoadExecutableIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            return path is null ? null : System.Drawing.Icon.ExtractAssociatedIcon(path);
        }
        catch
        {
            return null; // cai no ícone genérico do Windows
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Sem esconder antes, o ícone fica "fantasma" na bandeja até o mouse passar por cima.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }
}
