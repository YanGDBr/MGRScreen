using System.Net.Http;
using System.Text.Json;

namespace MGRScreen.App.Services;

/// <summary>
/// Pergunta ao GitHub se já saiu uma versão mais nova que a instalada. É só uma consulta pública
/// e anônima: nada do usuário ou da máquina vai na requisição, e nada é baixado — quem decide
/// abrir a página da versão é o usuário.
/// </summary>
public static class UpdateChecker
{
    /// <param name="InstallerUrl">Endereço do instalador do Windows nessa versão, quando a
    /// release tem um e o GitHub informou o SHA-256 dele. Sem os dois, não há atualização
    /// automática: só o link pra página.</param>
    public sealed record UpdateInfo(Version Version, string Tag, string Url,
        string? InstallerUrl = null, string? InstallerSha256 = null, long InstallerSize = 0);

    private const string LatestReleaseApi = "https://api.github.com/repos/YanGDBr/MGRScreen/releases/latest";
    private const string ReleasesPage = "https://github.com/YanGDBr/MGRScreen/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Versão do app (a do csproj), só Major.Minor.Build pra comparar com a tag.</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version);

    /// <summary>
    /// Devolve a versão publicada se ela for MAIOR que a atual; senão null. Nunca lança: sem
    /// rede, limite de requisições do GitHub, resposta estranha ou cancelamento também dão null —
    /// procurar atualização não pode atrapalhar quem só quer usar o app.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token)
    {
        var latest = await GetLatestAsync(token);
        return latest is not null && latest.Version > CurrentVersion ? latest : null;
    }

    /// <summary>A última versão publicada, seja ela mais nova que a atual ou não. Nunca lança.</summary>
    public static async Task<UpdateInfo?> GetLatestAsync(CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, token);
            if (!response.IsSuccessStatusCode)
            {
                return null; // 403/429 = limite de requisições; 404 = nenhuma versão publicada
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = json.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var tag = tagElement.GetString()!.Trim();
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
            {
                return null;
            }

            var version = Normalize(parsed);

            // Esse endereço vai ser aberto no navegador, então só passa se for mesmo uma página
            // https do GitHub; qualquer outra coisa vira a página de versões conhecida.
            var url = ReleasesPage;
            if (root.TryGetProperty("html_url", out var urlElement)
                && urlElement.ValueKind == JsonValueKind.String
                && Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                url = uri.AbsoluteUri;
            }

            var (installerUrl, installerHash, installerSize) = FindInstaller(root);
            return new UpdateInfo(version, tag, url, installerUrl, installerHash, installerSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Procura o instalador do Windows entre os arquivos da release. Só vale se vier com endereço
    /// https do GitHub, tamanho e o SHA-256 que o próprio GitHub calcula no upload — é esse hash
    /// que garante que o arquivo baixado é o que foi publicado.
    /// </summary>
    private static (string? Url, string? Sha256, long Size) FindInstaller(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !name.GetString()!.EndsWith("-windows-x64-setup.exe", StringComparison.OrdinalIgnoreCase)
                || !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String
                || !asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String
                || !asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes <= 0)
            {
                continue;
            }

            var hash = digest.GetString()!;
            if (!hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || hash.Length != 71
                || !hash.AsSpan(7).ToString().All(Uri.IsHexDigit)
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (uri.AbsoluteUri, hash[7..].ToUpperInvariant(), bytes);
        }

        return default;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // A API do GitHub recusa requisição sem User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MGRScreen");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // "1.2" e "1.2.0" têm que ser a mesma versão, e a revisão (4º número) não entra na conta.
    private static Version Normalize(Version? version) =>
        version is null
            ? new Version(0, 0, 0)
            : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
}
