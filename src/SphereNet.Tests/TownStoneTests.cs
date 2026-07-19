using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Guild;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit finding (wiki/test.txt #9): the town stone now runs the same stone
/// engine as guild stones (Source-X IT_STONE_TOWN == CItemStone): staff
/// establish the town at the stone, players request citizenship through the
/// candidate flow, the mayor (master) accepts, and citizenship carries a
/// MEMORY_TOWN link to the stone.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class TownStoneTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void TownStone_StaffEstablishes_CitizenJoins_WithTownMemory()
    {
        var world = CreateWorld();
        var guilds = new GuildManager();

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.Name = "Seer";
        gm.PrivLevel = PrivLevel.Counsel;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));

        var stone = world.CreateItem();
        stone.BaseId = 0x0ED4;
        stone.ItemType = ItemType.StoneTown;
        stone.Name = "Britain";
        world.PlaceItem(stone, new Point3D(100, 101, 0, 0));

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2614);
        client.SetEngines(guildManager: guilds);
        TestHarness.AttachCharacter(client, gm);

        // Establish: dclick opens the create gump; button 1 founds the town
        // named after the GM-named stone, founder = mayor.
        client.HandleDoubleClick(stone.Uid.Value);
        client.HandleGumpResponse(gm.Uid.Value, stone.Uid.Value, 1,
            [], Array.Empty<(ushort, string)>());

        var town = guilds.GetGuild(stone.Uid);
        Assert.NotNull(town);
        Assert.Equal("Britain", town!.Name);
        Assert.Equal(GuildPriv.Master, town.FindMember(gm.Uid)?.Priv);
        Assert.NotNull(gm.Memory_FindObjTypes(stone.Uid, MemoryType.Town));

        // Citizenship: a player applies (candidate), the mayor accepts via
        // the gump target flow — MEMORY_TOWN lands on the citizen.
        var citizen = world.CreateCharacter();
        citizen.IsPlayer = true;
        citizen.Name = "Peasant";
        world.PlaceCharacter(citizen, new Point3D(101, 100, 0, 0));
        town.AddRecruit(citizen.Uid);
        Assert.Equal(GuildPriv.Candidate, town.FindMember(citizen.Uid)?.Priv);

        client.HandleDoubleClick(stone.Uid.Value);
        client.HandleGumpResponse(gm.Uid.Value, stone.Uid.Value, 10,
            [], Array.Empty<(ushort, string)>());
        client.HandleTargetResponse(0, client.ActiveTargetCursorId,
            citizen.Uid.Value, citizen.X, citizen.Y, citizen.Z, 0);

        Assert.NotEqual(GuildPriv.Candidate, town.FindMember(citizen.Uid)?.Priv);
        Assert.NotNull(citizen.Memory_FindObjTypes(stone.Uid, MemoryType.Town));
    }

    [Fact]
    public void TownStone_RegularPlayer_CannotEstablish()
    {
        var world = CreateWorld();
        var guilds = new GuildManager();

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneTown;
        stone.Name = "Vesper";
        world.PlaceItem(stone, new Point3D(100, 101, 0, 0));

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2615);
        client.SetEngines(guildManager: guilds);
        TestHarness.AttachCharacter(client, player);

        client.HandleDoubleClick(stone.Uid.Value);
        // No create gump was offered; a forged response must not found a town.
        client.HandleGumpResponse(player.Uid.Value, stone.Uid.Value, 1,
            [], Array.Empty<(ushort, string)>());
        Assert.Null(guilds.GetGuild(stone.Uid));
    }
}
