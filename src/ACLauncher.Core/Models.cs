using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ACLauncher.Core;

/// <summary>The server emulator a server runs; it decides the client's command-line grammar.</summary>
public enum EmulatorType { ACE, GDLE }

public enum ServerSource { User, Published }

public sealed class Server
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary><c>host:port</c> of the server's login endpoint.</summary>
    public string Address { get; set; } = "";

    public EmulatorType Emulator { get; set; } = EmulatorType.ACE;
    public bool Rodat { get; set; }
    public string? DiscordUrl { get; set; }
    public string? WebsiteUrl { get; set; }

    /// <summary>PvE, PvP, … as the published list states it.</summary>
    public string? Type { get; set; }

    /// <summary>Stable, Development, … as the published list states it.</summary>
    public string? Status { get; set; }

    /// <summary>An Asheron's Call folder to use for this server instead of the default one.</summary>
    public string? GameDirectory { get; set; }

    public ServerSource Source { get; set; } = ServerSource.User;

    /// <summary>For a published server, the name of the list it came from.</summary>
    public string? ListName { get; set; }

    /// <summary>
    /// A published server's id is derived from its name, so account selections survive list refreshes
    /// and the same server keeps its id on every machine.
    /// </summary>
    public static Guid PublishedId(string serverName) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("published:" + NormalizeName(serverName))).AsSpan(0, 16));

    /// <summary>The id version 0.5.0 derived; used only to carry its account links over to <see cref="PublishedId"/>.</summary>
    public static Guid LegacyPublishedId(string serverName)
    {
#pragma warning disable CA5351 // Not a security use: reproduces identifiers an earlier version wrote.
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes("published:" + NormalizeName(serverName))));
#pragma warning restore CA5351
    }

    private static string NormalizeName(string name) => name.Trim().ToLowerInvariant();
}

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";

    /// <summary>
    /// The password, when it is kept in <c>accounts.json</c>. Empty when <see cref="PasswordInKeyring"/> is set,
    /// because the desktop keyring holds it instead.
    /// </summary>
    public string Password { get; set; } = "";

    public bool PasswordInKeyring { get; set; }

    public string? Alias { get; set; }

    /// <summary>Whether the account is ticked for the next launch.</summary>
    public bool Enabled { get; set; } = true;

    public List<AccountServer> Servers { get; set; } = [];

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Username : Alias!;
}

/// <summary>An account's relationship to one server.</summary>
public sealed class AccountServer
{
    public Guid ServerId { get; set; }

    /// <summary>Whether this account launches on this server.</summary>
    public bool Selected { get; set; }
}

public sealed class AccountsDocument
{
    public int Version { get; set; } = 1;
    public List<Account> Accounts { get; set; } = [];
}

public sealed class ServersDocument
{
    public int Version { get; set; } = 1;
    public List<Server> Servers { get; set; } = [];
}

public sealed class LauncherSettings
{
    public int Version { get; set; } = 1;

    /// <summary>The Asheron's Call folder: the directory holding <c>acclient.exe</c> and the <c>.dat</c> files.</summary>
    public string? GameDirectory { get; set; }

    /// <summary>Wine prefix directory; null means <see cref="AppPaths.DefaultPrefix"/>.</summary>
    public string? PrefixPath { get; set; }

    /// <summary>
    /// Which Proton runs the game: <c>latest:GE-Proton</c> or <c>latest:UMU-Proton</c> (the newest release of that
    /// build, installed and kept current by the launcher), or a directory holding a Proton build.
    /// </summary>
    public string ProtonPath { get; set; } = Proton.ProtonChoiceValue.LatestGE;

    /// <summary>A specific <c>umu-run</c> to use; null means the one on PATH, then the launcher's own copy.</summary>
    public string? UmuRunPath { get; set; }

    /// <summary>Seconds between starting clients when several accounts launch at once.</summary>
    public int LaunchDelaySeconds { get; set; } = 8;

    /// <summary>Set the client's <c>FullScreen=False</c> before each launch; fullscreen start fails on many systems.</summary>
    public bool ForceWindowed { get; set; } = true;

    public bool CheckServerStatus { get; set; } = true;
    public bool ShowEnabledAccountsOnly { get; set; }

    /// <summary>Published servers the user has hidden.</summary>
    public List<Guid> HiddenServers { get; set; } = [];

    public List<ServerListSource> ServerLists { get; set; } = ServerListSource.Defaults();

    /// <summary>Extra environment variables for every client (e.g. <c>DXVK_HUD</c>, <c>PROTON_USE_WINED3D</c>).</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    [JsonIgnore]
    public string EffectivePrefix => string.IsNullOrWhiteSpace(PrefixPath) ? AppPaths.DefaultPrefix : PrefixPath!;
}

public sealed class ServerListSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public EmulatorType DefaultEmulator { get; set; } = EmulatorType.ACE;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A list decides where accounts send their passwords, so only lists fetched over HTTPS are used: over plain
    /// HTTP anyone on the network path could add a server of their own.
    /// </summary>
    [JsonIgnore]
    public bool IsSecure => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public static List<ServerListSource> Defaults() =>
    [
        new() { Name = "Community", Url = "https://raw.githubusercontent.com/acresources/serverslist/master/Servers.xml", DefaultEmulator = EmulatorType.ACE },
    ];
}
