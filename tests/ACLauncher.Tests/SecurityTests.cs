using System.Xml;
using ACLauncher.Core;
using ACLauncher.Core.Launching;
using ACLauncher.Core.Servers;

namespace ACLauncher.Tests;

public sealed class SafeLinksTests
{
    [Theory]
    [InlineData("https://discord.gg/abc")]
    [InlineData("http://example.org/page")]
    [InlineData("  https://example.org  ")]
    public void AcceptsWebLinks(string link) => Assert.True(SafeLinks.TryGetWebLink(link, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.org/")]
    [InlineData("smb://host/share")]
    [InlineData("/home/user/.ssh/id_ed25519")]
    [InlineData("example.org")]
    [InlineData("-e https://example.org")]
    public void RefusesEverythingElse(string? link) => Assert.False(SafeLinks.TryGetWebLink(link, out _));

    [Fact]
    public void ServerListDropsLinksThatAreNotWebLinks()
    {
        const string xml = """
            <ArrayOfServerItem><ServerItem>
              <name>S</name><connect_string>h.example:9000</connect_string>
              <discord_url>file:///etc/passwd</discord_url><website_url>https://ok.example/</website_url>
            </ServerItem></ArrayOfServerItem>
            """;
        var server = Assert.Single(ServerListParser.Parse(xml, new ServerListSource { Name = "L" }));
        Assert.Null(server.DiscordUrl);
        Assert.Equal("https://ok.example/", server.WebsiteUrl);
    }
}

public sealed class ServerListHardeningTests
{
    [Fact]
    public void RefusesDocumentTypeDeclarations()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [ <!ENTITY lol "lol"> <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"> ]>
            <ArrayOfServerItem><ServerItem><name>&lol2;</name><connect_string>h:1</connect_string></ServerItem></ArrayOfServerItem>
            """;
        Assert.Throws<XmlException>(() => ServerListParser.Parse(xml, new ServerListSource { Name = "L" }));
    }

    [Fact]
    public void RefusesExternalEntities()
    {
        const string xml = """
            <!DOCTYPE x [ <!ENTITY secret SYSTEM "file:///etc/hostname"> ]>
            <ArrayOfServerItem><ServerItem><name>&secret;</name><connect_string>h:1</connect_string></ServerItem></ArrayOfServerItem>
            """;
        Assert.Throws<XmlException>(() => ServerListParser.Parse(xml, new ServerListSource { Name = "L" }));
    }

    [Theory]
    [InlineData("https://lists.example/a.xml", true)]
    [InlineData("http://lists.example/a.xml", false)]
    [InlineData("file:///tmp/list.xml", false)]
    [InlineData("not a url", false)]
    public void OnlyHttpsListsAreSecure(string url, bool secure) =>
        Assert.Equal(secure, new ServerListSource { Url = url }.IsSecure);

    [Fact]
    public void DefaultListsAreAllSecure() => Assert.All(ServerListSource.Defaults(), s => Assert.True(s.IsSecure));

    [Fact]
    public void PublishedIdsAreStableCaseInsensitiveAndDifferFromTheLegacyIds()
    {
        Assert.Equal(Server.PublishedId("Coldeve"), Server.PublishedId(" coldeve "));
        Assert.Equal(Server.LegacyPublishedId("Coldeve"), Server.LegacyPublishedId("COLDEVE"));
        Assert.NotEqual(Server.PublishedId("Coldeve"), Server.LegacyPublishedId("Coldeve"));
        Assert.NotEqual(Server.PublishedId("Coldeve"), Server.PublishedId("Leafdawning"));
    }
}

public sealed class PasswordExposureTests
{
    [Fact]
    public void RedactLineMasksEveryOccurrence() =>
        Assert.Equal("wine: cmd -v ******** and ********", ClientArguments.RedactLine("wine: cmd -v hunter2 and hunter2", "hunter2"));

    [Fact]
    public void LaunchTargetTextNeverContainsThePassword()
    {
        var target = new LaunchTarget(new Account { Username = "player" }, new Server { Name = "S" }, "hunter2");
        Assert.DoesNotContain("hunter2", target.ToString());
    }
}

public sealed class FilePermissionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aclauncher-perm-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SavedDocumentsAreOwnerOnlyEvenWhenReplacingAReadableFile()
    {
        var config = Path.Combine(_dir, "config");
        var file = Path.Combine(config, "accounts.json");
        Directory.CreateDirectory(config);
        File.WriteAllText(file, "{}");
        File.SetUnixFileMode(file, (UnixFileMode)0b110_100_100); // 0644, as an older version left it

        JsonStore.Save(file, new AccountsDocument { Accounts = [new Account { Username = "u" }] });

        Assert.Equal(AppPaths.PrivateFileMode, File.GetUnixFileMode(file));
        Assert.Contains("\"username\": \"u\"", File.ReadAllText(file));
    }

    [Fact]
    public void PrivateDirectoriesAreCreatedAndTightened()
    {
        var fresh = Path.Combine(_dir, "fresh", "nested");
        AppPaths.CreatePrivateDirectory(fresh);
        Assert.Equal(AppPaths.PrivateDirectoryMode, File.GetUnixFileMode(fresh));

        var loose = Path.Combine(_dir, "loose");
        Directory.CreateDirectory(loose);
        File.SetUnixFileMode(loose, (UnixFileMode)0b111_101_101); // 0755
        AppPaths.CreatePrivateDirectory(loose);
        Assert.Equal(AppPaths.PrivateDirectoryMode, File.GetUnixFileMode(loose));
    }
}

public sealed class DownloadDigestTests
{
    [Theory]
    [InlineData("sha256:eb590691841f7fad3fc3ad8fd5db4ccb87849fe7948e62b28ece7a4ee48cc851", "eb590691841f7fad3fc3ad8fd5db4ccb87849fe7948e62b28ece7a4ee48cc851")]
    [InlineData("sha256:EB590691841F7FAD3FC3AD8FD5DB4CCB87849FE7948E62B28ECE7A4EE48CC851", "EB590691841F7FAD3FC3AD8FD5DB4CCB87849FE7948E62B28ECE7A4EE48CC851")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("md5:0123456789abcdef0123456789abcdef", null)]
    [InlineData("sha256:xyz", null)]
    [InlineData("sha256:zb590691841f7fad3fc3ad8fd5db4ccb87849fe7948e62b28ece7a4ee48cc851", null)]
    public void ParsesOnlyWellFormedSha256Digests(string? digest, string? expected) =>
        Assert.Equal(expected, Umu.ParseSha256Digest(digest));
}
