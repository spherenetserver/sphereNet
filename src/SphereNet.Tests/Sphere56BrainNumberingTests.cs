using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Sphere 56T custom-version compatibility: a 0.56-family save numbers the NPC brains
/// NONE, ANIMAL, HUMAN, HEALER, GUARD, BANKER, VENDOR, BEGGAR, STABLE, THIEF, MONSTER,
/// BERSERK, UNDEAD, DRAGON, VENDOR_OFFDUTY. Source-X casts NPC= straight onto its own
/// shorter enum, which read MONSTER=10 as DRAGON and UNDEAD=12 as nothing at all. The
/// loader reads such a file (by its VERSION header) by meaning; this engine's own saves
/// and Source-X saves are read as they are.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class Sphere56BrainNumberingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;

    private const string Header56 = """
        TITLE=Sphere World Script
        VERSION=0.56T-Release
        PREVBUILD=906
        TIME=406248879
        SAVECOUNT=44765

        """;

    public Sphere56BrainNumberingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_brain56_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _scriptPath = Path.Combine(_dir, "pack.scp");
        _resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private GameWorld NewWorld(string pack = "[DEFNAME test_nothing]\ntest_nothing 1\n", int width = 256, int height = 256)
    {
        File.WriteAllText(_scriptPath, pack);
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, width, height);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static string Record(uint serial, string npc, int karma = -5000) => $"""
        [WORLDCHAR]
        SERIAL=0{serial:x}
        NPC={npc}
        P=100,{100 + (serial & 0xff)},0
        OKARMA={karma}

        """;

    private string WriteSave(string subdir, string header, params string[] records)
    {
        string dir = Path.Combine(_dir, subdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sphereworld.scp"), header + string.Concat(records) + "[EOF]\n");
        return dir;
    }

    private static WorldLoader Loader() => new(LoggerFactory.Create(_ => { }));

    private static Character Char(GameWorld world, uint serial)
    {
        var ch = world.FindChar(new Serial(serial));
        Assert.NotNull(ch);
        return ch!;
    }

    [Fact]
    public void A056SaveReadsItsBrainsByMeaning()
    {
        var world = NewWorld();
        string dir = WriteSave("s56", Header56,
            Record(0x101, "10"), Record(0x102, "12"), Record(0x103, "13"),
            Record(0x104, "11"), Record(0x105, "8"), Record(0x106, "7"),
            Record(0x107, "9"), Record(0x108, "14"), Record(0x109, "6"),
            Record(0x10a, "1"), Record(0x10b, "2"), Record(0x10c, "4"));

        Loader().Load(world, dir);

        Assert.Equal(NpcBrainType.Monster, Char(world, 0x101).NpcBrain);   // MONSTER=10
        Assert.Equal(NpcBrainType.Monster, Char(world, 0x102).NpcBrain);   // UNDEAD=12
        Assert.Equal(NpcBrainType.Dragon, Char(world, 0x103).NpcBrain);    // DRAGON=13
        Assert.Equal(NpcBrainType.Berserk, Char(world, 0x104).NpcBrain);   // BERSERK=11
        Assert.Equal(NpcBrainType.Stable, Char(world, 0x105).NpcBrain);    // STABLE=8
        Assert.Equal(NpcBrainType.Human, Char(world, 0x106).NpcBrain);     // BEGGAR=7
        Assert.Equal(NpcBrainType.Human, Char(world, 0x107).NpcBrain);     // THIEF=9
        Assert.Equal(NpcBrainType.Vendor, Char(world, 0x108).NpcBrain);    // VENDOR_OFFDUTY=14
        Assert.Equal(NpcBrainType.Vendor, Char(world, 0x109).NpcBrain);
        Assert.Equal(NpcBrainType.Animal, Char(world, 0x10a).NpcBrain);
        Assert.Equal(NpcBrainType.Human, Char(world, 0x10b).NpcBrain);
        Assert.Equal(NpcBrainType.Guard, Char(world, 0x10c).NpcBrain);
    }

    [Fact]
    public void ItsMonstersAndUndeadAreEvilAndDoNotBreathe()
    {
        var world = NewWorld();
        string dir = WriteSave("s56evil", Header56, Record(0x201, "10"), Record(0x202, "12"));

        Loader().Load(world, dir);

        foreach (uint serial in new uint[] { 0x201, 0x202 })
        {
            var npc = Char(world, serial);
            Assert.NotEqual(NpcBrainType.Dragon, npc.NpcBrain);
            Assert.True(npc.NpcBrain < NpcBrainType.Qty);
            // An evil creature hates every player outside a guarded town
            // (NPC_GetHostilityLevelToward), so it attacks.
            Assert.True(NpcAI.NotoIsEvil(npc, world));
        }
    }

    [Fact]
    public void ABrainWrittenByNameStillLoadsInA056Save()
    {
        var world = NewWorld();
        string dir = WriteSave("s56name", Header56,
            Record(0x301, "brain_undead"), Record(0x302, "brain_monster"), Record(0x303, "brain_thief"));

        Loader().Load(world, dir);

        Assert.Equal(NpcBrainType.Monster, Char(world, 0x301).NpcBrain);
        Assert.Equal(NpcBrainType.Monster, Char(world, 0x302).NpcBrain);
        Assert.Equal(NpcBrainType.Human, Char(world, 0x303).NpcBrain);
    }

    [Fact]
    public void ASaveWithoutA056HeaderIsReadAsItIs()
    {
        var world = NewWorld();
        // This engine's own text save: comment header, no VERSION.
        string own = WriteSave("own", "// SphereNet World Items Save\n",
            Record(0x401, "10"), Record(0x402, "8"));
        Loader().Load(world, own);
        Assert.Equal(NpcBrainType.Dragon, Char(world, 0x401).NpcBrain);
        Assert.Equal(NpcBrainType.Monster, Char(world, 0x402).NpcBrain);

        // A Source-X save writes its numeric build id.
        var world2 = NewWorld();
        string sx = WriteSave("sx", "TITLE=SphereServer World Script\nVERSION=110\n\n",
            Record(0x403, "10"), Record(0x404, "7"));
        Loader().Load(world2, sx);
        Assert.Equal(NpcBrainType.Dragon, Char(world2, 0x403).NpcBrain);
        Assert.Equal(NpcBrainType.Stable, Char(world2, 0x404).NpcBrain);
    }

    [Fact]
    public void SavingAfterLoadingA056SaveWritesThisEnginesNumbering()
    {
        var world = NewWorld();
        string dir = WriteSave("s56rt", Header56, Record(0x501, "10"), Record(0x502, "12"), Record(0x503, "13"));
        Loader().Load(world, dir);

        string outDir = Path.Combine(_dir, "resaved");
        Directory.CreateDirectory(outDir);
        Assert.True(new WorldSaver(LoggerFactory.Create(_ => { })).Save(world, outDir));

        var reloaded = NewWorld();
        Loader().Load(reloaded, outDir);

        Assert.Equal(NpcBrainType.Monster, Char(reloaded, 0x501).NpcBrain);
        Assert.Equal(NpcBrainType.Monster, Char(reloaded, 0x502).NpcBrain);
        Assert.Equal(NpcBrainType.Dragon, Char(reloaded, 0x503).NpcBrain);

        // And the file holds this engine's numbers, so nothing is translated twice.
        string text = string.Concat(Directory.GetFiles(outDir, "*.scp", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("VERSION=0.56", text);
        Assert.Contains("NPC=" + (int)NpcBrainType.Monster, text);
        Assert.DoesNotContain("NPC=12", text);
    }

    [Fact]
    public void A056PacksOwnBrainNumbersWin()
    {
        // A 0.56-numbered pack (brain_monster is not this engine's 8) that put its
        // undead brain on a number of its own.
        var world = NewWorld("""
            [DEFNAME brains]
            brain_human = 2
            brain_monster = 10
            brain_undead = 20
            brain_dragon = 13
            """);
        string dir = WriteSave("s56pack", Header56, Record(0x601, "20"), Record(0x602, "10"), Record(0x603, "13"));

        Loader().Load(world, dir);

        Assert.Equal(NpcBrainType.Monster, Char(world, 0x601).NpcBrain);
        Assert.Equal(NpcBrainType.Monster, Char(world, 0x602).NpcBrain);
        Assert.Equal(NpcBrainType.Dragon, Char(world, 0x603).NpcBrain);
    }

    [Fact]
    public void ASourceXNumberedPackDoesNotOverrideThe056Table()
    {
        var world = NewWorld("""
            [DEFNAME brains]
            brain_monster 8
            brain_berserk 9
            brain_dragon 10
            """);
        string dir = WriteSave("s56sxpack", Header56, Record(0x701, "10"), Record(0x702, "9"));

        Loader().Load(world, dir);

        Assert.Equal(NpcBrainType.Monster, Char(world, 0x701).NpcBrain);
        Assert.Equal(NpcBrainType.Human, Char(world, 0x702).NpcBrain);
    }

    /// <summary>Real data: a 0.56T-Release save. Every NPC=10/NPC=12 creature comes back
    /// a monster and only the NPC=13 ones as dragons. Skips when the save is absent.</summary>
    [Fact]
    public void ARealSphere56TSaveLoadsItsMonstersAsMonsters()
    {
        const string saveDir = @"C:\56T\save";
        string[] files = [Path.Combine(saveDir, "sphereworld.scp"), Path.Combine(saveDir, "spherechars.scp")];
        if (Gate.Missing(null, "56T save", !files.All(File.Exists)))
            return;

        int Count(string value) => files.Sum(f => File.ReadLines(f).Count(l => l.Trim() == "NPC=" + value));
        int monsters = Count("10") + Count("12");
        int dragons = Count("13");

        var world = NewWorld(width: 7168, height: 4096);
        Loader().Load(world, saveDir);
        var npcs = world.GetAllCharactersSnapshot().Where(c => !c.IsPlayer && !c.IsDeleted).ToList();

        Assert.All(npcs, c => Assert.True(c.NpcBrain < NpcBrainType.Qty));
        Assert.Equal(dragons, npcs.Count(c => c.NpcBrain == NpcBrainType.Dragon));
        Assert.True(npcs.Count(c => c.NpcBrain == NpcBrainType.Monster) >= monsters);
    }

    [Theory]
    [InlineData("0.56T-Release", true)]
    [InlineData("0.56T", true)]
    [InlineData("0.56b", true)]
    [InlineData("0.56a", true)]
    [InlineData("0.55i", true)]
    [InlineData("0.56c", false)]
    [InlineData("0.56d", false)]
    [InlineData("110", false)]
    [InlineData("X1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheVersionDecidesTheNumbering(string? version, bool legacy) =>
        Assert.Equal(legacy, WorldLoader.UsesLegacy056BrainNumbering(version));

    [Theory]
    [InlineData("brain_undead", NpcBrainType.Monster)]
    [InlineData("brain_thief", NpcBrainType.Human)]
    [InlineData("brain_beggar", NpcBrainType.Human)]
    [InlineData("brain_vendor_offduty", NpcBrainType.Vendor)]
    [InlineData("brain_beserk", NpcBrainType.Berserk)]
    [InlineData("brain_animal_trainer", NpcBrainType.Stable)]
    [InlineData("brain_stable", NpcBrainType.Stable)]
    [InlineData("brain_dragon", NpcBrainType.Dragon)]
    public void RetiredBrainNamesResolveByMeaning(string name, NpcBrainType expected)
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        Assert.True(ch.TrySetProperty("NPC", name));
        Assert.Equal(expected, ch.NpcBrain);

        // The same name in a 56T pack's own [CHARDEF] block.
        var def = new SphereNet.Scripting.Definitions.CharDef(ResourceId.Invalid);
        def.LoadFromKey("NPC", name);
        Assert.Equal(expected, def.NpcBrain);
    }

    [Fact]
    public void A056StorageLayerIsNotReadAsASpellLayer()
    {
        var world = NewWorld();
        string dir = WriteSave("s56layer", Header56, Record(0x801, "2") + """
            [WORLDITEM]
            SERIAL=040000801
            ID=0e75
            TYPE=t_container
            LAYER=80
            CONT=0801

            """);

        Loader().Load(world, dir);

        var box = world.FindItem(new Serial(0x40000801));
        Assert.NotNull(box);
        Assert.False(box!.IsDeleted);
        // Never on Source-X's LAYER_SPELL_Explosion (80).
        Assert.NotEqual((Layer)WorldLoader.Legacy056StorageLayer, box.EquipLayer);
    }
}

/// <summary>
/// Sphere 56T custom-version compatibility, script side: a 0.56-numbered pack defines
/// brain_monster=10, brain_undead=12... Scripts compare &lt;NPC&gt; against those defs and
/// write NPC=&lt;def&gt;, so the script-facing NPC key speaks the pack's numbering while
/// the engine keeps the Source-X brain underneath and saves write this engine's numbers.
/// A Source-X-numbered pack changes nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class Sphere56PackBrainScriptTests
{
    private const string Functions = """

        [FUNCTION f_brain_is_monster]
        IF (<NPC> == <DEF.brain_monster>)
            RETURN 1
        ENDIF
        RETURN 2

        [FUNCTION f_brain_is_dragon]
        IF (<NPC> == <DEF.brain_dragon>)
            RETURN 1
        ENDIF
        RETURN 2

        [FUNCTION f_brain_set_dragon]
        NPC=<DEF.brain_dragon>
        RETURN 1

        [FUNCTION f_brain_set_monster]
        NPC=<DEF.brain_monster>
        RETURN 1
        """;

    private const string Pack056 = """
        [DEFNAME brains]
        brain_none = 0
        brain_animal = 1
        brain_human = 2
        brain_healer = 3
        brain_guard = 4
        brain_banker = 5
        brain_vendor = 6
        brain_beggar = 7
        brain_stable = 8
        brain_thief = 9
        brain_monster = 10
        brain_berserk = 11
        brain_undead = 12
        brain_dragon = 13
        brain_vendor_offduty = 14
        brain_beserk = brain_berserk
        """ + Functions;

    private const string PackSourceX = """
        [DEFNAME brains]
        brain_none             0
        brain_animal           1
        brain_human            2
        brain_healer           3
        brain_guard            4
        brain_banker           5
        brain_vendor           6
        brain_animal_trainer   7
        brain_monster          8
        brain_berserk          9
        brain_dragon           10
        """ + Functions;

    private static (ScriptRuntimeStack Runtime, GameWorld World, string Dir) Boot(string pack)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_brainpack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "pack.scp");
        File.WriteAllText(file, pack);
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        runtime.Resources.LoadResourceFile(file);
        ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
        // <DEF.name> as the server answers it for a numeric DEFNAME.
        runtime.Interpreter.ServerPropertyResolver = p =>
            p.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) &&
            runtime.Resources.TryResolveDefNameValue(p[4..], out long v) ? v.ToString() : null;
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return (runtime, world, dir);
    }

    private static long? Run(ScriptRuntimeStack runtime, Character ch, string function)
    {
        Assert.True(runtime.Runner.TryRunFunctionNumeric(function, ch, null,
            new SphereNet.Scripting.Execution.TriggerArgs(), out long? value));
        return value;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AScriptComparesNpcWithThePacksBrainDef(bool legacyPack)
    {
        var (runtime, world, dir) = Boot(legacyPack ? Pack056 : PackSourceX);
        try
        {
            var ch = world.CreateCharacter();
            ch.NpcBrain = NpcBrainType.Monster;

            Assert.Equal(1, Run(runtime, ch, "f_brain_is_monster"));
            Assert.Equal(2, Run(runtime, ch, "f_brain_is_dragon"));
            Assert.True(ch.TryGetProperty("NPC", out string read));
            Assert.Equal(legacyPack ? "10" : "8", read);

            // NPC=<DEF.brain_dragon> writes the pack's number; the engine holds Dragon.
            Assert.Equal(1, Run(runtime, ch, "f_brain_set_dragon"));
            Assert.Equal(NpcBrainType.Dragon, ch.NpcBrain);
            Assert.Equal(1, Run(runtime, ch, "f_brain_is_dragon"));

            Assert.Equal(1, Run(runtime, ch, "f_brain_set_monster"));
            Assert.Equal(NpcBrainType.Monster, ch.NpcBrain);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnderA056PackTheRetiredAndRenumberedBrainsReadBackInItsNumbers()
    {
        var (_, world, dir) = Boot(Pack056);
        try
        {
            var ch = world.CreateCharacter();
            Assert.True(ch.TrySetProperty("NPC", "12"));           // brain_undead
            Assert.Equal(NpcBrainType.Monster, ch.NpcBrain);
            Assert.True(ch.TrySetProperty("NPC", "8"));            // brain_stable
            Assert.Equal(NpcBrainType.Stable, ch.NpcBrain);
            Assert.True(ch.TryGetProperty("NPCBRAIN", out string stable));
            Assert.Equal("8", stable);
            ch.NpcBrain = NpcBrainType.Berserk;
            Assert.True(ch.TryGetProperty("NPC", out string berserk));
            Assert.Equal("11", berserk);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnderA056PackSavesKeepThisEnginesNumbering()
    {
        var (_, world, dir) = Boot(Pack056);
        try
        {
            var ch = world.CreateCharacter();
            ch.NpcBrain = NpcBrainType.Monster;
            world.PlaceCharacter(ch, new Point3D(50, 50, 0, 0));
            string outDir = Path.Combine(dir, "save");
            Directory.CreateDirectory(outDir);
            var lf = LoggerFactory.Create(_ => { });
            Assert.True(new WorldSaver(lf).Save(world, outDir));

            string text = string.Concat(Directory.GetFiles(outDir, "*.scp", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            Assert.Contains("NPC=" + (int)NpcBrainType.Monster, text);

            // Read back under the same pack: still a monster, not the pack's stable (8).
            var world2 = new GameWorld(lf);
            world2.InitMap(0, 256, 256);
            ObjBase.ResolveWorld = () => world2;
            Item.ResolveWorld = () => world2;
            new WorldLoader(lf).Load(world2, outDir);
            var back = world2.FindChar(ch.Uid);
            Assert.NotNull(back);
            Assert.Equal(NpcBrainType.Monster, back!.NpcBrain);
        }
        finally { Directory.Delete(dir, true); }
    }
}
