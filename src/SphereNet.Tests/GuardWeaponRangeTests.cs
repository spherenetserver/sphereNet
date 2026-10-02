using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A guard summoned against an evil creature never killed it. The guard lands on
/// the criminal's tile (CallGuards Spell_Teleport to pCriminal->GetTopPoint(),
/// CCharFight.cpp:282) and carries a polearm scripted "RANGE=2". Source-X reads a
/// single RANGE value as the high end only (CBaseBaseDef::ConvertRangeStr,
/// CBase.cpp:486): 0..2. It was read as 2..2, so the guard, standing at distance 0
/// (or 1), was always "too close" and never swung - the fight went on forever.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GuardWeaponRangeTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly Dictionary<FieldInfo, object?> _saved = new();
    private readonly List<string> _dirs = [];

    public GuardWeaponRangeTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var (field, value) in _saved) field.SetValue(null, value);
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"spn_guardrange_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private void SetServer(string name, object? value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!_saved.ContainsKey(field))
            _saved.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private const string RangePack = """
        [ITEMDEF 0f00]
        DEFNAME=i_range_single
        TYPE=t_weapon_sword
        RANGE=2

        [ITEMDEF 0f01]
        DEFNAME=i_range_pair
        TYPE=t_weapon_sword
        RANGE=2,10

        [ITEMDEF 0f02]
        DEFNAME=i_range_reversed
        TYPE=t_weapon_sword
        RANGE=10,2

        [ITEMDEF 0f03]
        DEFNAME=i_range_none
        TYPE=t_weapon_sword

        [CHARDEF 0300]
        DEFNAME=c_range_single
        RANGE=3

        [CHARDEF 0301]
        DEFNAME=c_range_pair
        RANGE=1,4
        """;

    private ScriptRuntimeStack LoadPack(string text)
    {
        string file = Path.Combine(TempDir(), "pack.scp");
        File.WriteAllText(file, text);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(file);
        new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();
        return stack;
    }

    /// <summary>ConvertRangeStr: one value is the high end (low 0), two are low,high
    /// in order - for an ITEMDEF and a CHARDEF alike (CCharBase.cpp:412-416).</summary>
    [Fact]
    public void RangeKey_OneValueIsTheHighEnd_TwoAreLowHigh()
    {
        var stack = LoadPack(RangePack);
        try
        {
            Assert.Equal((0, 2), Range(DefinitionLoader.GetItemDef(0x0f00)!));
            Assert.Equal((2, 10), Range(DefinitionLoader.GetItemDef(0x0f01)!));
            Assert.Equal((2, 10), Range(DefinitionLoader.GetItemDef(0x0f02)!));
            var c1 = DefinitionLoader.GetCharDef(0x0300)!;
            var c2 = DefinitionLoader.GetCharDef(0x0301)!;
            Assert.Equal((0, 3), (c1.RangeMin, c1.RangeMax));
            Assert.Equal((1, 4), (c2.RangeMin, c2.RangeMax));

            // A melee weapon with RANGE=2 reaches its own tile, the next one and
            // the one after (Fight_Hit, CCharFight.cpp:1911-1913).
            var world = TestHarness.CreateWorld();
            var polearm = world.CreateItem();
            polearm.BaseId = 0x0f00;
            polearm.ItemType = ItemType.WeaponSword;
            Assert.Equal((0, 2), CombatHelper.GetWeaponRange(polearm));
        }
        finally { stack.LoggerFactory.Dispose(); }
    }

    private static (int, int) Range(SphereNet.Scripting.Definitions.ItemDef d) => (d.RangeMin, d.RangeMax);

    /// <summary>The read-back: PROPIWEAP_RANGE prints the high end alone when the
    /// low end is 0, else "low,high", and 1 for no range (CCPropsItemWeapon.cpp:105).</summary>
    [Fact]
    public void ItemRangeReadBack_FollowsTheSourceXFormat()
    {
        var stack = LoadPack(RangePack);
        try
        {
            var world = TestHarness.CreateWorld();
            ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;
            string Read(ushort id)
            {
                var it = world.CreateItem();
                it.BaseId = id;
                Assert.True(it.TryGetProperty("RANGE", out string v));
                return v;
            }
            Assert.Equal("2", Read(0x0f00));
            Assert.Equal("2,10", Read(0x0f01));
            Assert.Equal("1", Read(0x0f03));
        }
        finally { stack.LoggerFactory.Dispose(); }
    }

    // ------------------------------------------------------------ the fight

    private const string GuardPack = """
        [DEFNAME probe_guard_defs]
        guards { c_range_guard 1 }

        [ITEMDEF 0143e]
        DEFNAME=i_range_polearm
        TYPE=t_weapon_sword
        DAM=13,17
        SKILL=Swordsmanship
        SPEED=26
        TWOHANDS=Y
        RANGE=2

        [CHARDEF c_range_guard]
        ID=0190
        NAME=range guard

        ON=@Create
        NPC=brain_guard
        STR=160
        DEX=460
        SWORDSMANSHIP=95.0
        TACTICS=95.0

        ON=@NPCRestock
        ITEM=i_range_polearm

        [CHARDEF 0301]
        DEFNAME=c_range_evil_beast
        NAME=evil beast

        ON=@Create
        NPC=brain_animal
        KARMA=-2900
        STR=60
        WRESTLING=10.0
        """;

    private sealed record Arena(GameWorld World, NpcAI Ai, Character Caller);

    private Arena BuildArena(ScriptRuntimeStack stack, bool instantKill)
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var cfg = new SphereConfig { GuardsInstantKill = instantKill, GuardsOnMurderers = true };
        var ai = new NpcAI(world, cfg);
        SetServer("_triggerDispatcher", stack.Dispatcher);
        SetServer("_npcAI", ai);
        SetServer("_world", world);
        SetServer("_resources", stack.Resources);
        SetServer("_config", cfg);
        SetServer("_log", NullLogger.Instance);
        SetServer("_recordingEngine", new SphereNet.Game.Recording.RecordingEngine(Path.Combine(TempDir(), "rec")));
        ((Dictionary<uint, long>)typeof(SphereNet.Server.Program)
            .GetField("_lastCallGuards", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!).Clear();

        var town = new Region { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        town.AddRect(0, 0, 1000, 1000);
        world.AddRegion(town);

        var caller = world.CreateCharacter();
        caller.IsPlayer = true;
        caller.IsOnline = true;
        world.PlaceCharacter(caller, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(caller);
        // The NPC tick only runs where a player keeps the sectors awake.
        typeof(GameWorld).GetMethod("RefreshActiveSectors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(world, null);
        return new Arena(world, ai, caller);
    }

    private static Character SpawnFromDef(ScriptRuntimeStack stack, GameWorld world, string defName, Point3D at)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = false;
        Assert.True(CharDefHelper.TryApplyDefName(ch, defName, stack.Resources, stats: true, refresh: false, fireCreate: false));
        stack.Dispatcher.FireCharTrigger(ch, CharTrigger.Create, new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch });
        ch.Hits = ch.MaxHits;
        world.PlaceCharacter(ch, at);
        return ch;
    }

    /// <summary>Call the guards on <paramref name="criminal"/> and run the summoned
    /// guard's AI (the swing cadence collapsed) until the criminal is down.</summary>
    private Character CallAndFight(Arena a, Character criminal, int maxTicks = 600)
    {
        var call = typeof(SphereNet.Server.Program).GetMethod("CallGuards", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)call.Invoke(null, [a.Caller, criminal])!);
        var guard = Assert.Single(a.World.GetAllCharactersSnapshot(), c => c.TryGetTag("IS_CITY_GUARD", out _));
        Assert.Equal(criminal.Uid, guard.FightTarget);

        for (int i = 0; i < maxTicks && criminal.Hits > 0 && !criminal.IsDead && !criminal.IsDeleted; i++)
        {
            guard.NextNpcActionTime = 0;
            guard.NextAttackTime = 0;
            if (guard.HasPendingHit)
                guard.SwingHitTime = 0;
            a.Ai.OnTickAction(guard);
        }
        _out.WriteLine($"guard={guard.Name} at {guard.Position}, criminal at {criminal.Position} hits={criminal.Hits}/{criminal.MaxHits}");
        return guard;
    }

    /// <summary>The report: an unowned evil-karma animal in a guarded town. The
    /// summoned guard stands on its tile with a RANGE=2 polearm and cuts it down.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SummonedGuard_WithARange2Polearm_KillsAnEvilAnimalOnItsTile(bool instantKill)
    {
        var stack = LoadPack(GuardPack);
        try
        {
            var a = BuildArena(stack, instantKill);
            var beast = SpawnFromDef(stack, a.World, "c_range_evil_beast", new Point3D(103, 100, 0, 0));
            Assert.Equal(NpcBrainType.Animal, beast.NpcBrain);
            Assert.True(a.Ai.NotoIsEvil(beast)); // animal at -800 karma or less

            var guard = CallAndFight(a, beast);
            Assert.NotNull(guard.GetEquippedItem(Layer.TwoHanded));
            Assert.Equal(beast.Position, guard.Position); // placed on the criminal (CCharFight.cpp:282)
            Assert.True(beast.Hits <= 0 || beast.IsDead, $"the guard never brought it down ({beast.Hits} hits left)");
        }
        finally { stack.LoggerFactory.Dispose(); }
    }

    /// <summary>The same polearm one step away: a RANGE=2 melee weapon hits at
    /// distance 1 (it used to need exactly 2).</summary>
    [Fact]
    public void GuardWithARange2Polearm_HitsAnAdjacentTarget()
    {
        var stack = LoadPack(GuardPack);
        try
        {
            var a = BuildArena(stack, instantKill: false);
            var beast = SpawnFromDef(stack, a.World, "c_range_evil_beast", new Point3D(103, 100, 0, 0));
            var guard = SpawnFromDef(stack, a.World, "c_range_guard", new Point3D(104, 100, 0, 0));
            stack.Dispatcher.FireCharTrigger(guard, CharTrigger.NPCRestock, new SphereNet.Game.Scripting.TriggerArgs { CharSrc = guard });
            Assert.NotNull(guard.GetEquippedItem(Layer.TwoHanded));
            Assert.True(a.Ai.GuardLookAtChar(guard, beast, fromTrigger: false));

            short before = beast.Hits;
            for (int i = 0; i < 200 && beast.Hits == before; i++)
            {
                guard.NextNpcActionTime = 0;
                guard.NextAttackTime = 0;
                if (guard.HasPendingHit)
                    guard.SwingHitTime = 0;
                a.Ai.OnTickAction(guard);
            }
            Assert.True(beast.Hits < before, "the polearm never landed a blow at distance 1");
        }
        finally { stack.LoggerFactory.Dispose(); }
    }

    /// <summary>Sphere 56T custom-version compatibility, against the real pack: the
    /// pack's GUARDS (c_guard / c_guard_f, a halberd scripted RANGE=2) summoned on an
    /// unowned c_kirin (brain_animal, KARMA -2500..-2999) kill it. Skips cleanly
    /// without the pack (SPHERENET_56T_SCRIPTS, default C:\56T\scripts).</summary>
    [Fact]
    public void Sphere56TPack_SummonedGuardKillsAnUnownedKirin()
    {
        string root = Environment.GetEnvironmentVariable("SPHERENET_56T_SCRIPTS") ?? @"C:\56T\scripts";
        if (Gate.Missing(_out, "external script pack", !File.Exists(Path.Combine(root, "sphere_functions.scp")))) return;

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        try
        {
            stack.Resources.ScpBaseDir = root;
            foreach (string f in Directory.EnumerateFiles(root, "*.scp", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)) continue;
                try { stack.Resources.LoadResourceFile(f); }
                catch (Exception e) { _out.WriteLine($"load {f}: {e.Message}"); }
            }
            new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();
            if (Gate.Missing(_out, "external script pack",
                    !stack.Resources.ResolveDefName("c_kirin").IsValid)) return;

            var a = BuildArena(stack, instantKill: false);
            var kirin = SpawnFromDef(stack, a.World, "c_kirin", new Point3D(103, 100, 0, 0));
            Assert.False(kirin.NpcMaster.IsValid);
            Assert.True(a.Ai.NotoIsEvil(kirin));

            var guard = CallAndFight(a, kirin, maxTicks: 1500);
            var weapon = guard.GetEquippedItem(Layer.TwoHanded) ?? guard.GetEquippedItem(Layer.OneHanded);
            Assert.NotNull(weapon);
            Assert.Equal(0, CombatHelper.GetWeaponRange(weapon!).Min);
            Assert.True(kirin.Hits <= 0 || kirin.IsDead, $"the guard never brought the ki-rin down ({kirin.Hits} hits left)");
        }
        finally { stack.LoggerFactory.Dispose(); }
    }
}
