namespace ACLauncher.Core.Launching;

/// <summary>The outcome of checking a folder the user pointed at.</summary>
public sealed record GameInstallCheck(bool IsValid, string? ClientPath, string Message, IReadOnlyList<string> MissingDataFiles);

/// <summary>Validates an Asheron's Call folder and finds the client executable inside it.</summary>
public static class GameInstall
{
    public const string ClientFileName = "acclient.exe";

    /// <summary>The data files the client cannot run without.</summary>
    public static readonly IReadOnlyList<string> RequiredDataFiles =
        ["client_portal.dat", "client_cell_1.dat", "client_local_English.dat"];

    public static GameInstallCheck Check(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return new(false, null, "Choose the folder where Asheron's Call is installed.", []);
        if (!Directory.Exists(directory))
            return new(false, null, $"The folder {directory} does not exist.", []);

        // Linux file names are case-sensitive but Windows installs are not consistent about case.
        var client = FindFileIgnoringCase(directory, ClientFileName);
        if (client is null)
        {
            var hint = FindNestedClient(directory);
            var message = hint is null
                ? $"No {ClientFileName} in {directory}."
                : $"No {ClientFileName} in {directory}. Did you mean {Path.GetDirectoryName(hint)}?";
            return new(false, null, message, []);
        }

        var missing = RequiredDataFiles.Where(f => FindFileIgnoringCase(directory, f) is null).ToList();
        if (missing.Count > 0)
            return new(false, client, $"Found {ClientFileName}, but these data files are missing: {string.Join(", ", missing)}.", missing);

        return new(true, client, $"Found {ClientFileName}.", []);
    }

    internal static string? FindFileIgnoringCase(string directory, string fileName)
    {
        var exact = Path.Combine(directory, fileName);
        if (File.Exists(exact)) return exact;
        try
        {
            return Directory.EnumerateFiles(directory)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Looks two levels down for a client, to help a user who picked the parent folder.</summary>
    private static string? FindNestedClient(string directory)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(directory, ClientFileName, options).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
