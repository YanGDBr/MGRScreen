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

    // ---------------------------------------------------------------- aparência

    /// <summary>Cor de destaque em hexadecimal (#RRGGBB). O resto da paleta não muda.</summary>
    public string AccentColor { get; set; } = "#7C5CFF";

    // ---------------------------------------------------------------- disposição das telas

    /// <summary>"auto" (grade calculada), "focus" (uma grande + miniaturas) ou "free"
    /// (cada tela onde a pessoa deixou).</summary>
    public string LayoutMode { get; set; } = "auto";

    /// <summary>Posição e tamanho de cada tela no modo livre, em fração da área (x, y, largura,
    /// altura de 0 a 1), guardados por "nickname|tipo" — o id do participante muda a cada
    /// conexão.</summary>
    public Dictionary<string, double[]> FreeLayout { get; set; } = new();

    public bool ChatPanelOpen { get; set; }

    // ---------------------------------------------------------------- comportamento

    /// <summary>O que o X da sala faz: 0 = perguntar · 1 = manter em segundo plano (bandeja) ·
    /// 2 = fechar de vez.</summary>
    public int CloseBehavior { get; set; }

    /// <summary>Quem codifica e decodifica o vídeo: 0 = placa de vídeo (quando disponível) ·
    /// 1 = processador.</summary>
    public int VideoProcessing { get; set; }
    public bool Notifications { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Já viu a explicação de primeiro uso.</summary>
    public bool OnboardingDone { get; set; }

    // ---------------------------------------------------------------- atalhos e voz

    public string HotkeyToggleShare { get; set; } = "Ctrl+Alt+S";
    public string HotkeyToggleMic { get; set; } = "Ctrl+Alt+M";

    /// <summary>0 = apertar pra falar · 1 = ativação por voz · 2 = microfone sempre aberto ·
    /// 3 = não usar o microfone (o botão some da sala).</summary>
    public int VoiceMode { get; set; }
    public string PushToTalkKey { get; set; } = "F9";
    public string MicDeviceId { get; set; } = "";

    // ---------------------------------------------------------------- pastas

    public string RecordingsFolder { get; set; } = "";
    public string ScreenshotsFolder { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveRecordingsFolder => string.IsNullOrWhiteSpace(RecordingsFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MGRScreen")
        : RecordingsFolder;

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveScreenshotsFolder => string.IsNullOrWhiteSpace(ScreenshotsFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MGRScreen")
        : ScreenshotsFolder;

    /// <summary>Uma instância só pro app inteiro. Antes cada janela carregava a sua e, ao salvar,
    /// uma sobrescrevia o que a outra tinha mudado.</summary>
    public static AppSettings Current { get; } = Load();

    /// <summary>Avisa quem estiver aberto que algo mudou na tela de configurações.</summary>
    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();

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
