using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Gumps;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What DIALOGCLOSE does besides closing the window.
///
/// Upstream does not stop at the packet. CClient::Dialog_Close sends the close and then
/// feeds a gump response carrying the given button back through the receive path
/// (CClientDialog.cpp:200-222), because a client from 4.0.4a on does not send one of its
/// own. That synthetic answer is what runs the dialog's ON=&lt;button&gt; block.
///
/// A script pack leans on it. The stock admin panel's ON=0 is `CLEARCTAGS Dialog.Admin`
/// and `[FUNCTION admin]` opens with `DIALOGCLOSE d_admin` precisely to get that clear
/// before it rebuilds the client list. Closing without running the handler left the tags
/// standing, so each `.admin` appended its list to the previous one: the same player
/// appeared on page after page and the page count climbed until the gump stopped coming
/// up at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DialogCloseRunsHandlerTests
{
    private readonly ITestOutputHelper _out;
    public DialogCloseRunsHandlerTests(ITestOutputHelper output) => _out = output;

    private static (GameClient Client, Character Me) Stage(int port)
    {
        var world = TestHarness.CreateWorld();
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (client, me);
    }

    /// <summary>Register a gump the way a script dialog is registered: an entry in the
    /// open-dialog name table, an active gump id and a response callback.</summary>
    private static uint OpenNamed(GameClient client, Character me, string name,
        System.Action<uint> onButton)
    {
        var gump = new GumpBuilder(me.Uid.Value, (uint)name.GetHashCode(), 200, 200);
        gump.AddResizePic(0, 0, 5054, 200, 200);
        client.SendGump(gump, (button, _, _) => onButton(button));
        uint gumpId = client.Gumps.ActiveGumps.Last();
        client.Gumps.OpenScriptDialogs[name] = gumpId;
        return gumpId;
    }

    [Fact]
    public void ClosingADialogRunsItsZeroButtonHandler()
    {
        var (client, me) = Stage(16210);
        uint? pressed = null;
        uint gumpId = OpenNamed(client, me, "d_admin", b => pressed = b);

        Assert.True(client.CloseScriptDialog("d_admin"));

        _out.WriteLine($"close ran handler with button {pressed?.ToString() ?? "<none>"}");
        Assert.Equal(0u, pressed);
        // And the dialog is gone from every book the client keeps.
        Assert.False(client.Gumps.OpenScriptDialogs.ContainsKey("d_admin"));
        Assert.DoesNotContain(gumpId, client.Gumps.ActiveGumps);
        Assert.False(client.Gumps.Callbacks.ContainsKey(gumpId));
    }

    [Fact]
    public void AnExplicitButtonIsTheOneTheHandlerSees()
    {
        // DIALOGCLOSE takes the button as a second argument (CObjBase.cpp:2866); a pack
        // uses it to route a close through a specific handler rather than the cancel one.
        var (client, me) = Stage(16211);
        uint? pressed = null;
        OpenNamed(client, me, "d_admin", b => pressed = b);

        Assert.True(client.CloseScriptDialog("d_admin", buttonId: 4));

        Assert.Equal(4u, pressed);
    }

    [Fact]
    public void ClosingSomethingThatIsNotOpenDoesNothing()
    {
        var (client, _) = Stage(16212);
        Assert.False(client.CloseScriptDialog("d_admin"));
    }

    [Fact]
    public void TheClosePacketStillGoesOut()
    {
        // The handler is the addition, not the replacement: the client is still told to
        // take the window down.
        var (client, me) = Stage(16214 + 1000);
        OpenNamed(client, me, "d_admin", _ => { });
        // The harness snapshot does not drain and the queue is priority-ordered, so the
        // question is what the client HAS been sent, not what arrived last.
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(client.NetState),
            p => p.Span.Length > 2 && p.Span[0] == 0xBF && p.Span[3] == 0x00 && p.Span[4] == 0x04);

        client.CloseScriptDialog("d_admin");

        var sent = TestHarness.GetQueuedPackets(client.NetState).ToList();
        _out.WriteLine($"client holds {sent.Count} packet(s): " +
                       string.Join(",", sent.Select(p => $"0x{p.Span[0]:X2}")));
        // 0xBF subcommand 0x0004 - close generic gump.
        Assert.Contains(sent, p => p.Span.Length > 4 && p.Span[0] == 0xBF &&
                                   p.Span[3] == 0x00 && p.Span[4] == 0x04);
    }

    [Fact]
    public void ReopeningTwiceDoesNotLeaveTwoGumpsBehind()
    {
        // The shape of the live report: `.admin` run several times over. Each run closes
        // the previous window first, and every close has to be accounted for - a leaked
        // active gump is a callback that never runs and a name that never clears.
        var (client, me) = Stage(16214);
        int handled = 0;

        for (int i = 0; i < 5; i++)
        {
            client.CloseScriptDialog("d_admin");
            OpenNamed(client, me, "d_admin", _ => handled++);
        }
        client.CloseScriptDialog("d_admin");

        _out.WriteLine($"{handled} closes handled, {client.Gumps.ActiveGumps.Count} gumps still active");
        Assert.Equal(5, handled);
        Assert.Empty(client.Gumps.ActiveGumps);
        Assert.Empty(client.Gumps.Callbacks);
        Assert.Empty(client.Gumps.OpenScriptDialogs);
    }
}
