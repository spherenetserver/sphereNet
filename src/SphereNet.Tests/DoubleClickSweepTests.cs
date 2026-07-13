using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Components;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Network.State;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Double-clicks one instance of every ItemType as a GM and asserts nothing
/// crashes and nothing that should be persistent is destroyed. Several real
/// reports this project fixed were double-click handlers that threw, silently
/// consumed a fixture, or (for invisible items) desynced the object out of view.
/// A blanket sweep catches that whole class without logging into the game.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DoubleClickSweepTests
{
    private readonly ITestOutputHelper _out;
    public DoubleClickSweepTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void EveryItemType_DoubleClick_NeverThrows()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var client = CreateGm(lf, world, accounts, out var player);
        client.SetEngines(skillHandlers: new SkillHandlers(world));
        var pack = EquipBackpack(world, player);

        var failures = new List<string>();
        foreach (ItemType type in Enum.GetValues<ItemType>())
        {
            var item = world.CreateItem();
            item.BaseId = 0x1000;
            item.ItemType = type;
            pack.AddItem(item); // in the GM's own pack: visible + reachable

            try
            {
                client.HandleDoubleClick(item.Uid.Value);
            }
            catch (Exception ex)
            {
                failures.Add($"{type}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (string f in failures) _out.WriteLine(f);
        Assert.True(failures.Count == 0,
            $"{failures.Count} item type(s) threw on double-click:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void SpawnerDoubleClick_ByGm_NeverDeletesTheSpawner()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var client = CreateGm(lf, world, accounts, out var player);

        // On ground, invisible, with a live component — the worldgem the user hit.
        var spawner = world.CreateItem();
        spawner.BaseId = 0x1EA7;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.SetAttr(ObjAttributes.Invis);
        spawner.SpawnChar = new SpawnComponent(spawner, world) { CharDefId = 0x0190, MaxCount = 1 };
        world.PlaceItem(spawner, player.Position);

        client.HandleDoubleClick(spawner.Uid.Value);

        Assert.False(spawner.IsDeleted);
    }

    [Fact]
    public void ContainerDoubleClick_NeverDeletesTheContainer()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var client = CreateGm(lf, world, accounts, out var player);

        var crate = world.CreateItem();
        crate.BaseId = 0x0E3C;
        crate.ItemType = ItemType.Container;
        world.PlaceItem(crate, player.Position);

        client.HandleDoubleClick(crate.Uid.Value);

        Assert.False(crate.IsDeleted);
    }

    private static GameClient CreateGm(ILoggerFactory lf, GameWorld world,
        AccountManager accounts, out Character player)
    {
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(10_000, 20_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Auditor";
        player.PrivLevel = PrivLevel.GM;
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
