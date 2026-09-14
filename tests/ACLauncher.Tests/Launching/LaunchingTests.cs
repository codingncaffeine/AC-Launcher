using ACLauncher.Core;
using ACLauncher.Core.Launching;

namespace ACLauncher.Tests.Launching;

public sealed class ClientArgumentsTests
{
    private static Account Account(string user = "player", string password = "secret") => new() { Username = user, Password = password };

    [Fact]
    public void AceUsesSeparateUserPasswordAndHostPort()
    {
        var server = new Server { Name = "S", Address = "play.example.org:9000", Emulator = EmulatorType.ACE };
        Assert.Equal(["-a", "player", "-v", "secret", "-h", "play.example.org:9000", "-rodat", "off"], ClientArguments.Build(server, Account()));
    }

    [Fact]
    public void GdleUsesSplitHostPortAndCombinedCredentials()
    {
        var server = new Server { Name = "S", Address = "gdl.example.org:9050", Emulator = EmulatorType.GDLE, Rodat = true };
        Assert.Equal(["-h", "gdl.example.org", "-p", "9050", "-a", "player:secret", "-rodat", "on"], ClientArguments.Build(server, Account()));
    }

    [Theory]
    [InlineData("", "pw", "no user name")]
    [InlineData("pl ayer", "pw", "spaces")]
    [InlineData("player", "p w", "spaces")]
    public void RejectsAccountsTheClientCannotAccept(string user, string password, string expected)
    {
        var server = new Server { Name = "S", Address = "h:1" };
        var e = Assert.Throws<LaunchException>(() => ClientArguments.Build(server, Account(user, password)));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void RejectsInvalidServerAddress()
    {
        var e = Assert.Throws<LaunchException>(() => ClientArguments.Build(new Server { Name = "S", Address = "nohost" }, Account()));
        Assert.Contains("invalid address", e.Message);
    }

    [Fact]
    public void RedactMasksPassword() =>
        Assert.Equal("-a player -v ******** -h h:1", ClientArguments.Redact(["-a", "player", "-v", "secret", "-h", "h:1"], "secret"));
}

public sealed class GameInstallTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aclauncher-game-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    [Fact]
    public void AcceptsFolderWithClientAndDataFilesInAnyCase()
    {
        Touch("AcClient.EXE");
        Touch("client_portal.dat");
        Touch("CLIENT_CELL_1.DAT");
        Touch("client_local_English.dat");

        var check = GameInstall.Check(_dir);

        Assert.True(check.IsValid, check.Message);
        Assert.Equal(Path.Combine(_dir, "AcClient.EXE"), check.ClientPath);
    }

    [Fact]
    public void ReportsMissingDataFiles()
    {
        Touch("acclient.exe");
        Touch("client_portal.dat");

        var check = GameInstall.Check(_dir);

        Assert.False(check.IsValid);
        Assert.Equal(["client_cell_1.dat", "client_local_English.dat"], check.MissingDataFiles);
    }

    [Fact]
    public void PointsAtNestedFolderWhenParentWasChosen()
    {
        Touch(Path.Combine("Turbine", "Asheron's Call", "acclient.exe"));

        var check = GameInstall.Check(_dir);

        Assert.False(check.IsValid);
        Assert.Contains(Path.Combine(_dir, "Turbine", "Asheron's Call"), check.Message);
    }

    [Fact]
    public void RejectsMissingFolder() => Assert.False(GameInstall.Check(Path.Combine(_dir, "nope")).IsValid);
}

public sealed class LaunchEnvironmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aclauncher-env-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeGame(string name)
    {
        var game = Path.Combine(_dir, name);
        Directory.CreateDirectory(game);
        foreach (var f in new[] { "acclient.exe", "client_portal.dat", "client_cell_1.dat", "client_local_English.dat" })
            File.WriteAllText(Path.Combine(game, f), "");
        return game;
    }

    [Fact]
    public void BuildsUmuInvocationWithPrefixProtonAndWorkingDirectory()
    {
        var game = MakeGame("default");
        var env = new LaunchEnvironment("/usr/bin/umu-run", "/data/prefix", "GE-Proton", game,
            new Dictionary<string, string> { ["DXVK_HUD"] = "fps", ["WINEPREFIX"] = "/ignored" });
        var target = new LaunchTarget(new Account { Username = "player", Password = "pw" },
            new Server { Name = "S", Address = "h.example:9000" });

        var psi = env.BuildStartInfo(target, out var client, out _);

        Assert.Equal("/usr/bin/umu-run", psi.FileName);
        Assert.Equal(Path.Combine(game, "acclient.exe"), client);
        Assert.Equal([client, "-a", "player", "-v", "pw", "-h", "h.example:9000", "-rodat", "off"], psi.ArgumentList);
        Assert.Equal(game, psi.WorkingDirectory);
        Assert.Equal("/data/prefix", psi.Environment["WINEPREFIX"]);
        Assert.Equal("GE-Proton", psi.Environment["PROTONPATH"]);
        Assert.Equal("umu-default", psi.Environment["GAMEID"]);
        Assert.Equal("none", psi.Environment["STORE"]);
        Assert.Equal("fps", psi.Environment["DXVK_HUD"]);
    }

    [Fact]
    public void ServerFolderOverridesDefaultAndEmptyProtonUsesUmuDefault()
    {
        var fallback = MakeGame("default");
        var special = MakeGame("special");
        var env = new LaunchEnvironment("umu-run", "/p", "", fallback, new Dictionary<string, string>());
        var target = new LaunchTarget(new Account { Username = "u", Password = "p" },
            new Server { Name = "S", Address = "h:1", GameDirectory = special });

        var psi = env.BuildStartInfo(target, out var client, out _);

        Assert.Equal(Path.Combine(special, "acclient.exe"), client);
        Assert.False(psi.Environment.ContainsKey("PROTONPATH"));
    }

    [Fact]
    public void RefusesWhenNoGameFolderIsSet()
    {
        var env = new LaunchEnvironment("umu-run", "/p", "GE-Proton", null, new Dictionary<string, string>());
        var target = new LaunchTarget(new Account { Username = "u", Password = "p" }, new Server { Name = "S", Address = "h:1" });
        Assert.Throws<LaunchException>(() => env.BuildStartInfo(target, out _, out _));
    }
}

public sealed class ProcessTreeTests
{
    [Theory]
    [InlineData("1234 (wineserver) S 1 1234 1234 0", "wineserver", 1)]
    [InlineData("99 (a) b (c)) R 42 99 99 0", "a) b (c)", 42)]
    public void ParsesStatNamesWithSpacesAndParentheses(string stat, string name, int parent) =>
        Assert.Equal((name, parent), ProcessTree.ParseStat(stat));

    [Fact]
    public void FindsChildrenOfARealProcess()
    {
        using var shell = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", "sleep 30 & wait"]))!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            IReadOnlyList<int> descendants = [];
            while (DateTime.UtcNow < deadline && descendants.Count == 0)
            {
                descendants = ProcessTree.Descendants(shell.Id);
                if (descendants.Count == 0) Thread.Sleep(50);
            }
            var child = Assert.Single(descendants);
            Assert.Equal("sleep", ProcessTree.ReadStat(child)?.Name);

            ProcessTree.Kill(shell.Id);
            Assert.True(shell.WaitForExit(5000));
            // A killed child lingers briefly as a zombie until it is reaped; either state means it is dead.
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && IsAlive(child)) Thread.Sleep(20);
            Assert.False(IsAlive(child));
        }
        finally
        {
            if (!shell.HasExited) shell.Kill(entireProcessTree: true);
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var state = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0];
            return ProcessTree.ParseStat(stat)?.Name == "sleep" && state != "Z" && state != "X";
        }
        catch (IOException)
        {
            return false;
        }
    }
}
