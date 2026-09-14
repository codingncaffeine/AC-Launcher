using System.Formats.Tar;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
            // Relative PATH entries would resolve against the working directory; never run a program from there.
            if (!Path.IsPathRooted(dir)) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Downloads the latest self-contained umu release, checks it against the SHA-256 digest GitHub publishes
    /// for the file, and installs it as <see cref="ManagedRunPath"/>.
    /// </summary>
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
        var expected = ParseSha256Digest(asset.Digest)
            ?? throw new InvalidOperationException($"umu-launcher {release.TagName} has no published checksum, so it was not installed.");

        var toolsDir = Path.GetDirectoryName(Path.GetDirectoryName(ManagedRunPath)!)!;
        AppPaths.CreatePrivateDirectory(toolsDir);
        var staging = Path.Combine(toolsDir, ".umu-staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report($"Downloading umu-launcher {release.TagName}…");
            var archive = Path.Combine(staging, "umu.tar");
            string actual;
            var download = await http.GetStreamAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
            await using (download.ConfigureAwait(false))
            {
                var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1 << 16];
                    int read;
                    while ((read = await download.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                }
            }
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The umu-launcher download did not match its published checksum, so it was discarded.");

            // TarFile refuses entries that would land outside the destination.
            var unpacked = Path.Combine(staging, "unpacked");
            Directory.CreateDirectory(unpacked);
            await TarFile.ExtractToDirectoryAsync(archive, unpacked, overwriteFiles: false, cancellationToken).ConfigureAwait(false);

            var stagedRun = Path.Combine(unpacked, "umu", "umu-run");
            if (!File.Exists(stagedRun) || new FileInfo(stagedRun).LinkTarget is not null)
                throw new InvalidOperationException("The umu-launcher download did not contain umu/umu-run.");
            File.SetUnixFileMode(stagedRun, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var installDir = Path.GetDirectoryName(ManagedRunPath)!;
            if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true);
            Directory.Move(Path.Combine(unpacked, "umu"), installDir);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        progress?.Report($"Installed umu-launcher {release.TagName}.");
        Log.Info($"Installed umu-launcher {release.TagName} (SHA-256 verified) to {Path.GetDirectoryName(ManagedRunPath)}");
        return release.TagName;
    }

    /// <summary>GitHub's asset digest is <c>sha256:&lt;64 hex digits&gt;</c>.</summary>
    internal static string? ParseSha256Digest(string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal) && digest[7..].All(char.IsAsciiHexDigit)
            ? digest[7..]
            : null;

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] List<Asset> Assets);

    private sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}
