using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Two ways a loaded world grew NPCs it should not have.
///
/// 1. Sphere 56T custom-version compatibility: a 0.56 save does not list a char
///    spawner's members on the spawner. Each spawned creature wears an i_memory on
///    LAYER_SPECIAL with COLOR=0200 (MEMORY_ISPAWNED) and LINK=the spawn item; the
///    spawner only keeps the count in MORE2. Read only through ADDOBJ, every such
///    spawner loaded empty and spawned its quota again beside the creatures already
///    there - every NPC came out twice. Source-X hands the memory's wearer to the
///    spawner and drops the memory (CItemMemory::FixWeirdness, CItemMemory.cpp:112).
///
/// 2. A summoned guard's linger deadline lived only in an in-memory table, so the
///    guards alive at a world save came back after a restart with no deadline and
///    never left. Upstream it is the timer of a saved summon memory (CCharFight.cpp:281).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LegacySpawnMemoryAndGuardLingerTests : IDisposable
{
    private readonly List<string> _dirs = [];
    private readonly Dictionary<FieldInfo, object?> _saved = new();

    public void Dispose()
    {
        foreach (var (field, value) in _saved) field.SetValue(null, value);
        SummonedGuardTable().Clear();
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"spn_legacyspawn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static ResourceHolder EmptyResources() =>
        new(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());

    // ------------------------------------------------------------ spawn memory

    /// <summary>A spawner and its one creature as a 0.56T world file writes them.</summary>
    private const string Spawn56TWorld = """
        TITLE=Sphere World Script
        VERSION=0.56T-Release

        [WORLDITEM i_worldgem_bit]
        SERIAL=04000054a
        COLOR=020
        TIMER=12
        ATTR=0b0
        MORE1=c_spider_giant
        MORE2=01
        MOREP=4,6,2
        P=5233,993

        [WORLDCHAR c_spider_giant]
        CREATE=011f
        SERIAL=02dea3
        TIMER=0
        NPC=10
        HOMEDIST=2
        P=5234,993,-1
        DIR=7
        FLAGS=031000000
        HOME=5233,993
        OSTR=78
        HITS=78

        [WORLDITEM i_memory]
        SERIAL=04002de36
        COLOR=0200
        LINK=04000054a
        ATTR=04
        MORE2=01836d278
        MOREP=-1,-1
        LAYER=30
        CONT=02dea3

        [EOF]
        """;

    private static WorldLoader Loader()
    {
        var loader = new WorldLoader(LoggerFactory.Create(_ => { }))
        {
            ResolveItemDef = name => name.ToLowerInvariant() switch
            {
                "i_worldgem_bit" => (ushort)0x1EA7,
                "i_memory" => (ushort)0x2007,
                _ => (ushort)0,
            },
            ResolveCharDef = name => name.Equals("c_spider_giant", StringComparison.OrdinalIgnoreCase)
                ? (ushort)0x1C : (ushort)0,
        };
        return loader;
    }

    /// <summary>What the boot does after the load: the spawner gets its component
    /// (its TYPE comes from the itemdef there), then the legacy memories are read.</summary>
    private static Item BootSpawner(GameWorld world)
    {
        var spawner = world.FindItem(new Serial(0x4000054a))!;
        Assert.NotNull(spawner);
        spawner.ItemType = ItemType.SpawnChar;
        spawner.InitializeSpawnComponent(world, EmptyResources());
        spawner.SpawnChar!.CharDefId = 0x0190; // a creature the test world can make
        return spawner;
    }

    private GameWorld Load56TSpawnWorld()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "sphereworld.scp"), Spawn56TWorld);
        var world = TestHarness.CreateWorld();
        Loader().Load(world, dir);
        return world;
    }

    [Fact]
    public void TheLoadedSpawnMemoryIsWhereTheMembershipLives()
    {
        var world = Load56TSpawnWorld();
        var spider = world.FindChar(new Serial(0x2dea3))!;
        Assert.NotNull(spider);

        var mem = world.FindItem(new Serial(0x4002de36))!;
        Assert.NotNull(mem);
        Assert.True(mem.IsEquipped);
        Assert.Equal(Layer.Special, mem.EquipLayer);
        Assert.Equal(spider.Uid, mem.ContainedIn);
        Assert.Equal(new Serial(0x4000054a), mem.Link);
        Assert.Equal(MemoryType.ISpawned, (MemoryType)mem.Hue.Value);

        // The spawner itself names no member: ADDOBJ is a Source-X record.
        var spawner = BootSpawner(world);
        Assert.Equal(0, spawner.SpawnChar!.CurrentCount);
    }

    [Fact]
    public void AdoptingTheMemoryFillsTheSpawner_SoItDoesNotSpawnASecondCreature()
    {
        var world = Load56TSpawnWorld();
        var spawner = BootSpawner(world);
        var spider = world.FindChar(new Serial(0x2dea3))!;

        Assert.Equal(1, Item.AdoptLegacySpawnMemories(world));

        Assert.Equal(1, spawner.SpawnChar!.CurrentCount);
        Assert.Contains(spider.Uid, spawner.SpawnChar.SpawnedUids);
        Assert.True(spider.IsStatFlag(StatFlag.Spawned));
        Assert.True(spider.TryGetTag("SPAWNITEM", out string? back));
        Assert.Equal(0x4000054au, Convert.ToUInt32(back!.TrimStart('0'), 16));
        // The memory has done its job and is gone, as upstream removes it.
        Assert.Null(world.FindItem(new Serial(0x4002de36)));
        Assert.Null(spider.GetEquippedItem(Layer.Special));

        int npcs = world.GetAllCharactersSnapshot().Count(c => !c.IsPlayer && !c.IsDeleted);
        // Even forced well past its timer, a full spawner adds nobody.
        spawner.SpawnChar.OnTick(long.MaxValue);
        Assert.Equal(npcs, world.GetAllCharactersSnapshot().Count(c => !c.IsPlayer && !c.IsDeleted));
        Assert.Equal(1, spawner.SpawnChar.CurrentCount);
    }

    [Fact]
    public void WithoutTheMemoryTheSpawnerWouldHaveDoubledTheCreature()
    {
        // The failure itself: the same load, the memory left unread.
        var world = Load56TSpawnWorld();
        var spawner = BootSpawner(world);

        spawner.SpawnChar!.OnTick(long.MaxValue);

        Assert.Equal(1, spawner.SpawnChar.CurrentCount);
        Assert.Equal(2, world.GetAllCharactersSnapshot().Count(c => !c.IsPlayer && !c.IsDeleted));
    }

    [Fact]
    public void TheSpawnMemoryIsFoundEvenBehindAnotherMemory()
    {
        // The creature wears a second memory saved ahead of the spawn memory; the one
        // LAYER_SPECIAL slot is taken when the spawn memory arrives.
        const string otherMemory = """
            [WORLDITEM i_memory]
            SERIAL=04002de30
            COLOR=02
            LINK=0123
            LAYER=30
            CONT=02dea3

            [WORLDITEM i_memory]
            SERIAL=04002de36
            """;
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "sphereworld.scp"),
            Spawn56TWorld.Replace("[WORLDITEM i_memory]\r\nSERIAL=04002de36", otherMemory.ReplaceLineEndings("\r\n"))
                         .Replace("[WORLDITEM i_memory]\nSERIAL=04002de36", otherMemory.ReplaceLineEndings("\n")));
        var world = TestHarness.CreateWorld();
        Loader().Load(world, dir);
        Assert.NotNull(world.FindItem(new Serial(0x4002de30)));
        var spawner = BootSpawner(world);

        Assert.Equal(1, Item.AdoptLegacySpawnMemories(world));
        Assert.Equal(1, spawner.SpawnChar!.CurrentCount);
        Assert.Null(world.FindItem(new Serial(0x4002de36)));
        // The other memory is still worn on LAYER_SPECIAL beside it - never displaced.
        var other = world.FindItem(new Serial(0x4002de30))!;
        var spider = world.FindChar(new Serial(0x2dea3))!;
        Assert.False(other.IsDeleted);
        Assert.True(other.IsEquipped);
        Assert.Equal(spider.Uid, other.ContainedIn);
        Assert.Contains(other, spider.Memories);
    }

    private GameWorld LoadWorldText(string text)
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "sphereworld.scp"), text);
        var world = TestHarness.CreateWorld();
        Loader().Load(world, dir);
        return world;
    }

    /// <summary>One creature wearing three memories saved as item records, the way
    /// Source-X and 0.56 write them.</summary>
    private const string ThreeMemoriesWorld = """
        [WORLDCHAR c_spider_giant]
        SERIAL=0f08e
        NPC=1
        P=1000,1000,0

        [WORLDITEM i_memory]
        SERIAL=04000dc73
        COLOR=040
        LINK=09191
        ATTR=04
        LAYER=30
        CONT=0f08e

        [WORLDITEM i_memory]
        SERIAL=0400136b3
        COLOR=040
        LINK=0123ae
        ATTR=04
        LAYER=30
        CONT=0f08e

        [WORLDITEM i_memory]
        SERIAL=04000f03e
        COLOR=02
        LINK=0123af
        ATTR=04
        LAYER=30
        CONT=0f08e

        [EOF]
        """;

    [Fact]
    public void LayerSpecialHoldsEveryLoadedMemory_AndTheyRoundTripAsItemRecords()
    {
        var world = LoadWorldText(ThreeMemoriesWorld);
        var npc = world.FindChar(new Serial(0xf08e))!;
        uint[] uids = [0x4000dc73, 0x400136b3, 0x4000f03e];

        foreach (uint uid in uids)
        {
            var mem = world.FindItem(new Serial(uid))!;
            Assert.Contains(mem, npc.Memories);
            Assert.True(mem.IsEquipped);
            Assert.Equal(Layer.Special, mem.EquipLayer);
            Assert.Equal(npc.Uid, mem.ContainedIn);
        }
        // Nothing went to the ground or into a pack.
        Assert.Null(npc.GetEquippedItem(Layer.Special));
        Assert.DoesNotContain(world.GetAllObjects().OfType<Item>(), i => !i.ContainedIn.IsValid && i.BaseId == 0x2007);
        // MEMORYFIND by link and by type, FINDLAYER on the memory layer.
        Assert.Same(world.FindItem(new Serial(0x400136b3)), npc.Memory_FindObj(new Serial(0x123ae)));
        Assert.Same(world.FindItem(new Serial(0x4000f03e)), npc.Memory_FindTypes(MemoryType.IPet));
        Assert.NotNull(npc.FindLayer(Layer.Special));
        Assert.DoesNotContain(SphereNet.Game.Diagnostics.WorldInvariantAuditor.Audit(world),
            a => a.Kind == SphereNet.Game.Diagnostics.WorldInvariantAuditor.Kind.CharacterLimboItem);

        // Saved as item records (CONT + LAYER=30), not also as MEMORY lines.
        string outDir = TempDir();
        var lf = LoggerFactory.Create(_ => { });
        Assert.True(new WorldSaver(lf).Save(world, outDir));
        var reloaded = TestHarness.CreateWorld();
        new WorldLoader(lf).Load(reloaded, outDir);
        var back = reloaded.FindChar(npc.Uid)!;
        Assert.Equal(3, back.Memories.Count);
        foreach (uint uid in uids)
            Assert.Contains(reloaded.FindItem(new Serial(uid))!, back.Memories);
    }

    [Fact]
    public void EquippingASecondMemoryKeepsTheFirst()
    {
        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(1000, 1000, 0, 0));
        var first = world.CreateItem();
        first.BaseId = 0x2007;
        first.ItemType = ItemType.EqMemoryObj;
        first.Hue = new Color((ushort)MemoryType.Speak);
        var second = world.CreateItem();
        second.BaseId = 0x2007;
        second.ItemType = ItemType.EqMemoryObj;
        second.Hue = new Color((ushort)MemoryType.Speak);

        Assert.True(npc.Equip(first, Layer.Special));
        Assert.True(npc.Equip(second, Layer.Special));

        Assert.Contains(first, npc.Memories);
        Assert.Contains(second, npc.Memories);
        Assert.True(first.IsEquipped);
        Assert.Equal(npc.Uid, first.ContainedIn);

        // Moving one to the ground takes it off the memory layer and nothing else.
        Assert.True(world.PlaceItem(first, new Point3D(1001, 1000, 0, 0)));
        Assert.DoesNotContain(first, npc.Memories);
        Assert.Contains(second, npc.Memories);
        Assert.False(first.IsEquipped);
    }

    [Fact]
    public void ASpawnerAlreadyFullFromAddObjRefusesTheLegacyMember_LikeCCSpawnAddObj()
    {
        // What a save re-written by the build without adoption holds: the copy it
        // spawned is the spawner's ADDOBJ member, and the original creature still
        // wears its 0.56 spawn memory.
        string text = Spawn56TWorld
            .Replace("MOREP=4,6,2\r\n", "MOREP=4,6,2\r\nADDOBJ=03ad41\r\n")
            .Replace("MOREP=4,6,2\n", "MOREP=4,6,2\nADDOBJ=03ad41\n")
            .Replace("[EOF]", """
                [WORLDCHAR c_spider_giant]
                SERIAL=03ad41
                NPC=10
                P=5232,993,0

                [EOF]
                """);
        var world = LoadWorldText(text);
        var copy = world.FindChar(new Serial(0x3ad41))!;
        var original = world.FindChar(new Serial(0x2dea3))!;
        Assert.NotNull(copy);
        var spawner = BootSpawner(world);
        Assert.Equal(1, spawner.SpawnChar!.CurrentCount); // ADDOBJ filled AMOUNT 1

        Assert.Equal(0, Item.AdoptLegacySpawnMemories(world));

        // AddObj refused (list at max(AMOUNT,1)); the memory is gone all the same and
        // the original lives on as an ordinary NPC with no spawner.
        Assert.Equal(1, spawner.SpawnChar.CurrentCount);
        Assert.Contains(copy.Uid, spawner.SpawnChar.SpawnedUids);
        Assert.DoesNotContain(original.Uid, spawner.SpawnChar.SpawnedUids);
        Assert.False(original.IsDeleted);
        Assert.False(original.TryGetTag("SPAWNITEM", out _));
        Assert.Null(world.FindItem(new Serial(0x4002de36)));

        // Its 0.56 FLAGS still say spawned; CChar::FixWeirdness clears that when the
        // spawn link leads nowhere. The member keeps it.
        Assert.True(original.IsStatFlag(StatFlag.Spawned));
        Assert.Equal(1, Item.ClearSpawnedFlagWithoutSpawner(world));
        Assert.False(original.IsStatFlag(StatFlag.Spawned));
        Assert.True(copy.IsStatFlag(StatFlag.Spawned));

        // RESPAWN tops the spawner up: it is full, so nobody is added or removed.
        int npcs = world.GetAllCharactersSnapshot().Count(c => !c.IsDeleted);
        world.RespawnAllSpawners();
        Assert.Equal(npcs, world.GetAllCharactersSnapshot().Count(c => !c.IsDeleted));
        // RESPAWN FULL rebuilds the spawner and sweeps creatures still marked as
        // spawn children; the unlinked original carries no marker and stays.
        world.ResetAllSpawners();
        Assert.False(original.IsDeleted);
        Assert.True(copy.IsDeleted);
        Assert.Equal(1, spawner.SpawnChar.CurrentCount);
    }

    [Fact]
    public void AddObjPastTheAmountIsRefusedOnLoad_AndTheExtraStaysAsSaved()
    {
        var world = TestHarness.CreateWorld();
        var spawner = world.CreateItem();
        spawner.BaseId = 0x1EA7;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.More1 = 0x0190;
        // No AMOUNT: the capacity is max(AMOUNT, 1) = 1.
        world.PlaceItem(spawner, new Point3D(1000, 1000, 0, 0));
        var first = world.CreateCharacter();
        var extra = world.CreateCharacter();
        world.PlaceCharacter(first, new Point3D(1001, 1000, 0, 0));
        world.PlaceCharacter(extra, new Point3D(999, 1000, 0, 0));
        // As a save that over-accumulated writes them: both back-linked and flagged.
        foreach (var ch in new[] { first, extra })
        {
            ch.SetStatFlag(StatFlag.Spawned);
            ch.SetTag("SPAWNITEM", $"0{spawner.Uid.Value:x8}");
        }
        spawner.SetTag("ADDOBJ", $"0{first.Uid.Value:x},0{extra.Uid.Value:x}");

        spawner.InitializeSpawnComponent(world, EmptyResources());

        Assert.Equal(1, spawner.SpawnChar!.CurrentCount);
        Assert.Contains(first.Uid, spawner.SpawnChar.SpawnedUids);
        Assert.DoesNotContain(extra.Uid, spawner.SpawnChar.SpawnedUids);
        // Refused, not touched: its saved link still leads to a spawner, so like
        // CChar::FixWeirdness (GetSpawn() finds one) the flag is kept.
        Assert.False(extra.IsDeleted);
        Assert.Equal(0, Item.ClearSpawnedFlagWithoutSpawner(world));
        Assert.True(extra.IsStatFlag(StatFlag.Spawned));
        // Its death is no loss to the spawner (DelObj ignores a non-member).
        world.DeleteObject(extra);
        Assert.Equal(1, spawner.SpawnChar.CurrentCount);
    }

    [Fact]
    public void AChampionAltarTakesEveryAddObjMember()
    {
        var world = TestHarness.CreateWorld();
        var altar = world.CreateItem();
        altar.ItemType = ItemType.SpawnChampion;
        world.PlaceItem(altar, new Point3D(1000, 1000, 0, 0));
        var a = world.CreateCharacter();
        var b = world.CreateCharacter();
        world.PlaceCharacter(a, new Point3D(1001, 1000, 0, 0));
        world.PlaceCharacter(b, new Point3D(999, 1000, 0, 0));
        altar.SetTag("ADDOBJ", $"0{a.Uid.Value:x},0{b.Uid.Value:x}");

        altar.InitializeSpawnComponent(world, EmptyResources());

        Assert.Equal(2, altar.SpawnChar!.CurrentCount);
    }

    [Fact]
    public void AMemoryWhoseSpawnerIsGoneIsDropped_AndTheCreatureStays()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "sphereworld.scp"),
            Spawn56TWorld.Replace("LINK=04000054a", "LINK=04000ffff"));
        var world = TestHarness.CreateWorld();
        Loader().Load(world, dir);
        var spawner = BootSpawner(world);

        Assert.Equal(0, Item.AdoptLegacySpawnMemories(world));

        Assert.Null(world.FindItem(new Serial(0x4002de36)));
        Assert.NotNull(world.FindChar(new Serial(0x2dea3)));
        Assert.Equal(0, spawner.SpawnChar!.CurrentCount);
    }

    [Fact]
    public void AnOrdinaryMemoryOrAColouredItemIsNotASpawnLink()
    {
        var world = TestHarness.CreateWorld();
        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnChar;
        world.PlaceItem(spawner, new Point3D(1000, 1000, 0, 0));
        spawner.InitializeSpawnComponent(world, EmptyResources());

        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(1001, 1000, 0, 0));
        // A fight memory linked at the spawner's uid: no ISPAWNED bit.
        var fight = world.CreateItem();
        fight.BaseId = 0x2007;
        fight.Hue = new Color((ushort)MemoryType.Fight);
        fight.Link = spawner.Uid;
        npc.Equip(fight, Layer.Special);
        // A worn cloak dyed 0x0200: not a memory object at all.
        var cloak = world.CreateItem();
        cloak.BaseId = 0x1515;
        cloak.Hue = new Color(0x0200);
        cloak.Link = spawner.Uid;
        npc.Equip(cloak, Layer.Cape);

        Assert.Equal(0, Item.AdoptLegacySpawnMemories(world));
        Assert.Equal(0, spawner.SpawnChar!.CurrentCount);
        Assert.False(fight.IsDeleted);
        Assert.False(cloak.IsDeleted);
    }

    [Fact]
    public void AfterAdoptionTheNextSaveCarriesTheMembershipAsAddObj()
    {
        var world = Load56TSpawnWorld();
        var spawner = BootSpawner(world);
        Item.AdoptLegacySpawnMemories(world);

        string outDir = TempDir();
        var lf = LoggerFactory.Create(_ => { });
        Assert.True(new WorldSaver(lf).Save(world, outDir));

        var reloaded = TestHarness.CreateWorld();
        new WorldLoader(lf).Load(reloaded, outDir);
        var gem = reloaded.FindItem(spawner.Uid)!;
        gem.ItemType = ItemType.SpawnChar;
        gem.InitializeSpawnComponent(reloaded, EmptyResources());

        Assert.Equal(1, gem.SpawnChar!.CurrentCount);
        Assert.Equal(0, Item.AdoptLegacySpawnMemories(reloaded));
    }

    // ------------------------------------------------------------ guard linger

    private const string GuardPack = """
        [DEFNAME probe_guard_defs]
        guards { c_linger_guard 1 }

        [CHARDEF c_linger_guard]
        ID=0190
        NAME=linger guard
        """;

    private void SetServer(string name, object? value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!_saved.ContainsKey(field))
            _saved.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static Dictionary<Serial, long> SummonedGuardTable() =>
        (Dictionary<Serial, long>)typeof(SphereNet.Server.Program)
            .GetField("_summonedGuardExpiry", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static object? InvokeServer(string method, params object[] args) =>
        typeof(SphereNet.Server.Program)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args);

    /// <summary>A guarded town, a caller and a criminal, with the server wired to
    /// <paramref name="world"/>; returns the summoned guard.</summary>
    private Character SummonGuard(ScriptRuntimeStack stack, GameWorld world)
    {
        SetServer("_triggerDispatcher", stack.Dispatcher);
        SetServer("_npcAI", new NpcAI(world, new SphereConfig()));
        SetServer("_world", world);
        SetServer("_resources", stack.Resources);
        SetServer("_config", new SphereConfig());
        SetServer("_log", NullLogger.Instance);
        SetServer("_recordingEngine", new SphereNet.Game.Recording.RecordingEngine(Path.Combine(TempDir(), "rec")));
        ((Dictionary<uint, long>)typeof(SphereNet.Server.Program)
            .GetField("_lastCallGuards", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!).Clear();
        SummonedGuardTable().Clear();

        var town = new Region { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        town.AddRect(0, 0, 1000, 1000);
        world.AddRegion(town);

        var caller = world.CreateCharacter();
        caller.IsPlayer = true;
        world.PlaceCharacter(caller, new Point3D(100, 100, 0, 0));
        var criminal = world.CreateCharacter();
        criminal.IsPlayer = true;
        criminal.MaxHits = criminal.Hits = 100;
        criminal.SetStatFlag(StatFlag.Criminal);
        world.PlaceCharacter(criminal, new Point3D(102, 100, 0, 0));

        Assert.True((bool)InvokeServer("CallGuards", caller, criminal)!);
        return Assert.Single(world.GetAllCharactersSnapshot(), c => c.TryGetTag("IS_CITY_GUARD", out _));
    }

    [Fact]
    public void ASavedSummonedGuardKeepsItsLinger_AndLeavesWhenItRunsOut()
    {
        string pack = Path.Combine(TempDir(), "guards.scp");
        File.WriteAllText(pack, GuardPack);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        try
        {
            stack.Resources.LoadResourceFile(pack);
            new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();
            var world = TestHarness.CreateWorld();
            var guard = SummonGuard(stack, world);
            Assert.True(SummonedGuardTable().ContainsKey(guard.Uid));
            // The summon effect upstream marks the guard STATF_CONJURED (CCharSpell.cpp:1217).
            Assert.True(guard.IsStatFlag(StatFlag.Conjured));

            string saveDir = TempDir();
            var lf = LoggerFactory.Create(_ => { });
            Assert.True(new WorldSaver(lf).Save(world, saveDir));
            string chars = string.Concat(Directory.GetFiles(saveDir, "spherechars*")
                .Select(File.ReadAllText));
            // The deadline is written as time still to go, not as this uptime's tick.
            Assert.Contains("TAG.GUARD_EXPIRE_REMAINING=", chars);
            Assert.DoesNotContain("TAG.GUARD_EXPIRE_AT=", chars);

            // A restart: a fresh world and an empty expiry table.
            var reloaded = TestHarness.CreateWorld();
            new WorldLoader(lf).Load(reloaded, saveDir);
            SetServer("_world", reloaded);
            SummonedGuardTable().Clear();
            var back = reloaded.FindChar(guard.Uid)!;
            Assert.NotNull(back);

            long before = Environment.TickCount64;
            Assert.Equal(1, (int)InvokeServer("RegisterLoadedSummonedGuards")!);
            long expireAt = SummonedGuardTable()[back.Uid];
            // GUARDLINGER is 3 minutes; the save was taken right after the summon.
            Assert.InRange(expireAt - before, 3 * 60_000L - 5_000, 3 * 60_000L + 1_000);

            InvokeServer("CleanupSummonedGuards", expireAt - 1);
            Assert.False(back.IsDeleted);
            InvokeServer("CleanupSummonedGuards", expireAt);
            Assert.True(back.IsDeleted);
        }
        finally
        {
            stack.LoggerFactory.Dispose();
        }
    }

    [Fact]
    public void AGuardSavedByAnOlderBuildWithAnAbsoluteDeadlineLeavesAtOnce()
    {
        var world = TestHarness.CreateWorld();
        SetServer("_world", world);
        SetServer("_log", NullLogger.Instance);
        SetServer("_recordingEngine", new SphereNet.Game.Recording.RecordingEngine(Path.Combine(TempDir(), "rec")));
        SummonedGuardTable().Clear();

        // What an older save holds: the guard markers and an uptime tick.
        var stale = world.CreateCharacter();
        stale.NpcBrain = NpcBrainType.Guard;
        stale.SetTag("IS_CITY_GUARD", "1");
        stale.SetTag("GUARD_EXPIRE_AT", "987654321");
        world.PlaceCharacter(stale, new Point3D(100, 100, 0, 0));
        // A scripted town guard with no summon markers is not touched.
        var posted = world.CreateCharacter();
        posted.NpcBrain = NpcBrainType.Guard;
        world.PlaceCharacter(posted, new Point3D(101, 100, 0, 0));

        Assert.Equal(1, (int)InvokeServer("RegisterLoadedSummonedGuards")!);
        InvokeServer("CleanupSummonedGuards", Environment.TickCount64);

        Assert.True(stale.IsDeleted);
        Assert.False(posted.IsDeleted);
    }
}
