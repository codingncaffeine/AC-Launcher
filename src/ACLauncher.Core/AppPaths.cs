namespace ACLauncher.Core;

/// <summary>
/// Where the launcher keeps its files, following the XDG base directory spec.
/// Setting <c>AC_LAUNCHER_HOME</c> puts everything under one directory instead (used for testing).
/// </summary>
public static class AppPaths
{
    private const string AppDirName = "ac-launcher";

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

    // The spec says a relative value is invalid and must be ignored.
    private static string XdgDir(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : fallback;
    }
}
