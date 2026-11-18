using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace MGRScreen.App.Services;

/// <summary>
/// Baixa o instalador da versão nova e troca o app por ela, quando o usuário clica em "Atualizar
/// agora". Nada acontece sozinho em segundo plano: atualizar no meio de uma sala derrubaria a
/// transmissão de todo mundo.
///
/// Só a cópia INSTALADA se atualiza assim. A versão portátil (zip) não tem instalador nem
/// desinstalador ao lado, e um executável não consegue substituir a si mesmo — pra ela o botão
/// continua abrindo a página de download.
/// </summary>
public static class AppUpdater
{
    /// <summary>O instalador (Inno Setup) deixa o desinstalador ao lado do executável. É assim que
    /// se distingue a cópia instalada da portátil.</summary>
    public static bool IsInstalledCopy { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static bool CanInstall(UpdateChecker.UpdateInfo update) =>
        IsInstalledCopy && update.InstallerUrl is not null && update.InstallerSha256 is not null && update.InstallerSize > 0;

    /// <summary>
    /// Baixa o instalador pra pasta temporária e confere tamanho e SHA-256 contra o que o GitHub
    /// informou. Devolve o caminho do arquivo. Lança se o download falhar ou o arquivo não bater —
    /// nesse caso o que foi baixado é apagado e nada é executado.
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateChecker.UpdateInfo update, IProgress<double>? progress, CancellationToken token)
    {
        if (update.InstallerUrl is null || update.InstallerSha256 is null || update.InstallerSize <= 0)
        {
            throw new InvalidOperationException("Esta versão não tem instalador pra atualização automática.");
        }

        var folder = Path.Combine(Path.GetTempPath(), "MGRScreen-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"MGRScreen-{update.Version}-setup.exe");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MGRScreen");

            using var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > update.InstallerSize)
                    {
                        break; // maior que o anunciado: nem termina de baixar
                    }

                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report((double)total / update.InstallerSize);
                }
            }

            var hash = Convert.ToHexString(sha.GetHashAndReset());
            if (total != update.InstallerSize || !hash.Equals(update.InstallerSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("O arquivo baixado não confere com o publicado. Nada foi instalado.");
            }

            return path;
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Dispara o instalador em modo silencioso e, quando ele terminar, abre o app de novo. Quem
    /// chama precisa encerrar o app logo em seguida (o instalador troca os arquivos em uso).
    /// </summary>
    public static void InstallAndRestart(string installerPath)
    {
        var app = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Não sei onde está o executável do app.");

        // Um cmd escondido faz a sequência, porque este processo vai estar morto quando o
        // instalador terminar: espera 1 s (pra este processo sair), instala, e reabre o app.
        // "start /wait" espera o instalador; o "&" reabre mesmo que a instalação falhe, pra pessoa
        // não ficar sem app.
        var script = $"ping -n 2 127.0.0.1 >nul & start \"\" /wait \"{installerPath}\" /SP- /VERYSILENT " +
                     $"/SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS & start \"\" \"{app}\"";

        Process.Start(new ProcessStartInfo("cmd.exe", $"/s /c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
    }
}
