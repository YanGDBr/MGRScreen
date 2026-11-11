using System.Diagnostics;

namespace MGRScreen.Core.Encoding;

public static class FfmpegLocator
{
    /// <summary>
    /// Ordem de preferência: NVENC (Nvidia) > AMF (AMD) > QSV (Intel) > libx264 via CPU.
    /// Para os encoders de hardware, é feita uma sondagem real de 1 frame de teste com o ffmpeg
    /// para garantir que a GPU física e os drivers estão presentes e funcionais na máquina.
    /// </summary>
    private static readonly string[] EncoderPriority = { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };

    public static string FindFfmpegExecutable()
    {
        // 1) Instalação manual do FFmpeg na pasta "ffmpeg" ao lado do aplicativo.
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        // 2) Repositório em modo desenvolvimento (rodando via `dotnet run` a partir do src).
        var devPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(devPath))
        {
            return Path.GetFullPath(devPath);
        }

        // 3) PATH do sistema.
        return "ffmpeg.exe";
    }

    public static async Task<string> DetectBestVideoEncoderAsync(string ffmpegPath)
    {
        var available = await ListAvailableEncodersAsync(ffmpegPath);

        foreach (var candidate in EncoderPriority)
        {
            if (!available.Contains(candidate))
            {
                continue;
            }

            if (candidate == "libx264")
            {
                return "libx264";
            }

            if (await TestHardwareEncoderWorksAsync(ffmpegPath, candidate))
            {
                return candidate;
            }
        }

        return "libx264"; // fallback final, deve sempre existir em qualquer build do ffmpeg
    }

    private static async Task<bool> TestHardwareEncoderWorksAsync(string ffmpegPath, string encoder)
    {
        try
        {
            // Gera 1 frame de teste (256x256) em memória e tenta codificar com o encoder de hardware especificado.
            // Se os drivers da GPU correspondente não estiverem instalados (ex: NVENC em máquina AMD ou AMF em Nvidia),
            // o FFmpeg falhará imediatamente ao tentar inicializar o dispositivo e sairá com código != 0.
            var psi = new ProcessStartInfo(ffmpegPath, $"-hide_banner -loglevel error -f lavfi -i color=s=256x256:d=0.01 -c:v {encoder} -f null -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task<HashSet<string>> ListAvailableEncodersAsync(string ffmpegPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return result;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            foreach (var candidate in EncoderPriority)
            {
                if (output.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // ffmpeg.exe não encontrado — quem chamou deve avisar o usuário claramente
            // (ver README/tools/ffmpeg) em vez de travar aqui.
        }

        return result;
    }
}
