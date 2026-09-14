using System.Globalization;
using System.Text.RegularExpressions;

namespace ACLauncher.Core.Steam;

public enum ProtonSource
{
    /// <summary>A Valve build installed as a Steam app (<c>steamapps/common/Proton*</c>).</summary>
    Steam,
    /// <summary>A custom build such as GE-Proton in a <c>compatibilitytools.d</c> folder.</summary>
    CompatibilityTool,
}

/// <summary>An installed Proton build.</summary>
/// <param name="Name">Steam's internal tool name (<c>proton_experimental</c>, <c>GE-Proton10-4</c>), used by <c>config.vdf</c>.</param>
/// <param name="DisplayName">What to show the user.</param>
/// <param name="Directory">The directory holding the <c>proton</c> script.</param>
/// <param name="Version">Contents of the <c>version</c> file after its timestamp, if present.</param>
/// <param name="RequiredToolAppId">The Steam Linux Runtime app id the build asks to run inside, if any.</param>
public sealed record ProtonBuild(
    string Name,
    string DisplayName,
    string Directory,
    string? Version,
    uint? RequiredToolAppId,
    ProtonSource Source)
{
    public string Script => Path.Combine(Directory, "proton");
    public string WineBinDirectory => Path.Combine(Directory, "files", "bin");
}

/// <summary>Finds Proton builds across Steam libraries and compatibility-tool folders.</summary>
public static partial class ProtonLocator
{
    public static IReadOnlyList<ProtonBuild> Find(IReadOnlyList<SteamInstall> installs, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var builds = new List<ProtonBuild>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var install in installs)
        {
            foreach (var library in install.Libraries)
            {
                var common = Path.Combine(library, "steamapps", "common");
                if (!System.IO.Directory.Exists(common)) continue;
                foreach (var dir in SteamLocator.SafeEnumerateDirectories(common))
                {
                    if (TryReadSteamBuild(dir) is { } build && seen.Add(SteamLocator.RealPath(dir)))
                        builds.Add(build);
                }
            }
        }

        var toolRoots = installs.Select(i => Path.Combine(i.Root, "compatibilitytools.d"))
            .Append(Path.Combine(home, ".steam", "root", "compatibilitytools.d"))
            .Append("/usr/share/steam/compatibilitytools.d")
            .Append("/usr/local/share/steam/compatibilitytools.d");
        foreach (var root in toolRoots)
        {
            if (!System.IO.Directory.Exists(root)) continue;
            foreach (var dir in SteamLocator.SafeEnumerateDirectories(root))
            {
                foreach (var build in ReadCompatibilityTools(dir))
                {
                    if (seen.Add(SteamLocator.RealPath(build.Directory))) builds.Add(build);
                }
            }
        }

        return builds
            .OrderBy(b => b.Source)
            .ThenByDescending(b => b.Name == "proton_experimental")
            .ThenByDescending(b => b.DisplayName, NaturalComparer.Instance)
            .ToList();
    }

    internal static ProtonBuild? TryReadSteamBuild(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "proton"))) return null;
        var manifest = KeyValuesText.TryParseFile(Path.Combine(dir, "toolmanifest.vdf"))?["manifest"];
        if (manifest is null) return null;
        var displayName = Path.GetFileName(dir);
        return new ProtonBuild(
            SteamInternalName(displayName),
            displayName,
            dir,
            ReadVersion(dir),
            ParseAppId(manifest.GetString("require_tool_appid")),
            ProtonSource.Steam);
    }

    internal static IEnumerable<ProtonBuild> ReadCompatibilityTools(string dir)
    {
        var doc = KeyValuesText.TryParseFile(Path.Combine(dir, "compatibilitytool.vdf"));
        var tools = doc?.Find("compatibilitytools", "compat_tools");
        if (tools is null) yield break;
        foreach (var tool in tools.Children)
        {
            if (!tool.IsSection) continue;
            var installPath = tool.GetString("install_path") ?? ".";
            var toolDir = Path.GetFullPath(Path.Combine(dir, installPath));
            if (!File.Exists(Path.Combine(toolDir, "proton"))) continue;
            var manifest = KeyValuesText.TryParseFile(Path.Combine(toolDir, "toolmanifest.vdf"))?["manifest"];
            yield return new ProtonBuild(
                tool.Name,
                tool.GetString("display_name") ?? tool.Name,
                toolDir,
                ReadVersion(toolDir),
                ParseAppId(manifest?.GetString("require_tool_appid")),
                ProtonSource.CompatibilityTool);
        }
    }

    /// <summary>
    /// Steam's internal names for its own builds: "Proton - Experimental" is <c>proton_experimental</c>,
    /// "Proton Hotfix" is <c>proton_hotfix</c>, "Proton 9.0" is <c>proton_9</c>, "Proton 5.13" is <c>proton_513</c>.
    /// </summary>
    internal static string SteamInternalName(string directoryName)
    {
        if (directoryName.Contains("Experimental", StringComparison.OrdinalIgnoreCase)) return "proton_experimental";
        if (directoryName.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) return "proton_hotfix";
        var match = VersionPattern().Match(directoryName);
        if (!match.Success) return directoryName;
        var major = match.Groups[1].Value;
        var minor = match.Groups[2].Value;
        return minor is "" or "0" ? $"proton_{major}" : $"proton_{major}{minor}";
    }

    private static string? ReadVersion(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "version");
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            // "<unix timestamp> <name>"
            var space = text.IndexOf(' ');
            return space > 0 && long.TryParse(text[..space], out _) ? text[(space + 1)..] : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static uint? ParseAppId(string? text) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    [GeneratedRegex(@"Proton\s+(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}

/// <summary>Orders "Proton 10.0" after "Proton 9.0" by comparing digit runs numerically.</summary>
internal sealed partial class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var xs = Chunks().Matches(x);
        var ys = Chunks().Matches(y);
        for (var i = 0; i < Math.Min(xs.Count, ys.Count); i++)
        {
            var a = xs[i].Value;
            var b = ys[i].Value;
            int c;
            if (char.IsDigit(a[0]) && char.IsDigit(b[0]) &&
                long.TryParse(a, out var na) && long.TryParse(b, out var nb))
                c = na.CompareTo(nb);
            else
                c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return xs.Count.CompareTo(ys.Count);
    }

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Chunks();
}
