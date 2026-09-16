using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Which way a crafter faces while they work.
///
/// Upstream turns them toward the work site on every stroke: UpdateDir(m_Act_p)
/// "toward the forge" (Skill_Blacksmith, CCharSkill.cpp:3155) and "toward the fire
/// source" (Skill_Cooking, :2252). Nothing here did, so the hammer swung at whatever
/// the crafter happened to be looking at.
///
/// The work-site check already had to find the forge or the fire to allow the craft at
/// all; it just answered yes or no and threw the position away.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CraftWorkSiteFacingTests
{
    private readonly ITestOutputHelper _out;
    public CraftWorkSiteFacingTests(ITestOutputHelper output) => _out = output;

    private static (GameWorld World, CraftingEngine Craft, Character Me) Stage()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        return (world, new CraftingEngine(world), me);
    }

    private static Item Site(GameWorld world, ItemType type, short x, short y)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0FB1;
        item.ItemType = type;
        world.PlaceItem(item, new Point3D(x, y, 0, 0));
        return item;
    }

    [Fact]
    public void TheForgeIsFoundAndItsPositionComesBack()
    {
        var (world, craft, me) = Stage();
        var forge = Site(world, ItemType.Forge, 102, 100);

        Assert.True(craft.TryFindWorkSite(me, SkillType.Blacksmithing, out var at));
        _out.WriteLine($"forge at {at.X},{at.Y} (item at {forge.X},{forge.Y})");
        Assert.Equal(forge.X, at.X);
        Assert.Equal(forge.Y, at.Y);
    }

    [Fact]
    public void AFireCountsForCooking()
    {
        var (world, craft, me) = Stage();
        var fire = Site(world, ItemType.Campfire, 100, 103);

        Assert.True(craft.TryFindWorkSite(me, SkillType.Cooking, out var at));
        Assert.Equal(fire.Y, at.Y);
    }

    [Fact]
    public void AForgeOutOfRangeIsNotAWorkSite()
    {
        // Smithing needs one within two tiles (CCharSkill.cpp Skill_Blacksmith).
        var (world, craft, me) = Stage();
        Site(world, ItemType.Forge, 110, 100);

        Assert.False(craft.TryFindWorkSite(me, SkillType.Blacksmithing, out _));
    }

    [Fact]
    public void ASkillWithNoWorkSiteAnswersNo()
    {
        var (world, craft, me) = Stage();
        Site(world, ItemType.Forge, 101, 100);

        Assert.False(craft.TryFindWorkSite(me, SkillType.Tailoring, out _));
    }

    [Fact]
    public void FacingTheSiteTurnsTheCrafter()
    {
        var (world, craft, me) = Stage();
        me.Direction = Direction.West;
        Site(world, ItemType.Forge, 102, 100);

        Assert.True(craft.TryFindWorkSite(me, SkillType.Blacksmithing, out var at));
        SphereNet.Game.Skills.Information.ActiveSkillEngine.FaceSkillTarget(me, at);

        _out.WriteLine($"facing {me.Direction}");
        Assert.Equal(Direction.East, me.Direction);
    }
}
