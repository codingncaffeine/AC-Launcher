using System.Diagnostics;
using ACLauncher.Core;
using ACLauncher.Core.Launching;

namespace ACLauncher.Tests.Launching;

/// <summary>
/// Sessions follow the game client's own process, not the launch: the launch that hosts a prefix's shared
/// wineserver keeps running until every client in the prefix has exited.
/// </summary>
public sealed class SessionTrackingTests : IDisposable
{
    // Stand-in for umu-run: starts a child whose program is the game client, then lingers the way the launch
    // hosting a prefix's wineserver outlives its own client.
    private const string FakeUmu = """
        #!/bin/bash
        if [ -z "$FAKE_NO_CLIENT" ]; then
            ( exec -a "/games/acclient.exe" sleep "$FAKE_CLIENT_SECONDS" ) &
        fi
        sleep "$FAKE_HOST_SECONDS"
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("aclauncher-track-").FullName;
    private readonly List<int> _launches = [];

    public void Dispose()
    {
        foreach (var pid in _launches) ProcessTree.Kill(pid);
        Directory.Delete(_dir, recursive: true);
    }

    private LaunchEnvironment Environment(Dictionary<string, string> variables)
    {
        var game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        foreach (var f in new[] { "acclient.exe", "client_portal.dat", "client_cell_1.dat", "client_local_English.dat" })
            File.WriteAllText(Path.Combine(game, f), "");
        var umu = Path.Combine(_dir, "fake-umu-run");
        File.WriteAllText(umu, FakeUmu + "\n");
        File.SetUnixFileMode(umu, (UnixFileMode)0b111_101_101);
        return new LaunchEnvironment(umu, Path.Combine(_dir, "prefix"), "GE-Proton", game, variables);
    }

    private static LaunchTarget Target() =>
        new(new Account { Username = "player" }, new Server { Name = "Test", Address = "h.example:9000" }, "pw");

    private (GameManager Manager, TaskCompletionSource<GameSession> Ended) Manager()
    {
        var manager = new GameManager();
        var ended = new TaskCompletionSource<GameSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.SessionEnded += s => ended.TrySetResult(s);
        return (manager, ended);
    }

    private static async Task<int> WaitForClient(GameSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (session.ClientProcessId is null && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
        return session.ClientProcessId ?? throw new TimeoutException("the client process was never found");
    }

    [Fact]
    public async Task SessionEndsWhenItsClientExitsWhileTheLaunchLingers()
    {
        var (manager, ended) = Manager();
        var stopwatch = Stopwatch.StartNew();
        var session = manager.Start(Environment(new() { ["FAKE_CLIENT_SECONDS"] = "2", ["FAKE_HOST_SECONDS"] = "30" }), Target());
        _launches.Add(session.ProcessId);

        var finished = await ended.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Same(session, finished);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 1.5, 8);
        Assert.NotNull(session.ClientProcessId);
        Assert.True(ProcessTree.IsAlive(session.ProcessId), "the launch keeps running after its client exits");
        Assert.Empty(manager.Sessions);
        Assert.False(manager.IsRunning(session.Target.Account.Id, session.Target.Server.Id));
    }

    [Fact]
    public async Task StopEndsOnlyTheClientNotTheLaunchHostingTheWineserver()
    {
        var (manager, ended) = Manager();
        var session = manager.Start(Environment(new() { ["FAKE_CLIENT_SECONDS"] = "30", ["FAKE_HOST_SECONDS"] = "30" }), Target());
        _launches.Add(session.ProcessId);
        var client = await WaitForClient(session);

        session.Stop();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(ProcessTree.IsAlive(client), "the client was stopped");
        Assert.True(ProcessTree.IsAlive(session.ProcessId), "the launch, and anything it hosts, keeps running");
    }

    [Fact]
    public async Task ALaunchThatNeverStartsAClientEndsWhenTheLaunchExits()
    {
        var (manager, ended) = Manager();
        var session = manager.Start(Environment(new() { ["FAKE_NO_CLIENT"] = "1", ["FAKE_HOST_SECONDS"] = "1" }), Target());
        _launches.Add(session.ProcessId);

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

        Assert.Null(session.ClientProcessId);
        Assert.Equal(0, session.ExitCode);
    }
}
