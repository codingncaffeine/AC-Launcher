using System.Collections.Concurrent;
using System.Text.Json;
using ACLauncher.Core;
using ACLauncher.Core.Launching;
using ACLauncher.Core.Secrets;
using ACLauncher.Core.Servers;

namespace ACLauncher.Services;

/// <summary>The launcher's loaded configuration, server catalog, running games and server status.</summary>
public sealed class LauncherState : IDisposable
{
    private static readonly TimeSpan StatusTick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecheckUp = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecheckDown = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, ServerStatus> _statuses = new();
    private readonly CancellationTokenSource _shutdown = new();

    private LauncherState(LauncherSettings settings, AccountsDocument accounts, ServersDocument servers)
    {
        Settings = settings;
        AccountsDocument = accounts;
        UserServersDocument = servers;
        PublishedServers = ServerCatalog.LoadCached(settings.ServerLists, AppPaths.ServerListCacheDir);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ac-launcher/" + typeof(LauncherState).Assembly.GetName().Version?.ToString(3));
        MigrateStoredData();
    }

    /// <summary>Brings files written by earlier versions up to date.</summary>
    private void MigrateStoredData()
    {
        var insecure = Settings.ServerLists.Where(s => !s.IsSecure).ToList();
        if (insecure.Count > 0)
        {
            foreach (var source in insecure) Settings.ServerLists.Remove(source);
            Log.Info($"Stopped using server lists not served over HTTPS: {string.Join(", ", insecure.Select(s => s.Url))}");
            SaveSettings();
        }

        // 0.5.0 derived published-server ids with MD5; carry account links and hidden servers over to the new ids.
        var remap = new Dictionary<Guid, Guid>();
        foreach (var server in PublishedServers) remap.TryAdd(Server.LegacyPublishedId(server.Name), server.Id);
        var linksMoved = 0;
        foreach (var link in Accounts.SelectMany(a => a.Servers))
        {
            if (remap.TryGetValue(link.ServerId, out var id)) { link.ServerId = id; linksMoved++; }
        }
        if (linksMoved > 0)
        {
            SaveAccounts();
            Log.Info($"Updated {linksMoved} account server links to the current server ids");
        }
        var hidden = Settings.HiddenServers.Select(id => remap.GetValueOrDefault(id, id)).Distinct().ToList();
        if (!hidden.SequenceEqual(Settings.HiddenServers))
        {
            Settings.HiddenServers = hidden;
            SaveSettings();
        }
    }

    public LauncherSettings Settings { get; private set; }
    public AccountsDocument AccountsDocument { get; }
    public ServersDocument UserServersDocument { get; }
    public IReadOnlyList<Server> PublishedServers { get; private set; }
    public GameManager Games { get; } = new();

    /// <summary>Proton archives are streamed; anything read whole (lists, release data, checksums) is capped.</summary>
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(15), MaxResponseContentBufferSize = 16 * 1024 * 1024 };

    public AccountSecrets Secrets { get; } = new(new SecretToolStore());
    public CancellationToken ShutdownToken => _shutdown.Token;

    public List<Account> Accounts => AccountsDocument.Accounts;
    public List<Server> UserServers => UserServersDocument.Servers;

    /// <summary>Raised on a worker thread when the server catalog changes.</summary>
    public event Action? ServersChanged;

    /// <summary>Raised on a worker thread when a server's status changes.</summary>
    public event Action<Guid>? StatusChanged;

    public static LauncherState Load() => new(
        JsonStore.LoadOrNew<LauncherSettings>(AppPaths.SettingsFile),
        JsonStore.LoadOrNew<AccountsDocument>(AppPaths.AccountsFile),
        JsonStore.LoadOrNew<ServersDocument>(AppPaths.ServersFile));

    public IEnumerable<Server> AllServers => UserServers.Concat(PublishedServers.Where(p => UserServers.All(u => u.Id != p.Id)));

    public IEnumerable<Server> VisibleServers => AllServers.Where(s => !Settings.HiddenServers.Contains(s.Id));

    public Server? FindServer(Guid id) => AllServers.FirstOrDefault(s => s.Id == id);

    public ServerStatus StatusOf(Guid serverId) => _statuses.GetValueOrDefault(serverId, ServerStatus.Unknown);

    public void SaveSettings() => Save(() => JsonStore.Save(AppPaths.SettingsFile, Settings));
    public void SaveAccounts() => Save(() => JsonStore.Save(AppPaths.AccountsFile, AccountsDocument));

    /// <summary>Moves any password still kept in accounts.json into the desktop keyring.</summary>
    public async Task MigratePasswordsAsync()
    {
        try
        {
            if (await Secrets.MigrateAsync(Accounts, ShutdownToken)) SaveAccounts();
        }
        catch (OperationCanceledException)
        {
        }
    }
    public void SaveServers() => Save(() => JsonStore.Save(AppPaths.ServersFile, UserServersDocument));

    public void ReplaceSettings(LauncherSettings settings)
    {
        Settings = settings;
        SaveSettings();
        ServersChanged?.Invoke();
    }

    /// <summary>A deep copy for an editor that may be cancelled.</summary>
    public LauncherSettings CloneSettings() =>
        JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(Settings, JsonStore.Options), JsonStore.Options)!;

    public async Task RefreshServerListsAsync()
    {
        try
        {
            PublishedServers = await ServerCatalog.RefreshAsync(Http, Settings.ServerLists, AppPaths.ServerListCacheDir, ShutdownToken);
            ServersChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void NotifyServersChanged() => ServersChanged?.Invoke();

    /// <summary>Probes visible servers in the background: every minute while up, every 30 seconds otherwise.</summary>
    public async Task RunStatusLoopAsync()
    {
        using var gate = new SemaphoreSlim(8);
        var lastChecked = new ConcurrentDictionary<Guid, DateTime>();
        try
        {
            while (!ShutdownToken.IsCancellationRequested)
            {
                if (Settings.CheckServerStatus)
                {
                    var now = DateTime.UtcNow;
                    var due = VisibleServers.Where(s =>
                    {
                        var interval = StatusOf(s.Id).State == ServerUpState.Up ? RecheckUp : RecheckDown;
                        return now - lastChecked.GetValueOrDefault(s.Id, DateTime.MinValue) >= interval;
                    }).ToList();
                    await Task.WhenAll(due.Select(async server =>
                    {
                        lastChecked[server.Id] = now;
                        await gate.WaitAsync(ShutdownToken);
                        try
                        {
                            var status = await ServerStatusProbe.ProbeAsync(server.Address, ServerStatusProbe.DefaultTimeout, ShutdownToken);
                            _statuses[server.Id] = status;
                            StatusChanged?.Invoke(server.Id);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }));
                }
                await Task.Delay(StatusTick, ShutdownToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Resolves what a launch needs, or explains what is missing.</summary>
    /// <param name="protonPath">The Proton directory (or umu build name) to run with.</param>
    public LaunchEnvironment? ResolveEnvironment(string protonPath, out string? problem)
    {
        var umu = Umu.Find(Settings.UmuRunPath);
        if (umu is null)
        {
            problem = string.IsNullOrWhiteSpace(Settings.UmuRunPath)
                ? "umu-launcher is not installed."
                : $"The umu-run set in Settings does not exist: {Settings.UmuRunPath}";
            return null;
        }
        problem = null;
        return new LaunchEnvironment(umu, Settings.EffectivePrefix, protonPath, Settings.GameDirectory, Settings.Environment);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        Http.Dispose();
        _shutdown.Dispose();
    }

    private static void Save(Action save)
    {
        try
        {
            save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save settings", e);
        }
    }
}
