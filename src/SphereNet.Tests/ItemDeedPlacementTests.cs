using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A deed for an ordinary item places that item, as upstream's deed path does for any
/// target (OnTarg_Use_Multi: "Might be a IT_MULTI or it might not. place it anyhow",
/// CClientTarg.cpp:1683). A guildstone deed was read as a house deed or reported blank;
/// now it asks where, places the stone never movable, makes its placer the master and
/// asks for the name (Multi_Create, CItemMulti.cpp:3448), and refuses one already in a
/// guild (addTargetItems, CClientMsg.cpp:1858).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemDeedPlacementTests
{
    private const string Script = """
        [ITEMDEF 014ef]
        DEFNAME=i_deed
        TYPE=t_deed

        [ITEMDEF 0edd]
        DEFNAME=i_guildstone
        TYPE=t_stone_guild
        """;

    private static (GameClient Client, Character Player, GameWorld World, GuildManager Guilds) Setup()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_itemdeed_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, Script);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? "",
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 9411);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Founder";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);
        var guilds = new GuildManager();
        client.SetEngines(guildManager: guilds);
        return (client, player, world, guilds);
    }

    private static Item GuildDeed(GameWorld world, Character player)
    {
        var deed = world.CreateItem();
        deed.BaseId = 0x14EF;
        deed.ItemType = ItemType.Deed;
        deed.SetTag("MORE", "i_guildstone");
        player.Backpack!.AddItem(deed);
        return deed;
    }

    [Fact]
    public void AGuildstoneDeedPlacesTheStoneFoundsTheGuildAndNamesIt()
    {
        var (client, player, world, guilds) = Setup();
        var deed = GuildDeed(world, player);

        client.HandleDoubleClick(deed.Uid.Value);
        Assert.NotEqual(0u, client.ActiveTargetCursorId);
        client.HandleTargetResponse(1, client.ActiveTargetCursorId, 0, 102, 100, 0, 0);

        var stone = world.GetItemsInRange(new Point3D(102, 100, 0, 0), 0)
            .SingleOrDefault(i => i.ItemType == ItemType.StoneGuild);
        Assert.NotNull(stone);
        Assert.True(stone!.IsAttr(ObjAttributes.Move_Never));
        Assert.True(deed.IsDeleted);
        var guild = guilds.GetGuild(stone.Uid);
        Assert.NotNull(guild);
        Assert.Equal(GuildPriv.Master, guild!.FindMember(player.Uid)!.Priv);

        client.HandlePromptResponse(player.Uid.Value, stone.Uid.Value, 1, "Drill Guild");
        Assert.Equal("Stone for the Drill Guild", stone.Name);
        Assert.Equal(stone.Name, guild.Name);
    }

    [Fact]
    public void AGuildMemberIsNotOfferedASecondStone()
    {
        var (client, player, world, guilds) = Setup();
        var stone = world.CreateItem();
        stone.BaseId = 0x0EDD;
        world.PlaceItem(stone, new Point3D(105, 100, 0, 0));
        guilds.CreateGuild(stone.Uid, "first", player.Uid);
        var deed = GuildDeed(world, player);

        client.HandleDoubleClick(deed.Uid.Value);

        Assert.Equal(0u, client.ActiveTargetCursorId);
        Assert.False(deed.IsDeleted);
    }
}
