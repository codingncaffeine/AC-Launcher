using System.Xml;
using System.Xml.Linq;

namespace ACLauncher.Core.Servers;

/// <summary>
/// Reads the <c>ArrayOfServerItem</c> XML server lists published for emulator servers.
/// Two dialects exist: <c>server_host</c>/<c>server_port</c>/<c>discord_url</c> and
/// <c>connect_string</c>/<c>DiscordUrl</c>/<c>default_rodat</c>; both are accepted.
/// </summary>
public static class ServerListParser
{
    /// <summary>A list is plain data: no DTDs (so no entity expansion) and a size ceiling.</summary>
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        MaxCharactersInDocument = 8 * 1024 * 1024,
        IgnoreProcessingInstructions = true,
    };

    /// <exception cref="XmlException">The text is not XML, declares a DTD, or is too large.</exception>
    public static IReadOnlyList<Server> Parse(string xml, ServerListSource source)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, ReaderSettings);
        var doc = XDocument.Load(reader);
        var servers = new List<Server>();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "ServerItem"))
        {
            var name = Value(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var address = Value(item, "connect_string");
            if (string.IsNullOrWhiteSpace(address))
            {
                var host = Value(item, "server_host");
                var port = Value(item, "server_port");
                if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(port))
                    address = $"{host.Trim()}:{port.Trim()}";
            }
            if (!ServerAddress.TryParse(address, out var parsed)) continue;

            servers.Add(new Server
            {
                Id = Server.PublishedId(name),
                Name = name.Trim(),
                Description = Value(item, "description")?.Trim() ?? "",
                Address = parsed.ToString(),
                Emulator = ParseEmulator(Value(item, "emu"), source.DefaultEmulator),
                Rodat = string.Equals(Value(item, "default_rodat")?.Trim(), "On", StringComparison.OrdinalIgnoreCase),
                DiscordUrl = SafeLinks.WebLinkOrNull(Value(item, "discord_url") ?? Value(item, "DiscordUrl")),
                WebsiteUrl = SafeLinks.WebLinkOrNull(Value(item, "website_url")),
                Type = NullIfBlank(Value(item, "type")),
                Status = NullIfBlank(Value(item, "status")),
                Source = ServerSource.Published,
                ListName = source.Name,
            });
        }
        return servers;
    }

    internal static EmulatorType ParseEmulator(string? text, EmulatorType fallback) => text?.Trim().ToUpperInvariant() switch
    {
        "ACE" => EmulatorType.ACE,
        "GDL" or "GDLE" => EmulatorType.GDLE,
        _ => fallback,
    };

    // Element names differ in case between lists (DiscordUrl vs discord_url), so match exactly per name
    // but take the first non-empty occurrence.
    private static string? Value(XElement item, string name) =>
        item.Elements().FirstOrDefault(e => e.Name.LocalName == name && !string.IsNullOrWhiteSpace(e.Value))?.Value;

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
