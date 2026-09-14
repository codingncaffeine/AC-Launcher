namespace ACLauncher.Core;

/// <summary>
/// Where the launcher keeps its files, following the XDG base directory spec.
/// Setting <c>AC_LAUNCHER_HOME</c> puts everything under one directory instead (used for testing).
/// </summary>
public static class AppPaths
{
    private const string AppDirName = "ac-launcher";

    /// <summary>Owner read, write and search only — the launcher's folders hold account names and logs.</summary>
    public const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Owner read and write only.</summary>
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    static AppPaths() => Configure();

    public static string ConfigDir { get; private set; } = "";
    public static string DataDir { get; private set; } = "";
    public static string CacheDir { get; private set; } = "";
    public static string StateDir { get; private set; } = "";

    public static string LogDir => Path.Combine(StateDir, "logs");
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string AccountsFile => Path.Combine(ConfigDir, "accounts.json");
    public static string ServersFile => Path.Combine(ConfigDir, "servers.json");
    public static string ServerListCacheDir => Path.Combine(CacheDir, "server-lists");
    public static string DefaultPrefix => Path.Combine(DataDir, "prefix");

    /// <summary>Copies of the prefix for clients running at the same time (see <c>ClientPrefixes</c>).</summary>
    public static string ClientPrefixesDir => Path.Combine(DataDir, "client-prefixes");

    public static void Configure(string? rootOverride = null)
    {
        var root = rootOverride ?? Environment.GetEnvironmentVariable("AC_LAUNCHER_HOME");
        if (!string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetFullPath(root);
            ConfigDir = Path.Combine(root, "config");
            DataDir = Path.Combine(root, "data");
            CacheDir = Path.Combine(root, "cache");
            StateDir = Path.Combine(root, "state");
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ConfigDir = Path.Combine(XdgDir("XDG_CONFIG_HOME", Path.Combine(home, ".config")), AppDirName);
        DataDir = Path.Combine(XdgDir("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), AppDirName);
        CacheDir = Path.Combine(XdgDir("XDG_CACHE_HOME", Path.Combine(home, ".cache")), AppDirName);
        StateDir = Path.Combine(XdgDir("XDG_STATE_HOME", Path.Combine(home, ".local", "state")), AppDirName);
    }

    /// <summary>
    /// Creates the launcher's own folders readable by the owner only, and tightens them if an earlier
    /// version created them with the default (world-readable) mode.
    /// </summary>
    public static void EnsurePrivateDirectories()
    {
        foreach (var dir in new[] { ConfigDir, DataDir, CacheDir, StateDir, LogDir })
            CreatePrivateDirectory(dir);
    }

    public static void CreatePrivateDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir, PrivateDirectoryMode);
            if ((File.GetUnixFileMode(dir) & ~PrivateDirectoryMode) != 0)
                File.SetUnixFileMode(dir, PrivateDirectoryMode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ac-launcher: cannot secure {dir}: {e.Message}");
        }
    }

    // The spec says a relative value is invalid and must be ignored.
    private static string XdgDir(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : fallback;
    }
}
