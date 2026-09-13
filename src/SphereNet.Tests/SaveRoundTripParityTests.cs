using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Save, load, save again - and the two saves must say the same thing (port plan İŞ-3 /
/// PLAN-107).
///
/// This is the check that catches the class of bug no single assertion looks for: a
/// field that is written in one shape and read back in another drifts a little on every
/// save cycle, and nothing notices until the numbers are wrong. The equipped-bonus pool
/// that compounded on every duplicate (review 13J) was exactly that shape, and so is
/// every legacy key translated on the way in - if the translation is not stable, the
/// second save is not the first.
///
/// Two comparisons: a world built through the engine's own API, and a world loaded from
/// a CLASSIC record (the shapes a 0.56 shard writes), which has to reach a fixed point
/// in one cycle.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SaveRoundTripParityTests : IDisposable
{
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;
    private readonly ITestOutputHelper _out;
    private readonly List<string> _dirs = [];

    private const string Defs = """
        [ITEMDEF 01000]
        DEFNAME=i_token_rt
        NAME=Token
        TYPE=t_normal

        [ITEMDEF 0e75]
        DEFNAME=i_box_rt
        NAME=Box
        TYPE=t_container

        [ITEMDEF 01517]
        DEFNAME=i_shirt_rt
        NAME=Shirt
        TYPE=t_clothing
        LAYER=5

        [ITEMDEF 03eb2]
        NAME=Ship side
        TYPE=t_ship_side_locked

        [ITEMDEF 0ed4]
        DEFNAME=i_stone_rt
        NAME=Guildstone
        TYPE=t_stone_guild

        [MULTIDEF 05b]
        DEFNAME=m_ship_rt
        NAME=Test ship
        TYPE=t_ship

        [SKILL 54]
        DEFNAME=Skill_Sailormanship
        KEY=Sailormanship

        [CHARDEF 0190]
        DEFNAME=c_man_rt
        NAME=Man
        """;

    public SaveRoundTripParityTests(ITestOutputHelper output)
    {
        _out = output;
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_rt_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Defs);
        _resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose()
    {
        File.Delete(_scriptPath);
        foreach (string dir in _dirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private GameWorld NewWorld()
    {
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_rtd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private SphereNet.Persistence.Load.WorldLoader NewLoader()
    {
        var loader = new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }));
        loader.ResolveItemDef = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };
        loader.ResolveItemDefFullIndex = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
        };
        loader.ResolveMultiDef = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            if (!rid.IsValid || rid.Type != ResType.MultiDef || rid.Index is < 0 or > ushort.MaxValue)
                return null;
            string typeName = "";
            var link = _resources.GetResource(rid);
            if (link?.StoredKeys != null)
            {
                foreach (var key in link.StoredKeys)
                {
                    if (key.Key.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
                    {
                        typeName = key.Arg.Trim();
                        break;
                    }
                }
            }
            return ((ushort)rid.Index, SphereNet.Scripting.Definitions.ItemDef.ParseTypeName(typeName));
        };
        loader.ApplyCharDefFromName = (ch, defname) =>
            CharDefHelper.TryApplyDefName(ch, defname, _resources);
        return loader;
    }

    private void Save(GameWorld world, string dir) =>
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, dir);

    // ---------------------------------------------------------------- comparison

    /// <summary>Every record in a save directory: section header + serial -> the lines
    /// under it, in order. Comment lines carry the save index and the wall clock, so
    /// they are not part of what the save SAYS.</summary>
    private static Dictionary<string, List<string>> ReadRecords(string dir)
    {
        var records = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(dir, "*.scp").OrderBy(f => f))
        {
            string name = Path.GetFileName(file);
            string section = "";
            var lines = new List<string>();
            string key = "";

            void Flush()
            {
                if (section.Length == 0) return;
                string serial = lines.FirstOrDefault(l => l.StartsWith("SERIAL=", StringComparison.OrdinalIgnoreCase))
                                ?? $"#{records.Count}";
                key = $"{name}|{section}|{serial}";
                records[key] = [.. lines.OrderBy(l => l, StringComparer.Ordinal)];
            }

            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith('['))
                {
                    Flush();
                    section = line;
                    lines = [];
                    continue;
                }
                lines.Add(NormalizeClock(line));
            }
            Flush();
        }
        return records;
    }

    /// <summary>A clock keeps running between two saves, so what such a line SAYS is
    /// its key, not its value: the world time, a countdown timer and a character's age
    /// are all expected to have moved on.</summary>
    private static string NormalizeClock(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return line;
        string key = line[..eq].ToUpperInvariant();
        return key is "TIME" or "TIMER" or "TIMERMS" or "CREATE" or "TIMERD"
            ? $"{key}=<clock>"
            : line;
    }

    /// <summary>What the second save says that the first did not, and the other way
    /// round. Empty means the save reached a fixed point.</summary>
    /// <summary>What the server does after the loader returns
    /// (Program.WorldBootstrap.cs:177): a spawn item gets its component, which is what
    /// rebuilds the member list from the ADDOBJ records the save carries. Without this
    /// step the reloaded world holds a spawner that has forgotten its creature, and
    /// the second save writes one ADDOBJ line fewer - a difference in the harness, not
    /// in the engine. A round trip measured in a configuration nobody runs answers a
    /// question nobody asked.</summary>
    private static void FinishLoadLikeTheServer(GameWorld world)
    {
        foreach (var item in world.GetAllObjects().OfType<Item>().ToArray())
        {
            if (item.IsDeleted) continue;
            if (item.ItemType is not (ItemType.SpawnChar or ItemType.SpawnChampion or ItemType.SpawnItem))
                continue;
            item.InitializeSpawnComponent(world, null, item.Timeout);
        }
    }

    private List<string> Differences(string firstDir, string secondDir)
    {
        var first = ReadRecords(firstDir);
        var second = ReadRecords(secondDir);
        var diffs = new List<string>();

        foreach (var (key, lines) in first)
        {
            if (!second.TryGetValue(key, out var other))
            {
                diffs.Add($"LOST record {key}");
                continue;
            }
            foreach (string line in lines.Except(other, StringComparer.Ordinal))
                diffs.Add($"LOST {key}: {line}");
            foreach (string line in other.Except(lines, StringComparer.Ordinal))
                diffs.Add($"GAINED {key}: {line}");
        }
        foreach (string key in second.Keys.Except(first.Keys, StringComparer.OrdinalIgnoreCase))
            diffs.Add($"GAINED record {key}");

        foreach (string d in diffs.Take(40))
            _out.WriteLine(d);
        return diffs;
    }

    // ---------------------------------------------------------------- worlds

    private void BuildEngineWorld(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Rounder";
        ch.Str = 90; ch.Dex = 70; ch.Int = 50;
        ch.MaxHits = 100; ch.Hits = 87;
        ch.MaxMana = 60; ch.Mana = 41;
        ch.MaxStam = 80; ch.Stam = 33;
        ch.Fame = 1200; ch.Karma = -400;
        ch.Kills = 3;
        ch.ResFire = 25; ch.ResPoison = 10;
        ch.SetSkill(SkillType.Anatomy, 755);
        ch.SetSkill(SkillType.Spellweaving, 421);   // the pack calls it Sailormanship
        ch.SetTag("QUEST_STEP", "4");
        ch.Events.Add(ResourceId.FromString("e_test_event", ResType.Events));
        world.PlaceCharacter(ch, new Point3D(60, 60, 0, 0));

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        ch.Backpack = pack;

        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        shirt.Hue = new Color(0x0456);
        shirt.SetTag("BONUSHITSMAX", "20");
        ch.Equip(shirt, Layer.Shirt);

        var stack = world.CreateItem();
        stack.BaseId = 0x1000;
        stack.Amount = 17;
        stack.Name = "Renamed token";
        pack.AddItem(stack);
        stack.Position = new Point3D(21, 35, 0, 0);

        var innerBox = world.CreateItem();
        innerBox.BaseId = 0x0E75;
        innerBox.ItemType = ItemType.Container;
        pack.AddItem(innerBox);
        innerBox.Position = new Point3D(40, 50, 0, 0);

        var deep = world.CreateItem();
        deep.BaseId = 0x1000;
        deep.SetAttr(ObjAttributes.Newbie);
        deep.SetTimeout(Environment.TickCount64 + 60_000);
        innerBox.AddItem(deep);
        deep.Position = new Point3D(60, 70, 0, 0);

        var ground = world.CreateItem();
        ground.BaseId = 0x1000;
        ground.More1 = 0x1234;
        ground.More2 = 0x5678;
        ground.Link = ch.Uid;
        world.PlaceItem(ground, new Point3D(62, 60, 0, 0));

        // PLAN-107 names six things this cycle has to hold, and a whole-file
        // comparison proves nothing about a shape the world does not contain. Base
        // stats, amount, owner/parent and timers are above; these two were missing.

        // Spawn membership. The member list is written as ADDOBJ records and rebuilt
        // from them on load, so a spawner holding a live creature is the only way to
        // exercise that translation - and İŞ-55 showed how much rides on it.
        var spawner = world.CreateItem();
        spawner.BaseId = 0x1F13;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.Amount = 3;
        world.PlaceItem(spawner, new Point3D(64, 60, 0, 0));
        spawner.InitializeSpawnComponent(world, null);

        var spawned = world.CreateCharacter();
        spawned.BaseId = 0x0190;
        spawned.BodyId = 0x0190;
        spawned.Name = "Spawned";
        // A brain, because the spawn path gives one: a creature it produces with none
        // is defaulted to Monster (SpawnComponents.cs:314) and the load-time relink
        // repairs the same state the same way (Item.cs, RelinkSpawnedChildrenFromTag).
        // Handing the spawner a brainless creature builds a state the engine never
        // produces, and the round trip then reports the REPAIR as drift - NPC=0 on the
        // first save, NPC=8 on the second. The engine is right there; the fixture was
        // not.
        spawned.NpcBrain = NpcBrainType.Monster;
        world.PlaceCharacter(spawned, new Point3D(65, 60, 0, 0));
        spawner.SpawnChar?.AddObj(spawned.Uid);

        // Dynamic vendor stock. A vendor's sale layers hold items the shard restocked
        // rather than items a script placed, and they are written from the live
        // container - the shape most likely to come back in a different order or with
        // a different count.
        var vendor = world.CreateCharacter();
        vendor.BaseId = 0x0190;
        vendor.BodyId = 0x0190;
        vendor.Name = "Vendor";
        world.PlaceCharacter(vendor, new Point3D(66, 60, 0, 0));

        var stockBox = world.CreateItem();
        stockBox.BaseId = 0x0E75;
        stockBox.ItemType = ItemType.Container;
        vendor.Equip(stockBox, Layer.VendorExtra);

        var forSale = world.CreateItem();
        forSale.BaseId = 0x1000;
        forSale.Amount = 42;
        forSale.Price = 137;
        forSale.Name = "Stocked goods";
        stockBox.AddItem(forSale);
        forSale.Position = new Point3D(11, 12, 0, 0);
    }

    // ================================================================ İŞ-3

    [Fact]
    public void AWorldSavedTwiceSaysTheSameThingBothTimes()
    {
        var world = NewWorld();
        BuildEngineWorld(world);

        string first = NewDir();
        Save(world, first);

        var reloaded = NewWorld();
        NewLoader().Load(reloaded, first);
        FinishLoadLikeTheServer(reloaded);

        string second = NewDir();
        Save(reloaded, second);

        var diffs = Differences(first, second);
        Assert.True(diffs.Count == 0, $"the second save differs in {diffs.Count} places");
    }

    /// <summary>The field-level half of PLAN-107, and it is not the same check.
    ///
    /// Comparing two saves catches DRIFT - a field written in one shape and read back
    /// in another. It cannot catch OMISSION: a field never written at all produces two
    /// identical files and a green test. Deleting the spawn-member write from the
    /// saver leaves the fixed-point comparison perfectly happy.
    ///
    /// So the values are asserted on the far side of the cycle, for each of the six
    /// things the plan names.</summary>
    [Fact]
    public void EveryFieldThePlanNamesComesBackWithItsValue()
    {
        var world = NewWorld();
        BuildEngineWorld(world);

        string dir = NewDir();
        Save(world, dir);

        var reloaded = NewWorld();
        NewLoader().Load(reloaded, dir);
        FinishLoadLikeTheServer(reloaded);

        // 1. base stats - the BASE pool, not the effective one the suit inflates
        var player = reloaded.GetAllObjects().OfType<Character>()
            .Single(c => !c.IsDeleted && c.Name == "Rounder");
        _out.WriteLine($"player: str={player.Str} hits={player.Hits}/{player.BaseMaxHits} " +
                       $"fame={player.Fame} karma={player.Karma} kills={player.Kills}");
        Assert.Equal(90, player.Str);
        Assert.Equal(100, player.BaseMaxHits);
        Assert.Equal(87, player.Hits);
        Assert.Equal(1200, player.Fame);
        Assert.Equal(-400, player.Karma);
        Assert.Equal(755, player.GetSkill(SkillType.Anatomy));

        // 2. amount, and 3. owner/parent - the stack is still inside the pack
        var pack = player.GetEquippedItem(Layer.Pack);
        Assert.NotNull(pack);
        var stack = pack!.Contents.Single(i => i.Name == "Renamed token");
        Assert.Equal(17, stack.Amount);
        Assert.Equal(pack.Uid, stack.ContainedIn);
        Assert.Equal(player.Uid, pack.ContainedIn);

        // 4. spawn membership - the record the fixed-point test cannot vouch for
        var spawner = reloaded.GetAllObjects().OfType<Item>()
            .Single(i => !i.IsDeleted && i.ItemType == ItemType.SpawnChar);
        _out.WriteLine($"spawner holds {spawner.SpawnChar?.SpawnedUids.Count ?? -1} member(s)");
        Assert.Equal(1, spawner.SpawnChar?.SpawnedUids.Count);
        var member = reloaded.FindChar(spawner.SpawnChar!.SpawnedUids[0]);
        Assert.NotNull(member);
        Assert.Equal("Spawned", member!.Name);

        // 5. timers - a countdown keeps running, so what has to survive is that one is
        //    still armed, not the exact number
        var deep = reloaded.GetAllObjects().OfType<Item>()
            .Single(i => !i.IsDeleted && i.IsAttr(ObjAttributes.Newbie));
        _out.WriteLine($"timed item timeout={deep.Timeout}");
        Assert.True(deep.Timeout > 0, "the scheduled timer was lost across the cycle");

        // 6. dynamic vendor contents
        var vendor = reloaded.GetAllObjects().OfType<Character>()
            .Single(c => !c.IsDeleted && c.Name == "Vendor");
        var stockBox = vendor.GetEquippedItem(Layer.VendorExtra);
        Assert.NotNull(stockBox);
        var goods = stockBox!.Contents.Single();
        _out.WriteLine($"vendor stock: {goods.Name} x{goods.Amount} @ {goods.Price}");
        Assert.Equal("Stocked goods", goods.Name);
        Assert.Equal(42, goods.Amount);
        Assert.Equal(137, goods.Price);
    }

    [Fact]
    public void AClassicRecordReachesAFixedPointInOneCycle()
    {
        // The shapes a 0.56 shard writes, all of which are translated on the way in.
        string classic = NewDir();
        File.WriteAllText(Path.Combine(classic, "sphereworld.scp"), """
            [WORLDITEM i_stone_rt]
            SERIAL=04000200
            NAME=Round Table
            P=70,70,0
            ALIGN=1
            ABBREV=RT
            CHARTER0=We stand together
            MEMBER=0f9e6,Lord,2,0f9e6,1,0,50

            [WORLDITEM m_ship_rt]
            SERIAL=04000201
            P=72,70,0
            REGION.FLAGS=0c0
            REGION.TAG.owner=0f9e6
            HATCH=04000203
            PLANK=04000202

            [WORLDITEM 03eb2]
            SERIAL=04000202
            P=73,70,0

            [WORLDITEM i_box_rt]
            SERIAL=04000203
            P=74,70,0
            """);
        File.WriteAllText(Path.Combine(classic, "spherechars.scp"), """
            [WORLDCHAR c_man_rt]
            SERIAL=0f9e6
            NAME=Stigma
            P=71,70,0
            KILLSPLAYER=5
            KILLSNPC=12
            Sailormanship=1000
            Anatomy=507
            """);

        var world = NewWorld();
        NewLoader().Load(world, classic);

        string first = NewDir();
        Save(world, first);

        var reloaded = NewWorld();
        NewLoader().Load(reloaded, first);
        FinishLoadLikeTheServer(reloaded);

        string second = NewDir();
        Save(reloaded, second);

        // A save that lost everything would also be a fixed point, so first check that
        // the translations ARE in it - one line per thing translated on the way in.
        string written = string.Concat(Directory.EnumerateFiles(first, "*.scp").Select(File.ReadAllText));
        Assert.Contains("GUILD.MEMBERS", written);         // the classic stone's roster
        Assert.Contains("GUILD.ABBREV", written);
        Assert.Contains("SHIP.HOLD", written);             // the ship's hold uid
        Assert.Contains("SHIP.PLANKS", written);
        Assert.Contains("REGION.TAG.OWNER", written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KILLS=5", written);               // the murder count
        Assert.Contains("Spellweaving=1000", written);     // the pack calls it Sailormanship
        Assert.Contains("KILLSNPC", written);              // kept as script-readable data

        // The first save is the translation; from there on nothing may drift.
        var diffs = Differences(first, second);
        Assert.True(diffs.Count == 0, $"the second save differs in {diffs.Count} places");
    }
}
