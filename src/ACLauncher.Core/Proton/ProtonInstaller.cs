using System.Diagnostics;
using System.Security.Cryptography;
using ACLauncher.Core.Steam;

namespace ACLauncher.Core.Proton;

public sealed class ProtonDownloadException(string message) : Exception(message);

public readonly record struct ProtonInstallProgress(string Message, long BytesDone, long? BytesTotal);

public sealed record InstalledProton(string Name, string Directory, ProtonFamily? Family);

/// <summary>Downloads, verifies, unpacks and removes Proton builds in the launcher's own folder.</summary>
public static class ProtonInstaller
{
    private const long ProgressStep = 4 * 1024 * 1024;

    public static string DefaultRoot => Path.Combine(AppPaths.DataDir, "proton");

    /// <summary>Builds installed by the launcher, newest first.</summary>
    public static IReadOnlyList<InstalledProton> ListInstalled(string? root = null)
    {
        root ??= DefaultRoot;
        if (!Directory.Exists(root)) return [];
        return SteamLocator.SafeEnumerateDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && File.Exists(Path.Combine(d, "proton")))
            .Select(d => new InstalledProton(Path.GetFileName(d), d, ProtonFamilyInfo.FromName(Path.GetFileName(d))))
            .OrderByDescending(p => p.Name, NaturalComparer.Instance)
            .ToList();
    }

    public static string? FindInstalled(string name, string? root = null) =>
        ListInstalled(root).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))?.Directory;

    public static string? NewestInstalled(ProtonFamily family, string? root = null) =>
        ListInstalled(root).FirstOrDefault(p => p.Family == family)?.Directory;

    /// <summary>
    /// Downloads a release, checks it against every checksum published for it (its <c>.sha512sum</c> file and
    /// GitHub's SHA-256 digest), and unpacks it to <c>&lt;root&gt;/&lt;tag&gt;</c>. A release with no published
    /// checksum is refused. Nothing is left behind if any step fails or is cancelled.
    /// </summary>
    /// <returns>The installed build's directory.</returns>
    public static async Task<string> InstallAsync(HttpClient http, ProtonRelease release, IProgress<ProtonInstallProgress>? progress,
        CancellationToken cancellationToken, string? root = null)
    {
        root ??= DefaultRoot;
        ValidateName(release.Tag);
        var target = Path.Combine(root, release.Tag);
        if (File.Exists(Path.Combine(target, "proton"))) return target;
        if (!release.IsVerifiable)
            throw new ProtonDownloadException($"{release.Tag} publishes no checksum, so it cannot be verified and was not installed.");

        AppPaths.CreatePrivateDirectory(root);
        var work = Path.Combine(root, $".install-{release.Tag}");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        try
        {
            string? expectedSha512 = null;
            if (release.ChecksumUrl is not null)
            {
                progress?.Report(new($"Downloading the {release.Tag} checksum", 0, null));
                expectedSha512 = ParseChecksum(await http.GetStringAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false))
                    ?? throw new ProtonDownloadException($"The checksum published for {release.Tag} could not be read.");
            }
            var expectedSha256 = release.ArchiveDigest is { } digest ? Launching.Umu.ParseSha256Digest(digest) : null;
            if (expectedSha512 is null && expectedSha256 is null)
                throw new ProtonDownloadException($"{release.Tag} publishes no usable checksum, so it was not installed.");

            var archive = Path.Combine(work, Path.GetFileName(release.ArchiveName));
            var (sha512, sha256) = await DownloadAsync(http, release, archive, progress, cancellationToken).ConfigureAwait(false);
            if ((expectedSha512 is not null && !string.Equals(expectedSha512, sha512, StringComparison.OrdinalIgnoreCase)) ||
                (expectedSha256 is not null && !string.Equals(expectedSha256, sha256, StringComparison.OrdinalIgnoreCase)))
                throw new ProtonDownloadException($"{release.ArchiveName} did not match its published checksum, so it was discarded. Try again.");

            progress?.Report(new($"Unpacking {release.Tag}", 0, null));
            var unpacked = Path.Combine(work, "unpacked");
            Directory.CreateDirectory(unpacked);
            await ExtractAsync(archive, unpacked, cancellationToken).ConfigureAwait(false);
            var protonDir = FindProtonDirectory(unpacked)
                ?? throw new ProtonDownloadException($"{release.ArchiveName} does not contain a Proton build.");

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(protonDir, target);
            Log.Info($"Installed {release.Tag} to {target} (verified: {(expectedSha512 is not null ? "SHA-512" : "")}{(expectedSha512 is not null && expectedSha256 is not null ? " + " : "")}{(expectedSha256 is not null ? "SHA-256" : "")})");
            progress?.Report(new($"Installed {release.Tag}", 0, null));
            return target;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (IOException e)
            {
                Log.Warn($"Could not clean up {work}: {e.Message}");
            }
        }
    }

    public static void Remove(string name, string? root = null)
    {
        root ??= DefaultRoot;
        ValidateName(name);
        var dir = Path.Combine(root, name);
        if (!Directory.Exists(dir)) return;
        Directory.Delete(dir, recursive: true);
        Log.Info($"Removed {name} from {root}");
    }

    /// <summary>A <c>.sha512sum</c> file is <c>&lt;128 hex digits&gt;  &lt;file name&gt;</c>.</summary>
    internal static string? ParseChecksum(string text)
    {
        var first = text.Split((char[])['\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: 128 } && first.All(char.IsAsciiHexDigit) ? first : null;
    }

    /// <summary>The archive's top folder holds the <c>proton</c> script; accept the script at the top level too.</summary>
    internal static string? FindProtonDirectory(string dir)
    {
        if (File.Exists(Path.Combine(dir, "proton"))) return dir;
        return SteamLocator.SafeEnumerateDirectories(dir).FirstOrDefault(d => File.Exists(Path.Combine(d, "proton")));
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.Contains('/') || name.Contains('\0'))
            throw new ArgumentException($"'{name}' is not a valid Proton build name.", nameof(name));
    }

    private static async Task<(string Sha512, string Sha256)> DownloadAsync(HttpClient http, ProtonRelease release, string destination,
        IProgress<ProtonInstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(release.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength ?? (release.ArchiveSize > 0 ? release.ArchiveSize : null);
        var message = $"Downloading {release.Tag}";

        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0, reported = 0;
                progress?.Report(new(message, 0, total));
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    sha512.AppendData(buffer, 0, read);
                    sha256.AppendData(buffer, 0, read);
                    done += read;
                    if (done - reported >= ProgressStep)
                    {
                        reported = done;
                        progress?.Report(new(message, done, total));
                    }
                }
                progress?.Report(new(message, done, total));
                return (Convert.ToHexStringLower(sha512.GetHashAndReset()), Convert.ToHexStringLower(sha256.GetHashAndReset()));
            }
        }
    }

    /// <summary>
    /// Unpacks with the system <c>tar</c>, which reads both .tar.gz and .tar.xz, keeps the build's links and
    /// permissions, and refuses member names that climb out of the destination.
    /// </summary>
    private static async Task ExtractAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("tar", ["--no-same-owner", "-xf", archive, "-C", destination])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ProtonDownloadException($"Could not run tar to unpack Proton: {e.Message}");
        }
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
        if (process.ExitCode != 0)
            throw new ProtonDownloadException($"Unpacking {Path.GetFileName(archive)} failed: {(await errors.ConfigureAwait(false)).Trim()}");
    }
}
