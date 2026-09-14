namespace ACLauncher.Core;

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>Application log: a rotating file under the state directory plus a recent-entries buffer for the log window.</summary>
public static class Log
{
    private const int MaxRecent = 5000;
    private const long RotateBytes = 5 * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly Queue<LogEntry> Recent = new();
    private static StreamWriter? _writer;

    public static event Action<LogEntry>? EntryAdded;

    public static string? FilePath { get; private set; }

    public static void Open(string directory)
    {
        lock (Gate)
        {
            if (_writer is not null) return;
            try
            {
                AppPaths.CreatePrivateDirectory(directory);
                var path = Path.Combine(directory, "ac-launcher.log");
                if (File.Exists(path) && new FileInfo(path).Length > RotateBytes)
                    File.Move(path, path + ".1", overwrite: true);
                // The log names accounts and servers: owner-only, including a file an older version left readable.
                var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                    UnixCreateMode = AppPaths.PrivateFileMode,
                });
                if ((File.GetUnixFileMode(path) & ~AppPaths.PrivateFileMode) != 0)
                    File.SetUnixFileMode(path, AppPaths.PrivateFileMode);
                foreach (var old in Directory.GetFiles(directory))
                    if ((File.GetUnixFileMode(old) & ~AppPaths.PrivateFileMode) != 0)
                        File.SetUnixFileMode(old, AppPaths.PrivateFileMode);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                FilePath = path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop the launcher; entries still reach the in-memory buffer.
                Console.Error.WriteLine($"ac-launcher: cannot open log file: {e.Message}");
            }
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate) return Recent.ToArray();
    }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message}: {exception}");

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (Gate)
        {
            Recent.Enqueue(entry);
            while (Recent.Count > MaxRecent) Recent.Dequeue();
            try
            {
                _writer?.WriteLine($"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {Label(level)} {message}");
            }
            catch (IOException)
            {
            }
        }
        EntryAdded?.Invoke(entry);
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "INFO ",
    };
}
