namespace ACLauncher.Core;

/// <summary>Accepts only web links, so a link from a downloaded list can never open a local file or another handler.</summary>
public static class SafeLinks
{
    public static bool TryGetWebLink(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>The link as text if it is a web link, otherwise null.</summary>
    public static string? WebLinkOrNull(string? text) => TryGetWebLink(text, out var uri) ? uri.AbsoluteUri : null;
}
