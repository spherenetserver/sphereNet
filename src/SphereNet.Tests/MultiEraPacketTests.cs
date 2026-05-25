using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;

namespace SphereNet.Tests;

public class MultiEraPacketTests
{
    // --- NetState Helper Properties ---

    [Fact]
    public void NetState_Version70331_SupportsNewMobileIncoming()
    {
        var state = CreateNetState();
        state.ClientVersionNumber = 70_033_001;
        Assert.True(state.SupportsNewMobileIncoming);
    }

    [Fact]
    public void NetState_Version70130_SupportsNewCharacterList()
    {
        var state = CreateNetState();
        state.ClientVersionNumber = 70_013_000;
        Assert.True(state.SupportsNewCharacterList);
    }

    [Fact]
    public void NetState_Version70300_SupportsExtendedStatus()
    {
        var state = CreateNetState();
        state.ClientVersionNumber = 70_030_000;
        Assert.True(state.SupportsExtendedStatus);
    }

    [Fact]
    public void NetState_PreVersion70331_NoNewMobileIncoming()
    {
        var state = CreateNetState();
        state.ClientVersionNumber = 70_030_000;
        Assert.False(state.SupportsNewMobileIncoming);
    }

    [Fact]
    public void NetState_PreVersion70130_NoNewCharacterList()
    {
        var state = CreateNetState();
        state.ClientVersionNumber = 70_009_000;
        Assert.False(state.SupportsNewCharacterList);
    }

    // --- PacketDrawObject (0x78) Version Branching ---

    [Fact]
    public void PacketDrawObject_OldFormat_HueNonZero_Uses15BitWithSignalBit()
    {
        var equipment = new (uint Serial, ushort ItemId, byte Layer, ushort Hue)[]
        {
            (0x40000001, 0x1515, 0x06, 0x0035)
        };

        var pkt = new PacketDrawObject(
            0x00000001, 0x0190, 100, 200, 10, 0x00, 0, 0, 0x01,
            equipment, newMobileIncoming: false);
        var buf = pkt.Build();

        byte[] data = buf.Data;
        Assert.Equal(0x78, data[0]);

        // Equipment starts at offset 19 (1 opcode + 2 len + 4 serial + 2 body + 2 x + 2 y + 1 z + 1 dir + 2 hue + 1 flags + 1 noto = 19)
        int eqOfs = 19;
        // Serial (4 bytes)
        Assert.Equal(0x40, data[eqOfs]);
        Assert.Equal(0x00, data[eqOfs + 1]);
        Assert.Equal(0x00, data[eqOfs + 2]);
        Assert.Equal(0x01, data[eqOfs + 3]);
        // ItemId with signal bit: (0x1515 & 0x7FFF) | 0x8000 = 0x9515
        ushort writtenItemId = (ushort)((data[eqOfs + 4] << 8) | data[eqOfs + 5]);
        Assert.Equal(0x9515, writtenItemId);
        // Layer
        Assert.Equal(0x06, data[eqOfs + 6]);
        // Hue
        ushort writtenHue = (ushort)((data[eqOfs + 7] << 8) | data[eqOfs + 8]);
        Assert.Equal(0x0035, writtenHue);
    }

    [Fact]
    public void PacketDrawObject_OldFormat_HueZero_NoHueShort()
    {
        var equipment = new (uint Serial, ushort ItemId, byte Layer, ushort Hue)[]
        {
            (0x40000001, 0x1515, 0x06, 0x0000)
        };

        var pkt = new PacketDrawObject(
            0x00000001, 0x0190, 100, 200, 10, 0x00, 0, 0, 0x01,
            equipment, newMobileIncoming: false);
        var buf = pkt.Build();

        byte[] data = buf.Data;
        int eqOfs = 19;
        // ItemId: 0x1515 & 0x7FFF = 0x1515 (no signal bit)
        ushort writtenItemId = (ushort)((data[eqOfs + 4] << 8) | data[eqOfs + 5]);
        Assert.Equal(0x1515, writtenItemId);
        // Layer at +6
        Assert.Equal(0x06, data[eqOfs + 6]);
        // No hue short — next 4 bytes should be terminator (0x00000000)
        uint terminator = (uint)((data[eqOfs + 7] << 24) | (data[eqOfs + 8] << 16) |
                                 (data[eqOfs + 9] << 8) | data[eqOfs + 10]);
        Assert.Equal(0u, terminator);
    }

    [Fact]
    public void PacketDrawObject_NewFormat_AlwaysWritesHue()
    {
        var equipment = new (uint Serial, ushort ItemId, byte Layer, ushort Hue)[]
        {
            (0x40000001, 0x1515, 0x06, 0x0000)
        };

        var pkt = new PacketDrawObject(
            0x00000001, 0x0190, 100, 200, 10, 0x00, 0, 0, 0x01,
            equipment, newMobileIncoming: true);
        var buf = pkt.Build();

        byte[] data = buf.Data;
        int eqOfs = 19;
        // Serial
        Assert.Equal(0x40, data[eqOfs]);
        // ItemId: full 16-bit (0x1515 & 0xFFFF = 0x1515)
        ushort writtenItemId = (ushort)((data[eqOfs + 4] << 8) | data[eqOfs + 5]);
        Assert.Equal(0x1515, writtenItemId);
        // Layer
        Assert.Equal(0x06, data[eqOfs + 6]);
        // Hue ALWAYS written (even when 0)
        ushort writtenHue = (ushort)((data[eqOfs + 7] << 8) | data[eqOfs + 8]);
        Assert.Equal(0x0000, writtenHue);
        // Terminator after equipment
        uint terminator = (uint)((data[eqOfs + 9] << 24) | (data[eqOfs + 10] << 16) |
                                 (data[eqOfs + 11] << 8) | data[eqOfs + 12]);
        Assert.Equal(0u, terminator);
    }

    [Fact]
    public void PacketDrawObject_NewFormat_HueNonZero_NoSignalBit()
    {
        var equipment = new (uint Serial, ushort ItemId, byte Layer, ushort Hue)[]
        {
            (0x40000001, 0x1515, 0x06, 0x0035)
        };

        var pkt = new PacketDrawObject(
            0x00000001, 0x0190, 100, 200, 10, 0x00, 0, 0, 0x01,
            equipment, newMobileIncoming: true);
        var buf = pkt.Build();

        byte[] data = buf.Data;
        int eqOfs = 19;
        // Full 16-bit itemId, no signal bit
        ushort writtenItemId = (ushort)((data[eqOfs + 4] << 8) | data[eqOfs + 5]);
        Assert.Equal(0x1515, writtenItemId);
        // Hue always present
        ushort writtenHue = (ushort)((data[eqOfs + 7] << 8) | data[eqOfs + 8]);
        Assert.Equal(0x0035, writtenHue);
    }

    [Fact]
    public void PacketDrawObject_BothFormats_TerminatorIs4ByteZero()
    {
        var emptyEquip = Array.Empty<(uint, ushort, byte, ushort)>();

        var oldPkt = new PacketDrawObject(1, 0x0190, 0, 0, 0, 0, 0, 0, 0, emptyEquip, false);
        var newPkt = new PacketDrawObject(1, 0x0190, 0, 0, 0, 0, 0, 0, 0, emptyEquip, true);

        var oldBuf = oldPkt.Build();
        var newBuf = newPkt.Build();

        // Both end with 4-byte zero terminator (at offset 19)
        for (int i = 19; i < 23; i++)
        {
            Assert.Equal(0, oldBuf.Data[i]);
            Assert.Equal(0, newBuf.Data[i]);
        }
    }

    // --- PacketCharList (0xA9) Version Branching ---

    [Fact]
    public void PacketCharList_OldFormat_OmitsCoordinates()
    {
        var pkt = new PacketCharList(["TestChar"], maxChars: 1, newCharacterList: false);
        var buf = pkt.Build();
        byte[] data = buf.Data;

        Assert.Equal(0xA9, data[0]);

        // Old format: 1 char slot = 60 bytes (30+30), 9 cities * (1+31+31) = 567 bytes city
        // Total header: 3 (opcode+len) + 1 (charCount) + 60 (chars) + 1 (cityCount) + 567 (cities) + 4 (flags) = 636
        // Verify no trailing -1
        int len = (data[1] << 8) | data[2];
        Assert.Equal(636, len);
    }

    [Fact]
    public void PacketCharList_NewFormat_IncludesCoordinatesAndTrailing()
    {
        var pkt = new PacketCharList(["TestChar"], maxChars: 1, newCharacterList: true);
        var buf = pkt.Build();
        byte[] data = buf.Data;

        Assert.Equal(0xA9, data[0]);

        // New format: 1 char slot = 60 bytes, 9 cities * (1+32+32+4*6) = 9*89 = 801 bytes city
        // Total: 3 + 1 + 60 + 1 + 801 + 4 (flags) + 2 (trailing -1) = 872
        int len = (data[1] << 8) | data[2];
        Assert.Equal(872, len);
    }

    // --- RTT Measurement ---

    [Fact]
    public void RTT_PingResponse_UpdatesRttMs()
    {
        var state = CreateNetState();
        state.ClientVersionNumber = 70_020_000;

        long before = Environment.TickCount64;
        state.SendRttPing(before);

        Assert.False(state.HasRtt);

        byte sentSeq = GetRttPingSeq(state);
        state.OnPingReceived(sentSeq);

        Assert.True(state.HasRtt);
        Assert.True(state.RttMs >= 0);
    }

    [Fact]
    public void RTT_MismatchedSeq_DoesNotUpdateRtt()
    {
        var state = CreateNetState();

        state.SendRttPing(Environment.TickCount64);
        state.OnPingReceived(0x01); // client-initiated seq (no 0x80 bit)

        Assert.False(state.HasRtt);
    }

    [Fact]
    public void RTT_RateLimited_RejectsEarlyPing()
    {
        var savedInterval = NetState.RttPingIntervalMs;
        try
        {
            NetState.RttPingIntervalMs = 30_000;
            var state = CreateNetState();

            long now = 10_000;
            Assert.True(state.SendRttPing(now));
            Assert.False(state.SendRttPing(now + 1000)); // too early
            Assert.False(state.SendRttPing(now + 29_999)); // still too early
            Assert.True(state.SendRttPing(now + 30_000)); // just right
        }
        finally
        {
            NetState.RttPingIntervalMs = savedInterval;
        }
    }

    [Fact]
    public void RTT_ServerInitiated_UsesHighBitSeq()
    {
        var state = CreateNetState();

        state.SendRttPing(Environment.TickCount64);
        byte seq = GetRttPingSeq(state);

        Assert.True((seq & 0x80) != 0);
    }

    [Fact]
    public void RTT_Disabled_WhenIntervalZero()
    {
        var savedInterval = NetState.RttPingIntervalMs;
        try
        {
            NetState.RttPingIntervalMs = 0;
            var state = CreateNetState();

            Assert.False(state.SendRttPing(Environment.TickCount64));
        }
        finally
        {
            NetState.RttPingIntervalMs = savedInterval;
        }
    }

    // --- Config Defaults ---

    [Fact]
    public void SphereConfig_RttPingIntervalMs_Default30000()
    {
        var config = new SphereNet.Core.Configuration.SphereConfig();
        Assert.Equal(30_000, config.RttPingIntervalMs);
    }

    // --- Version-Branched Integration (Game Flow) ---

    [Fact]
    public void SendCharacterStatus_ExtendedClient_Expansion7()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new SphereNet.Game.World.GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 50; ch.MaxStam = 50; ch.MaxMana = 50;
        ch.Hits = 50; ch.Stam = 50; ch.Mana = 50;
        world.PlaceCharacter(ch, new SphereNet.Core.Types.Point3D(1000, 1000, 0, 0));

        var state = TestHarness.CreateActiveNetState(loggerFactory, 1);
        state.ClientVersionNumber = 70_030_000; // 7.0.30 → SupportsExtendedStatus
        var accounts = new SphereNet.Game.Accounts.AccountManager(loggerFactory);
        var client = new SphereNet.Game.Clients.GameClient(state, world, accounts,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        TestHarness.AttachCharacter(client, ch);

        client.SendCharacterStatus(ch);

        var packets = TestHarness.GetQueuedPackets(state).ToList();
        var statusPacket = packets.FirstOrDefault(p => p.Span.Length > 0 && p.Span[0] == 0x11);
        Assert.NotNull(statusPacket);

        // Expansion level byte is at offset 42 in the 0x11 packet
        // 1(opcode) + 2(len) + 4(serial) + 30(name) + 2(hits) + 2(maxhits) + 1(nameflag) = 42
        Assert.True(statusPacket.Span.Length > 42);
        byte expansion = statusPacket.Span[42];
        Assert.Equal(7, expansion);
    }

    [Fact]
    public void SendCharacterStatus_MLClient_Expansion5()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new SphereNet.Game.World.GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 50; ch.MaxStam = 50; ch.MaxMana = 50;
        ch.Hits = 50; ch.Stam = 50; ch.Mana = 50;
        world.PlaceCharacter(ch, new SphereNet.Core.Types.Point3D(1000, 1000, 0, 0));

        var state = TestHarness.CreateActiveNetState(loggerFactory, 1);
        state.ClientVersionNumber = 70_009_000; // 7.0.9 → ML, no ExtendedStatus
        var accounts = new SphereNet.Game.Accounts.AccountManager(loggerFactory);
        var client = new SphereNet.Game.Clients.GameClient(state, world, accounts,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        TestHarness.AttachCharacter(client, ch);

        client.SendCharacterStatus(ch);

        var packets = TestHarness.GetQueuedPackets(state).ToList();
        var statusPacket = packets.FirstOrDefault(p => p.Span.Length > 0 && p.Span[0] == 0x11);
        Assert.NotNull(statusPacket);

        Assert.True(statusPacket.Span.Length > 42);
        byte expansion = statusPacket.Span[42];
        Assert.Equal(5, expansion);
    }

    [Fact]
    public void CharList_OldClient_SmallerPacketSize()
    {
        var oldPkt = new PacketCharList(["Test"], maxChars: 1, newCharacterList: false);
        var newPkt = new PacketCharList(["Test"], maxChars: 1, newCharacterList: true);

        int oldLen = oldPkt.Build().Length;
        int newLen = newPkt.Build().Length;

        // Old format is smaller (no coordinates, no trailing -1)
        Assert.True(oldLen < newLen);
    }

    // --- Helpers ---

    private static NetState CreateNetState()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        return new NetState(loggerFactory.CreateLogger<NetState>());
    }

    private static byte GetRttPingSeq(NetState state)
    {
        var field = typeof(NetState).GetField("_rttPingSeq",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (byte)field.GetValue(state)!;
    }
}
