using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// wiki #1 — character creation parsed the gender/race byte with the pre-7.0
/// encoding for every client, so a modern (Stygian Abyss) client's human (2/3) was
/// read as elf and elf (4/5) as gargoyle; the chosen race was also never applied to
/// the body, and the shirt/pants tints at the tail of the create packet were not
/// read at all. These lock the corrected race decode (per client era) and the
/// shirt/pants parse.
/// </summary>
public sealed class CharCreatePacketTests
{
    // Build the 0xF8 (HS, 106-byte) create-character PAYLOAD (opcode already
    // stripped by framing → 105 bytes), placing only the fields under test.
    private static byte[] BuildHsPayload(byte genderRace, ushort shirtHue, ushort pantsHue)
    {
        var p = new byte[105];
        p[69] = genderRace;                     // gender/race byte
        WriteBE(p, 101, shirtHue);              // shirt hue
        WriteBE(p, 103, pantsHue);              // pants hue
        return p;
    }

    // The old 0x00 (104-byte) payload → 103 bytes; 3 skills instead of 4 shifts the
    // tail 2 bytes earlier.
    private static byte[] BuildOldPayload(byte genderRace, ushort shirtHue, ushort pantsHue)
    {
        var p = new byte[103];
        p[69] = genderRace;
        WriteBE(p, 99, shirtHue);
        WriteBE(p, 101, pantsHue);
        return p;
    }

    private static void WriteBE(byte[] p, int offset, ushort v)
    {
        p[offset] = (byte)(v >> 8);
        p[offset + 1] = (byte)(v & 0xFF);
    }

    private static CharCreateInfo ParseHs(byte genderRace, ushort shirt = 0, ushort pants = 0)
    {
        CharCreateInfo? captured = null;
        var state = new NetState(NullLogger<NetState>.Instance);
        state.CharCreateHandler = (_, info) => captured = info;
        new PacketCreateCharacterHS().OnReceive(new PacketBuffer(BuildHsPayload(genderRace, shirt, pants)), state);
        Assert.NotNull(captured);
        return captured!;
    }

    private static CharCreateInfo ParseOld(byte genderRace, uint clientVersion)
    {
        CharCreateInfo? captured = null;
        var state = new NetState(NullLogger<NetState>.Instance) { ClientVersionNumber = clientVersion };
        state.CharCreateHandler = (_, info) => captured = info;
        new PacketCreateCharacter().OnReceive(new PacketBuffer(BuildOldPayload(genderRace, 0, 0)), state);
        Assert.NotNull(captured);
        return captured!;
    }

    [Theory]
    [InlineData((byte)0x2, (byte)1, false)] // human male
    [InlineData((byte)0x3, (byte)1, true)]  // human female
    [InlineData((byte)0x4, (byte)2, false)] // elf male
    [InlineData((byte)0x5, (byte)2, true)]  // elf female
    [InlineData((byte)0x6, (byte)3, false)] // gargoyle male
    [InlineData((byte)0x7, (byte)3, true)]  // gargoyle female
    public void Hs_DecodesRaceAndSex_WithStygianEncoding(byte genderRace, byte expectedRace, bool expectedFemale)
    {
        var info = ParseHs(genderRace);
        Assert.Equal(expectedRace, info.Race);
        Assert.Equal(expectedFemale, info.Female);
    }

    [Fact]
    public void Hs_ReadsShirtAndPantsHue()
    {
        var info = ParseHs(0x2, shirt: 0x0123, pants: 0x0456);
        Assert.Equal((ushort)0x0123, info.ShirtHue);
        Assert.Equal((ushort)0x0456, info.PantsHue);
    }

    [Theory]
    // Old packet, legacy client (version 0 / < 7.0): 0/1 human, 2/3 elf.
    [InlineData((byte)0x0, 0u, (byte)1)]
    [InlineData((byte)0x2, 0u, (byte)2)]
    // Old packet but a 7.0+ client uses the Stygian encoding: 2/3 human.
    [InlineData((byte)0x2, 70_000_000u, (byte)1)]
    public void Old_DecodesRace_ByClientEra(byte genderRace, uint version, byte expectedRace)
    {
        Assert.Equal(expectedRace, ParseOld(genderRace, version).Race);
    }
}
