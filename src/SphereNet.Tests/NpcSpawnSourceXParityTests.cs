using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// NPC and spawner behaviour held to the Source-X reference where the engine had
/// invented its own: the guard summon (CChar::CallGuards, CCharFight.cpp:215-285),
/// SetID on a live character (CChar.cpp:1596-1640), the spawner's defaults and
/// capacity (CCSpawn.cpp:64-70, :123), DEFAULTCHAR (CChar.cpp:1600-1611), the
/// FOODTYPE diet (Food_CanEat, CCharStatus.cpp:888), NPC magery reach
/// (CCharNPCAct_Magic.cpp:173), breath and throw (CCharSkill.cpp:3279-3475), the
/// follow leash (UO_MAP_VIEW_RADAR) and the figurine (Make_Figurine,
/// CCharAct.cpp:3619).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcSpawnSourceXParityTests : IDisposable
{
    private const string Pack = """
        [DEFNAME probe_defs]
        guards { c_probe_guard 1 }
        defaultchar c_probe_default

        [CHARDEF c_probe_guard]
        ID=0190
        NAME=probe guard
        MAGERY=50.0

        [CHARDEF c_probe_guard2]
        ID=0191
        NAME=second guard

        [CHARDEF c_probe_default]
        ID=0190
        NAME=default one

        [CHARDEF c_probe_skilled]
        ID=0190
        NAME=skilled one
        TAG.PROBE=def
        MAGERY=80.0

        [CHARDEF c_probe_other]
        ID=0191
        NAME=other body
        TAG.PROBE=other
        MAGERY=10.0

        [EVENTS e_probe_callguards]
        ON=@CallGuards
        ARGN1=<RESOURCEINDEX c_probe_guard2>
        ARGN2=1
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"npcspawn-{Guid.NewGuid():N}.scp");
    private readonly Dictionary<FieldInfo, object?> _saved = new();
    private readonly ScriptRuntimeStack _stack = ScriptTestBootstrap.CreateRuntimeStack();
    private GameWorld _world = null!;

    public NpcSpawnSourceXParityTests()
    {
        File.WriteAllText(_path, Pack);
    }

    /// <summary>Load the pack and make the world. Called from the test body: the
    /// engine statics are reset between the constructor and the test.</summary>
    private void Load()
    {
        _stack.Resources.LoadResourceFile(_path);
        new DefinitionLoader(_stack.Resources, new SpellRegistry()).LoadAll();
        _world = TestHarness.CreateWorld();
    }

    public void Dispose()
    {
        foreach (var (field, value) in _saved) field.SetValue(null, value);
        File.Delete(_path);
        _stack.LoggerFactory.Dispose();
    }

    private void SetServer(string name, object value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!_saved.ContainsKey(field))
            _saved.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private int DefIndex(string name) => _stack.Resources.ResolveDefName(name).Index;

    // ------------------------------------------------------------------ guards

    private (Region Town, Character Caller, Character Criminal) GuardBench()
    {
        Load();
        SetServer("_triggerDispatcher", _stack.Dispatcher);
        SetServer("_npcAI", new NpcAI(_world, new SphereConfig()));
        SetServer("_world", _world);
        SetServer("_resources", _stack.Resources);
        SetServer("_config", new SphereConfig());
        SetServer("_log", NullLogger.Instance);
        ((Dictionary<uint, long>)typeof(SphereNet.Server.Program)
            .GetField("_lastCallGuards", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!).Clear();

        var town = new Region { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        town.AddRect(0, 0, 1000, 1000);
        _world.AddRegion(town);

        var caller = _world.CreateCharacter();
        caller.IsPlayer = true;
        _world.PlaceCharacter(caller, new Point3D(100, 100, 0, 0));
        var criminal = _world.CreateCharacter();
        criminal.IsPlayer = true;
        criminal.MaxHits = criminal.Hits = 100;
        criminal.SetStatFlag(StatFlag.Criminal);
        _world.PlaceCharacter(criminal, new Point3D(102, 100, 0, 0));
        return (town, caller, criminal);
    }

    private static bool CallGuards(Character caller, Character criminal) =>
        (bool)typeof(SphereNet.Server.Program)
            .GetMethod("CallGuards", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [caller, criminal])!;

    private List<Character> SummonedGuards(Character near) =>
        _world.GetCharsInRange(near.Position, 3)
            .Where(c => c.TryGetTag("IS_CITY_GUARD", out _))
            .ToList();

    [Fact]
    public void ASummonedGuardIsTheGuardsResource_MadeLikeAnyNpc_AndNotInvulnerable()
    {
        var (_, caller, criminal) = GuardBench();

        Assert.True(CallGuards(caller, criminal));

        var guard = Assert.Single(SummonedGuards(criminal));
        Assert.Equal(DefIndex("c_probe_guard"), guard.CharDefIndex);
        Assert.Equal("probe guard", guard.Name);
        Assert.False(guard.IsStatFlag(StatFlag.Invul));
        Assert.Equal(500, guard.GetSkill(SkillType.Magery)); // the definition, applied
        Assert.Equal(guard.MaxHits, guard.Hits);             // a new NPC stands full
    }

    [Fact]
    public void TheAreasOverrideGuardsTagChoosesTheGuard_AndARedAreaHuesItsName()
    {
        var (town, caller, criminal) = GuardBench();
        town.SetTag("OVERRIDE.GUARDS", "c_probe_guard2");
        town.SetTag("RED", "1");

        Assert.True(CallGuards(caller, criminal));

        var guard = Assert.Single(SummonedGuards(criminal));
        Assert.Equal(DefIndex("c_probe_guard2"), guard.CharDefIndex);
        Assert.True(guard.TryGetTag("NAME.HUE", out string? hue));
        Assert.Equal($"0{new SphereConfig().ColorNotoEvil:x}", hue);
    }

    [Fact]
    public void CallGuardsArgn1ChangesTheGuard_AndArgn2SummonsEvenWithAGuardAtHand()
    {
        var (_, caller, criminal) = GuardBench();
        caller.Events.Add(_stack.Resources.ResolveDefName("e_probe_callguards"));
        var free = _world.CreateCharacter();
        free.NpcBrain = NpcBrainType.Guard;
        free.Hits = free.MaxHits = 100;
        _world.PlaceCharacter(free, new Point3D(110, 100, 0, 0));

        Assert.True(CallGuards(caller, criminal));

        var guard = Assert.Single(SummonedGuards(criminal));
        Assert.Equal(DefIndex("c_probe_guard2"), guard.CharDefIndex);
        Assert.NotSame(free, guard);
    }

    [Fact]
    public void NoGuardComesWhenTheGuardsResourceDoesNotResolve()
    {
        var (town, caller, criminal) = GuardBench();
        town.SetTag("OVERRIDE.GUARDS", "c_nobody_defines_this");

        Assert.False(CallGuards(caller, criminal));
        Assert.Empty(SummonedGuards(criminal));
    }

    // ------------------------------------------------------------------ SetID

    [Fact]
    public void BodyOnALiveCharacterSwapsTheDefinitionAndNothingElse()
    {
        Load();
        var npc = _world.CreateCharacter();
        Assert.True(CharDefHelper.TryApplyDefName(npc, "c_probe_skilled", _stack.Resources, stats: true));
        npc.NpcBrain = NpcBrainType.Vendor;
        npc.SetSkill(SkillType.Magery, 123);

        Assert.True(npc.TrySetProperty("BODY", "c_probe_other"));

        Assert.Equal(DefIndex("c_probe_other"), npc.CharDefIndex);
        Assert.Equal((ushort)0x0191, npc.BodyId);
        Assert.Equal(NpcBrainType.Vendor, npc.NpcBrain);
        Assert.Equal(123, npc.GetSkill(SkillType.Magery));
        Assert.True(npc.TryGetTag("PROBE", out string? probe));
        Assert.Equal("def", probe);
    }

    // ------------------------------------------------------------------ spawner

    private Item SpawnGem()
    {
        Load();
        var gem = _world.CreateItem();
        gem.BaseId = 0x1F13;
        gem.ItemType = ItemType.SpawnChar;
        _world.PlaceItem(gem, new Point3D(500, 500, 0, 0));
        return gem;
    }

    [Fact]
    public void ANewSpawnerKeepsTheConstructorDefaults()
    {
        var gem = SpawnGem();
        long before = Environment.TickCount64;
        var spawn = new SpawnComponent(gem, _world);
        spawn.ApplyMoreP(); // no MOREP was ever set

        Assert.Equal(15, spawn.SpawnRange);
        long due = gem.Timeout - before;
        Assert.InRange(due, 15 * 60_000L - 1_000, 30 * 60_000L + 1_000);
    }

    [Fact]
    public void SpawnerAmountHasNoCapAndIsNotWrittenOntoTheGem()
    {
        var gem = SpawnGem();
        var spawn = new SpawnComponent(gem, _world);

        spawn.MaxCount = 400;

        Assert.Equal(400, spawn.MaxCount);
        Assert.Equal((ushort)1, gem.Amount);
    }

    [Fact]
    public void AMissingChardefSpawnsTheDefaultChar()
    {
        var gem = SpawnGem();
        var spawn = new SpawnComponent(gem, _world);

        var npc = spawn.SpawnSpecific(0x0ABCDE);

        Assert.NotNull(npc);
        Assert.Equal(DefIndex("c_probe_default"), npc!.CharDefIndex);
        Assert.Equal("default one", npc.Name);
    }

    // ------------------------------------------------------------------ food

    [Fact]
    public void ACreatureWithoutFoodtypeEatsNothing_AndOneWithItEatsItsQuantity()
    {
        Load();
        var ai = new NpcAI(_world, new SphereConfig());
        var sheep = _world.CreateCharacter();
        sheep.SetTag("OVERRIDE.NPCAI", "0x0010"); // NPC_AI_INTFOOD
        sheep.SetTag("MAXFOOD", "60");
        _world.PlaceCharacter(sheep, new Point3D(100, 100, 0, 0));
        var bread = _world.CreateItem();
        bread.ItemType = ItemType.Food;
        bread.Amount = 5;
        _world.PlaceItem(bread, new Point3D(101, 100, 0, 0));
        sheep.Food = 5;

        // No FOODTYPE: Food_CanEat answers 0 (CCharStatus.cpp:903-907).
        Assert.False(ai.NpcCanEat(sheep, bread));
        Assert.False(ai.RunFoodAI(sheep));
        Assert.Equal(5, bread.Amount);

        // "3 t_food": three at a bite (NPC_Food, CCharNPCAct.cpp:2560).
        sheep.SetTag("FOODTYPE", "3 t_food");
        Assert.Equal(3, ai.NpcFoodQty(sheep, bread));
        Assert.True(ai.RunFoodAI(sheep));
        Assert.Equal(2, bread.Amount);
    }

    // ------------------------------------------------------------------ magery reach

    [Fact]
    public void NpcMageryReachIsTenTilesWhateverTheCreaturesSight()
    {
        Load();
        var ai = new NpcAI(_world, new SphereConfig());
        var mage = _world.CreateCharacter();
        mage.VisualRange = 30;
        mage.Int = 100;
        mage.Mana = mage.MaxMana = 100;
        mage.NpcSpellAdd(SpellType.MagicArrow);
        _world.PlaceCharacter(mage, new Point3D(100, 100, 0, 0));
        var target = _world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        _world.PlaceCharacter(target, new Point3D(112, 100, 0, 0));
        int starts = 0;
        ai.OnNpcTryStartSpellCast = (_, _, _) => { starts++; return true; };
        ai.OnNpcCastSpell = (_, _, _) => starts++;

        var tryCast = typeof(NpcAI).GetMethod("TryNpcCastSpell", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 50; i++)
            Assert.False((bool)tryCast.Invoke(ai, [mage, target, 12])!);
        Assert.Equal(0, starts);
    }

    // ------------------------------------------------------------------ breath / throw

    [Fact]
    public void BreathDamageReadsTheCurrentHitPoints()
    {
        Load();
        var dragon = _world.CreateCharacter();
        dragon.Str = 400;
        dragon.MaxHits = 400;
        dragon.Hits = 100;
        var get = typeof(NpcAI).GetMethod("GetBreathDamage", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(5, (int)get.Invoke(null, [dragon])!);
    }

    [Fact]
    public void ASingleThrowRangeIsAMaximumWithAZeroMinimum()
    {
        Load();
        var ai = new NpcAI(_world, new SphereConfig());
        ai.Extras |= NpcAiExtraFlags.CombatExtras; // a THROWOBJ tag alone arms a thrower
        var thrower = _world.CreateCharacter();
        thrower.NpcBrain = NpcBrainType.Monster;
        thrower.Dex = 100;
        thrower.Hits = thrower.MaxHits = 100;
        thrower.Stam = thrower.MaxStam = 100;
        thrower.SetTag("THROWOBJ", "1");
        thrower.SetTag("THROWRANGE", "5");
        _world.PlaceCharacter(thrower, new Point3D(100, 100, 0, 0));
        var target = _world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        _world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        int throws = 0;
        ai.OnNpcThrow = (_, _, _) => throws++;
        long clock = 1_000_000;
        ai.NowMs = () => clock;
        var actFight = typeof(NpcAI).GetMethod("ActFight", BindingFlags.Instance | BindingFlags.NonPublic)!;
        actFight.Invoke(ai, [thrower, target, 100]);
        Assert.Equal(NpcAI.NpcSpecialKind.Throw, ai.PendingSpecial(thrower)); // started at distance 1
        // It flies when Skill_Act_Throwing's three second wind-up ends.
        clock += NpcAI.SpecialWindupMs;
        actFight.Invoke(ai, [thrower, target, 100]);

        Assert.Equal(1, throws);
    }

    // ------------------------------------------------------------------ hire

    [Fact]
    public void GoldHiresOnlyAfterTheHireTalk_AndTheHirelingsGearBecomesOwned()
    {
        Load();
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, _world, new SphereNet.Game.Accounts.AccountManager(lf), 6911);
        var owner = _world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 10;
        _world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = _world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, owner);

        var hireling = _world.CreateCharacter();
        hireling.SetTag("HIRE_WAGE", "10");
        _world.PlaceCharacter(hireling, new Point3D(101, 100, 0, 0));
        var sword = _world.CreateItem();
        sword.BaseId = 0x13B9;
        hireling.Equip(sword, Layer.OneHanded);

        Item Gold()
        {
            var g = _world.CreateItem();
            g.BaseId = 0x0EED;
            g.ItemType = ItemType.Gold;
            g.Amount = 20;
            owner.Backpack!.AddItem(g);
            return g;
        }
        void Give(Item g)
        {
            client.HandleItemPickup(g.Uid.Value, g.Amount);
            client.HandleItemDrop(g.Uid.Value, 0, 0, 0, hireling.Uid.Value);
        }

        // No hire talk: gold is just a gift, not a wage (CCharNPCAct.cpp:2078-2088).
        Give(Gold());
        Assert.False(hireling.HasOwner(owner.Uid));

        // NPC_OnHireHear leaves NPC_MEM_ACT_SPEAK_HIRE on the speaker's memory.
        var mem = hireling.Memory_AddObjTypes(owner.Uid, MemoryType.Speak);
        mem.More1 = Character.NpcMemActSpeakHire;
        Give(Gold());

        Assert.True(hireling.HasOwner(owner.Uid));
        Assert.True(sword.IsAttr(ObjAttributes.Owned)); // ContentAttrMod(ATTR_OWNED)
        Assert.Equal(0u, mem.More1 & 0xFFFF);            // the talk is settled
    }

    // ------------------------------------------------------------------ pets

    [Fact]
    public void ThePetFollowLeashIsTheMapViewRadar()
    {
        Assert.Equal(31, NpcAI.PetFollowMaxDistance);
    }

    [Fact]
    public void AFigurineCarriesTheCreaturesIconNameAndHue()
    {
        Load();
        var owner = _world.CreateCharacter();
        owner.IsPlayer = true;
        _world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pet = _world.CreateCharacter();
        pet.Name = "Rex";
        pet.Hue = new Color(0x0455);
        _world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner));
        var figurine = _world.CreateItem();
        figurine.BaseId = 0x2106;

        Assert.True(PetFigurine.Shrink(owner, pet, figurine, _world));

        Assert.Equal("Rex", figurine.Name);
        Assert.Equal((ushort)0x0455, figurine.Hue.Value);
        Assert.Equal((ushort)0x2100, figurine.BaseId); // no ICON: ITEMID_TRACK_WISP
    }

    [Fact]
    public void StablingAsksTheStablemastersSightNotADistance_AndSaysWhyItRefuses()
    {
        Load();
        var owner = _world.CreateCharacter();
        owner.IsPlayer = true;
        _world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var master = _world.CreateCharacter();
        master.NpcBrain = NpcBrainType.Stable;
        _world.PlaceCharacter(master, new Point3D(101, 100, 0, 0));
        var stray = _world.CreateCharacter();
        _world.PlaceCharacter(stray, new Point3D(102, 100, 0, 0));
        var pet = _world.CreateCharacter();
        pet.Name = "Far";
        _world.PlaceCharacter(pet, new Point3D(125, 100, 0, 0)); // well past 12 tiles
        Assert.True(pet.TryAssignOwnership(owner, owner));
        var stable = new StableEngine();

        Assert.Equal(SphereNet.Game.Messages.Msg.NpcStablemasterTargOwner,
            stable.StablePetReason(owner, stray, _world, master));
        Assert.Null(stable.StablePetReason(owner, pet, _world, master));
        Assert.Equal(1, stable.GetStabledCount(owner));
    }
}
