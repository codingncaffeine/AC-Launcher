using System.Diagnostics;
using System.Globalization;
using System.Text;

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

    /// <summary>True while the process exists and has not become a zombie.</summary>
    public static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')');
            return close >= 0 && close + 2 < stat.Length && stat[close + 2] is not ('Z' or 'X');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The first descendant whose program — the first word of its command line, a Linux or Windows path — has this
    /// file name. Under Wine the game client's process names <c>…\acclient.exe</c> there. Only that first word is
    /// read, so the rest of the command line (the account password) is never touched.
    /// </summary>
    public static int? FindDescendantRunning(int rootPid, string fileName)
    {
        foreach (var pid in Descendants(rootPid))
        {
            if (ProgramFileName(ReadProgram(pid)) is { } name && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return pid;
        }
        return null;
    }

    /// <summary>Kills one process and nothing else.</summary>
    public static void KillProcess(int pid)
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

    internal static string? ProgramFileName(string? program)
    {
        if (string.IsNullOrEmpty(program)) return null;
        var slash = program.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? program : program[(slash + 1)..];
    }

    private static string? ReadProgram(int pid)
    {
        try
        {
            var bytes = File.ReadAllBytes($"/proc/{pid}/cmdline");
            var end = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
