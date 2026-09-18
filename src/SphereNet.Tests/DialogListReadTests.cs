using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// DIALOGLIST answers with the dialogs this client has open.
///
/// Upstream reads it off the same map it closes them from: COUNT is how many are open,
/// and an indexed entry's ID is the dialog's NAME (OC_DIALOGLIST, CObjBase.cpp:1203 -
/// it converts the gump id back through ResourceGetName). The shard's staff function
/// walks the list and closes everything except two named dialogs, which is exactly that
/// shape:
///
///     IF (&lt;DIALOGLIST.COUNT&gt;)
///         FOR 0 &lt;EVAL &lt;DIALOGLIST.COUNT&gt;-1&gt;
///             IF !(STRMATCH('D_STAFF_PANEL','&lt;DIALOGLIST.&lt;dLOCAL._FOR&gt;.ID&gt;') ...)
///                 DIALOGCLOSE &lt;DIALOGLIST.&lt;dLOCAL._FOR&gt;.ID&gt;
///
/// None of it resolved, so the loop never ran and the function closed nothing. The
/// engine was already tracking the open dialogs by name - only the read was missing.
/// </summary>
public sealed class DialogListReadTests
{
    private static SphereNet.Game.Clients.GameClient Client(int port, params string[] openDialogs)
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

        uint gump = 0x1000;
        foreach (string d in openDialogs)
            client.Gumps.OpenScriptDialogs[d] = gump++;
        return client;
    }

    private static string Read(SphereNet.Game.Clients.GameClient client, string name)
    {
        Assert.True(client.TryResolveScriptVariable(name, client.Character!, null, out string v),
            $"{name} did not answer");
        return v;
    }

    /// <summary>How many are open, which is what the loop bounds itself with.</summary>
    [Fact]
    public void CountIsHowManyAreOpen()
    {
        var client = Client(5421, "d_staff_panel", "d_pin_login", "d_bank");
        Assert.Equal("3", Read(client, "DIALOGLIST.COUNT"));
        Assert.Equal("3", Read(client, "DIALOGLIST"));
    }

    /// <summary>An indexed entry answers with the dialog's name, which is what the
    /// pack compares against and hands to DIALOGCLOSE.</summary>
    [Fact]
    public void AnIndexedEntryAnswersWithTheDialogName()
    {
        var client = Client(5422, "d_staff_panel", "d_pin_login");

        var names = new[] { Read(client, "DIALOGLIST.0.ID"), Read(client, "DIALOGLIST.1.ID") };
        Assert.Contains("d_staff_panel", names);
        Assert.Contains("d_pin_login", names);
    }

    /// <summary>An index past the end answers 0 rather than refusing, so a loop that
    /// runs one step too far does not take the script down with it.</summary>
    [Fact]
    public void AnIndexPastTheEndIsZero()
    {
        var client = Client(5423, "d_bank");
        Assert.Equal("0", Read(client, "DIALOGLIST.9.ID"));
        Assert.Equal("0", Read(client, "DIALOGLIST.0.NOTHING"));
    }

    /// <summary>With nothing open the count is zero, which is the guard the pack's
    /// function opens with.</summary>
    [Fact]
    public void NothingOpenCountsZero()
    {
        var client = Client(5424);
        Assert.Equal("0", Read(client, "DIALOGLIST.COUNT"));
    }
}
