using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ACLauncher.Core.Launching;

/// <summary>
/// One Wine prefix per client running at the same time. The game client refuses to start while another client runs
/// "on the same machine", which it decides with a named mutex, and under Wine a named mutex is only visible inside one
/// prefix's wineserver. The first client runs in the configured prefix; each further client at the same time runs in a
/// copy of it, made once (a reflink where the filesystem supports one) and reused after that.
/// </summary>
public sealed class ClientPrefixes
{
    /// <summary>Written into every copy, naming the prefix it was copied from.</summary>
    public const string MarkerFileName = ".ac-launcher-copy-of";

    /// <summary>More clients at once than this is a runaway, not a player.</summary>
    public const int MaxClients = 32;

    private static readonly string[] SharedSettingsFiles = ["UserPreferences.ini", "acclient.keymap"];

    public ClientPrefixes(string basePrefix, string copiesRoot)
    {
        BasePrefix = Normalize(basePrefix);
        // Copies are kept apart per configured prefix, so choosing another prefix in Settings never reuses copies of
        // the old one.
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BasePrefix)))[..16];
        CopiesDirectory = Path.Combine(Normalize(copiesRoot), key);
    }

    public string BasePrefix { get; }
    public string CopiesDirectory { get; }

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Proton has finished creating a prefix once its registry exists.</summary>
    public static bool IsInitialized(string prefix) =>
        File.Exists(Path.Combine(prefix, "system.reg")) || File.Exists(Path.Combine(prefix, "pfx", "system.reg"));

    /// <summary>Client 1 uses the configured prefix; client n uses copy n.</summary>
    public string PathFor(int client) =>
        client <= 1 ? BasePrefix : Path.Combine(CopiesDirectory, client.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The first prefix with no client running in it, copying the configured prefix when every existing one is busy.
    /// The configured prefix must already be set up.
    /// </summary>
    /// <param name="inUse">Normalized prefixes that already have a client running.</param>
    /// <exception cref="LaunchException">A copy could not be made.</exception>
    public async Task<string> AcquireAsync(IReadOnlySet<string> inUse, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        for (var client = 1; client <= MaxClients; client++)
        {
            var path = PathFor(client);
            if (inUse.Contains(path)) continue;
            if (client > 1)
            {
                await EnsureCopyAsync(path, progress, cancellationToken).ConfigureAwait(false);
                ShareSettings(BasePrefix, path);
            }
            return path;
        }
        throw new LaunchException($"{MaxClients} clients are already running.");
    }

    private async Task EnsureCopyAsync(string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(path, MarkerFileName)) && IsInitialized(path)) return;
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new LaunchException($"{path} was not made by AC Launcher. Move it away so a copy of the Wine prefix can go there.");
        if (!IsInitialized(BasePrefix)) throw new LaunchException($"The Wine prefix {BasePrefix} is not set up yet.");

        Directory.CreateDirectory(CopiesDirectory);
        var partial = Path.Combine(CopiesDirectory, "." + Path.GetFileName(path) + ".partial");
        progress?.Report("Copying the Wine prefix for another client at the same time (only needed once)…");
        Log.Info($"Copying {BasePrefix} to {path} so another client can run at the same time");
        try
        {
            // rm and cp never follow the prefix's symlinks (dosdevices/z: points at /). wineserver saves the registry
            // by writing a temporary file and renaming it, so a prefix that is in use still copies whole files.
            await RunAsync("rm", ["-rf", "--", partial], cancellationToken).ConfigureAwait(false);
            await RunAsync("cp", ["-a", "--reflink=auto", "-T", "--", BasePrefix, partial], cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(partial, MarkerFileName), BasePrefix + "\n", cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(path)) Directory.Delete(path); // empty, checked above
            Directory.Move(partial, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or LaunchException)
        {
            try
            {
                await RunAsync("rm", ["-rf", "--", partial], CancellationToken.None).ConfigureAwait(false);
            }
            catch (LaunchException cleanup)
            {
                Log.Warn($"Could not remove {partial}: {cleanup.Message}");
            }
            if (e is OperationCanceledException or LaunchException) throw;
            throw new LaunchException($"Could not copy the Wine prefix to {path}: {e.Message}");
        }
        Log.Info($"Copied the Wine prefix to {path}");
    }

    /// <summary>
    /// Copies the client's settings and key map from the configured prefix into a copy when the configured prefix has
    /// the newer file, so what is changed in the first client reaches the others.
    /// </summary>
    internal static void ShareSettings(string from, string to)
    {
        var source = Path.GetDirectoryName(ClientPreferences.PathFor(from))!;
        var target = Path.GetDirectoryName(ClientPreferences.PathFor(to))!;
        foreach (var name in SharedSettingsFiles)
        {
            var sourceFile = Path.Combine(source, name);
            var targetFile = Path.Combine(target, name);
            try
            {
                if (!File.Exists(sourceFile)) continue;
                var written = File.GetLastWriteTimeUtc(sourceFile);
                if (File.Exists(targetFile) && File.GetLastWriteTimeUtc(targetFile) >= written) continue;
                Directory.CreateDirectory(target);
                File.Copy(sourceFile, targetFile, overwrite: true);
                File.SetLastWriteTimeUtc(targetFile, written);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not copy {sourceFile} to {targetFile}: {e.Message}");
            }
        }
    }

    private static async Task RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new LaunchException($"Could not run {program}: {e.Message}");
        }
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
        await output.ConfigureAwait(false);
        var message = (await errors.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
            throw new LaunchException($"{program} failed (exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}): {message}");
    }
}
