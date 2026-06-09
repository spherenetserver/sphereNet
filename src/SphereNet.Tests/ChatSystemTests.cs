using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.Chat;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using System.Text;

namespace SphereNet.Tests;

/// <summary>
/// UO chat (conference) system: 0xB2 outgoing byte layout (parsed back the
/// way the ClassicUO handler does), 0xB3 incoming command parse, and the
/// ChatEngine channel/membership state machine.
/// </summary>
public class ChatSystemTests
{
    // --- 0xB2 layout: parse exactly like ClassicUO PacketHandlers.ChatMessage ---

    private static (ushort Cmd, ushort? Code, string? S1, string? S2, ushort? Trailer)
        ParseChatPacket(PacketBuffer built, bool hasCode, int strings, bool hasTrailer)
    {
        byte[] data = built.Span.ToArray();
        Assert.Equal(0xB2, data[0]);
        int len = (data[1] << 8) | data[2];
        Assert.Equal(data.Length, len);

        int o = 3;
        ushort cmd = (ushort)((data[o] << 8) | data[o + 1]); o += 2;
        o += 4; // client: p.Skip(4)

        ushort? code = null;
        if (hasCode) { code = (ushort)((data[o] << 8) | data[o + 1]); o += 2; }

        string? ReadUnicodeBE()
        {
            var sb = new StringBuilder();
            while (o + 1 < data.Length)
            {
                ushort ch = (ushort)((data[o] << 8) | data[o + 1]); o += 2;
                if (ch == 0) break;
                sb.Append((char)ch);
            }
            return sb.ToString();
        }

        string? s1 = strings >= 1 ? ReadUnicodeBE() : null;
        string? s2 = strings >= 2 ? ReadUnicodeBE() : null;
        ushort? trailer = null;
        if (hasTrailer) { trailer = (ushort)((data[o] << 8) | data[o + 1]); o += 2; }
        Assert.Equal(data.Length, o);
        return (cmd, code, s1, s2, trailer);
    }

    [Fact]
    public void ChatPacket_CreateChannel_MatchesClientParse()
    {
        var built = PacketChatSystem.MakeCreateChannel("General").Build();
        var (cmd, _, s1, _, trailer) = ParseChatPacket(built, hasCode: false, strings: 1, hasTrailer: true);
        Assert.Equal(PacketChatSystem.CreateChannel, cmd);
        Assert.Equal("General", s1);
        Assert.Equal((ushort)'0', trailer); // '1' would mean password-protected
    }

    [Fact]
    public void ChatPacket_ChannelMessage_MatchesClientParse()
    {
        var built = PacketChatSystem.MakeChannelMessage("Yunus", "selam dunya").Build();
        var (cmd, code, s1, s2, _) = ParseChatPacket(built, hasCode: true, strings: 2, hasTrailer: false);
        Assert.Equal(PacketChatSystem.ChannelMessage, cmd);
        Assert.Equal((ushort)0, code);
        Assert.Equal("Yunus", s1);
        Assert.Equal("selam dunya", s2);
    }

    [Fact]
    public void ChatPacket_JoinedChannel_MatchesClientParse()
    {
        var built = PacketChatSystem.MakeJoinedChannel("General").Build();
        var (cmd, _, s1, _, _) = ParseChatPacket(built, hasCode: false, strings: 1, hasTrailer: false);
        Assert.Equal(PacketChatSystem.JoinedChannel, cmd);
        Assert.Equal("General", s1);
    }

    // --- 0xB3 incoming parse ---

    [Fact]
    public void ChatAction_ParsesLangCommandAndText()
    {
        // [lang:4]["TRK\0"][cmd 0x61][unicode "hi" + null]
        var payload = new byte[4 + 2 + 6];
        payload[0] = (byte)'T'; payload[1] = (byte)'R'; payload[2] = (byte)'K'; payload[3] = 0;
        payload[4] = 0x00; payload[5] = 0x61;
        payload[6] = 0x00; payload[7] = (byte)'h';
        payload[8] = 0x00; payload[9] = (byte)'i';
        payload[10] = 0x00; payload[11] = 0x00;

        var state = new NetState(NullLogger<NetState>.Instance);
        ushort seenCmd = 0; string seenText = "";
        state.ChatActionHandler = (_, cmd, text) => { seenCmd = cmd; seenText = text; };

        new PacketChatAction().OnReceive(new PacketBuffer(payload), state);

        Assert.Equal((ushort)0x61, seenCmd);
        Assert.Equal("hi", seenText);
    }

    // --- ChatEngine state machine ---

    [Fact]
    public void ChatEngine_JoinLeave_TracksMembershipAndAdhocChannels()
    {
        var engine = new ChatEngine("General");
        var a = new Serial(1);
        var b = new Serial(2);

        Assert.NotNull(engine.Join(a, "General"));
        Assert.NotNull(engine.Join(b, "General"));
        Assert.Equal(2, engine.GetChannel("General")!.Members.Count);

        // Joining another channel leaves the previous one.
        var adhoc = engine.Join(a, "Trade");
        Assert.NotNull(adhoc);
        Assert.Single(engine.GetChannel("General")!.Members);
        Assert.Equal("Trade", engine.GetMemberChannel(a)!.Name);

        // Ad-hoc channels disappear when emptied; static ones survive.
        engine.Leave(a);
        Assert.Null(engine.GetChannel("Trade"));
        engine.Leave(b);
        Assert.NotNull(engine.GetChannel("General"));
    }

    [Fact]
    public void ChatEngine_ChatNames_RoundTrip()
    {
        var engine = new ChatEngine();
        var uid = new Serial(42);
        Assert.Equal("", engine.GetChatName(uid));
        engine.SetChatName(uid, "Yunus");
        Assert.Equal("Yunus", engine.GetChatName(uid));
    }
}
