using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
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

    [Fact]
    public void OreDoubleClick_TargetForge_SmeltsOreIntoIngots()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.SetSkill(SkillType.Mining, 1000);

        var pack = EquipBackpack(world, player);
        var ore = world.CreateItem();
        ore.ItemType = ItemType.Ore;
        ore.BaseId = 0x19B9;
        ore.Name = "iron ore";
        ore.Amount = 4;
        pack.AddItem(ore);

        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        client.HandleDoubleClick(ore.Uid.Value);
        Assert.True(client.HasPendingTarget);

        client.HandleTargetResponse(0, 0, forge.Uid.Value, forge.X, forge.Y, forge.Z, 0);

        Assert.True(ore.IsDeleted);
        var ingots = Assert.Single(pack.Contents, i => i.ItemType == ItemType.Ingot);
        Assert.Equal(0x1BF2, ingots.BaseId);
        Assert.Equal(4, ingots.Amount);
        Assert.False(client.HasPendingTarget);
    }

    [Fact]
    public void OreDoubleClick_SmeltTriggerReturnTrue_CancelsNativeSmelt()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.SetSkill(SkillType.Mining, 1000);

        var pack = EquipBackpack(world, player);
        var ore = world.CreateItem();
        ore.ItemType = ItemType.Ore;
        ore.BaseId = 0x19B9;
        ore.Amount = 2;
        pack.AddItem(ore);

        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int smeltCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "Smelt", (_, args) =>
        {
            smeltCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(ore, args.ItemSrc);
            Assert.Same(forge, args.O1);
            Assert.Equal(2, args.N1);
            return TriggerResult.True;
        });
        client.SetEngines(triggerDispatcher: dispatcher);

        client.HandleDoubleClick(ore.Uid.Value);
        client.HandleTargetResponse(0, 0, forge.Uid.Value, forge.X, forge.Y, forge.Z, 0);

        Assert.Equal(1, smeltCount);
        Assert.False(ore.IsDeleted);
        Assert.Equal(2, ore.Amount);
        Assert.DoesNotContain(pack.Contents, i => i.ItemType == ItemType.Ingot);
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
