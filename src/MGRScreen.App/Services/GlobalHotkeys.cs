using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MGRScreen.App.Services;

/// <summary>
/// Atalhos de teclado que funcionam com o app em segundo plano (no meio de um jogo, por exemplo).
/// O Windows entrega cada atalho registrado como WM_HOTKEY na janela dona do registro, então tudo
/// aqui — registrar, remover e os callbacks — acontece na thread de UI dessa janela.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    // Sem isto, segurar o atalho dispara o callback dezenas de vezes por segundo.
    private const uint MOD_NOREPEAT = 0x4000;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private sealed record Registration(int NativeId, ModifierKeys Modifiers, Key Key, Action Callback);

    private readonly IntPtr _handle;
    private readonly HwndSource? _source;
    private readonly Dictionary<string, Registration> _registrations = new();
    private int _nextNativeId = 1;
    private bool _disposed;

    public GlobalHotkeys(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
    }

    /// <summary>
    /// Registra (ou troca) o atalho de <paramref name="id"/>. Devolve false se o texto for
    /// inválido ou se a combinação já estiver em uso por outro programa — nesse caso o atalho
    /// anterior do mesmo id, se havia, continua valendo.
    /// </summary>
    public bool Register(string id, string gesture, Action callback)
    {
        if (_disposed || !TryParse(gesture, out var modifiers, out var key))
        {
            return false;
        }

        // Uma letra sozinha como atalho global roubaria a tecla de todos os programas.
        if (modifiers == ModifierKeys.None && key is < Key.F1 or > Key.F24)
        {
            return false;
        }

        _registrations.TryGetValue(id, out var previous);

        // Mesma combinação de antes: só troca o callback. Tentar registrar de novo falharia,
        // porque pro Windows ela já está em uso (por nós mesmos).
        if (previous is not null && previous.Modifiers == modifiers && previous.Key == key)
        {
            _registrations[id] = previous with { Callback = callback };
            return true;
        }

        var nativeId = _nextNativeId++;
        if (!RegisterHotKey(_handle, nativeId, ToNativeModifiers(modifiers) | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            return false;
        }

        // Só solta o antigo depois que o novo pegou, pra uma troca que falha não deixar o
        // usuário sem atalho nenhum.
        if (previous is not null)
        {
            UnregisterHotKey(_handle, previous.NativeId);
        }

        _registrations[id] = new Registration(nativeId, modifiers, key, callback);
        return true;
    }

    public void Unregister(string id)
    {
        if (_registrations.Remove(id, out var registration))
        {
            UnregisterHotKey(_handle, registration.NativeId);
        }
    }

    /// <summary>
    /// Lê "Ctrl+Alt+S", "Shift+F8", "V"... Os modificadores aceitos são Ctrl, Alt, Shift e Win; a
    /// tecla é um nome de <see cref="Key"/> do WPF (sem diferenciar maiúsculas) ou um dígito.
    /// </summary>
    public static bool TryParse(string gesture, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var parts = gesture.Split('+');

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            var isLast = i == parts.Length - 1;

            ModifierKeys modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" or "windows" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };

            if (!isLast)
            {
                // Tudo antes da tecla tem que ser modificador, sem repetir.
                if (modifier == ModifierKeys.None || (modifiers & modifier) != 0)
                {
                    return false;
                }
                modifiers |= modifier;
                continue;
            }

            if (!TryParseKey(part, out key))
            {
                // "Ctrl+Alt" (só modificadores), "Ctrl+" ou um nome que não existe.
                modifiers = ModifierKeys.None;
                return false;
            }
        }

        return true;
    }

    private static bool TryParseKey(string text, out Key key)
    {
        key = Key.None;

        if (text.Length == 0)
        {
            return false;
        }

        // Os dígitos se chamam D0..D9 no enum, mas ninguém escreve "Ctrl+D1".
        if (text.Length == 1 && char.IsAsciiDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }

        // O Enum.TryParse também aceita o valor numérico ("44"); isso não é nome de tecla.
        if (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+')
        {
            return false;
        }

        return Enum.TryParse(text, ignoreCase: true, out key)
            && Enum.IsDefined(key)
            && key != Key.None
            && KeyInterop.VirtualKeyFromKey(key) != 0;
    }

    /// <summary>O inverso de <see cref="TryParse"/>: "Ctrl+Alt+S".</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);

        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            // O enum tem nomes repetidos pro mesmo valor e o ToString() escolhe o menos óbvio.
            Key.Capital => "CapsLock",
            Key.Return => "Enter",
            Key.Next => "PageDown",
            Key.Prior => "PageUp",
            Key.Snapshot => "PrintScreen",
            _ => key.ToString(),
        });

        return string.Join("+", parts);
    }

    /// <summary>
    /// A combinação está pressionada agora? Pensado pro "apertar pra falar": consultado num
    /// timer, funciona com o app em segundo plano e não tira a tecla de ninguém (ao contrário do
    /// <see cref="Register"/>), então aceita tecla sozinha. Pode ser chamado de qualquer thread.
    /// </summary>
    public static bool IsKeyDown(string gesture)
    {
        if (!TryParse(gesture, out var modifiers, out var key))
        {
            return false;
        }

        if (modifiers.HasFlag(ModifierKeys.Control) && !IsDown(VK_CONTROL)) return false;
        if (modifiers.HasFlag(ModifierKeys.Alt) && !IsDown(VK_MENU)) return false;
        if (modifiers.HasFlag(ModifierKeys.Shift) && !IsDown(VK_SHIFT)) return false;
        if (modifiers.HasFlag(ModifierKeys.Windows) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN)) return false;

        return IsDown(KeyInterop.VirtualKeyFromKey(key));
    }

    // O bit mais alto é "pressionada neste instante"; o mais baixo ("foi pressionada desde a
    // última consulta") não é confiável e fica de fora.
    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        uint native = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) native |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) native |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) native |= MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) native |= MOD_WIN;
        return native;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        var nativeId = wParam.ToInt32();

        foreach (var registration in _registrations.Values)
        {
            if (registration.NativeId == nativeId)
            {
                handled = true;
                // Sai do laço antes de chamar: o callback pode registrar/remover atalhos.
                registration.Callback();
                break;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var registration in _registrations.Values)
        {
            UnregisterHotKey(_handle, registration.NativeId);
        }
        _registrations.Clear();

        _source?.RemoveHook(WndProc);
    }
}
