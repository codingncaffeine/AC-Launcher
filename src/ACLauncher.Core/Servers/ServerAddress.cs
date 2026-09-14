using System.Globalization;

namespace ACLauncher.Core.Servers;

public readonly record struct ServerAddress(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";

    /// <summary>Parses <c>host:port</c>. The port must be 1–65535; surrounding whitespace is ignored.</summary>
    public static bool TryParse(string? text, out ServerAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon == trimmed.Length - 1) return false;
        var host = trimmed[..colon].Trim();
        if (host.Length == 0 || host.Any(char.IsWhiteSpace)) return false;
        if (!int.TryParse(trimmed[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return false;
        if (port is < 1 or > 65535) return false;
        address = new ServerAddress(host, port);
        return true;
    }
}
