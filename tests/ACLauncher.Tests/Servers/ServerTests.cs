using System.Buffers.Binary;
using ACLauncher.Core;
using ACLauncher.Core.Servers;

namespace ACLauncher.Tests.Servers;

public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("play.example.org:9000", "play.example.org", 9000)]
    [InlineData(" 10.0.0.5 : 9050 ", "10.0.0.5", 9050)]
    public void ParsesHostAndPort(string text, string host, int port)
    {
        Assert.True(ServerAddress.TryParse(text, out var address));
        Assert.Equal(new ServerAddress(host, port), address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.org")]
    [InlineData("example.org:")]
    [InlineData(":9000")]
    [InlineData("example.org:0")]
    [InlineData("example.org:65536")]
    [InlineData("example.org:90a0")]
    [InlineData("bad host:9000")]
    public void RejectsInvalidAddresses(string? text) => Assert.False(ServerAddress.TryParse(text, out _));
}

public sealed class ServerListParserTests
{
    private static readonly ServerListSource Community = new() { Name = "Community", DefaultEmulator = EmulatorType.ACE };

    [Fact]
    public void ReadsHostPortDialect()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ArrayOfServerItem>
              <ServerItem>
                <id>59544be2-9435-400d-a589-30b2a39ed071</id>
                <name>Sample Server</name>
                <description>A test server.</description>
                <emu>ACE</emu>
                <server_host>play.example.org</server_host>
                <server_port>9000</server_port>
                <type>PvE</type>
                <status>Stable</status>
                <website_url></website_url>
                <discord_url>https://discord.example/abc</discord_url>
              </ServerItem>
              <ServerItem>
                <name>No Address</name>
                <description>Skipped.</description>
              </ServerItem>
            </ArrayOfServerItem>
            """;

        var server = Assert.Single(ServerListParser.Parse(xml, Community));

        Assert.Equal("Sample Server", server.Name);
        Assert.Equal("play.example.org:9000", server.Address);
        Assert.Equal(EmulatorType.ACE, server.Emulator);
        Assert.Equal("PvE", server.Type);
        Assert.Equal("Stable", server.Status);
        Assert.Null(server.WebsiteUrl);
        Assert.Equal("https://discord.example/abc", server.DiscordUrl);
        Assert.Equal(ServerSource.Published, server.Source);
        Assert.Equal("Community", server.ListName);
        Assert.Equal(Server.PublishedId("sample server"), server.Id);
    }

    [Fact]
    public void ReadsConnectStringDialectWithGdlEmulatorName()
    {
        const string xml = """
            <ArrayOfServerItem>
              <ServerItem>
                <name>Retail Like</name>
                <description>GDL test.</description>
                <emu>GDL</emu>
                <connect_string>gdl.example.org:9000</connect_string>
                <DiscordUrl>https://discord.example/gdl</DiscordUrl>
                <default_rodat>On</default_rodat>
              </ServerItem>
            </ArrayOfServerItem>
            """;

        var server = Assert.Single(ServerListParser.Parse(xml, Community));

        Assert.Equal(EmulatorType.GDLE, server.Emulator);
        Assert.Equal("gdl.example.org:9000", server.Address);
        Assert.True(server.Rodat);
        Assert.Equal("https://discord.example/gdl", server.DiscordUrl);
    }

    [Fact]
    public void FallsBackToTheListsDefaultEmulator()
    {
        const string xml = "<ArrayOfServerItem><ServerItem><name>X</name><connect_string>h:1</connect_string></ServerItem></ArrayOfServerItem>";
        var gdle = new ServerListSource { Name = "GDLE", DefaultEmulator = EmulatorType.GDLE };
        Assert.Equal(EmulatorType.GDLE, Assert.Single(ServerListParser.Parse(xml, gdle)).Emulator);
    }

    [Fact]
    public void PublishedIdIgnoresCaseAndSurroundingSpace() =>
        Assert.Equal(Server.PublishedId("Coldeve"), Server.PublishedId("  coldeve "));
}

public sealed class AcPacketTests
{
    [Fact]
    public void Hash32PacksTrailingBytesBigEndFirst()
    {
        // length 6 << 16, plus word 0x04030201, plus 0x05 << 24, plus 0x06 << 16.
        byte[] data = [1, 2, 3, 4, 5, 6];
        Assert.Equal((6u << 16) + 0x04030201u + (5u << 24) + (6u << 16), AcPacket.Hash32(data));
    }

    [Fact]
    public void LoginRequestHasConsistentHeaderAndPayload()
    {
        var packet = AcPacket.BuildLoginRequest("", 1_700_000_000);

        Assert.Equal(AcPacket.FlagLoginRequest, AcPacket.ReadFlags(packet));
        var size = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(16));
        Assert.Equal(packet.Length - AcPacket.HeaderSize, size);
        Assert.Equal(0, size % 4);

        var payload = packet.AsSpan(AcPacket.HeaderSize);
        // "1802" as a 16-bit length-prefixed, 4-byte-aligned string.
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(payload));
        Assert.Equal("1802"u8.ToArray(), payload.Slice(2, 4).ToArray());
        // The length field counts everything after itself.
        Assert.Equal((uint)(payload.Length - 12), BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]));
        Assert.Equal(1_700_000_000u, BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]));

        var stored = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8));
        Assert.Equal(AcPacket.PacketChecksum(packet.AsSpan(0, AcPacket.HeaderSize), payload), stored);
    }

    [Fact]
    public void ChecksumChangesWhenPayloadChanges()
    {
        var a = AcPacket.BuildLoginRequest("", 1);
        var b = AcPacket.BuildLoginRequest("", 2);
        Assert.NotEqual(BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
    }

    [Fact]
    public void ReadFlagsRejectsShortPackets() => Assert.Null(AcPacket.ReadFlags(new byte[19]));
}
