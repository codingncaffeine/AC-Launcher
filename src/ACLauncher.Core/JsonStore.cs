using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACLauncher.Core;

/// <summary>Loads and atomically saves JSON documents.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads a document, or returns a new one if the file is missing. A corrupt file is set aside, not overwritten.</summary>
    public static T LoadOrNew<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
        }
        catch (JsonException e)
        {
            var aside = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            Log.Error($"Could not read {path}; moved it to {aside} and started fresh", e);
            try { File.Move(path, aside); } catch (IOException) { }
            return new T();
        }
    }

    /// <summary>
    /// Writes to a temporary file and renames it over the target, so a crash never leaves half a file.
    /// A private document is created readable by the owner only.
    /// </summary>
    public static void Save<T>(string path, T value, bool isPrivate = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.Delete(temp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (isPrivate && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
