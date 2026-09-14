using System.Text;
using System.Xml;

namespace ACLauncher.Core.Servers;

/// <summary>Downloads the published server lists, keeps a cached copy of each, and merges them.</summary>
public static class ServerCatalog
{
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);

    public static string CacheFile(string cacheDir, ServerListSource source)
    {
        var safe = new StringBuilder();
        foreach (var c in source.Name) safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return Path.Combine(cacheDir, (safe.Length == 0 ? "list" : safe.ToString()) + ".xml");
    }

    /// <summary>The servers from the cached copies only; used at startup before any download finishes.</summary>
    public static IReadOnlyList<Server> LoadCached(IEnumerable<ServerListSource> sources, string cacheDir) =>
        Merge(sources.Where(s => s.Enabled).Select(s => ReadCache(cacheDir, s)));

    /// <summary>
    /// Downloads every enabled list. A list that cannot be downloaded or parsed falls back to its cached
    /// copy, so an offline launcher still shows the servers it knew about.
    /// </summary>
    public static async Task<IReadOnlyList<Server>> RefreshAsync(HttpClient http, IEnumerable<ServerListSource> sources,
        string cacheDir, CancellationToken cancellationToken)
    {
        var tasks = sources.Where(s => s.Enabled).Select(async source =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DownloadTimeout);
                var xml = await http.GetStringAsync(source.Url, timeout.Token).ConfigureAwait(false);
                var servers = ServerListParser.Parse(xml, source);
                Directory.CreateDirectory(cacheDir);
                var file = CacheFile(cacheDir, source);
                await File.WriteAllTextAsync(file + ".tmp", xml, cancellationToken).ConfigureAwait(false);
                File.Move(file + ".tmp", file, overwrite: true);
                Log.Info($"Server list {source.Name}: {servers.Count} servers");
                return servers;
            }
            catch (Exception e) when (e is HttpRequestException or XmlException or IOException or TaskCanceledException
                                          && !cancellationToken.IsCancellationRequested)
            {
                var cached = ReadCache(cacheDir, source);
                Log.Warn($"Server list {source.Name} could not be downloaded ({e.Message}); using {cached.Count} cached servers");
                return cached;
            }
        });
        return Merge(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    /// <summary>Joins lists in order; a server already listed by an earlier list (same name) is not repeated.</summary>
    internal static IReadOnlyList<Server> Merge(IEnumerable<IReadOnlyList<Server>> lists)
    {
        var seen = new HashSet<Guid>();
        return lists.SelectMany(l => l).Where(s => seen.Add(s.Id))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IReadOnlyList<Server> ReadCache(string cacheDir, ServerListSource source)
    {
        var file = CacheFile(cacheDir, source);
        try
        {
            return File.Exists(file) ? ServerListParser.Parse(File.ReadAllText(file), source) : [];
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException)
        {
            Log.Warn($"Cached server list {file} is unreadable: {e.Message}");
            return [];
        }
    }
}
