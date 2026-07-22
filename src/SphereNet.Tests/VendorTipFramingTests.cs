using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 22 / T4 — two framing defects.
///
/// (1) The 0x74 vendor buy list wrote a per-row name length as a single byte from
/// name.Length + 1. A name of 255+ chars overflowed the byte (255 -> 0) while the
/// full string was still written, so the client mis-read the length and every
/// following row desynced. The length must always equal the bytes actually
/// written; long names are truncated deterministically.
///
/// (2) 0xA7 (Tip/Notice Request, client->server) was defined as variable length,
/// so the framer read the tip index word as a length field — dropping the
/// connection (index 0 -> length 0) or stalling. Source-X frames it as a fixed
/// 4-byte packet; the definition now matches, so it is consumed cleanly (no
/// handler needed) and a following packet frames correctly.
/// </summary>
public sealed class VendorTipFramingTests
{
    // ---- 0x74 vendor buy list: single-byte name length must not overflow ----

    private static VendorItem Item(uint serial, string name) => new()
    {
        Serial = serial,
        ItemId = 0x0EED,
        Hue = 0,
        Amount = 1,
        Price = 100,
        Name = name,
    };

    /// <summary>
    /// Walks the 0x74 rows exactly as the client does: 4-byte header (serial),
    /// 1-byte count, then per row [price:4][nameLen:1][name+NUL:nameLen]. Returns
    /// the decoded names, and throws if a row's declared length runs past the
    /// packet — i.e. if the writer desynced.
    /// </summary>
    private static List<string> ParseBuyRows(byte[] data, int expectedCount)
    {
        int total = (data[1] << 8) | data[2]; // 0x74 length field
        Assert.Equal(data.Length, total);

        int p = 3;
        p += 4;                       // container serial
        int count = data[p]; p += 1;
        Assert.Equal(expectedCount, count);

        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            p += 4;                   // price
            int nameLen = data[p]; p += 1;
            Assert.True(p + nameLen <= data.Length, $"row {i} name length {nameLen} overruns packet");
            // nameLen bytes = name chars + trailing NUL.
            int strLen = nameLen - 1;
            names.Add(System.Text.Encoding.ASCII.GetString(data, p, strLen));
            Assert.Equal(0, data[p + strLen]); // NUL terminator in place
            p += nameLen;
        }
        Assert.Equal(data.Length, p);  // consumed exactly — alignment intact
        return names;
    }

    [Theory]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(300)]
    [InlineData(1000)]
    public void BuyList_LongName_KeepsRowAlignment(int nameChars)
    {
        var items = new List<VendorItem>
        {
            Item(0x40000001, new string('A', nameChars)),
            Item(0x40000002, "short"),     // the row AFTER the long one must still parse
        };
        var buf = new PacketVendorBuyList(0x40000010, items).Build();
        var data = buf.Data[..buf.Length];

        var names = ParseBuyRows(data, 2);

        // The long name is truncated to fit the byte prefix (<= 254 chars); the
        // trailing row is intact, proving no desync.
        Assert.True(names[0].Length <= 254);
        Assert.Equal(new string('A', System.Math.Min(nameChars, 254)), names[0]);
        Assert.Equal("short", names[1]);
    }

    // ---- 0xA7 Tip Request framing: fixed 4 bytes, consumed with no handler ----

    [Fact]
    public void TipRequest_IsDefinedAsFixedFourBytes()
    {
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);
        Assert.Equal(4, PacketDefinitions.GetPacketLength(0xA7, state));
    }

    [Fact]
    public void TipRequest_FollowedByPing_BothConsumed_ConnectionStaysOpen()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        using var manager = new NetworkManager(1, loggerFactory) { UseCrypt = false, UseNoCrypt = true };
        var state = manager.GetState(0)!;
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        state.IsSeeded = true;
        ForceNoCrypto(state);

        // 0xA7 tip request (index=5, forward=1) immediately followed by a ping.
        // Old behaviour: framer read 0x0005 as a length and stalled/dropped.
        state.InjectReceived([0xA7, 0x00, 0x05, 0x01, 0x73, 0x42]);
        ProcessInput(manager, state);

        Assert.False(state.IsClosing);              // no desync-driven drop
        Assert.Equal(0, state.ReceivedData.Length); // both packets consumed
        var ping = Assert.Single(TestHarness.GetQueuedPackets(state));
        Assert.Equal([0x73, 0x42], ping.Span.ToArray()); // ping still framed & echoed
    }

    private static void ForceNoCrypto(NetState state)
    {
        var crypto = state.Crypto;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        crypto.GetType().GetField("_initialized", flags)!.SetValue(crypto, true);
        crypto.GetType().GetField("_encType", flags)!.SetValue(crypto, EncryptionType.None);
    }

    private static void ProcessInput(NetworkManager manager, NetState state)
    {
        typeof(NetworkManager)
            .GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [state]);
    }
}
