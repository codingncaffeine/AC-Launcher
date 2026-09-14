using System.Diagnostics;
using System.Text;
using ACLauncher.Core;
using ACLauncher.Core.Launching;

namespace ACLauncher.Tests.Launching;

/// <summary>
/// The game refuses a second client in the same Wine prefix, so each client running at the same time gets a copy of
/// the configured prefix.
/// </summary>
public sealed class ClientPrefixesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aclauncher-prefixes-").FullName;
    private readonly string _base;
    private readonly ClientPrefixes _prefixes;

    public ClientPrefixesTests()
    {
        _base = Path.Combine(_dir, "prefix");
        Directory.CreateDirectory(Path.Combine(_base, "dosdevices"));
        File.WriteAllText(Path.Combine(_base, "system.reg"), "WINE REGISTRY Version 2\n");
        // A real prefix maps z: to the whole filesystem; a copy that followed it would never finish.
        File.CreateSymbolicLink(Path.Combine(_base, "dosdevices", "z:"), "/");
        File.CreateSymbolicLink(Path.Combine(_base, "pfx"), ".");
        WriteSetting(_base, "UserPreferences.ini", "[Display]\r\nFullScreen=False\r\n", DateTime.UtcNow.AddHours(-1));
        _prefixes = new ClientPrefixes(_base, Path.Combine(_dir, "copies"));
    }

    public void Dispose() => DeleteTree(_dir);

    /// <summary>Deletes a folder without ever following a symlink out of it (dosdevices/z: points at /).</summary>
    private static void DeleteTree(string path)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) File.Delete(entry.FullName);
            else if (entry is DirectoryInfo) DeleteTree(entry.FullName);
            else entry.Delete();
        }
        Directory.Delete(path);
    }

    private static string SettingsFile(string prefix, string name) =>
        Path.Combine(Path.GetDirectoryName(ClientPreferences.PathFor(prefix))!, name);

    private static void WriteSetting(string prefix, string name, string text, DateTime writtenUtc)
    {
        var path = SettingsFile(prefix, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    private static HashSet<string> Busy(params string[] prefixes) => new(prefixes, StringComparer.Ordinal);

    [Fact]
    public async Task TheFirstClientUsesTheConfiguredPrefix()
    {
        var prefix = await _prefixes.AcquireAsync(Busy(), null, TestContext.Current.CancellationToken);

        Assert.Equal(_base, prefix);
        Assert.False(Directory.Exists(_prefixes.CopiesDirectory));
    }

    [Fact]
    public async Task ASecondClientGetsACopyWithTheSymlinksKeptAsLinks()
    {
        var prefix = await _prefixes.AcquireAsync(Busy(_base), null, TestContext.Current.CancellationToken);

        Assert.Equal(_prefixes.PathFor(2), prefix);
        Assert.True(ClientPrefixes.IsInitialized(prefix));
        Assert.Equal("/", new FileInfo(Path.Combine(prefix, "dosdevices", "z:")).LinkTarget);
        Assert.Equal(".", new FileInfo(Path.Combine(prefix, "pfx")).LinkTarget);
        Assert.Equal(_base + "\n", File.ReadAllText(Path.Combine(prefix, ClientPrefixes.MarkerFileName)));
        Assert.Equal("[Display]\r\nFullScreen=False\r\n", File.ReadAllText(SettingsFile(prefix, "UserPreferences.ini")));
        Assert.Empty(Directory.GetDirectories(_prefixes.CopiesDirectory, ".*"));
    }

    [Fact]
    public async Task AFreeCopyIsReusedAndABusyOneIsSkipped()
    {
        var token = TestContext.Current.CancellationToken;
        var second = await _prefixes.AcquireAsync(Busy(_base), null, token);
        var marker = Path.Combine(second, ClientPrefixes.MarkerFileName);
        File.SetLastWriteTimeUtc(marker, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(second, await _prefixes.AcquireAsync(Busy(_base), null, token));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(marker));

        var third = await _prefixes.AcquireAsync(Busy(_base, second), null, token);
        Assert.Equal(_prefixes.PathFor(3), third);
        Assert.True(ClientPrefixes.IsInitialized(third));
    }

    [Fact]
    public async Task AFolderItDidNotMakeIsLeftAlone()
    {
        var foreign = _prefixes.PathFor(2);
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "mine");

        var e = await Assert.ThrowsAsync<LaunchException>(() => _prefixes.AcquireAsync(Busy(_base), null, TestContext.Current.CancellationToken));

        Assert.Contains("not made by AC Launcher", e.Message);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(foreign, "keep.txt")));
    }

    [Fact]
    public async Task ACopyIsNotMadeFromAPrefixThatIsNotSetUp()
    {
        File.Delete(Path.Combine(_base, "system.reg"));

        await Assert.ThrowsAsync<LaunchException>(() => _prefixes.AcquireAsync(Busy(_base), null, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(_prefixes.PathFor(2)));
    }

    [Fact]
    public async Task NewerSettingsInTheConfiguredPrefixReachTheCopiesButNotTheOtherWayRound()
    {
        var token = TestContext.Current.CancellationToken;
        var copy = await _prefixes.AcquireAsync(Busy(_base), null, token);

        WriteSetting(_base, "acclient.keymap", "base keys", DateTime.UtcNow);
        WriteSetting(_base, "UserPreferences.ini", "base settings", DateTime.UtcNow.AddMinutes(-30));
        WriteSetting(copy, "UserPreferences.ini", "changed in the second client", DateTime.UtcNow);
        await _prefixes.AcquireAsync(Busy(_base), null, token);

        Assert.Equal("base keys", File.ReadAllText(SettingsFile(copy, "acclient.keymap")));
        Assert.Equal("changed in the second client", File.ReadAllText(SettingsFile(copy, "UserPreferences.ini")));
    }

    [Fact]
    public void CopiesOfDifferentPrefixesAreKeptApart()
    {
        var other = new ClientPrefixes(Path.Combine(_dir, "other"), Path.Combine(_dir, "copies"));
        var sameWithSlash = new ClientPrefixes(_base + "/", Path.Combine(_dir, "copies"));

        Assert.NotEqual(_prefixes.CopiesDirectory, other.CopiesDirectory);
        Assert.Equal(_prefixes.CopiesDirectory, sameWithSlash.CopiesDirectory);
        Assert.Equal(_base, sameWithSlash.PathFor(1));
    }
}

public sealed class RunningPrefixTests
{
    private static byte[] Environ(params string[] entries) => Encoding.UTF8.GetBytes(string.Join('\0', entries) + "\0");

    [Fact]
    public void TheCompatDataPathNamesThePrefix() =>
        Assert.Equal("/data/prefix", ProcessTree.PrefixFromEnvironment(
            Environ("HOME=/home/u", "WINEPREFIX=/data/prefix/pfx/", "STEAM_COMPAT_DATA_PATH=/data/prefix")));

    [Theory]
    [InlineData("WINEPREFIX=/data/prefix/pfx/", "/data/prefix")]
    [InlineData("WINEPREFIX=/data/plain/", "/data/plain")]
    public void WithoutItTheWinePrefixIsUsed(string entry, string expected) =>
        Assert.Equal(expected, ProcessTree.PrefixFromEnvironment(Environ("HOME=/home/u", entry)));

    [Fact]
    public void NoPrefixVariablesMeansNoPrefix() =>
        Assert.Null(ProcessTree.PrefixFromEnvironment(Environ("HOME=/home/u", "PATH=/usr/bin")));

    [Fact]
    public void AClientRunningOutsideTheLauncherIsFound()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "aclauncher-running-" + Guid.NewGuid().ToString("N"));
        var startInfo = new ProcessStartInfo("/bin/bash", ["-c", "exec -a 'C:\\Games\\acclient.exe' sleep 30"]);
        startInfo.Environment["STEAM_COMPAT_DATA_PATH"] = prefix + "/";
        using var client = Process.Start(startInfo)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var found = false;
            while (!found && DateTime.UtcNow < deadline)
            {
                found = ProcessTree.PrefixesRunning("acclient.exe").Contains(prefix);
                if (!found) Thread.Sleep(50);
            }
            Assert.True(found, "the running client's prefix was not found");
            Assert.DoesNotContain(prefix, ProcessTree.PrefixesRunning("otherclient.exe"));
        }
        finally
        {
            client.Kill();
        }
    }
}
