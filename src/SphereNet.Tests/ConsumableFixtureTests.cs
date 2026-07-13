using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X Use_Eat/Use_Drink consume exactly one unit and refuse an item the user
/// cannot move (a placed Move_Never fixture) rather than destroying it. SphereNet's
/// booze/potion handlers deleted the WHOLE stack on one use and had no movability
/// guard, so a single drink wiped a pile and a double-clicked keg/pitcher/food
/// decoration vanished. These tests lock the corrected behavior.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ConsumableFixtureTests
{
    [Fact]
    public void BoozeStack_OneDrink_ConsumesOnlyOneNotTheWholeStack()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var pack = EquipBackpack(world, player);
        var ale = world.CreateItem();
        ale.ItemType = ItemType.Booze;
        ale.Amount = 5;
        pack.AddItem(ale);

        client.HandleDoubleClick(ale.Uid.Value);

        Assert.False(ale.IsDeleted);
        Assert.Equal(4, ale.Amount); // exactly one bottle, not the pile
    }

    [Fact]
    public void BoozeFixture_NonGm_IsRefusedNotDestroyed()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var keg = world.CreateItem();
        keg.ItemType = ItemType.Booze;
        keg.SetAttr(ObjAttributes.Move_Never); // a placed keg decoration
        world.PlaceItem(keg, player.Position);

        client.HandleDoubleClick(keg.Uid.Value);

        Assert.False(keg.IsDeleted); // fixture survives
    }

    [Fact]
    public void PotionStack_OneUse_ConsumesOnlyOne()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var pack = EquipBackpack(world, player);
        var potions = world.CreateItem();
        potions.ItemType = ItemType.Potion;
        potions.SetTag("POTION_TYPE", "heal");
        potions.Amount = 3;
        pack.AddItem(potions);
        player.MaxHits = 100;
        player.Hits = 50;

        client.HandleDoubleClick(potions.Uid.Value);

        Assert.False(potions.IsDeleted);
        Assert.Equal(2, potions.Amount);
    }

    [Fact]
    public void FoodFixture_NonGm_IsRefusedNotEaten()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var loaf = world.CreateItem();
        loaf.ItemType = ItemType.Food;
        loaf.SetAttr(ObjAttributes.Move_Never); // decorative loaf on a table
        world.PlaceItem(loaf, player.Position);

        client.HandleDoubleClick(loaf.Uid.Value);

        Assert.False(loaf.IsDeleted);
    }

    [Fact]
    public void FoodStack_InPack_EatsOneUnit()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var pack = EquipBackpack(world, player);
        var apples = world.CreateItem();
        apples.ItemType = ItemType.Fruit;
        apples.Amount = 4;
        pack.AddItem(apples);
        player.Food = 0;

        client.HandleDoubleClick(apples.Uid.Value);

        Assert.False(apples.IsDeleted);
        Assert.Equal(3, apples.Amount);
        Assert.True(player.Food > 0);
    }

    private static (GameClient client, GameWorld world, Character player) Setup(PrivLevel priv)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(10_000, 20_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Tester";
        player.PrivLevel = priv;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return (client, world, player);
    }

    private static Item EquipBackpack(GameWorld world, Character player)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        return pack;
    }
}
