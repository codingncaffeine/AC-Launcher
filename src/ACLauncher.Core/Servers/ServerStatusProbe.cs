using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ACLauncher.Core.Servers;

public enum ServerUpState { Unknown, Up, Down }

public sealed record ServerStatus(ServerUpState State, TimeSpan? Latency, DateTime CheckedUtc, string? Detail = null)
{
    public static readonly ServerStatus Unknown = new(ServerUpState.Unknown, null, DateTime.MinValue);
}

/// <summary>Checks whether a game server answers on its login port.</summary>
public static class ServerStatusProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public static async Task<ServerStatus> ProbeAsync(string address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!ServerAddress.TryParse(address, out var parsed))
            return new ServerStatus(ServerUpState.Unknown, null, DateTime.UtcNow, "Invalid address");

        IPAddress ip;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(parsed.Host, cancellationToken).ConfigureAwait(false);
            ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        }
        catch (Exception e) when (e is SocketException or InvalidOperationException or ArgumentException)
        {
            return new ServerStatus(ServerUpState.Down, null, DateTime.UtcNow, "Host name not found");
        }

        using var client = new UdpClient(ip.AddressFamily);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            client.Connect(ip, parsed.Port);
            var packet = AcPacket.BuildLoginRequest("", (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var stopwatch = Stopwatch.StartNew();
            await client.SendAsync(packet, timeoutSource.Token).ConfigureAwait(false);
            var reply = await client.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var detail = AcPacket.ReadFlags(reply.Buffer) is { } flags ? $"flags 0x{flags:X8}" : "short reply";
            return new ServerStatus(ServerUpState.Up, stopwatch.Elapsed, DateTime.UtcNow, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServerStatus(ServerUpState.Down, null, DateTime.UtcNow, "No reply");
        }
        catch (SocketException e)
        {
            // An ICMP port-unreachable surfaces here as ConnectionRefused/ConnectionReset.
            return new ServerStatus(ServerUpState.Down, null, DateTime.UtcNow, e.SocketErrorCode.ToString());
        }
    }
}
