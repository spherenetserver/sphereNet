using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Items;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>MESSAGEUA / SAYUA / SYSMESSAGEUA read "hue,mode,font,lang,text" with
/// Str_ParseCmds (CObjBase.cpp:2422/2573, CClient.cpp:1633), whose separators are
/// "=, \t". Packs write the language and the text parted by a space - "65,6,6,0 text" -
/// and a split on commas alone found four fields and dropped the line: a vendor's
/// item names and prices never showed on a click.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MessageUaArgumentTests
{
    [Theory]
    [InlineData("65,6,6,0 (Frenzied Ostard)", "(Frenzied Ostard)")]
    [InlineData("1153,6,6,0 [12,500 Gold]", "[12,500 Gold]")]
    [InlineData("65,6,6,0,Comma form", "Comma form")]
    public void MessageUaSendsTheTextOverTheItem(string args, string expected)
    {
        using var lf = LoggerFactory.Create(b => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19790);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var deed = world.CreateItem();
        deed.BaseId = 0x14F0;
        world.PlaceItem(deed, new Point3D(1001, 1000, 0, 0));

        TestHarness.ClearQueuedPackets(client.NetState);
        Assert.True(client.TryExecuteScriptCommand(deed, "MESSAGEUA", args, null));

        var speech = TestHarness.GetQueuedPackets(client.NetState).Where(p => p.Span[0] == 0xAE).ToList();
        var packet = Assert.Single(speech);
        uint serial = (uint)((packet.Span[3] << 24) | (packet.Span[4] << 16) | (packet.Span[5] << 8) | packet.Span[6]);
        Assert.Equal(deed.Uid.Value, serial);
        // 0xAE: text is UTF-16BE after the 48-byte header.
        string text = System.Text.Encoding.BigEndianUnicode.GetString(packet.Span[48..].ToArray()).TrimEnd('\0');
        Assert.Equal(expected, text);
    }
}
