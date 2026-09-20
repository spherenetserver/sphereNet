using System.Reflection;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SpawnLootLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpawnRunsInitializationOnceAndCreatesLootOnlyOnDeath(bool useTemplate)
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"spawn_loot_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [ITEMDEF 0eed]
            DEFNAME=i_gold
            TYPE=t_gold
            [TEMPLATE t_skeleton_loot_probe]
            ITEM=i_gold,41
            [CHARDEF 032]
            DEFNAME=c_skeleton_probe
            NAME=Skeleton
            ON=@Create
            TAG.CREATE_COUNT=<EVAL <TAG0.CREATE_COUNT>+1>
            NPC=brain_monster
            STR=100
            ON=@NPCRestock
            TAG.RESTOCK_COUNT=<EVAL <TAG0.RESTOCK_COUNT>+1>
            ON=@CreateLoot
            TAG.LOOT_COUNT=<EVAL <TAG0.LOOT_COUNT>+1>
            """ + (useTemplate ? "\nITEM=t_skeleton_loot_probe\n" : "\nITEM=i_gold,41\n"));
        var previous = SpawnComponent.OnNpcScriptInit;
        try
        {
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
            var world = TestHarness.CreateWorld();
            typeof(SphereNet.Server.Program).GetMethod("ConfigureNpcSpawnScripts",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [world, runtime.Dispatcher]);
            var stone = world.CreateItem();
            stone.BaseId = 0x1F13;
            stone.ItemType = ItemType.SpawnChar;
            world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
            stone.SetTag("MORE1_DEFNAME", "c_skeleton_probe");
            stone.Amount = 1;
            stone.InitializeSpawnComponent(world, runtime.Resources);
            stone.SpawnChar!.RespawnNow();
            var npc = Assert.Single(world.GetCharsInRange(stone.Position, 18));
            Assert.Equal("1", npc.Tags.Get("CREATE_COUNT"));
            Assert.Equal("1", npc.Tags.Get("RESTOCK_COUNT"));
            Assert.False(npc.TryGetTag("LOOT_COUNT", out _));
            Assert.True(npc.Backpack == null || npc.Backpack.Contents.All(i => i.ItemType != ItemType.Gold));
            var death = new DeathEngine(world) { TriggerDispatcher = runtime.Dispatcher };
            npc.Hits = 0;
            var corpse = Assert.IsType<Item>(death.ProcessDeath(npc));
            var gold = Assert.Single(corpse.Contents, i => i.ItemType == ItemType.Gold);
            Assert.Equal(41, gold.Amount);
            Assert.Equal("1", npc.Tags.Get("LOOT_COUNT"));
            Assert.Null(death.ProcessDeath(npc));
        }
        finally
        {
            SpawnComponent.OnNpcScriptInit = previous;
            File.Delete(path);
        }
    }
}
