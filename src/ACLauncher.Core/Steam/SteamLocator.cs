namespace ACLauncher.Core.Steam;

/// <summary>A Steam installation: its root directory and the library folders it knows about.</summary>
public sealed record SteamInstall(string Root, IReadOnlyList<string> Libraries)
{
    public string CompatDataDirectory(string library) => Path.Combine(library, "steamapps", "compatdata");
}

/// <summary>
/// Finds Steam library folders on disk so Proton builds already installed there can be offered.
/// Only files are read; the Steam client is never needed or started.
/// </summary>
public static class SteamLocator
{
    /// <summary>Candidate Steam roots in the order Steam itself prefers them (native, then Flatpak, then Snap).</summary>
    public static IEnumerable<string> CandidateRoots(string home) =>
    [
        Path.Combine(home, ".local", "share", "Steam"),
        Path.Combine(home, ".steam", "steam"),
        Path.Combine(home, ".steam", "root"),
        Path.Combine(home, ".steam", "debian-installation"),
        Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
    ];

    public static IReadOnlyList<SteamInstall> Find(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var installs = new List<SteamInstall>();
        foreach (var candidate in CandidateRoots(home))
        {
            if (!Directory.Exists(Path.Combine(candidate, "steamapps"))) continue;
            var real = RealPath(candidate);
            if (!seen.Add(real)) continue;
            installs.Add(new SteamInstall(real, ReadLibraries(real)));
        }
        return installs;
    }

    /// <summary>The root itself plus every library listed in <c>steamapps/libraryfolders.vdf</c> that exists.</summary>
    public static IReadOnlyList<string> ReadLibraries(string root)
    {
        var libraries = new List<string> { root };
        var seen = new HashSet<string>(StringComparer.Ordinal) { RealPath(root) };
        var doc = KeyValuesText.TryParseFile(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
        foreach (var entry in doc?["libraryfolders"]?.Children ?? [])
        {
            // Old Steam wrote "1" "/path"; current Steam writes "1" { "path" "/path" ... }.
            var path = entry.IsSection ? entry.GetString("path") : entry.Text;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(Path.Combine(path, "steamapps"))) continue;
            if (seen.Add(RealPath(path))) libraries.Add(path);
        }
        return libraries;
    }

    /// <summary>Resolves an installed app's directory from <c>steamapps/appmanifest_&lt;id&gt;.acf</c>.</summary>
    public static string? FindAppInstallDir(SteamInstall install, uint appId)
    {
        foreach (var library in install.Libraries)
        {
            var manifest = KeyValuesText.TryParseFile(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"));
            var dir = manifest?["AppState"]?.GetString("installdir");
            if (string.IsNullOrEmpty(dir)) continue;
            var full = Path.Combine(library, "steamapps", "common", dir);
            if (Directory.Exists(full)) return full;
        }
        return null;
    }

    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            var target = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
            return target is null ? full : Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (IOException)
        {
            return full;
        }
    }

    internal static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
