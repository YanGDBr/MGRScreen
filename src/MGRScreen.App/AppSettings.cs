using System.IO;
using System.Text.Json;

namespace MGRScreen.App;

/// <summary>
/// As poucas coisas que vale lembrar entre uma execução e outra. Fica em
/// %APPDATA%\MGRScreen\settings.json — arquivo solto, não registro do Windows, pra continuar
/// sendo "copiar a pasta e usar".
/// </summary>
public sealed class AppSettings
{
    public string Nickname { get; set; } = "";

    /// <summary>Último IP digitado à mão, pra não redigitar quando a busca automática não acha
    /// (caso de port forwarding pela internet, por exemplo).</summary>
    public string LastManualAddress { get; set; } = "";

    /// <summary>Onde foi a última sala, pra oferecer "voltar" com um clique no menu.</summary>
    public string LastRoomAddress { get; set; } = "";
    public string LastRoomHost { get; set; } = "";

    /// <summary>Volume geral da sala, 0 a 100.</summary>
    public int MasterVolume { get; set; } = 100;

    /// <summary>Volume por pessoa (0 a 200) e quem está silenciado, guardados pelo nickname —
    /// o id de participante muda a cada conexão, o nickname não.</summary>
    public Dictionary<string, int> Volumes { get; set; } = new();
    public List<string> MutedNicknames { get; set; } = new();

    /// <summary>Índices dos combos da sala, pra não reconfigurar tudo toda vez.</summary>
    public int QualityIndex { get; set; }
    public int AudioModeIndex { get; set; }

    /// <summary>Monitor compartilhado da última vez (quando deu pra identificar).</summary>
    public string LastMonitorDeviceName { get; set; } = "";

    /// <summary>Última câmera usada — aparece primeiro na lista quando há mais de uma.</summary>
    public string LastCameraId { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MGRScreen",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Config corrompida ou sem permissão não pode impedir o app de abrir.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
