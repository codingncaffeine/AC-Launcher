using System.Text;

namespace ACLauncher.Core.Launching;

/// <summary>
/// The client's own settings file, <c>UserPreferences.ini</c>, which it keeps in the Wine user's
/// Documents folder. The client starts fullscreen by default, which fails with a DirectX error on
/// many systems; starting windowed avoids it.
/// </summary>
public static class ClientPreferences
{
    /// <summary>Proton always names the Wine user <c>steamuser</c>.</summary>
    public static string PathFor(string prefix) =>
        Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents", "Asheron's Call", "UserPreferences.ini");

    /// <summary>Sets <c>[Display] FullScreen=False</c>, creating the file or section if needed.</summary>
    /// <returns>True if the file was changed.</returns>
    public static bool EnsureWindowed(string iniPath)
    {
        var text = File.Exists(iniPath) ? File.ReadAllText(iniPath) : "";
        var updated = SetWindowed(text);
        if (updated == text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
        File.WriteAllText(iniPath, updated);
        return true;
    }

    internal static string SetWindowed(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) || text.Length == 0 ? "\r\n" : "\n";
        var lines = text.Length == 0 ? [] : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').ToList();

        var section = lines.FindIndex(l => l.Trim().Equals("[Display]", StringComparison.OrdinalIgnoreCase));
        if (section < 0)
        {
            lines.Add("[Display]");
            lines.Add("FullScreen=False");
        }
        else
        {
            var end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var key = lines.FindIndex(section + 1, end - section - 1,
                l => l.Split('=', 2)[0].Trim().Equals("FullScreen", StringComparison.OrdinalIgnoreCase));
            if (key < 0)
                lines.Insert(section + 1, "FullScreen=False");
            else if (!lines[key].Split('=', 2).ElementAtOrDefault(1)?.Trim().Equals("False", StringComparison.OrdinalIgnoreCase) ?? true)
                lines[key] = "FullScreen=False";
            else
                return text;
        }

        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(line).Append(newline);
        return sb.ToString();
    }
}
