using ACLauncher.Core.Steam;

namespace ACLauncher.Tests.Steam;

public sealed class LocatorTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("aclauncher-steam-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Make(params string[] parts)
    {
        var path = Path.Combine([_home, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void FindsRootOnceThroughSymlinkAndReadsLibraries()
    {
        var root = Make(".local", "share", "Steam", "steamapps");
        root = Path.GetDirectoryName(root)!;
        Make(".steam");
        Directory.CreateSymbolicLink(Path.Combine(_home, ".steam", "steam"), root);
        var extra = Make("games", "SteamLibrary", "steamapps");
        Put(Path.Combine(root, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
              "0" { "path" "{{root}}" }
              "1" { "path" "{{Path.GetDirectoryName(extra)}}" }
              "2" { "path" "/does/not/exist" }
            }
            """);

        var installs = SteamLocator.Find(_home);

        var install = Assert.Single(installs);
        Assert.Equal(root, install.Root);
        Assert.Equal([root, Path.GetDirectoryName(extra)!], install.Libraries);
    }

    [Fact]
    public void FindsSteamAndCompatibilityToolProtonBuilds()
    {
        var root = Path.GetDirectoryName(Make(".local", "share", "Steam", "steamapps"))!;
        foreach (var (dir, version) in new[] { ("Proton 9.0", "9.0-4"), ("Proton - Experimental", "experimental-11.0"), ("Proton 10.0", "10.0-3") })
        {
            var p = Make(".local", "share", "Steam", "steamapps", "common", dir);
            Put(Path.Combine(p, "proton"), "#!/usr/bin/env python3");
            Put(Path.Combine(p, "version"), $"1789159601 {version}");
            Put(Path.Combine(p, "toolmanifest.vdf"), "\"manifest\" { \"commandline\" \"/proton %verb%\" \"require_tool_appid\" \"4183110\" }");
        }
        // A folder under common/ that is not Proton must be ignored.
        Make(".local", "share", "Steam", "steamapps", "common", "SteamLinuxRuntime_4");

        var ge = Make(".local", "share", "Steam", "compatibilitytools.d", "GE-Proton10-4");
        Put(Path.Combine(ge, "proton"), "#!/usr/bin/env python3");
        Put(Path.Combine(ge, "compatibilitytool.vdf"), """
            "compatibilitytools"
            {
              "compat_tools"
              {
                "GE-Proton10-4"
                {
                  "install_path" "."
                  "display_name" "GE-Proton10-4"
                  "from_oslist" "windows"
                  "to_oslist" "linux"
                }
              }
            }
            """);

        var builds = ProtonLocator.Find(SteamLocator.Find(_home), _home);

        Assert.Equal(["proton_experimental", "proton_10", "proton_9", "GE-Proton10-4"], builds.Select(b => b.Name));
        Assert.Equal("experimental-11.0", builds[0].Version);
        Assert.Equal(4183110u, builds[0].RequiredToolAppId);
        Assert.Equal(ProtonSource.CompatibilityTool, builds[3].Source);
        Assert.Null(builds[3].RequiredToolAppId);
    }

    [Theory]
    [InlineData("Proton - Experimental", "proton_experimental")]
    [InlineData("Proton Hotfix", "proton_hotfix")]
    [InlineData("Proton 9.0", "proton_9")]
    [InlineData("Proton 10.0", "proton_10")]
    [InlineData("Proton 5.13", "proton_513")]
    [InlineData("Proton 7.0", "proton_7")]
    public void MapsSteamBuildNames(string dir, string expected) =>
        Assert.Equal(expected, ProtonLocator.SteamInternalName(dir));

    [Fact]
    public void ResolvesAppInstallDirFromManifest()
    {
        var root = Path.GetDirectoryName(Make(".local", "share", "Steam", "steamapps"))!;
        Make(".local", "share", "Steam", "steamapps", "common", "SteamLinuxRuntime_4");
        Put(Path.Combine(root, "steamapps", "appmanifest_4183110.acf"),
            "\"AppState\" { \"appid\" \"4183110\" \"installdir\" \"SteamLinuxRuntime_4\" }");

        var install = Assert.Single(SteamLocator.Find(_home));

        Assert.Equal(Path.Combine(root, "steamapps", "common", "SteamLinuxRuntime_4"), SteamLocator.FindAppInstallDir(install, 4183110));
        Assert.Null(SteamLocator.FindAppInstallDir(install, 1628350));
    }
}
