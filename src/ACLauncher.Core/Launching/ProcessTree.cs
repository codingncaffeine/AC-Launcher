using System.Diagnostics;
using System.Globalization;

namespace ACLauncher.Core.Launching;

/// <summary>Walks and stops a Linux process tree using <c>/proc</c>.</summary>
public static class ProcessTree
{
    /// <summary>
    /// Every descendant of <paramref name="rootPid"/>, deepest first. Processes named in
    /// <paramref name="spare"/> and their children are left out — a Wine prefix's <c>wineserver</c> is shared
    /// by every client in the prefix, so stopping it would take the other clients down too.
    /// </summary>
    public static IReadOnlyList<int> Descendants(int rootPid, IReadOnlySet<string>? spare = null)
    {
        var children = new Dictionary<int, List<int>>();
        var names = new Dictionary<int, string>();
        foreach (var dir in SafeEnumerate("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            if (ReadStat(pid) is not var (name, parent)) continue;
            names[pid] = name;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(pid);
        }

        var result = new List<int>();
        void Visit(int pid)
        {
            if (!children.TryGetValue(pid, out var kids)) return;
            foreach (var kid in kids)
            {
                if (spare is not null && names.TryGetValue(kid, out var n) && spare.Contains(n)) continue;
                Visit(kid);
                result.Add(kid);
            }
        }
        Visit(rootPid);
        return result;
    }

    /// <summary>Kills a process and its descendants, sparing the shared <c>wineserver</c>.</summary>
    public static void Kill(int rootPid)
    {
        var spare = new HashSet<string>(StringComparer.Ordinal) { "wineserver" };
        foreach (var pid in Descendants(rootPid, spare).Append(rootPid))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }

    /// <summary>Reads the command name and parent pid from <c>/proc/&lt;pid&gt;/stat</c>.</summary>
    internal static (string Name, int Parent)? ReadStat(int pid)
    {
        try
        {
            return ParseStat(File.ReadAllText($"/proc/{pid}/stat"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The name sits in parentheses and may itself contain spaces or parentheses, so fields are
    /// counted from the LAST closing parenthesis: state, then the parent pid.
    /// </summary>
    internal static (string Name, int Parent)? ParseStat(string stat)
    {
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        var rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 2 || !int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)) return null;
        return (stat[(open + 1)..close], parent);
    }

    private static IEnumerable<string> SafeEnumerate(string path)
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
