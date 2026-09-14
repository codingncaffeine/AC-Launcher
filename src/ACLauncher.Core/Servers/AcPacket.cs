using System.Buffers.Binary;
using System.Text;

namespace ACLauncher.Core.Servers;

/// <summary>
/// The small part of the game's UDP protocol the status probe needs: a login request.
/// A packet is a 20-byte header (sequence, flags, checksum, id, time, size, table) followed by the payload.
/// </summary>
public static class AcPacket
{
    public const int HeaderSize = 20;
    public const uint FlagLoginRequest = 0x00010000;
    public const uint FlagConnectRequest = 0x00040000;

    private const uint HeaderChecksumSeed = 0xBADD70DD;
    private const uint AuthTypeAccountPassword = 1;
    private const string ClientVersion = "1802";

    /// <summary>
    /// The protocol's 32-bit checksum: the length shifted left 16, plus every whole little-endian
    /// 32-bit word, plus the trailing bytes packed big-end-first.
    /// </summary>
    public static uint Hash32(ReadOnlySpan<byte> data)
    {
        var checksum = (uint)data.Length << 16;
        var words = data.Length / 4;
        for (var i = 0; i < words; i++)
            checksum += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4));
        var shift = 24;
        for (var i = words * 4; i < data.Length; i++, shift -= 8)
            checksum += (uint)data[i] << shift;
        return checksum;
    }

    /// <summary>Checksum of an unencrypted packet: the header hashed with the seed in its checksum field, plus the payload hash.</summary>
    public static uint PacketChecksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        Span<byte> seeded = stackalloc byte[HeaderSize];
        header[..HeaderSize].CopyTo(seeded);
        BinaryPrimitives.WriteUInt32LittleEndian(seeded[8..], HeaderChecksumSeed);
        return Hash32(seeded) + Hash32(payload);
    }

    /// <summary>
    /// Builds a login request. The probe sends an empty account name: a server answers it (with a refusal)
    /// without creating an account, which is all a status check needs.
    /// </summary>
    public static byte[] BuildLoginRequest(string account, uint timestamp)
    {
        var body = new List<byte>();
        WriteString16(body, ClientVersion);
        var lengthOffset = body.Count;
        WriteUInt32(body, 0); // length of everything after this field, patched below
        WriteUInt32(body, AuthTypeAccountPassword);
        WriteUInt32(body, 0); // auth flags
        WriteUInt32(body, timestamp);
        WriteString16(body, account);
        WriteString16(body, ""); // log in as
        WriteUInt32(body, 0); // password (32-bit length-prefixed, empty)

        var payload = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(lengthOffset), (uint)(payload.Length - lengthOffset - 4));

        var packet = new byte[HeaderSize + payload.Length];
        var header = packet.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], FlagLoginRequest);
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], PacketChecksum(header, payload));
        return packet;
    }

    /// <summary>Reads the header flags of a received packet, or null if it is too short to be one.</summary>
    public static uint? ReadFlags(ReadOnlySpan<byte> packet) =>
        packet.Length >= HeaderSize ? BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]) : null;

    // A 16-bit length, the bytes, then zero padding so length field + text end on a 4-byte boundary.
    private static void WriteString16(List<byte> buffer, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        buffer.Add((byte)bytes.Length);
        buffer.Add((byte)(bytes.Length >> 8));
        buffer.AddRange(bytes);
        var pad = (4 - (2 + bytes.Length) % 4) % 4;
        for (var i = 0; i < pad; i++) buffer.Add(0);
    }

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        buffer.AddRange(bytes);
    }
}
