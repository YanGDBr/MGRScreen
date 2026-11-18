using System.Diagnostics;
using System.IO;

namespace MGRScreen.App.Services;

/// <summary>
/// Grava em .mp4 uma transmissão que está sendo assistida (ou enviada), sem recodificar nada: o
/// H.264 que já trafega na rede vai direto pro disco e, no fim, o ffmpeg só o embrulha em MP4.
/// Custo de CPU durante a gravação: praticamente zero.
///
/// Limitações que vêm de gravar o stream cru: não tem áudio, e o stream não carrega relógio — o
/// MP4 sai com a taxa de quadros nominal, então se a transmissão entregou menos quadros que o
/// combinado, o vídeo gravado fica proporcionalmente mais curto que o tempo real.
/// </summary>
public sealed class StreamRecorder : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _outputPath;
    private readonly string _tempPath;
    private readonly int _fps;

    private readonly object _lock = new();
    private readonly Stopwatch _elapsed = new();
    private FileStream? _file;
    private volatile bool _started;
    private Task<string?>? _stopTask;

    /// <summary>
    /// Já cria a pasta e o arquivo temporário, pra um destino inválido (sem permissão, disco
    /// cheio) estourar aqui, na cara de quem clicou em gravar, e não depois no meio da rede.
    /// </summary>
    public StreamRecorder(string ffmpegPath, string outputMp4Path, int fps)
    {
        _ffmpegPath = ffmpegPath;
        _outputPath = Path.GetFullPath(outputMp4Path);
        _fps = fps > 0 ? fps : 30;

        // Mesmo nome do destino, pra fazer sentido pro usuário se o remux falhar e ele sobrar.
        _tempPath = Path.ChangeExtension(_outputPath, ".h264");
        if (string.Equals(_tempPath, _outputPath, StringComparison.OrdinalIgnoreCase))
        {
            _tempPath = _outputPath + ".tmp.h264";
        }

        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Buffer grande: a maioria das chamadas a Write vira só uma cópia em memória.
        _file = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 256 * 1024);
    }

    /// <summary>True depois do primeiro keyframe gravado.</summary>
    public bool HasStarted => _started;

    /// <summary>Tempo de gravação, contado a partir do primeiro keyframe.</summary>
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <summary>
    /// Recebe uma access unit H.264 (Annex-B), como chega da rede. Pode ser chamado de qualquer
    /// thread; o trabalho aqui é uma cópia pro buffer do arquivo, e o array não é guardado.
    /// </summary>
    public void Write(byte[] h264AccessUnit)
    {
        lock (_lock)
        {
            if (_file is null)
            {
                return; // já parou (ou o disco falhou)
            }

            if (!_started)
            {
                // Um arquivo que começa no meio de um GOP abre com segundos de imagem quebrada
                // (ou nem abre), então nada entra antes de um ponto em que o decoder consegue
                // começar do zero.
                if (!ContainsKeyframeStart(h264AccessUnit))
                {
                    return;
                }

                _started = true;
                _elapsed.Start();
            }

            try
            {
                _file.Write(h264AccessUnit, 0, h264AccessUnit.Length);
            }
            catch (IOException)
            {
                // Disco cheio ou removido. Quem chama é a thread da rede, que não pode cair por
                // causa da gravação: para de gravar aqui e o StopAsync aproveita o que deu.
                CloseFile();
            }
        }
    }

    /// <summary>
    /// Fecha a gravação e devolve o caminho do .mp4. Se o ffmpeg não conseguir montar o MP4,
    /// devolve o caminho do .h264 cru, que continua lá (o VLC abre). Null se nada foi gravado.
    /// Chamar de novo devolve o mesmo resultado.
    /// </summary>
    public Task<string?> StopAsync()
    {
        lock (_lock)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            CloseFile();
            // Em outra thread porque o remux de uma gravação longa lê e escreve o arquivo todo.
            _stopTask = Task.Run(FinishAsync);
            return _stopTask;
        }
    }

    // Chamado com _lock tomado.
    private void CloseFile()
    {
        _elapsed.Stop();

        try
        {
            _file?.Dispose();
        }
        catch (IOException)
        {
            // o flush final falhou (disco cheio); o que já foi pro disco ainda serve
        }

        _file = null;
    }

    private async Task<string?> FinishAsync()
    {
        try
        {
            if (!_started || !File.Exists(_tempPath) || new FileInfo(_tempPath).Length == 0)
            {
                TryDelete(_tempPath);
                return null;
            }

            if (await RemuxAsync() && File.Exists(_outputPath) && new FileInfo(_outputPath).Length > 0)
            {
                TryDelete(_tempPath);
                return _outputPath;
            }

            // Um .mp4 pela metade só confunde; o .h264 tem tudo o que foi gravado.
            TryDelete(_outputPath);
            return _tempPath;
        }
        catch
        {
            return File.Exists(_tempPath) ? _tempPath : null;
        }
    }

    private async Task<bool> RemuxAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // -r ANTES do -i: o stream cru não tem timestamps, é essa taxa que dá o tempo de
            // cada quadro. -c copy: só troca a embalagem, sem recodificar.
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y",
                         "-r", _fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "-f", "h264", "-i", _tempPath,
                         "-c", "copy", _outputPath,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            // O stderr precisa ser lido até o fim, senão um ffmpeg falador trava com o pipe cheio.
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stderr;

            return process.ExitCode == 0;
        }
        catch
        {
            return false; // ffmpeg.exe não encontrado, por exemplo
        }
    }

    /// <summary>
    /// Procura um NAL de SPS (tipo 7) ou de quadro IDR (tipo 5). Os NALs do Annex-B são separados
    /// por 00 00 01 (com ou sem um 00 a mais na frente), e o tipo são os 5 bits baixos do byte
    /// seguinte.
    /// </summary>
    private static bool ContainsKeyframeStart(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
            {
                continue;
            }

            var nalType = data[i + 3] & 0x1F;
            if (nalType is 5 or 7)
            {
                return true;
            }

            i += 2; // pula o start code; o laço soma o 1 que falta
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Fecha sem esperar: o MP4 continua sendo montado em segundo plano. Quem precisa do arquivo
    /// pronto (ou do caminho dele) deve chamar <see cref="StopAsync"/> e aguardar.
    /// </summary>
    public void Dispose() => _ = StopAsync();
}
