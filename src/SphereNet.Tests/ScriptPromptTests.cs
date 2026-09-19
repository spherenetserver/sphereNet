using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// A prompt asks its question as a system message, and the packet carries only the
/// context the answer comes back with.
///
/// Upstream sends the text with addSysMessage and then the prompt packet, which holds
/// two context words and a terminator - nothing else (CClientMsg.cpp:1796-1799,
/// PacketAddPrompt, send.cpp:2997). The client agrees: its handlers for both prompt
/// opcodes read one 64-bit id and stop (ASCIIPrompt / UnicodePrompt).
///
/// The question used to be written into the packet, where the client never looked for
/// it, and was sent nowhere else - so every prompt the packs raise opened a text box
/// that asked nothing. The live pack raises nine, for guild and town names.
///
/// PROMPTCONSOLEU is the Unicode form and must go out as 0xC2; it was being sent as the
/// ASCII 0x9A, which quietly dropped the distinction that form exists for.
/// </summary>
public sealed class ScriptPromptTests
{
    private static (SphereNet.Game.Clients.GameClient Client, SphereNet.Game.World.GameWorld World)
        Connected(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        TestHarness.ClearQueuedPackets(client.NetState);
        return (client, world);
    }

    /// <summary>The question goes out where the client will show it, and the packet
    /// that follows is the bare 15-byte prompt.</summary>
    [Fact]
    public void TheQuestionIsSentAsAMessageAndThePacketCarriesOnlyTheContext()
    {
        var (client, _) = Connected(5391);

        client.SendPrompt(0x1234, "Enter new town name");

        var sent = TestHarness.GetQueuedPackets(client.NetState);
        Assert.Contains(sent, p => p.Span[0] is 0x1C or 0xAE);   // the question, as a message
        var prompt = Assert.Single(sent, p => p.Span[0] == 0x9A);

        // 3-byte header + uid(4) + promptId(4) + zero(4) + terminator(1).
        Assert.Equal(16, prompt.Span.Length);
        Assert.Equal(0x1234u, (uint)((prompt.Span[7] << 24) | (prompt.Span[8] << 16) |
                                     (prompt.Span[9] << 8) | prompt.Span[10]));
    }

    /// <summary>Nothing of the question is left inside the packet, which is what made
    /// it invisible: the client reads a 64-bit id and stops.</summary>
    [Fact]
    public void ThePacketDoesNotCarryTheText()
    {
        var (client, _) = Connected(5392);

        client.SendPrompt(0x55, "Guild abbreviation");

        var prompt = Assert.Single(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0x9A);
        string raw = System.Text.Encoding.ASCII.GetString(prompt.Span.ToArray());
        Assert.DoesNotContain("Guild", raw, StringComparison.Ordinal);
    }

    /// <summary>The Unicode form is a different opcode, with the language tag and the
    /// wide terminator upstream writes.</summary>
    [Fact]
    public void TheUnicodeFormIsItsOwnPacket()
    {
        var (client, _) = Connected(5393);

        client.SendPrompt(0x77, "Yeni guild kisaltma", unicode: true);

        var sent = TestHarness.GetQueuedPackets(client.NetState);
        Assert.DoesNotContain(sent, p => p.Span[0] == 0x9A);
        var prompt = Assert.Single(sent, p => p.Span[0] == 0xC2);

        // 3-byte header + uid(4) + promptId(4) + zero(4) + language(4) + wide
        // terminator(2).
        Assert.Equal(21, prompt.Span.Length);
    }

    /// <summary>A prompt with no question sends no message, and still opens.</summary>
    [Fact]
    public void AQuestionlessPromptStillOpens()
    {
        var (client, _) = Connected(5394);

        client.SendPrompt(0x99, "");

        var sent = TestHarness.GetQueuedPackets(client.NetState);
        Assert.Single(sent, p => p.Span[0] == 0x9A);
        Assert.DoesNotContain(sent, p => p.Span[0] is 0x1C or 0xAE);
    }
}
