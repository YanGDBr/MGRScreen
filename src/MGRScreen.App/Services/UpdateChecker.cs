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
    public sealed record UpdateInfo(Version Version, string Tag, string Url);

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
            if (version <= CurrentVersion)
            {
                return null;
            }

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

            return new UpdateInfo(version, tag, url);
        }
        catch
        {
            return null;
        }
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
