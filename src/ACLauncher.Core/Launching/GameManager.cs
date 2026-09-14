using System.Diagnostics;

namespace ACLauncher.Core.Launching;

/// <param name="Password">The account's password, resolved from the keyring or the accounts file.</param>
public sealed record LaunchTarget(Account Account, Server Server, string Password)
{
    // Keep the password out of the record's generated ToString.
    public override string ToString() => $"LaunchTarget {{ Account = {Account.DisplayName}, Server = {Server.Name} }}";
}

/// <summary>Everything a launch needs from the launcher's configuration, resolved up front.</summary>
public sealed record LaunchEnvironment(
    string UmuRunPath,
    string PrefixPath,
    string ProtonPath,
    string? DefaultGameDirectory,
    IReadOnlyDictionary<string, string> ExtraEnvironment)
{
    /// <summary>umu's per-game fix database id; the game has no entry, so the generic one applies.</summary>
    public const string GameId = "umu-default";

    /// <exception cref="LaunchException">No usable game folder, or bad account/server data.</exception>
    public ProcessStartInfo BuildStartInfo(LaunchTarget target, out string clientPath, out IReadOnlyList<string> clientArguments)
    {
        var directory = string.IsNullOrWhiteSpace(target.Server.GameDirectory) ? DefaultGameDirectory : target.Server.GameDirectory;
        var check = GameInstall.Check(directory);
        if (!check.IsValid || check.ClientPath is null) throw new LaunchException(check.Message);
        clientPath = check.ClientPath;
        clientArguments = ClientArguments.Build(target.Server, target.Account, target.Password);

        // The client finds its .dat files relative to the working directory.
        return BuildUmuStartInfo(clientPath, clientArguments, Path.GetDirectoryName(clientPath)!, launchesClient: true);
    }

    /// <summary>Runs a Wine program in the prefix, e.g. <c>wineboot -u</c> or <c>winecfg</c>.</summary>
    public ProcessStartInfo BuildToolStartInfo(string program, IReadOnlyList<string> arguments) =>
        BuildUmuStartInfo(program, arguments, Directory.Exists(PrefixPath) ? PrefixPath : Path.GetTempPath(), launchesClient: false);

    /// <summary>Proton has finished creating the prefix once its registry exists.</summary>
    public bool IsPrefixInitialized => ClientPrefixes.IsInitialized(PrefixPath);

    private ProcessStartInfo BuildUmuStartInfo(string program, IReadOnlyList<string> arguments, string workingDirectory, bool launchesClient)
    {
        var startInfo = new ProcessStartInfo(UmuRunPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(program);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        foreach (var (key, value) in ExtraEnvironment) startInfo.Environment[key] = value;
        startInfo.Environment["WINEPREFIX"] = PrefixPath;
        startInfo.Environment["GAMEID"] = GameId;
        startInfo.Environment["STORE"] = "none";
        if (string.IsNullOrWhiteSpace(ProtonPath)) startInfo.Environment.Remove("PROTONPATH");
        else startInfo.Environment["PROTONPATH"] = ProtonPath;
        // umu's default verb, waitforexitandrun, waits for every program already running in the prefix to exit, so a
        // client would sit queued behind anything else in its prefix. "run" starts it at once.
        if (launchesClient && !ExtraEnvironment.ContainsKey("PROTON_VERB"))
            startInfo.Environment["PROTON_VERB"] = "run";
        return startInfo;
    }
}

/// <summary>A running game client started by the launcher.</summary>
public sealed class GameSession
{
    private int _ended;

    internal GameSession(LaunchTarget target, string clientFileName, string prefixPath)
    {
        Target = target;
        ClientFileName = clientFileName;
        PrefixPath = prefixPath;
    }

    public LaunchTarget Target { get; }
    public DateTime StartedUtc { get; internal set; }

    /// <summary>The Wine prefix the client runs in, normalized.</summary>
    public string PrefixPath { get; }

    /// <summary>The <c>umu-run</c> process that launched the client.</summary>
    public int ProcessId { get; internal set; }

    /// <summary>
    /// The game client's own process, once it has started. The session follows this process: the launch that
    /// started a prefix's wineserver keeps running until every program in the prefix has exited.
    /// </summary>
    public int? ClientProcessId { get; internal set; }

    public int? ExitCode { get; internal set; }
    public bool HasEnded => Volatile.Read(ref _ended) == 1;

    internal string ClientFileName { get; }

    /// <summary>True for the one caller that ends the session.</summary>
    internal bool TryMarkEnded() => Interlocked.Exchange(ref _ended, 1) == 0;

    /// <summary>Stops this client only — never other programs in the prefix, or its wineserver.</summary>
    public void Stop()
    {
        if (ClientProcessId is { } client && ProcessTree.IsAlive(client))
        {
            Log.Info($"Stopping {Target.Account.DisplayName} on {Target.Server.Name} (client pid {client})");
            ProcessTree.KillProcess(client);
            return;
        }
        // Still starting (e.g. downloading Proton): nothing else runs in this launch yet.
        Log.Info($"Stopping {Target.Account.DisplayName} on {Target.Server.Name} (launch pid {ProcessId})");
        ProcessTree.Kill(ProcessId);
    }
}

/// <summary>Starts game clients through umu and tracks them until they exit.</summary>
public sealed class GameManager
{
    private readonly Lock _gate = new();
    private readonly List<GameSession> _sessions = [];

    public event Action<GameSession>? SessionStarted;
    public event Action<GameSession>? SessionEnded;

    public IReadOnlyList<GameSession> Sessions
    {
        get { lock (_gate) return _sessions.ToArray(); }
    }

    public bool IsRunning(Guid accountId, Guid serverId)
    {
        lock (_gate) return _sessions.Any(s => s.Target.Account.Id == accountId && s.Target.Server.Id == serverId);
    }

    /// <summary>
    /// The prefixes a client is running in: this launcher's sessions, including ones still starting, and any game
    /// client found running on the system (for example one started before the launcher was).
    /// </summary>
    public IReadOnlySet<string> PrefixesInUse()
    {
        var prefixes = new HashSet<string>(ProcessTree.PrefixesRunning(GameInstall.ClientFileName), StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var session in _sessions) prefixes.Add(session.PrefixPath);
        }
        return prefixes;
    }

    /// <exception cref="LaunchException">The launch could not be started.</exception>
    public GameSession Start(LaunchEnvironment environment, LaunchTarget target)
    {
        var startInfo = environment.BuildStartInfo(target, out var clientPath, out var arguments);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(environment.PrefixPath))!);

        var name = target.Account.DisplayName;
        Log.Info($"Launching {name} on {target.Server.Name}: {environment.UmuRunPath} \"{clientPath}\" " +
                 $"{ClientArguments.Redact(arguments, target.Password)} " +
                 $"(WINEPREFIX={environment.PrefixPath}, PROTONPATH={(string.IsNullOrWhiteSpace(environment.ProtonPath) ? "<umu default>" : environment.ProtonPath)})");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var session = new GameSession(target, Path.GetFileName(clientPath), ClientPrefixes.Normalize(environment.PrefixPath));
        // umu, Proton and Wine can echo the command line; the password never reaches the log.
        var password = target.Password;
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Log.Info($"[{name}] {ClientArguments.RedactLine(e.Data, password)}"); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Log.Info($"[{name}] {ClientArguments.RedactLine(e.Data, password)}"); };
        process.Exited += (_, _) => OnExited(session, process);

        lock (_gate) _sessions.Add(session);
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            lock (_gate) _sessions.Remove(session);
            process.Dispose();
            throw new LaunchException($"Could not start {environment.UmuRunPath}: {e.Message}");
        }
        session.StartedUtc = DateTime.UtcNow;
        session.ProcessId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        SessionStarted?.Invoke(session);
        _ = FollowClientAsync(session);
        return session;
    }

    /// <summary>How often a session looks for its client process and checks it is still running.</summary>
    internal static readonly TimeSpan ClientPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Finds the session's game client under its launch, then ends the session when that client exits.</summary>
    private async Task FollowClientAsync(GameSession session)
    {
        using var timer = new PeriodicTimer(ClientPollInterval);
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            if (session.HasEnded) return;
            if (session.ClientProcessId is not { } client)
            {
                if (ProcessTree.FindDescendantRunning(session.ProcessId, session.ClientFileName) is { } found)
                {
                    session.ClientProcessId = found;
                    Log.Info($"{session.Target.Account.DisplayName} on {session.Target.Server.Name}: game client started (pid {found})");
                }
                continue;
            }
            if (!ProcessTree.IsAlive(client))
            {
                EndSession(session, "game client exited");
                return;
            }
        }
    }

    private void EndSession(GameSession session, string how)
    {
        if (!session.TryMarkEnded()) return;
        lock (_gate) _sessions.Remove(session);
        Log.Info($"{session.Target.Account.DisplayName} on {session.Target.Server.Name}: {how}");
        SessionEnded?.Invoke(session);
    }

    /// <summary>Starts every target in one environment; see the overload that picks an environment per target.</summary>
    public Task<int> LaunchAllAsync(LaunchEnvironment environment, IReadOnlyList<LaunchTarget> targets, TimeSpan delay,
        IProgress<string>? progress, CancellationToken cancellationToken) =>
        LaunchAllAsync((_, _) => Task.FromResult(environment), targets, delay, progress, cancellationToken);

    /// <summary>
    /// Starts each target in order with a pause between them, skipping ones already running.
    /// A target that fails is logged and the rest still launch.
    /// </summary>
    /// <param name="environmentFor">
    /// The environment a target launches in, above all its prefix. Called just before that target starts, so it
    /// sees every client started before it.
    /// </param>
    /// <returns>The number of clients started.</returns>
    public async Task<int> LaunchAllAsync(Func<LaunchTarget, CancellationToken, Task<LaunchEnvironment>> environmentFor,
        IReadOnlyList<LaunchTarget> targets, TimeSpan delay, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentFor);
        ArgumentNullException.ThrowIfNull(targets);
        var started = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var label = $"{target.Account.DisplayName} on {target.Server.Name}";
            if (IsRunning(target.Account.Id, target.Server.Id))
            {
                progress?.Report($"{label} is already running");
                continue;
            }
            try
            {
                var environment = await environmentFor(target, cancellationToken).ConfigureAwait(false);
                progress?.Report($"Launching {label} ({i + 1}/{targets.Count})");
                Start(environment, target);
                started++;
            }
            catch (LaunchException e)
            {
                Log.Error($"Launch failed for {label}: {e.Message}");
                progress?.Report($"Launch failed for {label}: {e.Message}");
                continue;
            }
            if (i < targets.Count - 1 && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        return started;
    }

    public void StopAll()
    {
        foreach (var session in Sessions) session.Stop();
    }

    /// <summary>
    /// Runs a Wine program in the prefix through umu and waits for it. The first run in a new prefix also
    /// downloads the runtime and Proton, so this can take minutes; output goes to the log.
    /// </summary>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunToolAsync(LaunchEnvironment environment, string program, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(environment.PrefixPath))!);
        var startInfo = environment.BuildToolStartInfo(program, arguments);
        Log.Info($"Running {program} {string.Join(' ', arguments)} in {environment.PrefixPath}");
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Log.Info($"[{program}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Log.Info($"[{program}] {e.Data}"); };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new LaunchException($"Could not start {environment.UmuRunPath}: {e.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessTree.Kill(process.Id);
            throw;
        }
        Log.Info($"{program} exited with code {process.ExitCode}");
        return process.ExitCode;
    }

    private void OnExited(GameSession session, Process process)
    {
        try
        {
            session.ExitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }
        process.Dispose();
        // Usually the client ended the session already; this covers a launch that never started one.
        EndSession(session, $"launch exited (code {session.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"})");
    }
}
