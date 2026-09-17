using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// A GM page is a script object (Source-X CGMPage, a CScriptObj rather than a
/// CObjBase - no uid, lives in a queue rather than in the world).
///
/// It was a five-field value record: an account name, free text, a handler NAME and
/// a status string. The reference distribution's own queue dialog needs four things
/// that record could not hold - CHARUID to find who paged, P to travel to where they
/// paged from, TIME as the AGE of the page, and HANDLED as the staff UID, comparable
/// to SRC and assignable - and behind all of it, the queue was empty anyway: the
/// .PAGE command logged the request and told the staff, and never enqueued it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GmPageObjectTests
{
    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static string Get(GmPage page, string key)
    {
        Assert.True(page.TryGetProperty(key, out string v), $"nothing answered {key}");
        return v;
    }

    /// <summary>The keys the reference answers (CGMPage::r_WriteVal).</summary>
    [Fact]
    public void APageAnswersForWhoPagedAndFromWhere()
    {
        var page = new GmPage
        {
            Account = "someaccount",
            CharUid = new Serial(0x40001234),
            Position = new Point3D(1500, 1620, 5, 0),
            Reason = "stuck in a wall, please help",
        };

        Assert.Equal("someaccount", Get(page, "ACCOUNT"));
        Assert.Equal("040001234", Get(page, "CHARUID"));
        Assert.Equal("stuck in a wall, please help", Get(page, "REASON"));
        Assert.Equal("1500", Get(page, "P.X"));
        Assert.Equal("1620", Get(page, "P.Y"));
        // P reads as a point a GO can take, which is what the queue dialog does
        // with it: SRC.GO <SERV.GMPAGE.<n>.P>.
        Assert.True(Point3D.TryParse(Get(page, "P"), out var parsed));
        Assert.Equal(1500, parsed.X);
        Assert.Equal(1620, parsed.Y);
        Assert.Equal(5, parsed.Z);
    }

    /// <summary>TIME is the page's AGE in seconds, not the moment it was made
    /// (GC_TIME formats the difference against now). The queue renders it as
    /// "&lt;TIME&gt;/60 minutes ago", which a unix stamp turns into nonsense.</summary>
    [Fact]
    public void TimeIsTheAgeOfThePageNotItsTimestamp()
    {
        GmPage.NowSeconds = () => 1_000_000;
        try
        {
            var page = new GmPage { Created = 1_000_000 - 180 };
            Assert.Equal("180", Get(page, "TIME"));

            // And written as an age, the way the reference reads one back.
            page.TrySetProperty("TIME", "600");
            Assert.Equal(1_000_000 - 600, page.Created);
            Assert.Equal("600", Get(page, "TIME"));
        }
        finally { GmPage.NowSeconds = null; }
    }

    /// <summary>HANDLED is the staff member's UID, readable and assignable. The
    /// dialog claims a page with HANDLED &lt;SRC&gt;, releases it with HANDLED 0 and
    /// compares the answer against SRC to decide which of the two it may do.</summary>
    [Fact]
    public void HandledIsAUidAScriptCanCompareAndAssign()
    {
        var page = new GmPage();
        Assert.Equal("0", Get(page, "HANDLED"));   // nobody has taken it

        page.TrySetProperty("HANDLED", "04000ABCD");
        Assert.Equal("04000ABCD", Get(page, "HANDLED"));

        page.TrySetProperty("HANDLED", "0");
        Assert.Equal("0", Get(page, "HANDLED"));
    }

    /// <summary>GMPAGEP is the page this staff character is handling
    /// (CLIR_GMPAGEP -&gt; CClient::m_pGMPage). A page is not a world object, so it
    /// can only be answered by the wider resolver - the one that returns what
    /// upstream's r_GetRef returns.</summary>
    [Fact]
    public void GmpagepResolvesToThePageThisStaffCharacterIsHandling()
    {
        var world = NewWorld();
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));

        var page = new GmPage { Account = "someone", Reason = "help me" };
        world.AddGmPage(page);

        // Unclaimed: nothing to resolve, which is what makes the dialog's bare
        // truth test false and lets this GM take a page.
        Assert.Null(gm.ResolveScriptRefHead("GMPAGEP"));

        page.Handler = gm.Uid;
        var resolved = gm.ResolveScriptRefHead("GMPAGEP");
        Assert.Same(page, resolved);
        Assert.True(resolved!.TryGetProperty("REASON", out string reason));
        Assert.Equal("help me", reason);

        // Another GM handling nothing still resolves to nothing.
        var other = world.CreateCharacter();
        other.IsPlayer = true;
        world.PlaceCharacter(other, new Point3D(101, 100, 0, 0));
        Assert.Null(other.ResolveScriptRefHead("GMPAGEP"));
    }

    /// <summary>The wider resolver still answers every head the narrow one did -
    /// it is a widening, not a replacement.</summary>
    [Fact]
    public void TheWiderResolverStillAnswersTheWorldObjectHeads()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var target = world.CreateItem();
        world.PlaceItem(target, new Point3D(101, 100, 0, 0));

        ch.TrySetProperty("ACT", $"0{target.Uid.Value:X}");
        Assert.Same(target, ch.ResolveScriptRefHead("ACT"));
        Assert.Same(ch, ch.ResolveScriptRefHead("TOPOBJ"));
    }

    /// <summary>The world finds the page a given staff member holds, and only
    /// theirs.</summary>
    [Fact]
    public void TheQueueFindsAPageByItsHandler()
    {
        var world = NewWorld();
        var first = new GmPage { Account = "a", Handler = new Serial(0x40000001) };
        var second = new GmPage { Account = "b", Handler = new Serial(0x40000002) };
        var loose = new GmPage { Account = "c" };
        world.AddGmPage(first);
        world.AddGmPage(second);
        world.AddGmPage(loose);

        Assert.Same(first, world.FindGmPageHandledBy(new Serial(0x40000001)));
        Assert.Same(second, world.FindGmPageHandledBy(new Serial(0x40000002)));
        Assert.Null(world.FindGmPageHandledBy(new Serial(0x40000009)));
        Assert.Null(world.FindGmPageHandledBy(Serial.Invalid));

        Assert.True(world.RemoveGmPage(second));
        Assert.Null(world.FindGmPageHandledBy(new Serial(0x40000002)));
        Assert.Equal(2, world.GmPages.Count);
    }

    /// <summary>The pack-facing path: the reference distribution's queue dialog
    /// writes "SERV.GMPAGE.&lt;n&gt;.HANDLED &lt;SRC&gt;" as a STATEMENT to claim a page,
    /// "HANDLED 0" to release it and ".DELETE" to drop it. All three used to land in
    /// the unimplemented-SERV-verb warning, so a GM could open the queue and change
    /// nothing in it.</summary>
    [Fact]
    public void TheQueueDialogsServStatementsReachThePage()
    {
        var world = NewWorld();
        using var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 4931);
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = SphereNet.Core.Enums.PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, gm);

        var page = new GmPage { Account = "someone", Reason = "help" };
        world.AddGmPage(page);

        Assert.True(client.TryExecuteScriptCommand(gm, "SERV.GMPAGE.0.HANDLED",
            $"0{gm.Uid.Value:X}", null));
        Assert.Equal(gm.Uid, page.Handler);
        // And now the GM is handling one, which is the test the dialog runs before
        // letting them take a second.
        Assert.Same(page, gm.ResolveScriptRefHead("GMPAGEP"));

        Assert.True(client.TryExecuteScriptCommand(gm, "SERV.GMPAGE.0.HANDLED", "0", null));
        Assert.False(page.Handler.IsValid);
        Assert.Null(gm.ResolveScriptRefHead("GMPAGEP"));

        Assert.True(client.TryExecuteScriptCommand(gm, "SERV.GMPAGE.0.DELETE", "", null));
        Assert.Empty(world.GmPages);
    }

    /// <summary>Read BARE, GMPAGEP is a truth test. Upstream answers a reference
    /// that is not a world object with 1, and an absent one with false
    /// (CScriptObj.cpp:511) - a page has no uid, so 1/0 is the whole answer, and it
    /// is what the queue dialog checks before letting a GM claim a second page.
    /// Dotted, it reads through to the page.</summary>
    [Fact]
    public void GmpagepReadsAsATruthTestAndThenThroughToThePage()
    {
        var world = NewWorld();
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));

        Assert.True(gm.TryGetProperty("GMPAGEP", out string none));
        Assert.Equal("0", none);

        var page = new GmPage
        {
            Account = "someone",
            Reason = "help me",
            CharUid = new Serial(0x40005555),
            Position = new Point3D(1200, 1300, 0, 0),
            Handler = gm.Uid,
        };
        world.AddGmPage(page);

        Assert.True(gm.TryGetProperty("GMPAGEP", out string held));
        Assert.Equal("1", held);

        Assert.True(gm.TryGetProperty("GMPAGEP.REASON", out string reason));
        Assert.Equal("help me", reason);
        Assert.True(gm.TryGetProperty("GMPAGEP.CHARUID", out string charUid));
        Assert.Equal("040005555", charUid);

        // A GM holding nothing answers 0 for the dotted form too, rather than
        // refusing the key and leaving the raw text in the line.
        var other = world.CreateCharacter();
        other.IsPlayer = true;
        world.PlaceCharacter(other, new Point3D(101, 100, 0, 0));
        Assert.True(other.TryGetProperty("GMPAGEP.REASON", out string otherReason));
        Assert.Equal("0", otherReason);
    }
}
