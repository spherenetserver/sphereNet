using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Which way a gatherer is facing while they work.
///
/// Upstream turns the character toward the point the skill is working on, on every
/// stroke: CChar::Skill_Stroke calls UpdateDir(m_Act_p) (CCharSkill.cpp:3608), the same
/// call the forge (:3155), the campfire (:2252) and the fishing spot get. Nothing here
/// did it, so a miner stood facing wherever they happened to be looking and swung the
/// pick across the wrong axis - which is what "the animation does not look right on the
/// last target" describes.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GatherFacingTests
{
    private readonly ITestOutputHelper _out;
    public GatherFacingTests(ITestOutputHelper output) => _out = output;

    private static (GameWorld World, Character Me) Stage()
    {
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.IsOnline = true;
        me.Str = 100;
        me.Direction = Direction.North;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        return (world, me);
    }

    [Theory]
    [InlineData(103, 100, Direction.East)]
    [InlineData(97, 100, Direction.West)]
    [InlineData(100, 103, Direction.South)]
    [InlineData(100, 97, Direction.North)]
    public void AGathererTurnsTowardTheTileTheyAreWorking(short x, short y, Direction expected)
    {
        var (world, me) = Stage();
        me.Direction = Direction.NorthWest;      // deliberately looking elsewhere

        // The facing happens before anything the skill may refuse over - a missing
        // pickaxe, a barren tile - so drive it through the same helper the skills use.
        ActiveSkillEngine.FaceSkillTarget(me, new Point3D(x, y, 0, 0));

        _out.WriteLine($"standing at {me.X},{me.Y} working {x},{y} -> {me.Direction}");
        Assert.Equal(expected, me.Direction);
    }

    [Fact]
    public void WorkingOnYourOwnTileLeavesYourFacingAlone()
    {
        var (_, me) = Stage();
        me.Direction = Direction.SouthEast;

        ActiveSkillEngine.FaceSkillTarget(me, me.Position);

        Assert.Equal(Direction.SouthEast, me.Direction);
    }

    [Fact]
    public void TheTurnMarksTheCharacterSoWatchersAreTold()
    {
        // A facing nobody is told about is a facing that only exists on the server.
        var (_, me) = Stage();
        me.Direction = Direction.North;

        ActiveSkillEngine.FaceSkillTarget(me, new Point3D(105, 100, 0, 0));

        Assert.Equal(Direction.East, me.Direction);
        Assert.True((me.DirtyFlags & DirtyFlag.Direction) != DirtyFlag.None,
            "the direction change was never flagged");
    }
}
