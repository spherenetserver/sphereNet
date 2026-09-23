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
/// A spawner's target read as Source-X reads it: MORE/MORE1/SPAWNID go through
/// GetArgDWVal (CCSpawn.cpp:943), so a [DEFNAME] alias is followed and a brace group
/// {a wa b wb} draws one member by weight. The worldgen spawn tables name every
/// creature this way ("jeweler" -> {c_jeweler 1 c_jeweler_f 1}) and set the target
/// after TYPE, so the live spawner has to take MORE1 as its target too.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnAliasTargetTests
{
    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_alias
        TYPE=t_spawn_char

        [ITEMDEF 01f14]
        DEFNAME=i_spawn_item_alias
        TYPE=t_spawn_item

        [ITEMDEF 01001]
        DEFNAME=i_prize_alias
        NAME=Prize

        [CHARDEF c_vendor_m_alias]
        DEFNAME=c_vendor_m_alias
        ID=0x190
        NAME=male vendor

        [CHARDEF c_vendor_f_alias]
        DEFNAME=c_vendor_f_alias
        ID=0x191
        NAME=female vendor

        [DEFNAME spawn_aliases]
        vendor_alias        {c_vendor_m_alias 0 c_vendor_f_alias 1}
        vendor_alias_outer  vendor_alias
        prize_alias         i_prize_alias

        [EOF]
        """;

    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_alias_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        return resources;
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Stone(GameWorld world, ItemType type)
    {
        var stone = world.CreateItem();
        stone.BaseId = type == ItemType.SpawnItem ? (ushort)0x1F14 : (ushort)0x1F13;
        stone.ItemType = type;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        return stone;
    }

    [Fact]
    public void AZeroWeightMemberIsNeverDrawnAndAliasesChain()
    {
        var res = LoadResources();
        for (int roll = 0; roll < 4; roll++)
        {
            int r = roll;
            Assert.Equal("c_vendor_f_alias", res.FollowResourceAlias("vendor_alias", max => r % max));
            Assert.Equal("c_vendor_f_alias", res.FollowResourceAlias("vendor_alias_outer", max => r % max));
        }
        Assert.Equal("c_vendor_m_alias", res.FollowResourceAlias("c_vendor_m_alias"));
        Assert.Equal("no_such_name", res.FollowResourceAlias("no_such_name"));
    }

    [Fact]
    public void MoreOneSetAfterTypeTargetsTheLiveSpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Stone(world, ItemType.SpawnChar);
        stone.InitializeSpawnComponent(world, res);

        Assert.True(stone.TrySetProperty("MORE1", "vendor_alias_outer"));

        int female = res.ResolveDefName("c_vendor_f_alias").Index;
        Assert.Equal(female, stone.SpawnChar!.CharDefId);
        Assert.Equal((uint)female, stone.More1);
        // The drawn member is what the save carries, so a restart keeps it.
        Assert.Equal("c_vendor_f_alias", stone.Tags.Get("MORE1_DEFNAME"));

        stone.SpawnChar.RespawnNow();
        Assert.Equal(1, stone.SpawnChar.CurrentCount);
    }

    [Fact]
    public void AnAliasParkedInTheTagResolvesOnInitialize()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Stone(world, ItemType.SpawnChar);
        stone.SetTag("MORE1_DEFNAME", "vendor_alias");

        stone.InitializeSpawnComponent(world, res);

        Assert.Equal(res.ResolveDefName("c_vendor_f_alias").Index, stone.SpawnChar!.CharDefId);
    }

    [Fact]
    public void ASpawnerTypedOnlyByItsItemdefBuildsItsComponentOnMoreOne()
    {
        var res = LoadResources();
        var world = NewWorld();
        var prevHook = Item.OnSpawnTypeChanged;
        Item.OnSpawnTypeChanged = it => it.InitializeSpawnComponent(world, res);
        try
        {
            var stone = world.CreateItem();
            stone.BaseId = 0x1F13; // i_spawn_char_alias: TYPE comes from the itemdef
            world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
            Assert.Equal(ItemType.SpawnChar, stone.ItemType);
            Assert.Null(stone.SpawnChar);

            Assert.True(stone.TrySetProperty("MORE1", "vendor_alias"));

            Assert.NotNull(stone.SpawnChar);
            Assert.Equal(res.ResolveDefName("c_vendor_f_alias").Index, stone.SpawnChar!.CharDefId);
        }
        finally
        {
            Item.OnSpawnTypeChanged = prevHook;
        }
    }

    [Fact]
    public void AnItemSpawnerFollowsTheAliasToo()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Stone(world, ItemType.SpawnItem);
        stone.InitializeSpawnComponent(world, res);

        Assert.True(stone.TrySetProperty("MORE1", "prize_alias"));

        Assert.Equal(res.ResolveDefName("i_prize_alias").Index, stone.SpawnItem!.ItemDefId);
        Assert.Equal("i_prize_alias", stone.Tags.Get("MORE1_DEFNAME"));
    }
}
