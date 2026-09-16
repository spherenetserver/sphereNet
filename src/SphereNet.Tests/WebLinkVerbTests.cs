using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Messages;
using SphereNet.Game.World;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// WEBLINK sends 0xA5 and the client hands the address straight to the shell
/// (ClassicUO PacketHandlers.cs:3461 -> PlatformHelper.LaunchBrowser). Upstream
/// announces the launch before it sends (addWebLaunch, CClientLog.cpp:211); with
/// no message the verb is invisible from the player's side, and the pause while
/// the shell decides what to do with the address - longest when it has no scheme
/// and Windows raises a dialog behind a fullscreen client - reads as the game
/// having stopped responding.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WebLinkVerbTests
{
    private static (GameWorld World, GameClient Client, SphereNet.Game.Objects.Characters.Character Me) Setup(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(lf, port);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.PrivLevel = PrivLevel.Owner;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (world, client, me);
    }

    /// <summary>0xA5: [A5][len:2][ascii url][00]</summary>
    private static string? SentUrl(NetState state)
    {
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length < 4 || s[0] != 0xA5) continue;
            int end = 3;
            while (end < s.Length && s[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(s[3..end]);
        }
        return null;
    }

    private static bool SaidLaunching(NetState state)
    {
        string expected = ServerMessages.Get(Msg.WebBrowserStart);
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length < 4 || s[0] != 0xAE) continue;   // unicode speech
            string text = System.Text.Encoding.BigEndianUnicode.GetString(s[48..]).TrimEnd('\0');
            if (text.Contains(expected, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    [Fact]
    public void TheAddressKeepsItsCase()
    {
        // The verb is matched on an uppercased copy; the URL must come off the
        // original, or every path and query after the host is destroyed.
        var (_, client, me) = Setup(4701);

        Assert.True(client.TryExecuteScriptCommand(
            me, "WEBLINK", "http://Example.COM/Path?A=b", null));

        Assert.Equal("http://Example.COM/Path?A=b", SentUrl(client.NetState));
    }

    [Fact]
    public void TheLaunchIsAnnouncedBeforeItIsSent()
    {
        var (_, client, me) = Setup(4702);

        Assert.True(client.TryExecuteScriptCommand(
            me, "WEBLINK", "http://example.com", null));

        Assert.True(SaidLaunching(client.NetState),
            "the player was told nothing before the browser was launched");
    }

    [Fact]
    public void AnEmptyAddressSendsNothingAtAll()
    {
        // The client ignores an empty address, but an empty 0xA5 is still a packet
        // and a message about a browser that will not open is worse than silence.
        var (_, client, me) = Setup(4703);

        Assert.True(client.TryExecuteScriptCommand(me, "WEBLINK", "   ", null));

        Assert.Null(SentUrl(client.NetState));
        Assert.False(SaidLaunching(client.NetState));
    }
}
