using System.Reflection;
using System.Runtime.InteropServices;
using MGRScreen.Core.Networking;

// Servidor de sala do MGRScreen pra rodar numa VPS.
//
// Ele só REPASSA: recebe o vídeo, o áudio, a voz e o chat de cada pessoa e entrega aos outros.
// Não decodifica nem recodifica nada, então quase não usa CPU — o que ele consome é banda.

// Os acentos das mensagens saem certos no terminal do Windows e nos logs do systemd.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var options = Options.Parse(args);
if (options is null)
{
    return 2;
}

var version = Assembly.GetExecutingAssembly().GetName().Version;
Console.WriteLine($"MGRScreen Server {version?.ToString(3)}");

if (options.Password is null)
{
    Console.WriteLine();
    Console.WriteLine("ATENÇÃO: sala SEM senha. Qualquer pessoa que descobrir este endereço consegue entrar e");
    Console.WriteLine("assistir. Numa VPS com IP público, prefira --senha (ou a variável MGRSCREEN_PASSWORD).");
}

using var server = new RoomServer(options.Name, options.Port, options.Port + 1, options.Password);
server.Log += line => Log(line);

try
{
    // Sem anúncio pelo Tailscale: numa VPS não há rede local nem tailnet pra avisar.
    server.Start(announceOnTailscale: false);
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Não consegui abrir as portas {options.Port} e {options.Port + 1}: {ex.Message}");
    Console.Error.WriteLine("Outro programa pode estar usando essas portas, ou falta permissão.");
    return 1;
}

Console.WriteLine();
Log($"Sala \"{options.Name}\" no ar, portas TCP {options.Port} (controle) e {options.Port + 1} (mídia).");
Log(options.Password is null ? "Sala aberta (sem senha)." : "Sala protegida por senha.");
Log("Libere essas duas portas no firewall. No MGRScreen, use \"Entrar por IP\" com o endereço desta máquina.");
if (options.Port != 51900)
{
    Log("AVISO: o MGRScreen só se conecta na porta 51900. Fora dela, os clientes não vão achar a sala.");
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // deixa o encerramento acontecer aqui embaixo, com a sala fechada direito
    stop.Cancel();
};
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true; // `systemctl stop` manda SIGTERM
    stop.Cancel();
});

// A cada 30 s, quanta gente há e quanta banda está saindo. Fica quieto com a sala vazia e parada,
// pra não encher o log de quem deixa o servidor ligado o mês inteiro.
var lastCount = -1;
try
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stop.Token);

        var mbps = server.TakeBytesRelayed() * 8 / 30.0 / 1_000_000;
        var count = server.ParticipantCount;
        if (count != lastCount || mbps >= 0.05)
        {
            Log($"{count} na sala · saída {mbps:0.0} Mbps");
            lastCount = count;
        }
    }
}
catch (OperationCanceledException)
{
}

Log("Encerrando a sala.");
server.Stop();
return 0;

static void Log(string line) => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}");

/// <summary>Opções da linha de comando, com as variáveis de ambiente como padrão (pra systemd e
/// Docker, e pra senha não aparecer na lista de processos).</summary>
internal sealed record Options(string Name, int Port, string? Password)
{
    public static Options? Parse(string[] args)
    {
        var name = Environment.GetEnvironmentVariable("MGRSCREEN_NAME");
        var password = Environment.GetEnvironmentVariable("MGRSCREEN_PASSWORD");
        var port = int.TryParse(Environment.GetEnvironmentVariable("MGRSCREEN_PORT"), out var envPort) ? envPort : 51900;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--nome" or "--name":
                    name = Next();
                    break;
                case "--senha" or "--password":
                    password = Next();
                    break;
                case "--porta" or "--port":
                    if (!int.TryParse(Next(), out port))
                    {
                        Console.Error.WriteLine("--porta precisa de um número.");
                        return null;
                    }
                    break;
                case "-h" or "--help" or "--ajuda":
                    PrintHelp();
                    return null;
                default:
                    Console.Error.WriteLine($"Opção desconhecida: {arg}");
                    PrintHelp();
                    return null;
            }
        }

        if (port is < 1 or > 65534)
        {
            Console.Error.WriteLine("Porta inválida.");
            return null;
        }

        return new Options(
            string.IsNullOrWhiteSpace(name) ? "Sala na VPS" : name.Trim(),
            port,
            string.IsNullOrEmpty(password) ? null : password);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Uso: mgrscreen-server [--nome "Sala da turma"] [--senha SEGREDO] [--porta 51900]

              --nome    Nome que aparece na lista de salas do MGRScreen. Padrão: "Sala na VPS".
              --senha   Senha pra entrar. Sem ela a sala fica aberta a quem souber o endereço.
              --porta   Porta de controle; a de mídia é a seguinte. Padrão: 51900 (e 51901).
                        O MGRScreen só conecta na 51900, então troque só se souber o que faz.

            Variáveis de ambiente equivalentes: MGRSCREEN_NAME, MGRSCREEN_PASSWORD, MGRSCREEN_PORT.
            A senha por variável é preferível: na linha de comando ela aparece na lista de processos.
            """);
    }
}
