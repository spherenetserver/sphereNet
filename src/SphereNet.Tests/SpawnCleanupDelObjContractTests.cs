using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A member the spawner only notices later - a creature flagged dead, an object that
/// was deleted or is missing - leaves through the same DelObj as any other member.
///
/// Upstream has one removal (CCSpawn::DelObj, CCSpawn.cpp:509): NOSLEEP goes on, a
/// parked timer is re-armed, the uid leaves the list, and only then does @DelObj run
/// with ARGN1 = the prepared seconds; whatever the script leaves in ARGN1 is the final
/// timer (:568-571). The sweep used to fire @DelObj from inside the list-removal
/// predicate, so the script saw the member still counted, a parked -1 instead of the
/// prepared delay, no NOSLEEP, and a -1 it wrote was re-armed by the sweep afterwards.
///
/// A world save is not a removal at all: CCSpawn::r_Write only skips uids whose object
/// no longer exists (CCSpawn.cpp:1132) and never fires a trigger or touches the timer.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnCleanupDelObjContractTests
{
    private readonly ITestOutputHelper _out;
    public SpawnCleanupDelObjContractTests(ITestOutputHelper output) => _out = output;

    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_cln
        TYPE=t_spawn_char

        [ITEMDEF 01ea7]
        DEFNAME=i_spawn_item_cln
        TYPE=t_spawn_item

        [ITEMDEF 0eed]
        DEFNAME=i_cln_coin

        [CHARDEF c_target_cln]
        DEFNAME=c_target_cln
        ID=0x27
        NAME=cleanup target
        """;

    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_cln_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        new SphereNet.Game.Definitions.DefinitionLoader(
            resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        return resources;
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 2048, 2048);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>A char spawner filled to its cap, so its timer is parked at -1.</summary>
    private static Item FullSpawner(GameWorld world, ResourceHolder res, int amount = 1)
    {
        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(1600, 1600, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_target_cln");
        stone.Amount = (ushort)amount;
        stone.InitializeSpawnComponent(world, res);
        for (int i = 0; i < amount; i++)
        {
            stone.SpawnChar!.ForceSpawn();
            stone.SpawnChar!.OnTick(Environment.TickCount64);
        }
        Assert.Equal(amount, stone.SpawnChar!.CurrentCount);
        Assert.True(stone.Timeout < 0, $"expected a parked timer, got {stone.Timeout}");
        Assert.False(stone.NeverSleeps);
        return stone;
    }

    private sealed class Seen
    {
        public int Calls;
        public int Count = -99;
        public long N1 = -99;
        public bool NoSleep;
    }

    /// <summary>Record what @DelObj is shown, then leave <paramref name="answer"/> in
    /// ARGN1 (null leaves it alone).</summary>
    private static Seen WatchDelObj(Item spawner, long? answer, Action? inside = null)
    {
        var seen = new Seen();
        SpawnComponent.OnSpawnTrigger = (item, trig, args) =>
        {
            if (trig != ItemTrigger.DelObj) return TriggerResult.Default;
            seen.Calls++;
            seen.Count = spawner.SpawnChar?.CurrentCount ?? CountItemMembers(spawner);
            seen.N1 = args.N1;
            seen.NoSleep = spawner.NeverSleeps;
            if (answer.HasValue) args.N1 = answer.Value;
            inside?.Invoke();
            return TriggerResult.Default;
        };
        return seen;
    }

    private static int CountItemMembers(Item spawner) => spawner.SpawnItem!.SpawnedUids.Count;

    private static long RemainingSec(Item spawner) =>
        (spawner.Timeout - Environment.TickCount64) / 1000;

    private static Character Member(GameWorld world, Item spawner, int i = 0) =>
        world.FindChar(spawner.SpawnChar!.SpawnedUids[i])!;

    // ---- tick path -------------------------------------------------------

    [Fact]
    public void ADeadMemberFoundOnTheTickSeesTheNormalDelObjState()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = FullSpawner(world, res);
        Member(world, spawner).SetStatFlag(StatFlag.Dead);
        var seen = WatchDelObj(spawner, answer: null);

        spawner.SpawnChar!.OnTick(Environment.TickCount64);
        _out.WriteLine($"calls={seen.Calls} count={seen.Count} n1={seen.N1} nosleep={seen.NoSleep} timer={spawner.Timeout}");

        Assert.Equal(1, seen.Calls);
        Assert.Equal(0, seen.Count);
        // TIMELO/TIMEHI default 15..30 minutes.
        Assert.InRange(seen.N1, 15 * 60 - 1, 30 * 60);
        Assert.True(seen.NoSleep);
        Assert.True(spawner.NeverSleeps);
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
        Assert.True(spawner.Timeout > 0);
    }

    [Fact]
    public void ADelObjAnswerOfMinusOneKeepsTheTimerParked()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = FullSpawner(world, res);
        Member(world, spawner).SetStatFlag(StatFlag.Dead);
        var seen = WatchDelObj(spawner, answer: -1);

        spawner.SpawnChar!.OnTick(Environment.TickCount64);
        _out.WriteLine($"calls={seen.Calls} timer={spawner.Timeout}");

        Assert.Equal(1, seen.Calls);
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
        Assert.True(spawner.Timeout < 0, $"the script parked the timer, the sweep re-armed it: {spawner.Timeout}");

        // A later tick must not re-arm it either: nothing more was lost.
        spawner.SpawnChar!.OnTick(Environment.TickCount64);
        Assert.True(spawner.Timeout < 0);
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
    }

    [Fact]
    public void ADelObjAnswerOfThirtySecondsIsKept()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = FullSpawner(world, res);
        Member(world, spawner).SetStatFlag(StatFlag.Dead);
        WatchDelObj(spawner, answer: 30);

        spawner.SpawnChar!.OnTick(Environment.TickCount64);

        Assert.InRange(RemainingSec(spawner), 28, 30);
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
    }

    [Fact]
    public void ADeletedOrMissingMemberLeavesThroughTheSameDelObj()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = FullSpawner(world, res, amount: 2);
        // One member deleted without telling the spawner, one live, and one uid that
        // resolves to nothing at all.
        var deleted = Member(world, spawner, 0);
        var live = Member(world, spawner, 1);
        deleted.CompleteDeletion();
        var missing = new Serial(0x0007FFF0);
        spawner.SpawnChar!.MaxCount = 3;
        spawner.SpawnChar!.RegisterExisting(missing);
        Assert.True(spawner.Timeout < 0);

        var counts = new List<int>();
        var n1s = new List<long>();
        SpawnComponent.OnSpawnTrigger = (_, trig, args) =>
        {
            if (trig == ItemTrigger.DelObj)
            {
                counts.Add(spawner.SpawnChar!.CurrentCount);
                n1s.Add(args.N1);
            }
            return TriggerResult.Default;
        };

        spawner.SpawnChar!.CleanupDead();
        _out.WriteLine($"counts={string.Join(",", counts)} n1={string.Join(",", n1s)}");

        // Upstream runs @DelObj whether or not the object is still there (:565).
        Assert.Equal(new[] { 2, 1 }, counts);
        Assert.All(n1s, n => Assert.True(n > 0, $"N1 {n}"));
        Assert.Equal(new[] { live.Uid }, spawner.SpawnChar!.SpawnedUids.ToArray());
        Assert.True(spawner.NeverSleeps);
    }

    [Fact]
    public void ACallbackThatRemovesAnotherMemberDoesNotBreakTheSweep()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = FullSpawner(world, res, amount: 2);
        var a = Member(world, spawner, 0);
        var b = Member(world, spawner, 1);
        a.SetStatFlag(StatFlag.Dead);
        b.SetStatFlag(StatFlag.Dead);

        int calls = 0;
        SpawnComponent.OnSpawnTrigger = (_, trig, _) =>
        {
            if (trig != ItemTrigger.DelObj) return TriggerResult.Default;
            calls++;
            // The script releases the other dead member itself.
            spawner.SpawnChar!.DelObj(b.Uid);
            return TriggerResult.Default;
        };

        spawner.SpawnChar!.CleanupDead();

        Assert.Equal(2, calls);           // once per member, never twice for b
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
    }

    [Fact]
    public void AStoppedSpawnerKeepsItsTimerParkedWhenItLosesAMember()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = FullSpawner(world, res);
        var child = Member(world, spawner);
        spawner.SpawnChar!.Stop();
        // A member re-linked onto a stopped spawner (load-time ADDOBJ), then lost.
        var other = world.CreateCharacter();
        world.PlaceCharacter(other, new Point3D(1601, 1600, 0, 0));
        spawner.SpawnChar!.RegisterExisting(other.Uid);
        other.SetStatFlag(StatFlag.Dead);
        WatchDelObj(spawner, answer: null);

        spawner.SpawnChar!.CleanupDead();

        Assert.True(child.IsDeleted);
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
        Assert.True(spawner.Timeout < 0, $"a stopped spawner was re-armed: {spawner.Timeout}");
    }

    // ---- save path -------------------------------------------------------

    [Fact]
    public void AWorldSaveRunsNoDelObjAndLeavesTheTimerAlone()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = FullSpawner(world, res);
        var child = Member(world, spawner);
        child.SetStatFlag(StatFlag.Dead);
        var seen = WatchDelObj(spawner, answer: null);

        string dir = Path.Combine(Path.GetTempPath(), $"spn_cln_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(new WorldSaver(LoggerFactory.Create(_ => { })).Save(world, dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        _out.WriteLine($"save: calls={seen.Calls} members={spawner.SpawnChar!.CurrentCount} timer={spawner.Timeout}");
        Assert.Equal(0, seen.Calls);
        Assert.Equal(1, spawner.SpawnChar!.CurrentCount);
        Assert.True(spawner.Timeout < 0);

        // The tick that follows removes it with the full contract.
        spawner.SpawnChar!.OnTick(Environment.TickCount64);
        Assert.Equal(1, seen.Calls);
        Assert.Equal(0, seen.Count);
        Assert.True(seen.N1 > 0);
        Assert.True(seen.NoSleep);
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
    }

    [Fact]
    public void AWorldSaveWritesNoAddObjForAMemberThatNoLongerExists()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = FullSpawner(world, res);
        var live = Member(world, spawner);
        var missing = new Serial(0x0007FFF1);
        spawner.SpawnChar!.MaxCount = 2;
        spawner.SpawnChar!.RegisterExisting(missing);
        int calls = 0;
        SpawnComponent.OnSpawnTrigger = (_, trig, _) =>
        {
            if (trig == ItemTrigger.DelObj) calls++;
            return TriggerResult.Default;
        };

        string dir = Path.Combine(Path.GetTempPath(), $"spn_cln_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(new WorldSaver(LoggerFactory.Create(_ => { })).Save(world, dir));
            string text = string.Join("\n", Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            Assert.Contains($"ADDOBJ=0{live.Uid.Value:x}", text);
            Assert.DoesNotContain($"ADDOBJ=0{missing.Value:x}", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Assert.Equal(0, calls);
        Assert.Equal(2, spawner.SpawnChar!.CurrentCount);
    }

    // ---- item spawner ----------------------------------------------------

    [Fact]
    public void AnItemSpawnerSweepSharesTheSameDelObjContract()
    {
        LoadResources();
        var world = NewWorld();
        var stone = world.CreateItem();
        stone.BaseId = 0x1EA7;
        stone.ItemType = ItemType.SpawnItem;
        world.PlaceItem(stone, new Point3D(1500, 1500, 0, 0));
        var spawn = new ItemSpawnComponent(stone, world) { MaxCount = 1 };
        var coin = world.CreateItem();
        coin.BaseId = 0x0EED;
        world.PlaceItem(coin, new Point3D(1501, 1500, 0, 0));
        Assert.True(spawn.AddObj(coin.Uid));
        Assert.True(stone.Timeout < 0);

        var counts = new List<int>();
        long seenN1 = -99;
        bool noSleep = false;
        SpawnComponent.OnSpawnTrigger = (_, trig, args) =>
        {
            if (trig != ItemTrigger.DelObj) return TriggerResult.Default;
            counts.Add(spawn.SpawnedUids.Count);
            seenN1 = args.N1;
            noSleep = stone.NeverSleeps;
            args.N1 = -1;
            return TriggerResult.Default;
        };

        coin.CompleteDeletion();    // gone without telling the spawner
        spawn.OnTick(Environment.TickCount64);

        Assert.Equal(new[] { 0 }, counts);
        Assert.True(seenN1 > 0, $"N1 {seenN1}");
        Assert.True(noSleep);
        Assert.Empty(spawn.SpawnedUids);
        Assert.True(stone.Timeout < 0, $"the script parked the timer: {stone.Timeout}");
    }
}
