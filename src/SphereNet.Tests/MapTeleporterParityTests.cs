using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Walking through a dungeon entrance played the teleport sound. Source-X keeps the
/// [TELEPORTERS] list as sector data, not items (CSectorBase::GetTeleport), and
/// walks a character through one with Spell_Teleport(dst, true, false, false) -
/// silent, no effect (CCharAct.cpp:5063-5094). A telepad or moongate item shows the
/// configured teleport effect and sound unless MORE2 makes it quiet (Use_MoonGate,
/// CCharUse.cpp:266-268). The engine turned every list line into an invisible
/// telepad item a GM with ALLSHOW could see, and played sound 0x1FE on every one.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MapTeleporterParityTests
{
    private static (GameWorld World, MovementEngine Engine, Character Walker, List<Point3D> Effects) Stage()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Stam = ch.MaxStam = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var effects = new List<Point3D>();
        Character.OnTeleportEffect = (_, from) => effects.Add(from);
        return (world, new MovementEngine(world), ch, effects);
    }

    [Fact]
    public void AMapTeleporterStepIsSilentAndPlacesNoItem()
    {
        var (world, engine, ch, effects) = Stage();
        var (added, _) = world.SetMapTeleports(
        [
            new GameWorld.MapTeleport(new Point3D(101, 100, 0, 0), new Point3D(200, 210, 0, 0), false),
        ]);
        Assert.Equal(1, added);
        Assert.Empty(world.GetItemsInRange(new Point3D(101, 100, 0, 0), 0));

        int resyncs = 0;
        engine.OnTeleport = (_, _, _) => resyncs++;

        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 0));

        Assert.Equal((short)200, ch.X);
        Assert.Equal((short)210, ch.Y);
        Assert.Equal(1, resyncs);
        Assert.Empty(effects);
    }

    [Fact]
    public void AMapTeleporterMoreThanFiveZAwayDoesNothing()
    {
        var (world, engine, ch, _) = Stage();
        world.SetMapTeleports(
        [
            new GameWorld.MapTeleport(new Point3D(101, 100, 20, 0), new Point3D(200, 210, 0, 0), false),
        ]);

        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 0));

        Assert.Equal((short)101, ch.X);
    }

    [Fact]
    public void ANpcIsRefusedByAPlayersOnlyMapTeleporter()
    {
        var (world, engine, _, _) = Stage();
        var npc = world.CreateCharacter();
        npc.Stam = npc.MaxStam = 100;
        world.PlaceCharacter(npc, new Point3D(300, 300, 0, 0));
        world.SetMapTeleports(
        [
            new GameWorld.MapTeleport(new Point3D(301, 300, 0, 0), new Point3D(200, 210, 0, 0), false),
        ]);

        Assert.False(engine.TryMove(npc, Direction.East, running: false, sequence: 0));

        Assert.Equal((short)300, npc.X);
        Assert.Equal((short)300, npc.Y);
    }

    [Theory]
    [InlineData(ItemType.Telepad)]
    [InlineData(ItemType.Moongate)]
    public void ATelepadShowsTheTeleportFromWhereItStood(ItemType type)
    {
        var (world, engine, ch, effects) = Stage();
        var pad = world.CreateItem();
        pad.BaseId = 0x1BC3;
        pad.ItemType = type;
        pad.MoreP = new Point3D(200, 210, 0, 0);
        world.PlaceItem(pad, new Point3D(101, 100, 0, 0));

        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 0));

        Assert.Equal((short)200, ch.X);
        var from = Assert.Single(effects);
        Assert.Equal((short)101, from.X);
        Assert.Equal((short)100, from.Y);
    }

    [Fact]
    public void AQuietTelepadTeleportsWithoutEffect()
    {
        var (world, engine, ch, effects) = Stage();
        var pad = world.CreateItem();
        pad.BaseId = 0x1BC3;
        pad.ItemType = ItemType.Telepad;
        pad.MoreP = new Point3D(200, 210, 0, 0);
        pad.More2 = 1;
        world.PlaceItem(pad, new Point3D(101, 100, 0, 0));

        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 0));

        Assert.Equal((short)200, ch.X);
        Assert.Empty(effects);
    }

    [Fact]
    public void OnlyTheOldTeleporterItemsAreSweptNotWorldBuiltTelepads()
    {
        var world = TestHarness.CreateWorld();
        var old = world.CreateItem();
        old.BaseId = 0x1BC3;
        old.ItemType = ItemType.Telepad;
        old.SetAttr(ObjAttributes.Invis | ObjAttributes.Static | ObjAttributes.Move_Never);
        world.PlaceItem(old, new Point3D(101, 100, 0, 0));

        // Worldgen decoration: its own graphic, attr_static only.
        var decor = world.CreateItem();
        decor.BaseId = 0x17EE;
        decor.ItemType = ItemType.Telepad;
        decor.SetAttr(ObjAttributes.Static);
        world.PlaceItem(decor, new Point3D(102, 100, 0, 0));

        Assert.True(SphereNet.Server.Program.IsLegacyMapTeleporterItem(old));
        Assert.False(SphereNet.Server.Program.IsLegacyMapTeleporterItem(decor));
    }

    [Fact]
    public void ASecondTeleporterFromTheSamePointIsRefused()
    {
        var world = TestHarness.CreateWorld();
        var (added, skipped) = world.SetMapTeleports(
        [
            new GameWorld.MapTeleport(new Point3D(101, 100, 0, 0), new Point3D(200, 210, 0, 0), false),
            new GameWorld.MapTeleport(new Point3D(101, 100, 0, 0), new Point3D(300, 310, 0, 0), false),
        ]);

        Assert.Equal(1, added);
        Assert.Equal(1, skipped);
        Assert.Equal((short)200, world.GetMapTeleport(new Point3D(101, 100, 3, 0))!.Dest.X);
    }
}
