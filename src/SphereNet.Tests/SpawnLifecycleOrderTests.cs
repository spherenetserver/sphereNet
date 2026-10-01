using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The order a spawner does its work in, and what each step may touch.
///
/// GenerateChar (CCSpawn.cpp:378) runs the definition's own @Create and @NPCRestock
/// first (NPC_LoadScript, :415 -> CCharNPC.cpp:265), then @Spawn, placement, AddObj, and
/// only then the TEVENTS/EVENTSPET @Create chain (NPC_CreateTrigger, :465). AddObj
/// (:583) parks the timer on the last slot, fires @AddObj, applies the seconds it leaves
/// in ARGN1, and only then puts the uid on the list (:643/:648/:661). OnTickComponent
/// arms the default schedule BEFORE generating (:690). COUNT is the member count
/// (:857). DelObj and KillChildren keep any spawn point awake until its quota is full
/// again (:535/:718/:665). GenerateChar refuses a spawn point that is not top level
/// whoever asks (:383). A champion's ADDOBJ verb only attaches its event and never
/// enrolls anything (CCChampion.cpp:1138/756).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnLifecycleOrderTests
{
    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_lo
        TYPE=t_spawn_char

        [ITEMDEF 01f14]
        DEFNAME=i_spawn_item_lo
        TYPE=t_spawn_item

        [ITEMDEF 01000]
        DEFNAME=i_prize_lo
        NAME=Prize

        [CHARDEF c_wave_lo]
        DEFNAME=c_wave_lo
        ID=0x27
        NAME=wave lo

        [CHARDEF c_boss_lo]
        DEFNAME=c_boss_lo
        ID=0x9B
        NAME=boss lo

        [CHAMPION champ_lo]
        DEFNAME=champ_lo
        NAME=Lo
        LEVELMAX=5
        SPAWNSMAX=100
        NPCGROUP[1]=c_wave_lo
        NPCGROUP[2]=c_wave_lo
        NPCGROUP[3]=c_wave_lo
        NPCGROUP[4]=c_wave_lo
        CHAMPIONID=c_boss_lo

        [EOF]
        """;

    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_lo_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        try
        {
            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
            { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
            resources.LoadResourceFile(tempFile);
            new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
            return resources;
        }
        finally { File.Delete(tempFile); }
    }

    private static GameWorld NewWorld(int size = 256)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, size, size);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Spawner(GameWorld world, ResourceHolder res, ItemType type,
        string target, int maxCount = 1, Point3D? at = null)
    {
        var stone = world.CreateItem();
        stone.BaseId = type == ItemType.SpawnItem ? (ushort)0x1F14 : (ushort)0x1F13;
        stone.ItemType = type;
        world.PlaceItem(stone, at ?? new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", target);
        stone.Amount = (ushort)maxCount;
        stone.InitializeSpawnComponent(world, res);
        return stone;
    }

    private static Item ChampionAltar(GameWorld world, ResourceHolder res)
    {
        var altar = world.CreateItem();
        altar.BaseId = 0x1F13;
        altar.ItemType = ItemType.SpawnChampion;
        world.PlaceItem(altar, new Point3D(100, 100, 0, 0));
        altar.SetTag("MORE1_DEFNAME", "champ_lo");
        altar.InitializeSpawnComponent(world, res);
        Assert.NotNull(altar.Champion);
        return altar;
    }

    private static string Read(ObjBase obj, string key)
    {
        Assert.True(obj.TryGetProperty(key, out string value), $"{key} should resolve");
        return value;
    }

    // ============================================================ champion ADDOBJ

    [Fact]
    public void ChampionAddObjDoesNotEnrollAPlayerAndStopLeavesThePlayerAlone()
    {
        // ICHMPV_ADDOBJ -> CCChampion::AddObj (CCChampion.cpp:1138/756) only attaches
        // the champion event; membership is GenerateChar's own NPC-only AddObj
        // (CCSpawn.cpp:609). So STOP -> KillChildren never reaches a player.
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(105, 105, 0, 0));

        Assert.True(altar.TryExecuteCommand("ADDOBJ", $"0{player.Uid.Value:X}", null!));
        Assert.DoesNotContain(player.Uid, altar.SpawnChar!.SpawnedUids);

        Assert.True(altar.TryExecuteCommand("STOP", "", null!));
        Assert.False(player.IsDeleted);
        Assert.NotNull(world.FindChar(player.Uid));
    }

    [Fact]
    public void ChampionAddObjOfAForeignNpcDoesNotEnrollItEither()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res);
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(106, 106, 0, 0));

        Assert.True(altar.TryExecuteCommand("ADDOBJ", $"0{npc.Uid.Value:X}", null!));
        Assert.True(altar.TryExecuteCommand("STOP", "", null!));

        Assert.False(npc.IsDeleted);
    }

    [Fact]
    public void ChampionStopStillClearsItsOwnWave()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res);
        altar.Champion!.Start();
        var members = altar.SpawnChar!.SpawnedUids.Select(world.FindChar).ToList();
        Assert.NotEmpty(members);

        Assert.True(altar.TryExecuteCommand("STOP", "", null!));

        Assert.All(members, m => Assert.True(m == null || m.IsDeleted));
    }

    // ============================================================ contained altar

    [Fact]
    public void AChampionAltarInsideABagSpawnsNothing()
    {
        // GenerateChar(rid) leaves at once for a spawn point that is not top level,
        // forced chardef or not (CCSpawn.cpp:383).
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res);
        var bag = world.CreateItem();
        world.PlaceItem(bag, new Point3D(100, 100, 0, 0));
        bag.AddItem(altar);
        Assert.True(altar.Champion!.TrySetProperty("LEVEL", "5"));   // boss is next

        Assert.True(altar.TryExecuteCommand("ADDSPAWN", "", null!));

        Assert.Equal(0, altar.SpawnChar!.CurrentCount);
        Assert.False(altar.Champion.ChampionSummoned.IsValid);
    }

    [Fact]
    public void AChampionAltarOnTheGroundStillSpawnsItsBoss()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res);
        Assert.True(altar.Champion!.TrySetProperty("LEVEL", "5"));

        Assert.True(altar.TryExecuteCommand("ADDSPAWN", "", null!));

        Assert.Equal(1, altar.SpawnChar!.CurrentCount);
        Assert.True(altar.Champion.ChampionSummoned.IsValid);
    }

    // ============================================================ COUNT

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ACreatureSpawnersCountIsItsMemberCount(int cap)
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_lo", cap);
        stone.SpawnChar!.RespawnNow();
        Assert.Equal(cap, stone.SpawnChar.CurrentCount);

        // ISPW_COUNT -> GetCurrentSpawned (CCSpawn.cpp:857), reached before the
        // item's own keys (CItem.cpp:2659).
        Assert.Equal(cap.ToString(), Read(stone, "COUNT"));
        // PILE/MORE2 on a creature spawner reads the same count (:883-886).
        Assert.Equal(cap.ToString(), Read(stone, "PILE"));

        world.DeleteObject(world.FindChar(stone.SpawnChar.SpawnedUids[0])!);
        Assert.Equal((cap - 1).ToString(), Read(stone, "COUNT"));
    }

    [Fact]
    public void AnItemSpawnersCountIsItsMemberCount()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_lo");
        stone.SpawnItem!.RespawnNow();

        Assert.Equal("1", Read(stone, "COUNT"));
    }

    [Fact]
    public void AnOrdinaryContainersCountIsStillItsContents()
    {
        var world = NewWorld();
        var box = world.CreateItem();
        box.ItemType = ItemType.Container;
        world.PlaceItem(box, new Point3D(50, 50, 0, 0));
        box.AddItem(world.CreateItem());
        box.AddItem(world.CreateItem());

        Assert.Equal("2", Read(box, "COUNT"));
    }

    // ============================================================ @AddObj timer

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheSecondsAddObjChoosesSurviveTheCreatureSpawnersTick(int cap)
    {
        // OnTickComponent arms the default schedule BEFORE generating (CCSpawn.cpp:690)
        // and AddObj applies ARGN1 last (:655). The tick used to re-arm (or park) the
        // timer after the spawn and threw the script's answer away.
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_lo", cap);
        stone.SpawnChar!.SetDelay(15, 15);
        SpawnComponent.OnSpawnTrigger = (_, trigger, args) =>
        {
            if (trigger == ItemTrigger.AddObj)
                args.N1 = 30;
            return TriggerResult.Default;
        };
        try
        {
            stone.SpawnChar.ForceSpawn();
            stone.SpawnChar.OnTick(Environment.TickCount64);
        }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Equal(1, stone.SpawnChar.CurrentCount);
        long remainingMs = stone.Timeout - Environment.TickCount64;
        Assert.InRange(remainingMs, 25_000, 31_000);
    }

    [Fact]
    public void WithoutAnAddObjAnswerTheTickStillParksAFullSpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_lo", 1);
        stone.SpawnChar!.ForceSpawn();
        stone.SpawnChar.OnTick(Environment.TickCount64);

        Assert.Equal(1, stone.SpawnChar.CurrentCount);
        Assert.True(stone.Timeout <= 0, $"a full spawner should be parked, found {stone.Timeout}");
    }

    [Fact]
    public void WithoutAnAddObjAnswerASpawnerWithRoomKeepsItsSchedule()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_lo", 2);
        stone.SpawnChar!.SetDelay(15, 15);
        stone.SpawnChar.ForceSpawn();
        stone.SpawnChar.OnTick(Environment.TickCount64);

        Assert.Equal(1, stone.SpawnChar.CurrentCount);
        long remainingMs = stone.Timeout - Environment.TickCount64;
        Assert.InRange(remainingMs, 14 * 60_000, 15 * 60_000 + 1000);
    }

    // ============================================================ membership order

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AddObjSeesTheMembershipWithoutTheNewCreature(int cap)
    {
        // @AddObj fires before the uid joins the list (CCSpawn.cpp:648 then :661).
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_lo", cap);
        var seenCounts = new List<int>();
        var seenScriptCounts = new List<string>();
        SpawnComponent.OnSpawnTrigger = (spawner, trigger, _) =>
        {
            if (trigger == ItemTrigger.AddObj)
            {
                seenCounts.Add(spawner.SpawnChar!.CurrentCount);
                seenScriptCounts.Add(Read(spawner, "COUNT"));
            }
            return TriggerResult.Default;
        };
        try { stone.SpawnChar!.RespawnNow(); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Equal(Enumerable.Range(0, cap).ToList(), seenCounts);
        Assert.Equal(Enumerable.Range(0, cap).Select(i => i.ToString()).ToList(), seenScriptCounts);
        Assert.Equal(cap, stone.SpawnChar.CurrentCount);
    }

    [Fact]
    public void ItemAddObjSeesTheMembershipWithoutTheNewItem()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_lo");
        int seen = -1;
        SpawnComponent.OnSpawnTrigger = (spawner, trigger, _) =>
        {
            if (trigger == ItemTrigger.AddObj)
                seen = spawner.SpawnItem!.CurrentCount;
            return TriggerResult.Default;
        };
        try { stone.SpawnItem!.RespawnNow(); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Equal(0, seen);
        Assert.Equal(1, stone.SpawnItem.CurrentCount);
    }

    [Fact]
    public void LiveAddObjOfACreatureAlsoFiresBeforeEnrolling()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_lo", 2);
        var loose = world.CreateCharacter();
        world.PlaceCharacter(loose, new Point3D(101, 100, 0, 0));
        int seen = -1;
        SpawnComponent.OnSpawnTrigger = (spawner, trigger, _) =>
        {
            if (trigger == ItemTrigger.AddObj)
                seen = spawner.SpawnChar!.CurrentCount;
            return TriggerResult.Default;
        };
        try { Assert.True(stone.TrySetProperty("ADDOBJ", $"0{loose.Uid.Value:X}")); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Equal(0, seen);
        Assert.Equal(1, stone.SpawnChar!.CurrentCount);
    }

    [Fact]
    public void ACreatureDeletedByAddObjIsNotEnrolled()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_lo", 1);
        SpawnComponent.OnSpawnTrigger = (_, trigger, args) =>
        {
            if (trigger == ItemTrigger.AddObj && args.SpawnedChar != null)
                world.DeleteObject(args.SpawnedChar);
            return TriggerResult.Default;
        };
        try
        {
            stone.SpawnChar!.ForceSpawn();
            stone.SpawnChar.OnTick(Environment.TickCount64);
        }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Empty(stone.SpawnChar.SpawnedUids);
    }

    // ============================================================ item spawner sleep

    [Fact]
    public void AnItemSpawnerInASleepingSectorRefillsAfterLosingAChild()
    {
        // DelObj sets CAN_O_NOSLEEP on any spawn point (CCSpawn.cpp:535) and AddObj
        // clears it once the quota is full (:665). The item side never set it, so the
        // re-armed deadline sat in a sleeping sector and the spawner stayed empty.
        var res = LoadResources();
        var world = NewWorld(2048);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.MaxHits = 100; player.Hits = 100;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(player);
        world.OnTick();

        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_lo",
            at: new Point3D(1200, 1200, 0, 0));
        stone.SpawnItem!.RespawnNow();
        Assert.Equal(1, stone.SpawnItem.CurrentCount);
        Assert.False(stone.NeverSleeps, "a full spawner may sleep with its sector");

        var child = world.FindItem(stone.SpawnItem.SpawnedUids[0])!;
        world.TryDeleteObject(child);
        Assert.Equal(0, stone.SpawnItem.CurrentCount);
        Assert.True(stone.NeverSleeps, "a spawner that owes an item keeps ticking");

        // Bring the re-armed deadline due and let the WORLD timer queue run it.
        stone.SetTimeout(Math.Max(1, Environment.TickCount64 - 1));
        world.OnTick();

        Assert.Equal(1, stone.SpawnItem.CurrentCount);
        Assert.False(stone.NeverSleeps, "the quota is full again, so it may sleep again");
    }

    [Fact]
    public void ACreatureSpawnerInASleepingSectorRefillsAfterLosingAChild()
    {
        var res = LoadResources();
        var world = NewWorld(2048);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.MaxHits = 100; player.Hits = 100;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(player);
        world.OnTick();

        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_lo",
            at: new Point3D(1200, 1200, 0, 0));
        stone.SpawnChar!.RespawnNow();
        var child = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;
        world.TryDeleteObject(child);
        Assert.True(stone.NeverSleeps);

        stone.SetTimeout(Math.Max(1, Environment.TickCount64 - 1));
        world.OnTick();

        Assert.Equal(1, stone.SpawnChar.CurrentCount);
    }

    // ============================================================ script order

    [Fact]
    public void TheDefinitionsOwnScriptRunsBeforeSpawnAndTheGeneralChainAfterAddObj()
    {
        // NPC_LoadScript(true) - CHARDEF @Create, then @NPCRestock - runs before @Spawn
        // (CCSpawn.cpp:415 -> CCharNPC.cpp:279-290); the TEVENTS/EVENTSPET @Create chain
        // runs after AddObj (NPC_CreateTrigger, CCSpawn.cpp:464-465).
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"spawn_order_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [EVENTS e_order_probe]
            ON=@Create
            TAG.GENERAL=1
            TAG.GENERAL_SAW_RESTOCK=<TAG0.RESTOCKED>

            [CHARDEF 033]
            DEFNAME=c_order_probe
            NAME=Order
            TEVENTS=e_order_probe
            ON=@Create
            TAG.EARLY=1
            TAG.EARLY_COUNT=<EVAL <TAG0.EARLY_COUNT>+1>
            ON=@NPCRestock
            TAG.RESTOCKED=1
            TAG.RESTOCK_COUNT=<EVAL <TAG0.RESTOCK_COUNT>+1>
            """);
        try
        {
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
            var world = TestHarness.CreateWorld();
            typeof(SphereNet.Server.Program).GetMethod("ConfigureNpcSpawnScripts",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [world, runtime.Dispatcher]);

            string? atSpawnEarly = null, atSpawnRestock = null, atSpawnGeneral = null;
            string? atAddObjGeneral = null;
            SpawnComponent.OnSpawnTrigger = (_, trigger, args) =>
            {
                var ch = args.SpawnedChar;
                if (ch == null) return TriggerResult.Default;
                if (trigger == ItemTrigger.Spawn)
                {
                    atSpawnEarly = ch.Tags.Get("EARLY");
                    atSpawnRestock = ch.Tags.Get("RESTOCKED");
                    atSpawnGeneral = ch.Tags.Get("GENERAL");
                }
                else if (trigger == ItemTrigger.AddObj)
                    atAddObjGeneral = ch.Tags.Get("GENERAL");
                return TriggerResult.Default;
            };

            var stone = world.CreateItem();
            stone.BaseId = 0x1F13;
            stone.ItemType = ItemType.SpawnChar;
            world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
            stone.SetTag("MORE1_DEFNAME", "c_order_probe");
            stone.Amount = 1;
            stone.InitializeSpawnComponent(world, runtime.Resources);
            stone.SpawnChar!.RespawnNow();
            var npc = world.FindChar(Assert.Single(stone.SpawnChar.SpawnedUids))!;

            Assert.Equal("1", atSpawnEarly);
            Assert.Equal("1", atSpawnRestock);
            Assert.Null(atSpawnGeneral);
            Assert.Null(atAddObjGeneral);
            Assert.Equal("1", npc.Tags.Get("GENERAL"));
            Assert.Equal("01", npc.Tags.Get("GENERAL_SAW_RESTOCK"));   // <TAG0.RESTOCKED> reads in Sphere hex
            // Each block exactly once.
            Assert.Equal("1", npc.Tags.Get("EARLY_COUNT"));
            Assert.Equal("1", npc.Tags.Get("RESTOCK_COUNT"));
        }
        finally
        {
            SpawnComponent.OnSpawnTrigger = null;
            SpawnComponent.OnNpcScriptInit = null;
            SpawnComponent.OnNpcCreateTrigger = null;
            File.Delete(path);
        }
    }
}
