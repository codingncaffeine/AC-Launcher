using ACLauncher.Core.Servers;

namespace ACLauncher.Core.Launching;

public sealed class LaunchException(string message) : Exception(message);

/// <summary>Builds the game client's command line for a server.</summary>
public static class ClientArguments
{
    /// <summary>
    /// ACE servers take <c>-a user -v password -h host:port</c>; GDLE servers take
    /// <c>-h host -p port -a user:password</c>. Both take <c>-rodat on|off</c>.
    /// </summary>
    /// <param name="password">The account's password, resolved from wherever it is stored.</param>
    /// <exception cref="LaunchException">The account or server cannot produce a valid command line.</exception>
    public static IReadOnlyList<string> Build(Server server, Account account, string password)
    {
        if (string.IsNullOrWhiteSpace(account.Username))
            throw new LaunchException("The account has no user name.");
        if (!ServerAddress.TryParse(server.Address, out var address))
            throw new LaunchException($"Server {server.Name} has an invalid address '{server.Address}' (expected host:port).");
        // The client splits its command line on spaces itself, so a space can never reach it intact.
        if (account.Username.Any(char.IsWhiteSpace) || password.Any(char.IsWhiteSpace))
            throw new LaunchException($"Account {account.DisplayName}: the game client cannot accept spaces in a user name or password.");

        var rodat = server.Rodat ? "on" : "off";
        return server.Emulator switch
        {
            EmulatorType.GDLE =>
                ["-h", address.Host, "-p", address.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 "-a", $"{account.Username}:{password}", "-rodat", rodat],
            _ =>
                ["-a", account.Username, "-v", password, "-h", address.ToString(), "-rodat", rodat],
        };
    }

    /// <summary>A line of output with every occurrence of the password masked.</summary>
    public static string RedactLine(string line, string password) =>
        password.Length == 0 ? line : line.Replace(password, "********", StringComparison.Ordinal);

    /// <summary>The arguments as text for the log, with the password masked.</summary>
    public static string Redact(IEnumerable<string> arguments, string password) =>
        string.Join(' ', arguments.Select(a => password.Length == 0 ? a : a.Replace(password, "********", StringComparison.Ordinal)));
}
