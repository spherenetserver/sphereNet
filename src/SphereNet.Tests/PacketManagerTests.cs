using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using SphereNet.Network.Manager;
using System.Reflection;

namespace SphereNet.Tests;

public class PacketManagerTests
{
    private sealed class TestPacketHandler(byte packetId) : PacketHandler(packetId, 0)
    {
        public bool Received { get; private set; }

        public override void OnReceive(PacketBuffer buffer, NetState state)
        {
            Received = true;
        }
    }

    [Fact]
    public void PacketManager_RegistersStandardExtendedAndEncodedHandlers()
    {
        var manager = new PacketManager();
        var standard = new TestPacketHandler(0x02);
        var extended = new TestPacketHandler(0xBF);
        var encoded = new TestPacketHandler(0xD7);

        manager.Register(standard);
        manager.RegisterExtended(0x001C, extended);
        manager.RegisterEncoded(0x0001, encoded);

        Assert.Same(standard, manager.GetHandler(0x02));
        Assert.Same(extended, manager.GetExtendedHandler(0x001C));
        Assert.Same(encoded, manager.GetEncodedHandler(0x0001));
        Assert.Null(manager.GetExtendedHandler(0xFFFF));
        Assert.Null(manager.GetEncodedHandler(0xFFFF));
    }

    [Fact]
    public void PacketBuffer_ReadPastEnd_SetsUnderrunFlag()
    {
        var buffer = new PacketBuffer([0x01]);

        Assert.Equal((ushort)0, buffer.ReadUInt16());
        Assert.True(buffer.IsUnderrun);
    }

    [Fact]
    public void PacketProfileRequest_TruncatedBio_DoesNotOverread()
    {
        var handler = new PacketProfileRequest();
        var buffer = new PacketBuffer([0x01, 0, 0, 0, 1, 0, 1, 0, 10]);
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);
        bool invoked = false;
        state.ProfileRequestHandler = (_, mode, serial, bio) =>
        {
            invoked = true;
            Assert.Equal((byte)1, mode);
            Assert.Equal("", bio);
        };

        handler.OnReceive(buffer, state);

        Assert.True(invoked);
        Assert.False(buffer.IsUnderrun);
    }

    [Fact]
    public void PacketVendorBuy_RoutesRoundtripEntriesToState()
    {
        var handler = new PacketVendorBuy();
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);
        uint vendor = 0;
        byte flag = 0;
        List<VendorBuyEntry>? entries = null;
        state.VendorBuyHandler = (_, vendorSerial, buyFlag, items) =>
        {
            vendor = vendorSerial;
            flag = buyFlag;
            entries = items;
        };

        var buffer = new PacketBuffer([
            0x00, 0x00, 0x01, 0x00,
            0x01,
            0x1A, 0x40, 0x00, 0x00, 0x01, 0x00, 0x02
        ]);

        handler.OnReceive(buffer, state);

        Assert.Equal(0x00000100u, vendor);
        Assert.Equal((byte)1, flag);
        Assert.NotNull(entries);
        var entry = Assert.Single(entries!);
        Assert.Equal((byte)0x1A, entry.Layer);
        Assert.Equal(0x40000001u, entry.ItemSerial);
        Assert.Equal((ushort)2, entry.Amount);
    }

    [Fact]
    public void PacketVendorSell_RoutesRoundtripEntriesToState()
    {
        var handler = new PacketVendorSell();
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);
        uint vendor = 0;
        List<VendorSellEntry>? entries = null;
        state.VendorSellHandler = (_, vendorSerial, items) =>
        {
            vendor = vendorSerial;
            entries = items;
        };

        var buffer = new PacketBuffer([
            0x00, 0x00, 0x01, 0x00,
            0x00, 0x01,
            0x40, 0x00, 0x00, 0x02, 0x00, 0x03
        ]);

        handler.OnReceive(buffer, state);

        Assert.Equal(0x00000100u, vendor);
        Assert.NotNull(entries);
        var entry = Assert.Single(entries!);
        Assert.Equal(0x40000002u, entry.ItemSerial);
        Assert.Equal((ushort)3, entry.Amount);
    }

    [Fact]
    public void PacketSecureTrade_RoutesActionSessionAndParamToState()
    {
        var handler = new PacketSecureTrade();
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);
        byte action = 0;
        uint session = 0;
        uint param = 0;
        state.SecureTradeHandler = (_, a, s, p) =>
        {
            action = a;
            session = s;
            param = p;
        };

        var buffer = new PacketBuffer([
            0x02,
            0x80, 0x00, 0x00, 0x01,
            0x40, 0x00, 0x00, 0x02
        ]);

        handler.OnReceive(buffer, state);

        Assert.Equal((byte)2, action);
        Assert.Equal(0x80000001u, session);
        Assert.Equal(0x40000002u, param);
    }

    [Fact]
    public void ProtocolMatrix_DocumentsAllRegisteredIncomingHandlers()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        using var network = new NetworkManager(1, loggerFactory);
        string matrix = File.ReadAllText(FindRepoFile("docs", "PROTOCOL_MATRIX.md"));

        foreach (byte opcode in GetRegisteredOpcodes(network))
            Assert.Contains($"`0x{opcode:X2}`", matrix);
    }

    [Fact]
    public void RegisteredPacketHandlers_TruncatedPayloads_DoNotThrow()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        using var network = new NetworkManager(1, loggerFactory);
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);

        foreach (var handler in GetRegisteredHandlers(network))
        {
            int payloadLength = handler.ExpectedLength > 0 ? Math.Max(0, handler.ExpectedLength - 1) : 1;
            byte[] payload = new byte[Math.Min(payloadLength, 8)];
            var buffer = new PacketBuffer(payload);

            var ex = Record.Exception(() => handler.OnReceive(buffer, state));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void VariableLengthPacketHandlers_RandomBodies_DoNotThrow()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        using var network = new NetworkManager(1, loggerFactory);
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);
        var random = new Random(56);

        foreach (var handler in GetRegisteredHandlers(network).Where(h => h.ExpectedLength == 0))
        {
            byte[] payload = new byte[32];
            random.NextBytes(payload);
            var buffer = new PacketBuffer(payload);

            var ex = Record.Exception(() => handler.OnReceive(buffer, state));
            Assert.Null(ex);
        }
    }

    private static IEnumerable<byte> GetRegisteredOpcodes(NetworkManager network) =>
        GetRegisteredHandlers(network).Select(handler => handler.PacketId).Order();

    private static IEnumerable<PacketHandler> GetRegisteredHandlers(NetworkManager network)
    {
        var manager = network.Packets;
        var handlers = (PacketHandler?[])typeof(PacketManager)
            .GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        return handlers.Where(handler => handler != null).Cast<PacketHandler>();
    }

    [Fact]
    public void PacketWorldItemSA_Build_CorrectOpcodeAndLength()
    {
        var packet = new PacketWorldItemSA(0x40000001, 0x0EED, 100, 1000, 2000, 10, 0x0035);
        var buf = packet.Build();

        Assert.Equal(24, buf.Length);
        Assert.Equal(0xF3, buf.Data[0]);
    }

    [Fact]
    public void PacketWorldItemSA_HighSeas_Build_CorrectLength()
    {
        var packet = new PacketWorldItemSA(0x40000001, 0x0EED, 100, 1000, 2000, 10, 0x0035,
            highSeas: true);
        var buf = packet.Build();

        Assert.Equal(26, buf.Length);
        Assert.Equal(0xF3, buf.Data[0]);
    }

    [Fact]
    public void PacketPopupMessage_Build_CorrectOpcodeAndLength()
    {
        var packet = new PacketPopupMessage(0x05);
        var buf = packet.Build();

        Assert.Equal(2, buf.Length);
        Assert.Equal(0x53, buf.Data[0]);
        Assert.Equal(0x05, buf.Data[1]);
    }

    [Fact]
    public void PacketClilocMessageAffix_Build_CorrectOpcode()
    {
        var packet = new PacketClilocMessageAffix(
            0x00000001, 0x0190, 0x06, 0x0035, 0x0003,
            1042762, "TestName", " crafted", "");
        var buf = packet.Build();

        Assert.Equal(0xCC, buf.Data[0]);
        Assert.True(buf.Length > 30);
    }

    [Fact]
    public void PacketNewAnimation_Build_CorrectOpcodeAndLength()
    {
        var packet = new PacketNewAnimation(0x00000001, 11, 0, 5);
        var buf = packet.Build();

        Assert.Equal(10, buf.Length);
        Assert.Equal(0xE2, buf.Data[0]);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
