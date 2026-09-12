using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What @HouseDesignCommit is told, and when (port plan İŞ-35 / PLAN-503).
///
/// Source-X fires it BEFORE the commit with ARGN1 = the committed design's tile
/// count, ARGN2 = the working design's, ARGN3 = the new revision, ARGO = the multi
/// and LOCAL.FIXTURES.OLD / FIXTURES.NEW / MAXZ; RETURN 1 abandons the commit
/// (CItemMultiCustom.cpp:314-327). It returns early when the revision has not moved
/// (:277), so an unchanged design is not a commit at all.
///
/// The reference pack prices a build from those two counts - (new - old) * 500 gold -
/// and RETURNs 1 when the owner cannot afford it (Scripts-X house_typedefs.scp:
/// 603-615). This engine fired the trigger AFTER the commit with ARGN1 = the
/// revision and nothing else, so that pack read a revision as an "old tile count",
/// charged from garbage, and its refusal was ignored.
/// </summary>
public sealed class HouseDesignCommitTriggerParityTests
{
    private static (CustomHousingEngine Engine, Character Ch, Item Multi) CreateSession()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var housing = new HousingEngine(world, new MultiRegistry());
        var engine = new CustomHousingEngine(world, housing);
        var ch = world.CreateCharacter();
        var multi = world.CreateItem();
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
        engine.Begin(ch, multi);
        return (engine, ch, multi);
    }

    [Fact]
    public void TheCountsAreTheTwoDesignsNotTheRevision()
    {
        var (engine, ch, multi) = CreateSession();
        Assert.True(engine.Build(ch, 0x0064, 2, 3));
        Assert.True(engine.Build(ch, 0x0064, 3, 3));
        Assert.NotNull(engine.Commit(ch));           // first design: 2 tiles

        engine.Begin(ch, multi);
        Assert.True(engine.Build(ch, 0x0064, 4, 3)); // now 3

        var preview = engine.PreviewCommit(ch);

        Assert.NotNull(preview);
        Assert.Equal(2, preview!.Value.OldTiles);
        Assert.Equal(3, preview.Value.NewTiles);
        Assert.Same(multi, preview.Value.Multi);
    }

    [Fact]
    public void ANewHouseReportsNoOldTiles()
    {
        var (engine, ch, _) = CreateSession();
        Assert.True(engine.Build(ch, 0x0064, 2, 3));

        var preview = engine.PreviewCommit(ch);

        Assert.NotNull(preview);
        Assert.Equal(0, preview!.Value.OldTiles);
        Assert.Equal(1, preview.Value.NewTiles);
    }

    [Fact]
    public void TheRevisionIsTheOneTheCommitWillLand()
    {
        var (engine, ch, _) = CreateSession();
        Assert.True(engine.Build(ch, 0x0064, 2, 3));

        uint announced = engine.PreviewCommit(ch)!.Value.Revision;
        uint landed = engine.Commit(ch)!.Value;

        Assert.Equal(landed, announced);
    }

    [Fact]
    public void AnUnchangedDesignIsNotACommit()
    {
        // Source-X returns early on an equal revision (:277) - nobody is charged
        // for pressing the button twice on the same design.
        var (engine, ch, multi) = CreateSession();
        Assert.True(engine.Build(ch, 0x0064, 2, 3));
        Assert.NotNull(engine.Commit(ch));

        engine.Begin(ch, multi);                     // reopened, nothing edited

        Assert.Null(engine.PreviewCommit(ch));
    }

    [Fact]
    public void MaxZIsTheTallestTileInTheWorkingDesign()
    {
        var (engine, ch, _) = CreateSession();
        Assert.True(engine.Build(ch, 0x0064, 2, 3));  // story 1 -> z 7
        engine.SetLevel(ch, 2);
        Assert.True(engine.Build(ch, 0x0066, 2, 3));  // story 2 -> z 27

        Assert.Equal(27, engine.PreviewCommit(ch)!.Value.MaxZ);
    }

    [Fact]
    public void TheFixtureCountsCoverBothDesigns()
    {
        // A plain floor tile is not a fixture, so a design of them reports zero on
        // both sides and the pack's FIXTURES.OLD/NEW read as real numbers, not blanks.
        var (engine, ch, _) = CreateSession();
        Assert.True(engine.Build(ch, 0x0064, 2, 3));

        var preview = engine.PreviewCommit(ch)!.Value;

        Assert.Equal(0, preview.OldFixtures);
        Assert.Equal(0, preview.NewFixtures);
    }

    [Fact]
    public void WithoutASessionThereIsNothingToPreview()
    {
        var (engine, ch, _) = CreateSession();
        engine.End(ch);

        Assert.Null(engine.PreviewCommit(ch));
    }

    // ---- the veto, end to end -------------------------------------------

    [Fact]
    public void AScriptThatReturnsOneStopsTheCommit()
    {
        // Scripts-X RETURNs 1 from @HouseDesignCommit when the owner cannot afford
        // the build (house_typedefs.scp:611-614). The design must stay uncommitted
        // and the session must stay open so they can change it and try again.
        string scriptFile = Path.Combine(Path.GetTempPath(),
            $"spherenet_hdcommit_{Guid.NewGuid():N}.scp");
        File.WriteAllText(scriptFile, """
            [EVENTS e_hdcommit_probe]
            ON=@HouseDesignCommit
            TAG.SEEN_OLD=<ARGN1>
            TAG.SEEN_NEW=<ARGN2>
            TAG.SEEN_REV=<ARGN3>
            TAG.SEEN_MAXZ=<LOCAL.MAXZ>
            TAG.SEEN_FIXNEW=<LOCAL.FIXTURES.NEW>
            RETURN 1
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(scriptFile);

            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var housing = new HousingEngine(world, new MultiRegistry());
            var custom = new CustomHousingEngine(world, housing);

            var owner = world.CreateCharacter();
            owner.IsPlayer = true;
            owner.PrivLevel = PrivLevel.Player;
            owner.Events.Add(stack.Resources.ResolveDefName("e_hdcommit_probe"));
            world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

            var multi = world.CreateItem();
            multi.ItemType = ItemType.MultiCustom;
            multi.BaseId = 0x4064;
            multi.SetTag("HOUSE.OWNER", $"0{owner.Uid.Value:X}");
            world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
            var house = housing.RegisterExistingMulti(multi);
            Assert.NotNull(house);
            house!.Owner = owner.Uid;

            var accounts = new AccountManager(loggerFactory);
            var client = TestHarness.CreateClient(loggerFactory, world, accounts, 1);
            TestHarness.AttachCharacter(client, owner);
            client.SetEngines(housingEngine: housing, triggerDispatcher: stack.Dispatcher,
                customHousing: custom);

            custom.Begin(owner, multi);
            Assert.True(custom.Build(owner, 0x0064, 2, 3));

            client.HandleEncodedCommand(EncodedCommandRegistry.Commit, multi.Uid.Value,
                new PacketBuffer(Array.Empty<byte>()));

            // The script got the real numbers, not a revision masquerading as a count.
            Assert.True(owner.TryGetTag("SEEN_OLD", out string? seenOld));
            Assert.Equal("0", seenOld);
            Assert.True(owner.TryGetTag("SEEN_NEW", out string? seenNew));
            Assert.Equal("1", seenNew);
            Assert.True(owner.TryGetTag("SEEN_MAXZ", out string? seenMaxZ));
            Assert.Equal("7", seenMaxZ);
            Assert.True(owner.TryGetTag("SEEN_FIXNEW", out string? seenFix));
            Assert.Equal("0", seenFix);

            // And the refusal held: nothing was committed, the session is still open.
            Assert.Empty(custom.GetCommittedTiles(multi));
            Assert.NotNull(custom.GetSession(owner.Uid));
        }
        finally
        {
            File.Delete(scriptFile);
        }
    }

    [Fact]
    public void AScriptThatSaysNothingLetsTheCommitThrough()
    {
        string scriptFile = Path.Combine(Path.GetTempPath(),
            $"spherenet_hdcommit_ok_{Guid.NewGuid():N}.scp");
        File.WriteAllText(scriptFile, """
            [EVENTS e_hdcommit_ok]
            ON=@HouseDesignCommit
            TAG.SAW=1
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(scriptFile);

            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var housing = new HousingEngine(world, new MultiRegistry());
            var custom = new CustomHousingEngine(world, housing);

            var owner = world.CreateCharacter();
            owner.IsPlayer = true;
            owner.PrivLevel = PrivLevel.Player;
            owner.Events.Add(stack.Resources.ResolveDefName("e_hdcommit_ok"));
            world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

            var multi = world.CreateItem();
            multi.ItemType = ItemType.MultiCustom;
            multi.BaseId = 0x4064;
            multi.SetTag("HOUSE.OWNER", $"0{owner.Uid.Value:X}");
            world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
            var house = housing.RegisterExistingMulti(multi);
            house!.Owner = owner.Uid;

            var accounts = new AccountManager(loggerFactory);
            var client = TestHarness.CreateClient(loggerFactory, world, accounts, 2);
            TestHarness.AttachCharacter(client, owner);
            client.SetEngines(housingEngine: housing, triggerDispatcher: stack.Dispatcher,
                customHousing: custom);

            custom.Begin(owner, multi);
            Assert.True(custom.Build(owner, 0x0064, 2, 3));

            client.HandleEncodedCommand(EncodedCommandRegistry.Commit, multi.Uid.Value,
                new PacketBuffer(Array.Empty<byte>()));

            Assert.True(owner.TryGetTag("SAW", out string? saw) && saw == "1");
            Assert.Single(custom.GetCommittedTiles(multi));
            Assert.Null(custom.GetSession(owner.Uid));   // committing ends the session
        }
        finally
        {
            File.Delete(scriptFile);
        }
    }
}
