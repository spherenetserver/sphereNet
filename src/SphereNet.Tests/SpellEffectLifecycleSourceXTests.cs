using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Core.Configuration;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The spell-effect lifecycle the Source-X way: the effect IS its worn IT_SPELL memory
/// (CChar::Spell_Effect_Create / Spell_Effect_Add / Spell_Effect_Remove /
/// Spell_Equip_OnTick, CCharSpell.cpp:2040 / :965 / :541 / :1738). Each test pins one
/// finding of the magic-lifecycle audit (B01-B21, B25) with its numbers; the expected
/// values are read off the local Source-X C++.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellEffectLifecycleSourceXTests : IDisposable
{
    private readonly List<string> _temp = [];
    private readonly Dictionary<FieldInfo, object?> _savedServer = new();

    public void Dispose()
    {
        foreach (var (field, value) in _savedServer) field.SetValue(null, value);
        foreach (var path in _temp)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else File.Delete(path);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ fixture

    private static GameWorld World()
    {
        var w = TestHarness.CreateWorld();
        Character.ResolveCharByUid = w.FindChar;
        Character.MagicFlags = 0;
        return w;
    }

    private static Character Char(GameWorld w, int x = 100)
    {
        var c = w.CreateCharacter();
        c.IsPlayer = true;
        c.BodyId = 0x190;
        c.Name = "Probe";
        c.Str = c.Dex = c.Int = 50;
        c.Hits = c.MaxHits = 100;
        c.Mana = c.MaxMana = 100;
        c.Stam = c.MaxStam = 100;
        c.SetSkill(SkillType.Magery, 1000);
        c.SetSkill(SkillType.SpiritSpeak, 1000);
        w.PlaceCharacter(c, new Point3D((short)x, 100, 0, 0));
        return c;
    }

    private static SpellDef Def(SpellType id = SpellType.Strength, int effect = 20, int duration = 600,
        SpellFlag flags = SpellFlag.Good | SpellFlag.TargChar) => new()
    {
        Id = id, Name = id.ToString(), Flags = flags,
        ManaCost = 0, CastTimeBase = 1, EffectBase = effect, EffectScale = effect,
        DurationBase = duration, DurationScale = duration, RuneItemId = 0x2085,
    };

    private static (SpellEngine E, GameWorld W, Character C, Character T, SpellRegistry R) Setup(params SpellDef[] defs)
    {
        var w = World();
        var c = Char(w);
        var t = Char(w, 101);
        var r = new SpellRegistry();
        if (defs.Length == 0) r.Register(Def());
        foreach (var d in defs) r.Register(d);
        return (new SpellEngine(w, r), w, c, t, r);
    }

    private static void Apply(SpellEngine e, Character c, Character t, SpellType id = SpellType.Strength) =>
        e.ApplyDirectEffect(c, t, id, 1000);

    private static Item? Mem(Character c, SpellType spell) =>
        c.Memories.FirstOrDefault(m => !m.IsDeleted && m.ItemType == ItemType.Spell && m.MoreP.X == (int)spell);

    private static string Tag(ObjBase obj, string key) => obj.TryGetTag(key, out var v) ? v ?? "" : "missing";

    private static long NumTag(ObjBase obj, string key) =>
        ScriptNumber.TryParseToken(Tag(obj, key), out long n) ? n : -1;

    private static int Str(Character c) => CombatEngine.EffectiveStr(c);

    private TriggerDispatcher Script(string content, params Character[] eventHolders)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string file = Path.Combine(Path.GetTempPath(), $"magic-lifecycle-{Guid.NewGuid():N}.scp");
        _temp.Add(file);
        File.WriteAllText(file, content);
        stack.Resources.LoadResourceFile(file);
        var rid = stack.Resources.ResolveDefName("e_magic");
        if (rid.IsValid)
            foreach (var ch in eventHolders)
                ch.Events.Add(rid);
        stack.Dispatcher.BuildUsedTriggerCache();
        return stack.Dispatcher;
    }

    /// <summary>Install the production character hooks (Program.RefreshCharacterScriptHooks)
    /// on this dispatcher, as the server does at startup.</summary>
    private void WireHost(SpellEngine e, GameWorld w, TriggerDispatcher d)
    {
        SetServer("_world", w);
        SetServer("_spellEngine", e);
        SetServer("_triggerDispatcher", d);
        typeof(SphereNet.Server.Program)
            .GetMethod("RefreshCharacterScriptHooks", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);
        e.TriggerDispatcher = d;
    }

    private void SetServer(string name, object value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!_savedServer.ContainsKey(field))
            _savedServer.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    /// <summary>A real world save written by WorldSaver and read back by WorldLoader,
    /// with a fresh engine taking the loaded memories in (what startup does).</summary>
    private (Character Reloaded, SpellEngine Engine, GameWorld World) RoundTrip(
        GameWorld src, SpellRegistry registry, Character subject)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"magic-lifecycle-save-{Guid.NewGuid():N}");
        _temp.Add(dir);
        Directory.CreateDirectory(dir);
        var lf = LoggerFactory.Create(_ => { });
        var saver = new WorldSaver(lf) { Format = SaveFormat.Text, ShardCount = 0 };
        Assert.True(saver.Save(src, dir));

        var next = TestHarness.CreateWorld();
        Character.ResolveCharByUid = next.FindChar;
        new WorldLoader(lf).Load(next, dir);
        var reloaded = next.FindChar(subject.Uid) ?? throw new InvalidOperationException("not loaded");
        var engine = new SpellEngine(next, registry);
        engine.RestorePersistedEffectsFromWorld();
        return (reloaded, engine, next);
    }

    // ------------------------------------------------- B01: @SpellEffectAdd verdict

    /// <summary>B01: the character's @SpellEffectAdd runs on the worn memory (ARGO) with
    /// the caster as SRC, BEFORE the effect: RETURN 1 deletes the memory and nothing is
    /// applied, RETURN 0 keeps the memory and skips the engine's part
    /// (CCharSpell.cpp:984-998). It used to be a notice that could veto nothing.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void B01_CharSpellEffectAdd_VerdictAndArgo(int verdict)
    {
        var (e, w, c, t, _) = Setup();
        var d = Script($"[EVENTS e_magic]\nON=@SpellEffectAdd\nTAG.ADD_FIRED=1\nTAG.ADD_ARGO=<ARGO.UID>\nTAG.ADD_SRC=<SRC.UID>\nRETURN {verdict}\n", t);
        WireHost(e, w, d);

        Apply(e, c, t);

        Assert.Equal("1", Tag(t, "ADD_FIRED"));
        Assert.Equal(c.Uid.Value, (uint)NumTag(t, "ADD_SRC"));
        Assert.True(NumTag(t, "ADD_ARGO") > 0);
        Assert.Equal(50, Str(t));                               // was 70: the veto stopped nothing
        Assert.Equal(verdict == 0, Mem(t, SpellType.Strength) != null);
    }

    // --------------------------------------- B02: [SPELL] @EffectAdd before native

    /// <summary>B02: the spell's @EffectAdd sees the character before the effect
    /// (<c>&lt;STR&gt;</c> = 50, not 70), and a level the script writes to ARGO.MOREY is
    /// the level applied (55, not 70) - Spell_Effect_Add reads m_spelllevel by
    /// reference after the hooks (CCharSpell.cpp:980, :1000-1014, :1587).</summary>
    [Fact]
    public void B02_EffectAddStage_RunsBeforeNative_AndItsMoreyIsTheLevel()
    {
        var (e, _, c, t, _) = Setup();
        e.TriggerDispatcher = Script("[SPELL 16]\nON=@EffectAdd\nTAG.SEEN_STR=<STR>\nARGO.MOREY=5\n");

        Apply(e, c, t);

        Assert.Equal(50, NumTag(t, "SEEN_STR"));
        Assert.Equal(5, Mem(t, SpellType.Strength)!.MoreP.Y);
        Assert.Equal(55, Str(t));
    }

    // -------------------------------------------------- B03: removal before undo

    /// <summary>B03: @SpellEffectRemove and [SPELL] @EffectRemove run BEFORE the undo,
    /// on the live memory (ARGO) with the caster as SRC; RETURN 0 lets the memory go but
    /// keeps the undo from running (CCharSpell.cpp:558-577). They used to see 50 with
    /// no ARGO and the target as SRC, and could not stop the undo.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void B03_RemoveStages_SeeTheLiveEffect_AndReturnZeroSkipsTheUndo(bool charHook)
    {
        var (e, w, c, t, _) = Setup();
        string prefix = charHook ? "[EVENTS e_magic]\nON=@SpellEffectRemove" : "[SPELL 16]\nON=@EffectRemove";
        var d = Script(prefix + "\nTAG.REMOVE_FIRED=1\nTAG.REMOVE_STR=<STR>\nTAG.REMOVE_ARGO=<ARGO.UID>\nTAG.REMOVE_SRC=<SRC.UID>\nRETURN 0\n", t);
        if (charHook) WireHost(e, w, d); else e.TriggerDispatcher = d;

        Apply(e, c, t);
        var m = Mem(t, SpellType.Strength)!;
        Assert.True(e.RemoveEffectByMemory(m));

        Assert.Equal("1", Tag(t, "REMOVE_FIRED"));
        Assert.Equal(70, NumTag(t, "REMOVE_STR"));
        Assert.Equal(m.Uid.Value, (uint)NumTag(t, "REMOVE_ARGO"));
        Assert.Equal(c.Uid.Value, (uint)NumTag(t, "REMOVE_SRC"));
        Assert.Equal(70, Str(t));                               // RETURN 0: no undo
        Assert.True(m.IsDeleted);
    }

    /// <summary>B03: a spent Magic Reflection goes through the same removal - its
    /// memory is deleted (CCharSpell.cpp:3786-3788) and its @EffectRemove runs.</summary>
    [Fact]
    public void B03_ReflectionConsumption_RunsTheRemovalStage()
    {
        var fire = Def(SpellType.Fireball, 10, flags: SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage);
        var (e, _, c, t, _) = Setup(Def(SpellType.MagicReflect), fire);
        e.TriggerDispatcher = Script($"[SPELL {(int)SpellType.MagicReflect}]\nON=@EffectRemove\nTAG.REFLECT_REMOVED=1\n");

        Apply(e, c, t, SpellType.MagicReflect);
        Assert.True(t.IsStatFlag(StatFlag.Reflection));
        Apply(e, c, t, SpellType.Fireball);

        Assert.Equal("1", Tag(t, "REFLECT_REMOVED"));
        Assert.False(t.IsStatFlag(StatFlag.Reflection));
        Assert.Null(Mem(t, SpellType.MagicReflect));
    }

    // --------------------------------------------- B04: add callback deletes ARGO

    /// <summary>B04: an add hook that deletes its own memory leaves nothing behind - the
    /// effect is applied only to a memory that is still worn after the hooks (50, not
    /// 70), and no later pass finds an orphaned bonus.</summary>
    [Fact]
    public void B04_AddCallbackDeletingTheMemory_LeavesNoOrphanBonus()
    {
        var (e, w, c, t, _) = Setup();
        Character.OnSpellEffectAdd = (target, _, memory, _) =>
        {
            w.DeleteObject(memory);
            return TriggerResult.Default;
        };

        Apply(e, c, t);

        Assert.Null(Mem(t, SpellType.Strength));
        Assert.Equal(50, Str(t));
        e.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(50, Str(t));
    }

    // ------------------------------------------------------ B05: base vs modifier

    /// <summary>B05: a stat spell moves the MODIFIER (Stat_AddMod, CCharSpell.cpp:1587),
    /// so OSTR stays the base (50, not 70) and MODSTR carries the 20; a script that
    /// sets OSTR=100 under the buff gets 120, and the removal leaves 100, not 80.</summary>
    [Fact]
    public void B05_StatSpell_IsAModifier_BaseEditsSurviveTheBuff()
    {
        var (e, _, c, t, _) = Setup();
        Apply(e, c, t);

        Assert.True(t.TryGetProperty("OSTR", out var ostr));
        Assert.True(t.TryGetProperty("MODSTR", out var modstr));
        Assert.Equal("50", ostr);
        Assert.Equal("20", modstr);

        Assert.True(t.TrySetProperty("OSTR", "100"));
        Assert.Equal(120, Str(t));

        Assert.True(e.RemoveEffectByMemory(Mem(t, SpellType.Strength)!));
        Assert.Equal(100, Str(t));
        Assert.Equal(100, t.Str);
        Assert.Equal(0, t.ModStr);
    }

    [Fact]
    public void B05_AgilityAndCunning_AreModifiersToo()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.Agility), Def(SpellType.Cunning));
        Character.MagicFlags = (int)MagicConfigFlags.StackStats;
        Apply(e, c, t, SpellType.Agility);
        Apply(e, c, t, SpellType.Cunning);

        Assert.Equal(50, t.Dex);
        Assert.Equal(20, t.ModDex);
        Assert.Equal(50, t.Int);
        Assert.Equal(20, t.ModInt);
        Assert.Equal(70, CombatEngine.EffectiveDex(t));
        Assert.Equal(70, CombatEngine.EffectiveInt(t));
    }

    // ---------------------------------------------------- B06: curse recast/floor

    /// <summary>B06: a recast curse lands where the first one did (10, not 41): the old
    /// memory is removed first (Spell_Effect_Create, CCharSpell.cpp:2056-2081) and the new
    /// add limits its penalty against the ADJUSTED stat (_CheckLimitEffectStat :1373).</summary>
    [Theory]
    [InlineData(SpellType.Weaken)]
    [InlineData(SpellType.Clumsy)]
    [InlineData(SpellType.Feeblemind)]
    [InlineData(SpellType.Curse)]
    public void B06_CurseRecast_LandsWhereTheFirstCastDid(SpellType id)
    {
        var (e, _, c, t, _) = Setup(Def(id, effect: 40));
        Apply(e, c, t, id);
        Apply(e, c, t, id);

        int actual = id switch
        {
            SpellType.Clumsy => CombatEngine.EffectiveDex(t),
            SpellType.Feeblemind => CombatEngine.EffectiveInt(t),
            _ => Str(t),
        };
        Assert.Equal(10, actual);
    }

    /// <summary>B06: with MODSTR=-30 the adjusted strength is 20, so a 40-point Weaken
    /// stops it at 1 (19 taken), not 0.</summary>
    [Fact]
    public void B06_CurseFloor_UsesTheAdjustedStat()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.Weaken, effect: 40));
        t.ModStr = -30;
        Apply(e, c, t, SpellType.Weaken);

        Assert.Equal(1, Str(t));
        Assert.Equal(19, Mem(t, SpellType.Weaken)!.MoreP.Y);
    }

    // ------------------------------------ B08: veto before aggression/reflection

    /// <summary>B08: the target's @SpellEffect runs before the harm checks
    /// (CCharSpell.cpp:3712-3730 before :3748-3811): a RETURN 1 makes no aggression
    /// memory and spends no reflection.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void B08_SpellEffectVeto_ComesBeforeAggressionAndReflection(bool reflection)
    {
        var fire = Def(SpellType.Fireball, 10, flags: SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage);
        var (e, _, c, t, _) = Setup(fire);
        e.TriggerDispatcher = Script("[EVENTS e_magic]\nON=@SpellEffect\nTAG.EFFECT_SEEN=1\nRETURN 1\n", t);
        if (reflection) t.SetStatFlag(StatFlag.Reflection);

        Apply(e, c, t, SpellType.Fireball);

        Assert.Equal("1", Tag(t, "EFFECT_SEEN"));
        Assert.Null(t.Memory_FindObjTypes(c.Uid, MemoryType.Aggreived));
        Assert.Equal(100, t.Hits);
        Assert.Equal(100, c.Hits);
        if (reflection)
            Assert.True(t.IsStatFlag(StatFlag.Reflection));
    }

    // ------------------------------------------------------ B09: CANHARMSELF

    /// <summary>B09: without MAGICF_CANHARMSELF a harmful spell on oneself is refused
    /// (CCharSpell.cpp:3750-3759): 100 hits stay 100, not 90. With the flag it lands.</summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData((int)MagicConfigFlags.CanHarmSelf, 90)]
    public void B09_HarmingOneself_NeedsCanHarmSelf(int magicFlags, int expectedHits)
    {
        var fire = Def(SpellType.Fireball, 10, flags: SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage);
        var (e, _, c, _, _) = Setup(fire);
        Character.MagicFlags = magicFlags;

        Apply(e, c, c, SpellType.Fireball);

        Assert.Equal(expectedHits, c.Hits);
    }

    // ---------------------------------------------------- B11-B15: persistence

    /// <summary>B11: a +200 the @EffectAdd stage suppressed (RETURN 0) is not applied by a
    /// save/load (50, not 250) - the load re-adds nothing, as upstream's does - and its
    /// later removal takes back nothing that was never given.</summary>
    [Fact]
    public void B11_SuppressedEffect_StaysSuppressedThroughSaveLoad()
    {
        var (e, w, c, t, r) = Setup(Def(effect: 200));
        e.TriggerDispatcher = Script("[SPELL 16]\nON=@EffectAdd\nRETURN 0\n");
        Apply(e, c, t);
        Assert.Equal(50, Str(t));

        var (reloaded, engine, _) = RoundTrip(w, r, t);

        Assert.Equal(50, Str(reloaded));
        var memory = Mem(reloaded, SpellType.Strength);
        Assert.NotNull(memory);
        Assert.True(engine.RemoveEffectByMemory(memory!));
        Assert.Equal(50, Str(reloaded));
    }

    /// <summary>B11/B05: an ordinary +200 comes back as 250 adjusted (MODSTR=200 in the
    /// record, the memory worn without re-adding), and its expiry leaves 50.</summary>
    [Fact]
    public void B11_OrdinaryEffect_ComesBackOnce_AndExpiresCleanly()
    {
        var (e, w, c, t, r) = Setup(Def(effect: 200));
        Apply(e, c, t);

        var (reloaded, engine, _) = RoundTrip(w, r, t);

        Assert.Equal(50, reloaded.Str);
        Assert.Equal(200, reloaded.ModStr);
        Assert.Equal(250, Str(reloaded));
        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(50, Str(reloaded));
        Assert.Null(Mem(reloaded, SpellType.Strength));
    }

    /// <summary>B12: Reactive Armour's share (EFFECT 200 / 10 = 20) is the memory's
    /// m_PolyStr (CCharSpell.cpp:1411) and survives a save/load (20, not 0).</summary>
    [Fact]
    public void B12_ReactiveArmorPercent_SurvivesSaveLoad()
    {
        var (e, w, c, t, r) = Setup(Def(SpellType.ReactiveArmor, effect: 200));
        Apply(e, c, t, SpellType.ReactiveArmor);
        Assert.Equal(20, t.ReactiveArmorPercent);

        var (reloaded, engine, _) = RoundTrip(w, r, t);

        Assert.Equal(20, reloaded.ReactiveArmorPercent);
        Assert.True(reloaded.IsStatFlag(StatFlag.Reactive));
        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(0, reloaded.ReactiveArmorPercent);
        Assert.False(reloaded.IsStatFlag(StatFlag.Reactive));
    }

    /// <summary>B13: a ticking effect is its memory with its charges and timer: a
    /// Regenerate saved mid-way comes back with them and keeps ticking.</summary>
    [Fact]
    public void B13_PeriodicEffect_SurvivesSaveLoad_AndKeepsTicking()
    {
        var regen = Def(SpellType.Regenerate, effect: 10,
            flags: SpellFlag.Good | SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Tick);
        var (e, w, c, t, r) = Setup(regen);
        Apply(e, c, t, SpellType.Regenerate);
        var memory = Mem(t, SpellType.Regenerate)!;
        uint charges = memory.More2;
        Assert.True(charges > 1);

        var (reloaded, engine, _) = RoundTrip(w, r, t);
        var loaded = Mem(reloaded, SpellType.Regenerate);

        Assert.NotNull(loaded);
        Assert.Equal(charges, loaded!.More2);
        reloaded.Hits = 40;
        engine.ProcessExpirations(Environment.TickCount64 + 2_100);
        Assert.Equal(50, reloaded.Hits);
        Assert.Equal(charges - 1, loaded.More2);
    }

    /// <summary>B13: the Blood Oath bond is the caster's memory LINKed to the enemy
    /// (CCharSpell.cpp:4115, :1312) and survives a save/load.</summary>
    [Fact]
    public void B13_BloodOathBond_SurvivesSaveLoad()
    {
        var (e, w, c, t, r) = Setup(Def(SpellType.BloodOath, effect: 0));
        Apply(e, c, t, SpellType.BloodOath);
        Assert.Equal(t.Uid, c.BloodOathEnemy);

        var (reloaded, _, _) = RoundTrip(w, r, c);

        Assert.Equal(t.Uid, reloaded.BloodOathEnemy);
        Assert.True(reloaded.BloodOathLevel > 0);
    }

    /// <summary>B14: the memory is an ordinary saved item, so its LINK (the caster), a
    /// script's TAG and a script-moved layer all come back.</summary>
    [Fact]
    public void B14_MemoryLinkTagAndLayer_SurviveSaveLoad()
    {
        var (e, w, c, t, r) = Setup();
        Apply(e, c, t);
        var memory = Mem(t, SpellType.Strength)!;
        memory.SetTag("OVERRIDE.MARK", "100");
        memory.EquipLayer = (Layer)47;
        memory.SetAttr(ObjAttributes.Move_Never);

        var (reloaded, _, _) = RoundTrip(w, r, t);
        var loaded = Mem(reloaded, SpellType.Strength);

        Assert.NotNull(loaded);
        Assert.Equal(c.Uid, loaded!.Link);
        Assert.Equal("100", Tag(loaded, "OVERRIDE.MARK"));
        Assert.Equal((Layer)47, loaded.EquipLayer);
        Assert.True(loaded.IsAttr(ObjAttributes.Move_Never));
    }

    /// <summary>B15: a memory with no timer (DURATION=0) has no timer after a restart
    /// either - nothing to recompute against a new uptime - so TIMER still reads -1 and
    /// a recast still switches it off (CCharSpell.cpp:2063-2069).</summary>
    [Fact]
    public void B15_PermanentTimer_IsStillOffAfterSaveLoad()
    {
        var (e, w, c, t, r) = Setup(Def(duration: 0));
        Apply(e, c, t);
        Assert.Equal(70, Str(t));

        var (reloaded, engine, _) = RoundTrip(w, r, t);
        var loaded = Mem(reloaded, SpellType.Strength)!;

        Assert.True(loaded.TryGetProperty("TIMER", out var timer));
        Assert.Equal("-1", timer);
        Assert.Equal(70, Str(reloaded));
        engine.ApplyDirectEffect(reloaded, reloaded, SpellType.Strength, 1000);
        Assert.Equal(50, Str(reloaded));
        Assert.Null(Mem(reloaded, SpellType.Strength));
    }

    // ------------------------------------------- B16-B18: the periodic lifecycle

    private static SpellDef RegenDef() => Def(SpellType.Regenerate, effect: 10,
        flags: SpellFlag.Good | SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Tick);

    /// <summary>B16: every ticking memory runs @SpellEffectTick and [SPELL] @EffectTick
    /// (CCharSpell.cpp:1974-1999), not only poison: RETURN 1 from either ends the effect
    /// without the tick (40 hits stay 40, not 50).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void B16_RegenerateTickStages_Run_AndReturnOneVetoes(bool charHook)
    {
        var (e, w, c, t, _) = Setup(RegenDef());
        t.Hits = 40;
        string prefix = charHook
            ? "[EVENTS e_magic]\nON=@SpellEffectTick"
            : $"[SPELL {(int)SpellType.Regenerate}]\nON=@EffectTick";
        var d = Script(prefix + "\nTAG.TICK_FIRED=1\nTAG.TICK_ARGO=<ARGO.UID>\nRETURN 1\n", t);
        if (charHook) WireHost(e, w, d); else e.TriggerDispatcher = d;

        Apply(e, c, t, SpellType.Regenerate);
        var memory = Mem(t, SpellType.Regenerate)!;
        e.ProcessExpirations(Environment.TickCount64 + 2_100);

        Assert.Equal("1", Tag(t, "TICK_FIRED"));
        Assert.Equal(memory.Uid.Value, (uint)NumTag(t, "TICK_ARGO"));
        Assert.Equal(40, t.Hits);
        Assert.True(memory.IsDeleted);
    }

    /// <summary>B16: a script-made SPELLFLAG_TICK spell with a layer ticks its stage
    /// when its memory timer runs out (the default branch, CCharSpell.cpp:1966-1970).</summary>
    [Fact]
    public void B16_GenericTickSpell_RunsItsStage()
    {
        var def = Def((SpellType)900, duration: 10);
        def.Flags = SpellFlag.Tick;
        def.Layer = (Layer)70;
        var (e, _, c, t, _) = Setup(def);
        e.TriggerDispatcher = Script("[SPELL 900]\nON=@EffectTick\nTAG.CUSTOM_TICK=1\nTAG.CUSTOM_CHARGES=<LOCAL.Charges>\n");

        Apply(e, c, t, (SpellType)900);
        e.ProcessExpirations(Environment.TickCount64 + 1_500);

        Assert.Equal("1", Tag(t, "CUSTOM_TICK"));
        Assert.Equal(1, NumTag(t, "CUSTOM_CHARGES"));
    }

    /// <summary>B16: LOCAL.Effect written by the stage is what the tick heals.</summary>
    [Fact]
    public void B16_TickStage_EffectReadback()
    {
        var (e, _, c, t, _) = Setup(RegenDef());
        t.Hits = 40;
        e.TriggerDispatcher = Script($"[SPELL {(int)SpellType.Regenerate}]\nON=@EffectTick\nLOCAL.Effect=25\n");

        Apply(e, c, t, SpellType.Regenerate);
        e.ProcessExpirations(Environment.TickCount64 + 2_100);

        Assert.Equal(65, t.Hits);
    }

    /// <summary>B17: the memory's fields ARE the periodic state. Strangle at Spirit
    /// Speak 100.0 has power 10 (MOREY) and 10 charges (MORE2), its timer is the 5 s to
    /// the first tick; after it, 9 charges remain (CCharSpell.cpp:1227-1230, :2032-2035).</summary>
    [Fact]
    public void B17_StrangleMemory_CarriesPowerChargesAndNextTick()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.Strangle, effect: 0,
            flags: SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Tick));
        Character.MagicFlags = 0;
        Apply(e, c, t, SpellType.Strangle);
        var memory = Mem(t, SpellType.Strangle)!;

        Assert.Equal(10, memory.MoreP.Y);
        Assert.Equal(10u, memory.More2);
        long remaining = memory.Timeout - Environment.TickCount64;
        Assert.InRange(remaining, 4_500, 5_000);

        long now = Environment.TickCount64 + 5_100;
        e.ProcessExpirations(now);

        Assert.Equal(9u, memory.More2);
        Assert.Equal(10, memory.MoreP.Y);
        Assert.Equal(4_000, memory.Timeout - now);
    }

    /// <summary>B17: MORE2 = 0 ends a Regenerate at its next tick without healing
    /// (CCharSpell.cpp:1784-1785).</summary>
    [Fact]
    public void B17_ZeroCharges_StopTheTick()
    {
        var (e, _, c, t, _) = Setup(RegenDef());
        t.Hits = 40;
        Apply(e, c, t, SpellType.Regenerate);
        var memory = Mem(t, SpellType.Regenerate)!;
        memory.More2 = 0;

        e.ProcessExpirations(Environment.TickCount64 + 2_100);

        Assert.Equal(40, t.Hits);
        Assert.True(memory.IsDeleted);
    }

    /// <summary>B17: TIMER 0 on the memory runs its next tick at once (50, not 40 with
    /// the effect gone): the timer is the tick clock, not a separate expiry.</summary>
    [Fact]
    public void B17_TimerZero_RunsTheTickNow()
    {
        var (e, _, c, t, _) = Setup(RegenDef());
        t.Hits = 40;
        Apply(e, c, t, SpellType.Regenerate);
        var memory = Mem(t, SpellType.Regenerate)!;

        Assert.True(memory.TryExecuteCommand("TIMER", "0", null!));
        e.ProcessExpirations(Environment.TickCount64 + 1);

        Assert.Equal(50, t.Hits);
        Assert.False(memory.IsDeleted);
    }

    /// <summary>B18: Strangle's next delay comes from the ticks done BEFORE the charge
    /// is spent: 5 s to the first, then 4, 3, 2, 1, 1 (CCharSpell.cpp:1934-1949) - the
    /// first reschedule used to be 3 s.</summary>
    [Fact]
    public void B18_StrangleDelays_Are_4_3_2_1()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.Strangle, effect: 0,
            flags: SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Tick));
        t.Hits = t.MaxHits = 30_000;
        Apply(e, c, t, SpellType.Strangle);
        var memory = Mem(t, SpellType.Strangle)!;

        long now = memory.Timeout;
        var delays = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            e.ProcessExpirations(now);
            delays.Add(memory.Timeout - now);
            now = memory.Timeout;
        }

        Assert.Equal([4_000L, 3_000L, 2_000L, 1_000L, 1_000L], delays);
    }

    // ------------------------------------------------------- B19: Evil Omen

    /// <summary>B19: Evil Omen is an ordinary memory on LAYER_SPELL_Evil_Omen (58)
    /// (CCharSpell.cpp:4122-4124): its add stage runs, Remove Curse takes it, and a
    /// spent omen is gone.</summary>
    [Fact]
    public void B19_EvilOmen_IsAMemoryInTheLifecycle()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.EvilOmen, effect: 10,
            flags: SpellFlag.TargChar | SpellFlag.Harm));
        e.TriggerDispatcher = Script($"[SPELL {(int)SpellType.EvilOmen}]\nON=@EffectAdd\nTAG.OMEN_ADDED=1\n");

        Apply(e, c, t, SpellType.EvilOmen);

        var omen = Mem(t, SpellType.EvilOmen);
        Assert.NotNull(omen);
        Assert.Equal(SpellLayers.EvilOmen, omen!.EquipLayer);
        Assert.Equal("1", Tag(t, "OMEN_ADDED"));
        Assert.True(t.EvilOmenActive);

        e.StripCurseEffects(t);

        Assert.True(omen.IsDeleted);
        Assert.False(t.ConsumeEvilOmen());
    }

    [Fact]
    public void B19_ConsumingTheOmen_DeletesItsMemory()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.EvilOmen, effect: 10,
            flags: SpellFlag.TargChar | SpellFlag.Harm));
        Apply(e, c, t, SpellType.EvilOmen);
        var omen = Mem(t, SpellType.EvilOmen)!;

        Assert.True(t.ConsumeEvilOmen());
        Assert.True(omen.IsDeleted);
        Assert.False(t.ConsumeEvilOmen());
    }

    // ------------------------------------------------------ B20: death policy

    /// <summary>B20: death is Spell_Dispel(100) (CCharAct.cpp:4397): the ordinary stat
    /// memory goes, a MOVE_NEVER one stays (70, not 50), and a necromancy memory outside
    /// LAYER_SPELL_STATS..Summon stays with its effect (Corpse Skin's -15 fire).</summary>
    [Theory]
    [InlineData("ordinary")]
    [InlineData("movenever")]
    [InlineData("necromancy")]
    public void B20_Death_IsSpellDispel100(string mode)
    {
        var id = mode == "necromancy" ? SpellType.CorpseSkin : SpellType.Strength;
        var (e, _, c, t, _) = Setup(Def(id));
        Apply(e, c, t, id);
        if (mode == "movenever")
            Mem(t, id)!.SetAttr(ObjAttributes.Move_Never);

        t.SetStatFlag(StatFlag.Dead);
        e.ClearAllEffectsOnDeath(t);

        Assert.Equal(mode != "ordinary", Mem(t, id) != null);
        if (mode == "ordinary") Assert.Equal(50, Str(t));
        if (mode == "movenever") Assert.Equal(70, Str(t));
        if (mode == "necromancy") Assert.Equal(-15, t.ResFire);
    }

    // --------------------------------------------- B21: ARGN1 readback / ARGO

    /// <summary>B21: @Effect's ARGN1 is read back into the spell the native switch runs
    /// (spell = m_iN1, CCharSpell.cpp:3732): a Strength turned into Agility raises DEX.</summary>
    [Fact]
    public void B21_EffectArgn1_ChangesTheSpellApplied()
    {
        var (e, _, c, t, _) = Setup(Def(), Def(SpellType.Agility));
        e.TriggerDispatcher = Script("[SPELL 16]\nON=@Effect\nARGN1=9\n");

        Apply(e, c, t);

        Assert.Equal(50, Str(t));
        Assert.Equal(70, CombatEngine.EffectiveDex(t));
    }

    /// <summary>B21: the source item is @SpellEffect's ARGO (pSourceItem,
    /// CCharSpell.cpp:3703) - a wand's UID on a wand cast, a potion's on a drink.</summary>
    [Fact]
    public void B21_SourceItem_IsTheSpellEffectArgo()
    {
        var (e, w, c, t, _) = Setup();
        c.PrivLevel = PrivLevel.GM;
        var wand = w.CreateItem();
        wand.ItemType = ItemType.Wand;
        wand.MoreP = new Point3D(16, 1000, 0, 0);
        wand.More2 = 3;
        wand.SetAttr(ObjAttributes.Magic);
        c.Equip(wand, Layer.OneHanded);
        c.SetTag("WAND_UID", wand.Uid.Value.ToString());
        e.TriggerDispatcher = Script("[EVENTS e_magic]\nON=@SpellEffect\nTAG.FX_ARGO=<ARGO.UID>\n", t);

        c.BeginCast(SpellType.Strength, t.Uid, t.Position);
        Assert.True(e.CastDone(c));
        Assert.Equal(wand.Uid.Value, (uint)NumTag(t, "FX_ARGO"));

        var potion = w.CreateItem();
        potion.ItemType = ItemType.Potion;
        t.RemoveTag("FX_ARGO");
        e.ApplyDirectEffect(t, t, SpellType.Strength, 500, potion);
        Assert.Equal(potion.Uid.Value, (uint)NumTag(t, "FX_ARGO"));
    }

    // ------------------------------------------------ B25: classic save memories

    private (GameWorld W, Character T, SpellEngine E) LoadClassic(string worldText)
    {
        var w = World();
        string file = Path.Combine(Path.GetTempPath(), $"classic-spell-{Guid.NewGuid():N}.scp");
        _temp.Add(file);
        File.WriteAllText(file, worldText);
        new WorldLoader(LoggerFactory.Create(_ => { })).LoadFile(w, file);
        var t = w.FindChar(new Serial(1))!;
        var r = new SpellRegistry();
        r.Register(Def());
        var e = new SpellEngine(w, r);
        e.RestorePersistedEffectsFromWorld();
        return (w, t, e);
    }

    /// <summary>B25: a classic save states the base (OSTR=50), the modifier the spell put
    /// on (MODSTR=20) and the IT_SPELL memory on layer 32 (MOREX=16, MOREY=20). It loads
    /// to 70 - not 90 - and deleting the memory takes the 20 back (50, not 70), as
    /// upstream's removal does (CCharAct.cpp:560 -> Spell_Effect_Remove).</summary>
    [Fact]
    public void B25_ClassicSpellMemory_JoinsTheLifecycle()
    {
        var (w, t, _) = LoadClassic(
            "[WORLDCHAR 0190]\nSERIAL=01\nNAME=Legacy\nOSTR=50\nODEX=50\nOINT=50\nMODSTR=20\nP=100,100,0,0\n" +
            $"[WORLDITEM 02085]\nSERIAL=040000001\nTYPE={(int)ItemType.Spell}\nMOREP=16,20,0,0\nCONT=01\nLAYER=32\nTIMER=60\nLINK=01\n");

        var memory = t.FindLayer(SpellLayers.Stats);
        Assert.NotNull(memory);
        Assert.Equal(70, Str(t));

        w.DeleteObject(memory!);

        Assert.Equal(50, Str(t));
        Assert.Equal(0, t.ModStr);
    }

    /// <summary>B25: a hand-written Source-X world snippet - TYPE=t_spell by name, an
    /// IT_SPELL memory on LAYER_SPELL_STATS with its timer and caster - loads into a
    /// working effect that expires on its own clock and takes back exactly its modifier.</summary>
    [Fact]
    public void B25_SourceXSnippet_LoadsAWorkingRemovableTickingEffect()
    {
        var (_, t, e) = LoadClassic(
            "[WORLDCHAR c_man]\nSERIAL=01\nNAME=Blessed\nBODY=0190\nP=100,100,0,0\nOSTR=60\nODEX=55\nOINT=40\n" +
            "MODSTR=15\nFLAGS=0\n" +
            "[WORLDITEM i_rune_strength]\nSERIAL=040000002\nID=02085\nTYPE=t_spell\nATTR=0204\n" +
            "MOREP=16,15,0\nMORE2=1\nLINK=01\nCONT=01\nLAYER=32\nTIMER=30\n");

        var memory = t.FindLayer(SpellLayers.Stats);
        Assert.NotNull(memory);
        Assert.Equal(ItemType.Spell, memory!.ItemType);
        Assert.Equal(75, Str(t));                               // 60 + 15, not 90

        e.ProcessExpirations(Environment.TickCount64 + 31_000);

        Assert.True(memory.IsDeleted);
        Assert.Equal(60, Str(t));
        Assert.Equal(60, t.Str);
    }

    /// <summary>B25: a classic memory on a spell layer past the equipment slots (Evil Omen,
    /// 58) is worn, not dropped into the backpack.</summary>
    [Fact]
    public void B25_ClassicMemoryOnAHighSpellLayer_IsWorn()
    {
        var (_, t, _) = LoadClassic(
            "[WORLDCHAR 0190]\nSERIAL=01\nNAME=Omened\nP=100,100,0,0\n" +
            $"[WORLDITEM 02085]\nSERIAL=040000003\nTYPE={(int)ItemType.Spell}\nMOREP={(int)SpellType.EvilOmen},10,0,0\nCONT=01\nLAYER=58\n");

        Assert.True(t.EvilOmenActive);
        Assert.True(t.ConsumeEvilOmen());
        Assert.False(t.EvilOmenActive);
    }
}
