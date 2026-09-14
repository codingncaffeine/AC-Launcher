using System.Formats.Tar;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACLauncher.Core.Launching;

/// <summary>
/// Locates <c>umu-run</c>, the tool that runs Proton outside Steam, and can install the project's
/// self-contained release into the launcher's data folder when the system has none.
/// </summary>
public static class Umu
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Open-Wine-Components/umu-launcher/releases/latest";

    /// <summary>Where the launcher's own copy lives: <c>&lt;data&gt;/tools/umu/umu-run</c>.</summary>
    public static string ManagedRunPath => Path.Combine(AppPaths.DataDir, "tools", "umu", "umu-run");

    /// <summary>An explicit path first, then <c>umu-run</c> on PATH, then the launcher's own copy.</summary>
    public static string? Find(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;
        return FindOnPath("umu-run") ?? (File.Exists(ManagedRunPath) ? ManagedRunPath : null);
    }

    /// <summary>The self-contained release is a Python zip application, so it needs <c>python3</c>.</summary>
    public static bool HasPython => FindOnPath("python3") is not null;

    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Downloads the latest self-contained umu release and installs it as <see cref="ManagedRunPath"/>.</summary>
    /// <returns>The installed version tag.</returns>
    public static async Task<string> InstallManagedAsync(HttpClient http, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Looking up the latest umu-launcher release…");
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("ac-launcher");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The release lookup returned nothing.");
        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith("-zipapp.tar", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Release {release.TagName} has no zipapp download.");

        var toolsDir = Path.GetDirectoryName(Path.GetDirectoryName(ManagedRunPath)!)!;
        var staging = Path.Combine(toolsDir, ".umu-staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        progress?.Report($"Downloading umu-launcher {release.TagName}…");
        await using (var download = await http.GetStreamAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false))
            await TarFile.ExtractToDirectoryAsync(download, staging, overwriteFiles: true, cancellationToken).ConfigureAwait(false);

        var stagedRun = Path.Combine(staging, "umu", "umu-run");
        if (!File.Exists(stagedRun))
            throw new InvalidOperationException("The umu-launcher download did not contain umu/umu-run.");
        File.SetUnixFileMode(stagedRun, File.GetUnixFileMode(stagedRun) | UnixFileMode.UserExecute | UnixFileMode.UserRead);

        var installDir = Path.GetDirectoryName(ManagedRunPath)!;
        if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true);
        Directory.Move(Path.Combine(staging, "umu"), installDir);
        Directory.Delete(staging, recursive: true);

        progress?.Report($"Installed umu-launcher {release.TagName}.");
        Log.Info($"Installed umu-launcher {release.TagName} to {installDir}");
        return release.TagName;
    }

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] List<Asset> Assets);

    private sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
