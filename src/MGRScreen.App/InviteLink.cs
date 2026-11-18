using Microsoft.Win32;

namespace MGRScreen.App;

/// <summary>
/// Link de convite no formato <c>mgrscreen://endereço</c>. Clicar nele (no Discord, no navegador)
/// abre o MGRScreen já tentando entrar naquela sala, em vez de a pessoa copiar e colar um IP.
/// A senha da sala nunca vai no link.
/// </summary>
public sealed record InviteLink(string Address)
{
    private const string Scheme = "mgrscreen";

    public static string Create(string address) => $"{Scheme}://{address}";

    public static InviteLink? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !text.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var address = text[(Scheme.Length + 3)..].Trim().TrimEnd('/');

        // Só o que um endereço pode ter: nome de máquina, IPv4 ou IPv6. Qualquer outra coisa é
        // lixo (ou tentativa de injetar argumento) e o link é ignorado.
        var valid = address.Length is > 0 and <= 253 &&
                    address.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']');

        return valid ? new InviteLink(address) : null;
    }

    /// <summary>
    /// Associa o esquema <c>mgrscreen://</c> a este executável, só para o usuário atual (HKCU, sem
    /// pedir administrador). Refaz a cada execução porque a versão portátil pode mudar de pasta.
    /// </summary>
    public static void RegisterProtocol()
    {
        try
        {
            // Só o próprio MGRScreen.exe se registra: hospedado por outro executável (testes,
            // `dotnet run`), o link apontaria pra coisa errada.
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) ||
                !string.Equals(System.IO.Path.GetFileNameWithoutExtension(exe), "MGRScreen", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            key.SetValue("", "URL:MGRScreen");
            key.SetValue("URL Protocol", "");

            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // Sem permissão pra escrever no registro: o link só não vai funcionar.
        }
    }
}
