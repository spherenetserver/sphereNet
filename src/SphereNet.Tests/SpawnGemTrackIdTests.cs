using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CCSpawn::SetTrackID (CCSpawn.cpp:768): a char spawner shows the ICON
/// statuette of the creature it makes, the wisp when that creature has none or the
/// target is a [SPAWN] group, and any other spawner is the large world gem; it is
/// invisible and dark red unless coloured. Field report: gems kept whatever graphic
/// the save carried, so ones saved before a creature's ICON changed showed the old
/// statuette.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnGemTrackIdTests
{
    private const string Script = """
        [ITEMDEF 01ea7]
        DEFNAME=i_worldgem_bit_t
        TYPE=t_spawn_char

        [ITEMDEF 01f14]
        DEFNAME=i_spawn_item_t
        TYPE=t_spawn_item

        [ITEMDEF 02100]
        DEFNAME=i_pet_wisp_t

        [ITEMDEF 02122]
        DEFNAME=i_pet_mustang_t

        [ITEMDEF 01000]
        DEFNAME=i_prize_t

        [CHARDEF 0c8]
        DEFNAME=c_mustang_t
        ICON=i_pet_mustang_t

        [CHARDEF 0c9]
        DEFNAME=c_mustang_child_t
        ID=c_mustang_t

        [CHARDEF 0ca]
        DEFNAME=c_no_icon_t

        [SPAWN spawn_group_t]
        ID=c_mustang_t,1

        [EOF]
        """;

    private static (GameWorld, ResourceHolder) Setup()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_track_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        File.Delete(tempFile);

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return (world, resources);
    }

    private static Item Gem(GameWorld world, ResourceHolder res, ItemType type, ushort baseId)
    {
        var gem = world.CreateItem();
        gem.BaseId = baseId;
        gem.ItemType = type;
        world.PlaceItem(gem, new Point3D(100, 100, 0, 0));
        gem.InitializeSpawnComponent(world, res);
        return gem;
    }

    [Theory]
    [InlineData("c_mustang_t", 0x2122)]       // its ICON
    [InlineData("c_mustang_child_t", 0x2122)] // inherited through ID=
    [InlineData("c_no_icon_t", 0x2100)]       // no ICON: the wisp
    [InlineData("spawn_group_t", 0x2100)]     // a [SPAWN] group: the wisp
    public void ACharSpawnerShowsItsCreaturesIcon(string spawnId, int expected)
    {
        var (world, res) = Setup();
        var gem = Gem(world, res, ItemType.SpawnChar, 0x1EA7);
        Assert.True(gem.TrySetProperty("DISPID", "0e75"));   // what an old save carried

        Assert.True(gem.TrySetProperty("SPAWNID", spawnId));

        Assert.Equal((ushort)expected, gem.DispIdFull);
        Assert.True(gem.IsAttr(ObjAttributes.Invis));
        Assert.Equal((ushort)0x0020, gem.Hue.Value);        // HUE_RED_DARK when uncoloured
    }

    [Fact]
    public void AColouredGemKeepsItsColourAndAnItemSpawnerIsTheLargeGem()
    {
        var (world, res) = Setup();
        var gem = Gem(world, res, ItemType.SpawnItem, 0x1F14);
        gem.Hue = new Color(0x0481);

        gem.ApplySpawnTrackId();

        Assert.Equal((ushort)0x1F13, gem.DispIdFull);
        Assert.Equal((ushort)0x0481, gem.Hue.Value);
    }
}
