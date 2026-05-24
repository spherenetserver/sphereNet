using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Network.State;

namespace SphereNet.Tests;

public class ItemUseParityTests
{
    [Fact]
    public void WeaponDoubleClick_TargetWeapon_AppliesBackpackPoisonPotion()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out var state, out var player);
        player.PrivLevel = PrivLevel.GM;
        player.SetSkill(SkillType.Poisoning, 1000);

        var pack = EquipBackpack(world, player);
        var blade = world.CreateItem();
        blade.ItemType = ItemType.WeaponSword;
        blade.Name = "poison blade";
        pack.AddItem(blade);
        var poison = world.CreateItem();
        poison.ItemType = ItemType.Potion;
        poison.Quality = 80;
        poison.SetTag("POTION_SPELL", "Poison");
        pack.AddItem(poison);

        client.SetEngines(skillHandlers: new SkillHandlers(world));
        client.HandleDoubleClick(blade.Uid.Value);

        Assert.True(client.HasPendingTarget);
        client.HandleTargetResponse(0, 0, blade.Uid.Value, 0, 0, 0, 0);

        Assert.True(blade.TryGetTag("POISON_SKILL", out var poisonSkill));
        Assert.Equal("80", poisonSkill);
        Assert.True(poison.IsDeleted);
        Assert.False(client.HasPendingTarget);
    }

    [Fact]
    public void WeaponDoubleClick_TargetDamagedItem_RepairsThroughTinkering()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.PrivLevel = PrivLevel.GM;
        player.SetSkill(SkillType.Tinkering, 1000);

        var pack = EquipBackpack(world, player);
        var tool = world.CreateItem();
        tool.ItemType = ItemType.WeaponMaceSmith;
        pack.AddItem(tool);

        var damaged = world.CreateItem();
        damaged.ItemType = ItemType.Armor;
        damaged.HitsMax = 50;
        damaged.HitsCur = 10;
        damaged.Name = "damaged armor";
        pack.AddItem(damaged);

        client.SetEngines(skillHandlers: new SkillHandlers(world));
        client.HandleDoubleClick(tool.Uid.Value);

        Assert.True(client.HasPendingTarget);
        client.HandleTargetResponse(0, 0, damaged.Uid.Value, 0, 0, 0, 0);

        Assert.True(damaged.HitsCur > 10);
        Assert.False(client.HasPendingTarget);
    }

    [Fact]
    public void TrapDoubleClick_RoutesThroughRemoveTrapAndDisarmsActiveTrap()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.PrivLevel = PrivLevel.GM;
        player.SetSkill(SkillType.RemoveTrap, 1000);

        var trap = world.CreateItem();
        trap.ItemType = ItemType.TrapActive;
        world.PlaceItem(trap, player.Position);

        client.SetEngines(skillHandlers: new SkillHandlers(world));
        client.HandleDoubleClick(trap.Uid.Value);

        Assert.Equal(ItemType.Trap, trap.ItemType);
        Assert.False(client.HasPendingTarget);
    }

    private static GameClient CreatePlayingClient(
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
        GameWorld world,
        AccountManager accounts,
        out NetState state,
        out Character player)
    {
        state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(10_000, 20_000));
        var client = new GameClient(state, world, accounts, loggerFactory.CreateLogger<GameClient>());
        player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Tester";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return client;
    }

    private static Item EquipBackpack(GameWorld world, Character player)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        return pack;
    }
}
