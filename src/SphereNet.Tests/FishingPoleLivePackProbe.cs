using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Live-pack diagnostic for the "i_fishing_pole dclick does nothing" report.
/// Loads the REAL server script pack (skips when absent), materializes the pole
/// exactly like .add / restock does, and drives the full double-click pipeline.
/// Prints each stage so the failing link is visible.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class FishingPoleLivePackProbe
{
    private const string Root = @"C:\sphereNetServer";
    private readonly ITestOutputHelper _out;
    public FishingPoleLivePackProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void LivePack_FishingPole_DClick_EquipsAndTargets()
    {
        string scripts = Path.Combine(Root, "scripts");
        string mul = Path.Combine(Root, "mul");
        if (!Directory.Exists(scripts) || !File.Exists(Path.Combine(mul, "tiledata.mul")))
        {
            _out.WriteLine("live pack not available");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (string f in ScriptResourceManifest.Resolve(scripts))
            resources.LoadResourceFile(f);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var map = new SphereNet.MapData.MapDataManager(mul);
        map.Load();
        var tile = map.GetItemTileData(0x0DBF);
        _out.WriteLine($"tiledata 0x0DBF: flags={tile.Flags} quality(layer)={tile.Quality}");

        // --- Stage 1: def resolution ---
        var rid = resources.ResolveDefName("i_fishing_pole");
        _out.WriteLine($"i_fishing_pole rid: valid={rid.IsValid} type={rid.Type} index=0x{rid.Index:X}");
        var def = DefinitionLoader.GetItemDef(0x0DBF);
        _out.WriteLine($"itemdef 0x0DBF: {(def == null ? "NULL" : $"Type={def.Type} DispIndex=0x{def.DispIndex:X}")}");
        var defDupe = DefinitionLoader.GetItemDef(0x0DC0);
        _out.WriteLine($"itemdef 0x0DC0: {(defDupe == null ? "NULL" : $"Type={defDupe.Type} DispIndex=0x{defDupe.DispIndex:X}")}");

        // --- Stage 2: materialization (the .add / restock path) ---
        var world = TestHarness.CreateWorld();
        world.MapData = map;
        var accounts = new AccountManager(lf);
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(30_000, 40_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Prober";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);

        var pole = world.CreateItem();
        bool applied = ItemDefHelper.ApplyInstanceMetadata(pole, 0x0DBF);
        pack.AddItem(pole);
        _out.WriteLine($"materialized: applied={applied} BaseId=0x{pole.BaseId:X} ItemType={pole.ItemType} Name='{pole.Name}'");

        // --- Stage 3: full dclick pipeline WITH the live script trigger chain ---
        var interpreter = new SphereNet.Scripting.Execution.ScriptInterpreter(
            new SphereNet.Scripting.Expressions.ExpressionParser(),
            lf.CreateLogger<SphereNet.Scripting.Execution.ScriptInterpreter>());
        var runner = new SphereNet.Scripting.Execution.TriggerRunner(
            interpreter, resources, lf.CreateLogger<SphereNet.Scripting.Execution.TriggerRunner>());
        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.ScriptDebug = true;
        dispatcher.DebugLog = msg => _out.WriteLine(msg);
        var dclickResult = dispatcher.FireItemTrigger(pole, ItemTrigger.DClick,
            new SphereNet.Game.Scripting.TriggerArgs { CharSrc = player, ItemSrc = pole });
        _out.WriteLine($"script @DClick result: {dclickResult}");

        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);
        client.HandleDoubleClick(pole.Uid.Value);

        _out.WriteLine($"after dclick: IsEquipped={pole.IsEquipped} layer={pole.EquipLayer} pendingTarget={client.HasPendingTarget}");

        Assert.Equal(ItemType.FishPole, pole.ItemType);
        Assert.True(client.HasPendingTarget, "fishing target cursor must open");
        Assert.True(pole.IsEquipped, "pole must be armed on dclick (Source-X CClientUse dclick-equip)");
    }

    [Fact]
    public void LivePack_SpawnedOrc_HasStatsAndName()
    {
        string scripts = Path.Combine(Root, "scripts");
        if (!Directory.Exists(scripts))
        {
            _out.WriteLine("live pack not available");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (string f in ScriptResourceManifest.Resolve(scripts))
            resources.LoadResourceFile(f);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(40_000, 50_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Prober";
        player.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var interpreter = new SphereNet.Scripting.Execution.ScriptInterpreter(
            new SphereNet.Scripting.Expressions.ExpressionParser(),
            lf.CreateLogger<SphereNet.Scripting.Execution.ScriptInterpreter>());
        var runner = new SphereNet.Scripting.Execution.TriggerRunner(
            interpreter, resources, lf.CreateLogger<SphereNet.Scripting.Execution.TriggerRunner>());
        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher { Resources = resources, Runner = runner };
        client.SetEngines(triggerDispatcher: dispatcher);

        // Mirror Program.EngineWiring's OnNpcSpawned: spawned NPCs run @Create
        // (the block that assigns STR/DEX/INT in classic packs) and @CreateLoot.
        world.OnNpcSpawned = npc =>
        {
            dispatcher.FireCharTrigger(npc, CharTrigger.Create,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
            dispatcher.FireCharTrigger(npc, CharTrigger.CreateLoot,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
            // Same post-@Create vitals top-off as Program.EngineWiring — @Create
            // raises max vitals, current values must follow for a fresh spawn.
            npc.Hits = npc.MaxHits;
            npc.Stam = npc.MaxStam;
            npc.Mana = npc.MaxMana;
        };

        var rid = resources.ResolveDefName("c_orc");
        _out.WriteLine($"c_orc rid: valid={rid.IsValid} type={rid.Type} index=0x{rid.Index:X}");

        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnChar;
        world.PlaceItem(spawner, player.Position);
        spawner.SpawnChar = new SphereNet.Game.Components.SpawnComponent(spawner, world)
        {
            CharDefId = rid.Index,
            SpawnRange = 0,
            MaxCount = 1,
        };

        client.HandleDoubleClick(spawner.Uid.Value);
        Assert.Equal(1, spawner.SpawnChar.CurrentCount);
        var uid = Assert.Single(spawner.SpawnChar.SpawnedUids);
        var orc = world.FindChar(uid);
        Assert.NotNull(orc);

        _out.WriteLine($"spawned: name='{orc!.Name}' body=0x{orc.BodyId:X} str={orc.Str} dex={orc.Dex} int={orc.Int} " +
                       $"hits={orc.Hits}/{orc.MaxHits} fame={orc.Fame} karma={orc.Karma} brain={orc.NpcBrain}");

        // [CHARDEF 011] c_orc: NAME=#NAMES_ORC the Orc, @Create STR={96 120}.
        Assert.False(string.IsNullOrWhiteSpace(orc.Name), "spawned NPC must have a name");
        Assert.DoesNotContain("Spawn_", orc.Name);
        Assert.InRange((int)orc.Str, 96, 120);
        Assert.InRange((int)orc.Dex, 81, 105);
        Assert.InRange((int)orc.Int, 36, 60);
        Assert.True(orc.MaxHits > 1, "MaxHits must reflect @Create stats");
        Assert.Equal(orc.MaxHits, orc.Hits); // fresh spawn at full health
    }

    [Fact]
    public void LivePack_WorldgemBit_GmDClick_TogglesSpawn()
    {
        string scripts = Path.Combine(Root, "scripts");
        if (!Directory.Exists(scripts))
        {
            _out.WriteLine("live pack not available");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (string f in ScriptResourceManifest.Resolve(scripts))
            resources.LoadResourceFile(f);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(50_000, 60_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.Name = "Prober";
        gm.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, gm);

        var interpreter = new SphereNet.Scripting.Execution.ScriptInterpreter(
            new SphereNet.Scripting.Expressions.ExpressionParser(),
            lf.CreateLogger<SphereNet.Scripting.Execution.ScriptInterpreter>());
        var runner = new SphereNet.Scripting.Execution.TriggerRunner(
            interpreter, resources, lf.CreateLogger<SphereNet.Scripting.Execution.TriggerRunner>());
        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher { Resources = resources, Runner = runner };
        client.SetEngines(triggerDispatcher: dispatcher);

        // Mirror the live wiring: TYPE=t_spawn_char at materialization/runtime
        // must attach the spawn component (Program.EngineWiring).
        Item.OnSpawnTypeChanged = it => it.InitializeSpawnComponent(world, resources);
        try
        {
            var gem = world.CreateItem();
            bool applied = ItemDefHelper.ApplyInstanceMetadata(gem, 0x1EA7); // i_worldgem_bit
            var orcRid = resources.ResolveDefName("c_orc");
            gem.More1 = (uint)orcRid.Index;
            gem.Amount = 1;
            world.PlaceItem(gem, new Point3D(101, 100, 0, 0));
            if (gem.SpawnChar == null)
                gem.InitializeSpawnComponent(world, resources);

            _out.WriteLine($"gem: applied={applied} base=0x{gem.BaseId:X} type={gem.ItemType} " +
                           $"spawnComp={(gem.SpawnChar != null ? "yes" : "NULL")} more1=0x{gem.More1:X}");

            client.HandleDoubleClick(gem.Uid.Value);
            int count1 = gem.SpawnChar?.CurrentCount ?? -1;
            _out.WriteLine($"after dclick #1: children={count1}");

            // MOREZ=0 gem: the child must appear adjacent to the gem (Source-X
            // CCSpawn MoveNear dist 1), not scattered across a 15-tile radius.
            if (gem.SpawnChar != null && gem.SpawnChar.CurrentCount == 1)
            {
                var child = world.FindChar(gem.SpawnChar.SpawnedUids[0]);
                Assert.NotNull(child);
                int dist = gem.Position.GetDistanceTo(child!.Position);
                _out.WriteLine($"child at {child.Position}, distance={dist}");
                Assert.True(dist <= 1, $"MOREZ=0 spawn must be adjacent, was {dist} tiles away");
            }

            client.HandleDoubleClick(gem.Uid.Value);
            int count2 = gem.SpawnChar?.CurrentCount ?? -1;
            _out.WriteLine($"after dclick #2: children={count2}");

            Assert.Equal(ItemType.SpawnChar, gem.ItemType);
            Assert.NotNull(gem.SpawnChar);
            Assert.Equal(1, count1); // first dclick spawns
            Assert.Equal(0, count2); // second dclick clears
        }
        finally
        {
            Item.OnSpawnTypeChanged = null;
        }
    }

    [Fact]
    public void LiveSave_FishingPoleInstances_HaveFishPoleType()
    {
        string scripts = Path.Combine(Root, "scripts");
        string mul = Path.Combine(Root, "mul");
        string save = Path.Combine(Root, "save");
        if (!Directory.Exists(scripts) || !File.Exists(Path.Combine(mul, "tiledata.mul")) ||
            !File.Exists(Path.Combine(save, "sphereworld.scp")))
        {
            _out.WriteLine("live save not available");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (string f in ScriptResourceManifest.Resolve(scripts))
            resources.LoadResourceFile(f);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var map = new SphereNet.MapData.MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);

        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var loader = new SphereNet.Persistence.Load.WorldLoader(lf);
        loader.ResolveItemDef = defname =>
        {
            var rid2 = resources.ResolveDefName(defname);
            if (rid2.IsValid && rid2.Type == ResType.ItemDef)
            {
                var d = DefinitionLoader.GetItemDef(rid2.Index);
                return d != null && d.DispIndex > 0 ? d.DispIndex : (ushort)rid2.Index;
            }
            return 0;
        };
        loader.ResolveCharDef = defname =>
        {
            int idx = CharDefHelper.ResolveDefIndex(defname, resources);
            return idx != 0 ? CharDefHelper.ResolveBodyId(idx, resources) : (ushort)0;
        };
        loader.ApplyCharDefFromName = (ch, defname) => CharDefHelper.TryApplyDefName(ch, defname, resources);
        loader.ResolveEquipLayerFromTile = baseId => map.GetItemTileData(baseId).Quality;
        loader.Load(world, save);

        int total = 0, wrongType = 0;
        foreach (var it in world.GetAllObjects().OfType<Item>())
        {
            if (it.BaseId != 0x0DBF && it.BaseId != 0x0DC0) continue;
            total++;
            if (it.ItemType != ItemType.FishPole)
            {
                wrongType++;
                if (wrongType <= 10)
                    _out.WriteLine($"pole uid=0{it.Uid.Value:X} base=0x{it.BaseId:X} type={it.ItemType} name='{it.Name}' cont=0{it.ContainedIn.Value:X}");
            }
        }
        _out.WriteLine($"fishing poles in save: total={total} wrongType={wrongType}");

        Assert.True(total == 0 || wrongType == 0,
            $"{wrongType}/{total} live fishing poles resolve to a non-FishPole type");
    }
}
