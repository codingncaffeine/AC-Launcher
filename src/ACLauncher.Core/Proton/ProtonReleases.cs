using System.Text.Json;

namespace ACLauncher.Core.Proton;

public enum ProtonFamily { GEProton, UMUProton }

/// <summary>A Proton build published as GitHub releases.</summary>
public sealed record ProtonFamilyInfo(ProtonFamily Family, string Name, string Repository, string Description)
{
    public static readonly IReadOnlyList<ProtonFamilyInfo> All =
    [
        new(ProtonFamily.GEProton, "GE-Proton", "GloriousEggroll/proton-ge-custom",
            "A community build of Proton with extra game fixes and media codecs."),
        new(ProtonFamily.UMUProton, "UMU-Proton", "Open-Wine-Components/umu-proton",
            "Valve's Proton, packaged to run outside Steam."),
    ];

    public static ProtonFamilyInfo Get(ProtonFamily family) => All.First(i => i.Family == family);

    /// <summary>Which build a release or install folder name belongs to, if it is one of ours.</summary>
    public static ProtonFamily? FromName(string name) =>
        name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase) ? ProtonFamily.GEProton
        : name.StartsWith("UMU-Proton", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ULWGL-Proton", StringComparison.OrdinalIgnoreCase) ? ProtonFamily.UMUProton
        : null;
}

/// <summary>The values <see cref="LauncherSettings.ProtonPath"/> takes besides a directory.</summary>
public static class ProtonChoiceValue
{
    public const string LatestGE = "latest:GE-Proton";
    public const string LatestUMU = "latest:UMU-Proton";

    public static ProtonFamily? LatestFamily(string? value) => value switch
    {
        LatestGE => ProtonFamily.GEProton,
        LatestUMU => ProtonFamily.UMUProton,
        _ => null,
    };
}

public sealed record ProtonRelease(
    ProtonFamily Family,
    string Tag,
    DateTimeOffset Published,
    bool Prerelease,
    string ArchiveName,
    string ArchiveUrl,
    long ArchiveSize,
    string? ChecksumUrl);

/// <summary>Lists the releases of a Proton build from GitHub, cached for an hour.</summary>
public static class ProtonReleases
{
    private const int PageSize = 100;
    private const int MaxPages = 5;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    /// <summary>Newest first. Falls back to a stale cache when GitHub cannot be reached.</summary>
    /// <exception cref="ProtonDownloadException">No list could be fetched and none is cached.</exception>
    public static async Task<IReadOnlyList<ProtonRelease>> GetAsync(HttpClient http, ProtonFamily family, string cacheDir,
        bool forceRefresh, CancellationToken cancellationToken)
    {
        var info = ProtonFamilyInfo.Get(family);
        var cacheFile = Path.Combine(cacheDir, $"proton-releases-{family}.json");
        if (!forceRefresh && File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheLifetime
            && ReadCache(cacheFile) is { } fresh)
            return fresh;

        try
        {
            var releases = new List<ProtonRelease>();
            for (var page = 1; page <= MaxPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{info.Repository}/releases?per_page={PageSize}&page={page}");
                request.Headers.UserAgent.ParseAdd("ac-launcher");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                releases.AddRange(Parse(json, family, out var count));
                if (count < PageSize) break;
            }
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(releases, JsonStore.Options), cancellationToken).ConfigureAwait(false);
            return releases;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or TaskCanceledException
                                      && !cancellationToken.IsCancellationRequested)
        {
            if (ReadCache(cacheFile) is { } stale)
            {
                Log.Warn($"Could not refresh the {info.Name} release list ({e.Message}); using the cached list");
                return stale;
            }
            throw new ProtonDownloadException($"Could not get the {info.Name} release list: {e.Message}");
        }
    }

    /// <summary>Reads one page of the GitHub releases API, keeping releases with an x86-64 archive.</summary>
    internal static List<ProtonRelease> Parse(string json, ProtonFamily family, out int releaseCount)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a list of releases.");
        releaseCount = root.GetArrayLength();

        var releases = new List<ProtonRelease>();
        foreach (var release in root.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = release.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tag)) continue;

            JsonElement? archive = null;
            string? checksumUrl = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (IsForeignArchitecture(name)) continue;
                if (name.EndsWith(".sha512sum", StringComparison.Ordinal))
                    checksumUrl = asset.GetProperty("browser_download_url").GetString();
                else if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tar.xz", StringComparison.Ordinal))
                    archive = asset;
            }
            if (archive is not { } a) continue;

            var published = release.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetDateTimeOffset()
                : release.GetProperty("created_at").GetDateTimeOffset();
            releases.Add(new ProtonRelease(
                family,
                tag.Trim(),
                published,
                release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                a.GetProperty("name").GetString()!,
                a.GetProperty("browser_download_url").GetString()!,
                a.GetProperty("size").GetInt64(),
                checksumUrl));
        }
        return releases;
    }

    private static bool IsForeignArchitecture(string name) =>
        name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) || name.Contains("arm64", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProtonRelease>? ReadCache(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<ProtonRelease>>(File.ReadAllText(file), JsonStore.Options) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
