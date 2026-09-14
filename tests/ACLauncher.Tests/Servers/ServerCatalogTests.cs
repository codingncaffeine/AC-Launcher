using System.Net;
using ACLauncher.Core;
using ACLauncher.Core.Servers;

namespace ACLauncher.Tests.Servers;

public sealed class ServerCatalogTests : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("aclauncher-lists-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private static string List(params (string Name, string Address)[] servers) =>
        "<ArrayOfServerItem>" +
        string.Concat(servers.Select(s => $"<ServerItem><name>{s.Name}</name><connect_string>{s.Address}</connect_string></ServerItem>")) +
        "</ArrayOfServerItem>";

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!));
    }

    [Fact]
    public async Task DownloadsCachesMergesAndFallsBackToCache()
    {
        var a = new ServerListSource { Name = "List A", Url = "https://lists.example/a.xml" };
        var b = new ServerListSource { Name = "List B", Url = "https://lists.example/b.xml" };
        var off = new ServerListSource { Name = "Off", Url = "https://lists.example/off.xml", Enabled = false };
        // B is offline now but was cached earlier.
        File.WriteAllText(ServerCatalog.CacheFile(_cache, b), List(("Beta", "b.example:9000"), ("alpha", "dup.example:9000")));

        using var http = new HttpClient(new FakeHandler(uri => uri.AbsolutePath switch
        {
            "/a.xml" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(List(("Alpha", "a.example:9000"))) },
            "/off.xml" => throw new InvalidOperationException("a disabled list must not be fetched"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        var servers = await ServerCatalog.RefreshAsync(http, [a, b, off], _cache, CancellationToken.None);

        Assert.Equal(["Alpha", "Beta"], servers.Select(s => s.Name));
        Assert.Equal("a.example:9000", servers[0].Address); // the earlier list wins a duplicate name
        Assert.True(File.Exists(ServerCatalog.CacheFile(_cache, a)));
        Assert.Equal(["Alpha", "Beta"], ServerCatalog.LoadCached([a, b], _cache).Select(s => s.Name));
    }

    [Fact]
    public async Task InvalidXmlKeepsThePreviousCache()
    {
        var a = new ServerListSource { Name = "A", Url = "https://lists.example/a.xml" };
        File.WriteAllText(ServerCatalog.CacheFile(_cache, a), List(("Kept", "k.example:9000")));
        using var http = new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html") }));

        var servers = await ServerCatalog.RefreshAsync(http, [a], _cache, CancellationToken.None);

        Assert.Equal("Kept", Assert.Single(servers).Name);
        Assert.Contains("Kept", File.ReadAllText(ServerCatalog.CacheFile(_cache, a)));
    }

    [Fact]
    public void CacheFileNameIsSanitized() =>
        Assert.Equal(Path.Combine(_cache, "a_b__c.xml"), ServerCatalog.CacheFile(_cache, new ServerListSource { Name = "a/b .c" }));
}
