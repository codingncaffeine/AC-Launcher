using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ACLauncher.Core.Proton;

namespace ACLauncher.Tests.Proton;

public sealed class ProtonReleasesTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string Page = """
        [
          {
            "tag_name": "GE-Proton11-6", "draft": false, "prerelease": false, "published_at": "2026-08-28T21:37:07Z",
            "created_at": "2026-08-28T20:00:00Z",
            "assets": [
              { "name": "GE-Proton11-6-aarch64.sha512sum", "size": 159, "browser_download_url": "https://dl.example/a.sum" },
              { "name": "GE-Proton11-6-aarch64.tar.gz", "size": 616776749, "browser_download_url": "https://dl.example/a.tgz" },
              { "name": "GE-Proton11-6-x86_64.sha512sum", "size": 158, "browser_download_url": "https://dl.example/x.sum" },
              { "name": "GE-Proton11-6-x86_64.tar.gz", "size": 533700853, "browser_download_url": "https://dl.example/x.tgz",
                "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }
            ]
          },
          {
            "tag_name": "GE-Proton12-0-rc1", "draft": false, "prerelease": true, "published_at": null,
            "created_at": "2026-09-01T00:00:00Z",
            "assets": [ { "name": "GE-Proton12-0-rc1.tar.gz", "size": 1, "browser_download_url": "https://dl.example/rc.tgz", "digest": "md5:abc" } ]
          },
          { "tag_name": "Draft", "draft": true, "prerelease": false, "published_at": null, "created_at": "2026-09-01T00:00:00Z", "assets": [] },
          { "tag_name": "SourceOnly", "draft": false, "prerelease": false, "published_at": "2026-01-01T00:00:00Z", "created_at": "2026-01-01T00:00:00Z",
            "assets": [ { "name": "notes.txt", "size": 1, "browser_download_url": "https://dl.example/n" } ] },
          {
            "tag_name": "GE-Proton7-54", "draft": false, "prerelease": false, "published_at": "2023-04-03T17:24:42Z",
            "created_at": "2023-04-03T17:24:42Z",
            "assets": [
              { "name": "GE-Proton7-54.sha512sum", "size": 151, "browser_download_url": "https://dl.example/7.sum" },
              { "name": "GE-Proton7-54.tar.gz", "size": 414903154, "browser_download_url": "https://dl.example/7.tgz" }
            ]
          }
        ]
        """;

    [Fact]
    public void KeepsX8664ArchivesAndSkipsDraftsAndSourceOnlyReleases()
    {
        var releases = ProtonReleases.Parse(Page, ProtonFamily.GEProton, out var count);

        Assert.Equal(5, count);
        Assert.Equal(["GE-Proton11-6", "GE-Proton12-0-rc1", "GE-Proton7-54"], releases.Select(r => r.Tag));

        var latest = releases[0];
        Assert.Equal("GE-Proton11-6-x86_64.tar.gz", latest.ArchiveName);
        Assert.Equal("https://dl.example/x.tgz", latest.ArchiveUrl);
        Assert.Equal("https://dl.example/x.sum", latest.ChecksumUrl);
        Assert.Equal(Digest, latest.ArchiveDigest);
        Assert.Equal(533700853, latest.ArchiveSize);
        Assert.False(latest.Prerelease);
        Assert.True(latest.IsVerifiable);

        var rc = releases[1];
        Assert.True(rc.Prerelease);
        Assert.Null(rc.ChecksumUrl);
        Assert.Null(rc.ArchiveDigest); // an unrecognised digest is not trusted
        Assert.False(rc.IsVerifiable);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), rc.Published);

        Assert.Equal("https://dl.example/7.sum", releases[2].ChecksumUrl);
        Assert.Null(releases[2].ArchiveDigest);
        Assert.True(releases[2].IsVerifiable);
    }

    [Theory]
    [InlineData("GE-Proton10-4", ProtonFamily.GEProton)]
    [InlineData("UMU-Proton-9.0-4e", ProtonFamily.UMUProton)]
    [InlineData("ULWGL-Proton-8.0-5", ProtonFamily.UMUProton)]
    [InlineData("Proton - Experimental", null)]
    public void RecognisesFamilies(string name, ProtonFamily? family) => Assert.Equal(family, ProtonFamilyInfo.FromName(name));

    [Fact]
    public void LatestChoiceValuesMapToFamilies()
    {
        Assert.Equal(ProtonFamily.GEProton, ProtonChoiceValue.LatestFamily(ProtonChoiceValue.LatestGE));
        Assert.Equal(ProtonFamily.UMUProton, ProtonChoiceValue.LatestFamily(ProtonChoiceValue.LatestUMU));
        Assert.Null(ProtonChoiceValue.LatestFamily("/opt/proton"));
    }
}

public sealed class ProtonInstallerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aclauncher-proton-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Root => Path.Combine(_dir, "root");

    private byte[] MakeArchive(string topFolder, bool withProtonScript = true)
    {
        var source = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(source, topFolder, "files", "bin"));
        if (withProtonScript) File.WriteAllText(Path.Combine(source, topFolder, "proton"), "#!/usr/bin/env python3\n");
        File.WriteAllText(Path.Combine(source, topFolder, "files", "bin", "wine"), "binary");
        File.CreateSymbolicLink(Path.Combine(source, topFolder, "files", "bin", "wine64"), "wine");
        var archive = Path.Combine(_dir, topFolder + ".tar.gz");
        using (var tar = Process.Start(new ProcessStartInfo("tar", ["-czf", archive, "-C", source, topFolder]))!)
            tar.WaitForExit();
        Directory.Delete(source, recursive: true);
        return File.ReadAllBytes(archive);
    }

    private static string Sha512File(byte[] archive, string name) => $"{Convert.ToHexStringLower(SHA512.HashData(archive))}  {name}\n";
    private static string Sha256Digest(byte[] archive) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(archive));

    private static ProtonRelease Release(string tag, string? checksumUrl = "https://dl.example/sum", string? digest = null) =>
        new(ProtonFamily.GEProton, tag, DateTimeOffset.UnixEpoch, false, tag + ".tar.gz",
            "https://dl.example/archive", 0, checksumUrl, digest);

    private sealed class FakeHandler(byte[] archive, string checksum) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            HttpContent content = request.RequestUri!.AbsolutePath == "/sum"
                ? new StringContent(checksum)
                : new ByteArrayContent(archive);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task InstallsVerifiedArchiveKeepingLinksAndListsIt()
    {
        var archive = MakeArchive("GE-Proton9-1");
        using var handler = new FakeHandler(archive, Sha512File(archive, "GE-Proton9-1.tar.gz"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var reports = new List<ProtonInstallProgress>();

        var dir = await ProtonInstaller.InstallAsync(http, Release("GE-Proton9-1", digest: Sha256Digest(archive)),
            new SyncProgress(reports), CancellationToken.None, Root);

        Assert.Equal(Path.Combine(Root, "GE-Proton9-1"), dir);
        Assert.True(File.Exists(Path.Combine(dir, "proton")));
        Assert.Equal("wine", new FileInfo(Path.Combine(dir, "files", "bin", "wine64")).LinkTarget);
        Assert.Equal(["GE-Proton9-1"], Directory.GetFileSystemEntries(Root).Select(Path.GetFileName));
        Assert.Contains(reports, r => r.BytesDone == archive.Length && r.BytesTotal == archive.Length);

        // Already installed: no second download.
        var before = handler.Requests;
        Assert.Equal(dir, await ProtonInstaller.InstallAsync(http, Release("GE-Proton9-1"), null, CancellationToken.None, Root));
        Assert.Equal(before, handler.Requests);
    }

    [Fact]
    public async Task VerifiesWithGitHubDigestWhenNoChecksumFileIsPublished()
    {
        var archive = MakeArchive("GE-Proton9-4");
        using var handler = new FakeHandler(archive, "");
        using var http = new HttpClient(handler, disposeHandler: false);

        var dir = await ProtonInstaller.InstallAsync(http, Release("GE-Proton9-4", checksumUrl: null, digest: Sha256Digest(archive)),
            null, CancellationToken.None, Root);

        Assert.True(File.Exists(Path.Combine(dir, "proton")));
    }

    [Fact]
    public async Task RejectsArchiveThatFailsItsChecksumFileAndLeavesNothing()
    {
        var archive = MakeArchive("GE-Proton9-2");
        using var handler = new FakeHandler(archive, new string('a', 128) + "  GE-Proton9-2.tar.gz");
        using var http = new HttpClient(handler, disposeHandler: false);

        var e = await Assert.ThrowsAsync<ProtonDownloadException>(() =>
            ProtonInstaller.InstallAsync(http, Release("GE-Proton9-2"), null, CancellationToken.None, Root));

        Assert.Contains("checksum", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task RejectsArchiveThatFailsTheDigestEvenWhenTheChecksumFileMatches()
    {
        var archive = MakeArchive("GE-Proton9-5");
        using var handler = new FakeHandler(archive, Sha512File(archive, "GE-Proton9-5.tar.gz"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var wrongDigest = "sha256:" + new string('b', 64);

        await Assert.ThrowsAsync<ProtonDownloadException>(() =>
            ProtonInstaller.InstallAsync(http, Release("GE-Proton9-5", digest: wrongDigest), null, CancellationToken.None, Root));
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task RefusesReleaseWithoutAnyChecksumBeforeDownloading()
    {
        using var handler = new FakeHandler([1, 2, 3], "");
        using var http = new HttpClient(handler, disposeHandler: false);

        var e = await Assert.ThrowsAsync<ProtonDownloadException>(() =>
            ProtonInstaller.InstallAsync(http, Release("GE-Proton4-1", checksumUrl: null), null, CancellationToken.None, Root));

        Assert.Contains("no checksum", e.Message);
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(Root, "GE-Proton4-1")));
    }

    [Fact]
    public async Task RejectsVerifiedArchiveWithoutProtonScript()
    {
        var archive = MakeArchive("stuff", withProtonScript: false);
        using var handler = new FakeHandler(archive, "");
        using var http = new HttpClient(handler, disposeHandler: false);

        var e = await Assert.ThrowsAsync<ProtonDownloadException>(() =>
            ProtonInstaller.InstallAsync(http, Release("GE-Proton9-3", checksumUrl: null, digest: Sha256Digest(archive)),
                null, CancellationToken.None, Root));

        Assert.Contains("does not contain a Proton build", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public void ListsNewestFirstByVersionAndRemovesByName()
    {
        foreach (var name in new[] { "GE-Proton9-27", "GE-Proton10-4", "UMU-Proton-10.0-4", ".install-GE-Proton11-0" })
        {
            Directory.CreateDirectory(Path.Combine(Root, name));
            File.WriteAllText(Path.Combine(Root, name, "proton"), "");
        }
        Directory.CreateDirectory(Path.Combine(Root, "not-a-build"));

        Assert.Equal(["UMU-Proton-10.0-4", "GE-Proton10-4", "GE-Proton9-27"], ProtonInstaller.ListInstalled(Root).Select(p => p.Name));
        Assert.Equal(Path.Combine(Root, "GE-Proton10-4"), ProtonInstaller.NewestInstalled(ProtonFamily.GEProton, Root));
        Assert.Equal(Path.Combine(Root, "UMU-Proton-10.0-4"), ProtonInstaller.NewestInstalled(ProtonFamily.UMUProton, Root));

        ProtonInstaller.Remove("GE-Proton10-4", Root);
        Assert.Equal(Path.Combine(Root, "GE-Proton9-27"), ProtonInstaller.NewestInstalled(ProtonFamily.GEProton, Root));
        Assert.Throws<ArgumentException>(() => ProtonInstaller.Remove("../outside", Root));
        Assert.Throws<ArgumentException>(() => ProtonInstaller.Remove(".install-GE-Proton11-0", Root));
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("xyz  file")]
    public void RejectsUnreadableChecksums(string text) => Assert.Null(ProtonInstaller.ParseChecksum(text));

    [Fact]
    public void ReadsChecksumFile()
    {
        var hex = new string('F', 128);
        Assert.Equal(hex, ProtonInstaller.ParseChecksum($"{hex}  GE-Proton11-6-x86_64.tar.gz\n"));
    }

    private sealed class SyncProgress(List<ProtonInstallProgress> reports) : IProgress<ProtonInstallProgress>
    {
        public void Report(ProtonInstallProgress value) => reports.Add(value);
    }
}
