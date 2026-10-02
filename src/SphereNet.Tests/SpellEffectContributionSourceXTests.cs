using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a spell effect contributes, kept on its memory the way Source-X keeps it: the
/// necromancy forms' bodies and shares (CCharSpell.cpp:1038-1083, 626-661), Corpse
/// Skin's fields (:1332), Curse Weapon on the weapon (:1355, CCharAct.cpp:548/3418),
/// Mind Rot on LOWERMANACOST (:1351, CResourceCalc.cpp:545), Invisibility ending the
/// Hiding (:1197), LOCAL.Delay=0 on both tick paths (:2001/:2032) and the tick
/// damage keeping NODISTURB / NOUNPARALYZE (CCharFight.cpp:798/881).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellEffectContributionSourceXTests : IDisposable
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

    private static SpellDef Def(SpellType id, int effect = 20, int duration = 600,
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
        foreach (var d in defs) r.Register(d);
        return (new SpellEngine(w, r), w, c, t, r);
    }

    private static void Apply(SpellEngine e, Character c, Character t, SpellType id) =>
        e.ApplyDirectEffect(c, t, id, 1000);

    private static Item? Mem(Character c, SpellType spell) =>
        c.Memories.FirstOrDefault(m => !m.IsDeleted && m.ItemType == ItemType.Spell && m.MoreP.X == (int)spell);

    private static short MemPolyStr(Item m) => unchecked((short)(m.More1 & 0xFFFF));
    private static short MemPolyDex(Item m) => unchecked((short)(m.More1 >> 16));
    private static int MemLevel(Item m) => (ushort)m.MoreP.Y;

    private static void SetMemFields(Item m, short polyStr, short polyDex, uint charges, int level)
    {
        m.More1 = (ushort)polyStr | ((uint)(ushort)polyDex << 16);
        m.More2 = charges;
        m.MoreP = new Point3D(m.MoreP.X, (short)level, m.MoreP.Z, m.MoreP.Map);
    }

    private TriggerDispatcher Script(string content)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string file = Path.Combine(Path.GetTempPath(), $"magic-contrib-{Guid.NewGuid():N}.scp");
        _temp.Add(file);
        File.WriteAllText(file, content);
        stack.Resources.LoadResourceFile(file);
        stack.Dispatcher.BuildUsedTriggerCache();
        return stack.Dispatcher;
    }

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

    private (Character Reloaded, SpellEngine Engine, GameWorld World) RoundTrip(
        GameWorld src, SpellRegistry registry, Character subject)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"magic-contrib-save-{Guid.NewGuid():N}");
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

    private static void SetRegen(Character c, int value)
    {
        c.TrySetProperty("REGENVALHITS", value.ToString());
        c.TrySetProperty("REGENVALMANA", value.ToString());
        c.TrySetProperty("REGENVALSTAM", value.ToString());
    }

    /// <summary>Write the memory's fields from the character's @SpellEffectAdd - what a
    /// script's ARGO.MORE1L / MORE1H / MORE2 / MOREY does before the native add reads them.</summary>
    private static void PresetFields(short polyStr, short polyDex, uint charges, int level = -1) =>
        Character.OnSpellEffectAdd = (_, _, mem, _) =>
        {
            SetMemFields(mem, polyStr, polyDex, charges, level >= 0 ? level : MemLevel(mem));
            return TriggerResult.Default;
        };

    // --------------------------------------------------------- N06: form bodies

    [Theory]
    [InlineData(SpellType.HorrificBeast, 0x2EA)]
    [InlineData(SpellType.WraithForm, 0x01A)]
    [InlineData(SpellType.LichForm, 0x018)]
    [InlineData(SpellType.VampiricEmbrace, 0x13D)]
    public void NecroForm_TakesItsBody_AndRemovalRestoresThePreviousOne(SpellType form, int body)
    {
        var (e, _, c, _, _) = Setup(Def(form));
        Apply(e, c, c, form);

        Assert.Equal((ushort)body, c.BodyId);
        Assert.Equal((ushort)0x190, c.OBody);
        Assert.True(c.IsStatFlag(StatFlag.Polymorph));

        Assert.True(e.RemoveEffectByMemory(Mem(c, form)!));
        Assert.Equal((ushort)0x190, c.BodyId);
        Assert.Equal((ushort)0, c.OBody);
        Assert.False(c.IsStatFlag(StatFlag.Polymorph));
    }

    [Fact]
    public void NecroForm_SwitchingForms_RestoresTheOriginalBody()
    {
        var (e, _, c, _, _) = Setup(Def(SpellType.WraithForm), Def(SpellType.LichForm));
        c.ResFire = c.ResPhysical = c.ResEnergy = c.ResPoison = c.ResCold = 50;
        Apply(e, c, c, SpellType.WraithForm);
        Apply(e, c, c, SpellType.LichForm);

        Assert.Null(Mem(c, SpellType.WraithForm));
        Assert.Equal((ushort)0x018, c.BodyId);
        Assert.Equal((ushort)0x190, c.OBody);
        Assert.Equal(50, c.ResPhysical);           // Wraith's +15 went with it

        e.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal((ushort)0x190, c.BodyId);
        Assert.Equal(50, c.ResFire);
        Assert.Equal(50, c.ResPoison);
        Assert.Equal(50, c.ResCold);
    }

    [Fact]
    public void NecroForm_BodyAndShare_SurviveSaveLoad_AndComeOffAfterwards()
    {
        var (e, w, c, _, r) = Setup(Def(SpellType.LichForm));
        SetRegen(c, 20);
        c.ResFire = c.ResPoison = c.ResCold = 50;
        PresetFields(5, 2, 7);
        Apply(e, c, c, SpellType.LichForm);
        Character.OnSpellEffectAdd = null;
        Assert.Equal(25, c.RegenValMana);

        var (reloaded, engine, _) = RoundTrip(w, r, c);

        Assert.Equal((ushort)0x018, reloaded.BodyId);
        Assert.Equal((ushort)0x190, reloaded.OBody);
        Assert.Equal(25, reloaded.RegenValMana);     // not applied a second time
        Assert.Equal(18, reloaded.RegenValHits);
        Assert.Equal(43, reloaded.ResFire);

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal((ushort)0x190, reloaded.BodyId);
        Assert.Equal(20, reloaded.RegenValMana);
        Assert.Equal(20, reloaded.RegenValHits);
        Assert.Equal(50, reloaded.ResFire);
        Assert.Equal(50, reloaded.ResPoison);
        Assert.Equal(50, reloaded.ResCold);
    }

    /// <summary>A Lich memory saved before the forms took a body (no OBODY, still the
    /// human body, the fixed -10/+10/+10 already in the record) is taken in as the form
    /// it is, and its removal takes back exactly what that add applied.</summary>
    [Fact]
    public void LegacyLichMemory_IsAdopted_AndRemovedWithoutDrift()
    {
        var (e, w, c, _, _) = Setup(Def(SpellType.LichForm));
        c.ResFire = 40; c.ResPoison = 60; c.ResCold = 60;     // base 50 with the old shift
        var mem = w.CreateItem();
        mem.BaseId = 0x2085;
        mem.ItemType = ItemType.Spell;
        mem.MoreP = new Point3D((short)SpellType.LichForm, 15, 0, 0);
        mem.More2 = 1;
        c.MemoryState.AttachSpellEffect(mem, SpellLayers.ForSpell(SpellType.LichForm, null));

        Assert.Equal(1, e.RestorePersistedEffects(c));
        Assert.Equal((ushort)0x018, c.BodyId);
        Assert.True(c.LichFormActive);

        Assert.True(e.RemoveEffectByMemory(mem));
        Assert.Equal((ushort)0x190, c.BodyId);
        Assert.Equal(50, c.ResFire);
        Assert.Equal(50, c.ResPoison);
        Assert.Equal(50, c.ResCold);
    }

    // ------------------------------------------------ N07: shares off the memory

    [Fact]
    public void LichForm_ShareIsTheMemorysFields()
    {
        var (e, _, c, _, _) = Setup(Def(SpellType.LichForm));
        SetRegen(c, 20);
        c.ResFire = c.ResPoison = c.ResCold = 50;
        PresetFields(5, 2, 7);

        Apply(e, c, c, SpellType.LichForm);
        Assert.Equal(25, c.RegenValMana);
        Assert.Equal(18, c.RegenValHits);
        Assert.Equal(43, c.ResFire);
        Assert.Equal(57, c.ResPoison);
        Assert.Equal(57, c.ResCold);

        Assert.True(e.RemoveEffectByMemory(Mem(c, SpellType.LichForm)!));
        Assert.Equal(20, c.RegenValMana);
        Assert.Equal(20, c.RegenValHits);
        Assert.Equal(50, c.ResFire);
        Assert.Equal(50, c.ResPoison);
        Assert.Equal(50, c.ResCold);
    }

    [Fact]
    public void VampiricEmbrace_ShareIsTheMemorysFields()
    {
        var (e, _, c, _, _) = Setup(Def(SpellType.VampiricEmbrace));
        SetRegen(c, 20);
        c.ResFire = 50;
        PresetFields(5, 2, 7, level: 20);

        Apply(e, c, c, SpellType.VampiricEmbrace);
        Assert.Equal(5, CombatEngine.GetOnHitPropertyValue(c, null, "HITLEECHLIFE"));
        Assert.Equal(22, c.RegenValStam);
        Assert.Equal(27, c.RegenValMana);
        Assert.Equal(30, c.ResFire);
        Assert.Equal(20, MemLevel(Mem(c, SpellType.VampiricEmbrace)!));

        Assert.True(e.RemoveEffectByMemory(Mem(c, SpellType.VampiricEmbrace)!));
        Assert.Equal(0, CombatEngine.GetOnHitPropertyValue(c, null, "HITLEECHLIFE"));
        Assert.Equal(20, c.RegenValStam);
        Assert.Equal(20, c.RegenValMana);
        Assert.Equal(50, c.ResFire);
    }

    [Fact]
    public void HorrificBeast_ShareIsTheMemorysCharges()
    {
        var (e, _, c, _, _) = Setup(Def(SpellType.HorrificBeast));
        SetRegen(c, 20);
        PresetFields(0, 0, 7);

        Apply(e, c, c, SpellType.HorrificBeast);
        Assert.Equal(27, c.RegenValHits);
        Assert.True(c.HorrificBeastActive);

        Assert.True(e.RemoveEffectByMemory(Mem(c, SpellType.HorrificBeast)!));
        Assert.Equal(20, c.RegenValHits);
        Assert.False(c.HorrificBeastActive);
    }

    [Fact]
    public void WraithForm_SetsItsOwnFields_AndShiftsResists()
    {
        var (e, _, c, _, _) = Setup(Def(SpellType.WraithForm));
        c.ResPhysical = c.ResFire = c.ResEnergy = 50;

        Apply(e, c, c, SpellType.WraithForm);
        var mem = Mem(c, SpellType.WraithForm)!;
        Assert.Equal(15, MemPolyStr(mem));
        Assert.Equal(5, MemPolyDex(mem));
        Assert.Equal(5u, mem.More2);
        Assert.Equal(65, c.ResPhysical);
        Assert.Equal(45, c.ResFire);
        Assert.Equal(45, c.ResEnergy);

        Assert.True(e.RemoveEffectByMemory(mem));
        Assert.Equal(50, c.ResPhysical);
        Assert.Equal(50, c.ResFire);
        Assert.Equal(50, c.ResEnergy);
    }

    [Fact]
    public void CorpseSkin_KeepsItsShiftOnTheMemory()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.CorpseSkin, flags: SpellFlag.Harm | SpellFlag.TargChar));
        t.ResFire = t.ResPoison = t.ResCold = t.ResPhysical = 50;

        Apply(e, c, t, SpellType.CorpseSkin);
        var mem = Mem(t, SpellType.CorpseSkin)!;
        Assert.Equal(10, MemPolyStr(mem));
        Assert.Equal(15, MemPolyDex(mem));
        Assert.Equal(35, t.ResFire);
        Assert.Equal(35, t.ResPoison);
        Assert.Equal(60, t.ResCold);
        Assert.Equal(60, t.ResPhysical);

        Assert.True(e.RemoveEffectByMemory(mem));
        Assert.Equal(50, t.ResFire);
        Assert.Equal(50, t.ResPoison);
        Assert.Equal(50, t.ResCold);
        Assert.Equal(50, t.ResPhysical);
    }

    // ------------------------------------------------- N08: Curse Weapon on the weapon

    private static Item Sword(GameWorld w, int leech)
    {
        var sword = w.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        if (leech != 0)
            sword.SetTag("HITLEECHLIFE", leech.ToString());
        return sword;
    }

    private static int Leech(Item weapon) => CombatEngine.GetItemNumProperty(weapon, "HITLEECHLIFE");

    [Fact]
    public void CurseWeapon_WithoutAWeapon_LeavesNoMemory()
    {
        var (e, _, c, _, _) = Setup(Def(SpellType.CurseWeapon, effect: 12));
        Apply(e, c, c, SpellType.CurseWeapon);
        Assert.Null(Mem(c, SpellType.CurseWeapon));
    }

    [Fact]
    public void CurseWeapon_FollowsTheWeaponOnAndOff()
    {
        var (e, w, c, _, _) = Setup(Def(SpellType.CurseWeapon, effect: 12));
        var sword = Sword(w, 7);
        Assert.True(c.Equip(sword, Layer.OneHanded));

        Apply(e, c, c, SpellType.CurseWeapon);
        var mem = Mem(c, SpellType.CurseWeapon)!;
        Assert.Equal(50, MemLevel(mem));
        Assert.Equal(57, Leech(sword));

        // Taken off: the curse leaves with it; back on: it comes back.
        Assert.Same(sword, c.Unequip(Layer.OneHanded));
        Assert.Equal(7, Leech(sword));
        Assert.True(c.Equip(sword, Layer.OneHanded));
        Assert.Equal(57, Leech(sword));

        // Another weapon in hand takes the curse instead.
        c.Unequip(Layer.OneHanded);
        var axe = Sword(w, 0);
        Assert.True(c.Equip(axe, Layer.OneHanded));
        Assert.Equal(7, Leech(sword));
        Assert.Equal(50, Leech(axe));

        e.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(0, Leech(axe));
        Assert.Equal(7, Leech(sword));
    }

    [Fact]
    public void CurseWeapon_SavedWeapon_IsNotCursedTwiceOnLoad()
    {
        var (e, w, c, _, r) = Setup(Def(SpellType.CurseWeapon, effect: 12));
        var sword = Sword(w, 7);
        Assert.True(c.Equip(sword, Layer.OneHanded));
        Apply(e, c, c, SpellType.CurseWeapon);

        var (reloaded, engine, world) = RoundTrip(w, r, c);
        var loadedSword = world.FindItem(sword.Uid)!;
        Assert.Equal(57, Leech(loadedSword));

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(7, Leech(loadedSword));
        Assert.Null(Mem(reloaded, SpellType.CurseWeapon));
    }

    // ------------------------------------------------------- N09: Mind Rot

    [Fact]
    public void MindRot_TakesTenOffLowerManaCost_AndTheCostIsTheOrdinaryFormula()
    {
        var w = World();
        var gm = Char(w);
        gm.PrivLevel = PrivLevel.GM;
        var r = new SpellRegistry();
        r.Register(Def(SpellType.MindRot, effect: 20, flags: SpellFlag.Harm | SpellFlag.TargChar));
        r.Register(new SpellDef
        {
            Id = SpellType.MagicArrow, Name = "MagicArrow",
            Flags = SpellFlag.TargChar | SpellFlag.Damage, ManaCost = 100, CastTimeBase = 1,
        });
        var e = new SpellEngine(w, r);

        var caster = w.CreateCharacter();                 // a creature: no book, no reagents
        caster.SetSkill(SkillType.Magery, 1000);
        caster.MaxMana = 200; caster.Mana = 200;
        caster.MaxHits = 100; caster.Hits = 100;
        caster.SetTag("LOWERMANACOST", "50");
        w.PlaceCharacter(caster, new Point3D(102, 100, 0, 0));
        var target = Char(w, 103);

        Apply(e, gm, caster, SpellType.MindRot);
        Assert.Equal(40, caster.Tags.GetInt("LOWERMANACOST"));
        Assert.Equal(10, MemLevel(Mem(caster, SpellType.MindRot)!));

        Assert.True(e.CastStart(caster, SpellType.MagicArrow, target.Uid, target.Position) >= 0);
        Assert.True(e.CastDone(caster));
        Assert.Equal(200 - 60, caster.Mana);              // 100 - 100*40/100

        Assert.True(e.RemoveEffectByMemory(Mem(caster, SpellType.MindRot)!));
        Assert.Equal(50, caster.Tags.GetInt("LOWERMANACOST"));
    }

    // ------------------------------------------------- N10: Invisibility and Hiding

    [Fact]
    public void Invisibility_EndsTheHiding_SoItsEndLeavesTheCharacterVisible()
    {
        var (e, _, c, _, _) = Setup(Def(SpellType.Invisibility));
        c.SetStatFlag(StatFlag.Hidden);

        Apply(e, c, c, SpellType.Invisibility);
        Assert.True(c.IsStatFlag(StatFlag.Invisible));
        Assert.False(c.IsStatFlag(StatFlag.Hidden));

        e.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.False(c.IsStatFlag(StatFlag.Invisible));
        Assert.False(c.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void Invisibility_RevealVeto_KeepsTheHiding()
    {
        var (e, _, c, _, _) = Setup(Def(SpellType.Invisibility));
        c.SetStatFlag(StatFlag.Hidden);
        Character.OnRevealing = _ => false;              // @Reveal RETURN 1

        Apply(e, c, c, SpellType.Invisibility);
        Assert.True(c.IsStatFlag(StatFlag.Invisible));
        Assert.True(c.IsStatFlag(StatFlag.Hidden));
    }

    // --------------------------------------------------- N13: LOCAL.Delay=0

    private static SpellDef TickDef()
    {
        var def = Def((SpellType)900, duration: 10);
        def.Flags = SpellFlag.Tick;
        def.Layer = (Layer)70;
        return def;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroTickDelay_IsKept_OnBothPaths_AndDoesNotRerunInTheSamePass(bool host)
    {
        var (e, w, c, t, _) = Setup(TickDef());
        var d = Script("[SPELL 900]\nON=@EffectTick\nLOCAL.Delay=0\nLOCAL.Charges=3\n");
        if (host) WireHost(e, w, d); else e.TriggerDispatcher = d;

        Apply(e, c, t, (SpellType)900);
        var mem = Mem(t, (SpellType)900)!;
        long now = Environment.TickCount64 + 1_500;
        e.ProcessExpirations(now);

        Assert.False(mem.IsDeleted);
        Assert.Equal(2u, mem.More2);
        Assert.Equal(now + 1, mem.Timeout);              // not the 5 s default

        e.ProcessExpirations(now + 1);                    // the next pass runs it again
        Assert.Equal(now + 2, mem.Timeout);
    }

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("1.5", true, 1500)]
    [InlineData("-1", false, 0)]
    public void TickDelayReadback_Policy(string value, bool read, int ms)
    {
        var locals = new SphereNet.Scripting.Variables.VarMap();
        locals.Set("Delay", value);
        Assert.Equal(read, SpellEngine.TryReadTickDelay(locals, out int delayMs));
        Assert.Equal(ms, delayMs);
    }

    // ----------------------------------- N14: tick damage keeps NODISTURB / NOUNPARALYZE

    private static void HarmTick(DamageType type) =>
        Character.OnSpellEffectTick = (_, ctx) =>
        {
            ctx.Damage = 10;
            ctx.DamageType = (int)type;
            return true;
        };

    private static SpellDef HarmTickDef()
    {
        var def = TickDef();
        def.Flags = SpellFlag.Tick | SpellFlag.Harm;
        return def;
    }

    private static void StartCasting(SpellEngine e, GameWorld w, Character caster, Character target)
    {
        var pack = w.CreateItem();
        pack.ItemType = ItemType.Container;
        caster.Equip(pack, Layer.Pack);
        var book = w.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.More1 = 1u << ((int)SpellType.Clumsy - 1);
        pack.AddItem(book);
        Assert.True(e.CastStart(caster, SpellType.Clumsy, target.Uid, target.Position) > 0);
        Assert.True(caster.IsCasting);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TickDamage_NoDisturb_KeepsTheCast(bool noDisturb)
    {
        var clumsy = Def(SpellType.Clumsy);
        clumsy.CastTimeBase = 50;
        clumsy.InterruptBase = 1000;
        clumsy.InterruptScale = 1000;
        var (e, w, c, t, _) = Setup(HarmTickDef(), clumsy);
        Apply(e, c, t, (SpellType)900);
        StartCasting(e, w, t, c);
        HarmTick(DamageType.Magic | DamageType.Fire | (noDisturb ? DamageType.NoDisturb : 0));

        e.ProcessExpirations(Environment.TickCount64 + 1_500);

        Assert.Equal(90, t.Hits);
        Assert.Equal(noDisturb, t.IsCasting);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TickDamage_NoUnparalyze_KeepsTheParalysis(bool noUnparalyze)
    {
        var (e, _, c, t, _) = Setup(HarmTickDef(), Def(SpellType.Paralyze, flags: SpellFlag.Harm | SpellFlag.TargChar));
        Character.BreakParalyzeHook = e.BreakParalyze;   // the server's wiring of the damage entry
        Character.ResolveSpellDef = e.GetSpellDef;
        Apply(e, c, t, SpellType.Paralyze);
        Assert.True(t.IsStatFlag(StatFlag.Freeze));
        Apply(e, c, t, (SpellType)900);
        HarmTick(DamageType.Magic | DamageType.Fire | (noUnparalyze ? DamageType.NoUnparalyze : 0));

        e.ProcessExpirations(Environment.TickCount64 + 1_500);

        Assert.Equal(90, t.Hits);
        Assert.Equal(noUnparalyze, t.IsStatFlag(StatFlag.Freeze));
        Assert.Equal(noUnparalyze, Mem(t, SpellType.Paralyze) != null);
    }
}
