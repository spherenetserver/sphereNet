using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Where a spell memory is worn and what it does, the Source-X way:
/// a potion's memory goes on LAYER_FLAG_Potion (OnSpellEffect, CCharSpell.cpp:3884-4151);
/// the native add/remove is chosen by the definition's LAYER first (:1021, :579);
/// the elemental Reactive Armor / Magic Reflection / Protection branches
/// (:1393, :1628, :1666 and :877, :895, :910); and the Explosion timer gate (:3960).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellDeliveryLayerAndWardParityTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        Character.CombatFlags = 0;
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
        Character.CombatFlags = 0;
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
        w.PlaceCharacter(c, new Point3D((short)x, 100, 0, 0));
        return c;
    }

    private static SpellDef Def(SpellType id, int effect = 20, int duration = 600,
        SpellFlag flags = SpellFlag.Good | SpellFlag.TargChar, Layer layer = Layer.None) => new()
    {
        Id = id, Name = id.ToString(), Flags = flags, Layer = layer,
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

    private static Item Potion(GameWorld w)
    {
        var potion = w.CreateItem();
        potion.ItemType = ItemType.Potion;
        return potion;
    }

    private static List<Item> Mems(Character c, SpellType spell) =>
        c.Memories.Where(m => !m.IsDeleted && m.ItemType == ItemType.Spell && m.MoreP.X == (int)spell).ToList();

    private static Item? Mem(Character c, SpellType spell) => Mems(c, spell).FirstOrDefault();

    private static int Level(Item mem) => (ushort)mem.MoreP.Y;

    private static void Expire(SpellEngine e) => e.ProcessExpirations(Environment.TickCount64 + 120_000);

    private TriggerDispatcher Script(string content)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string file = Path.Combine(Path.GetTempPath(), $"delivery-layer-{Guid.NewGuid():N}.scp");
        _temp.Add(file);
        File.WriteAllText(file, content);
        stack.Resources.LoadResourceFile(file);
        stack.Dispatcher.BuildUsedTriggerCache();
        return stack.Dispatcher;
    }

    private (Character Reloaded, SpellEngine Engine) RoundTrip(GameWorld src, SpellRegistry registry, Character subject)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"delivery-layer-save-{Guid.NewGuid():N}");
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
        return (reloaded, engine);
    }

    // --------------------------------------------- N04: the potion's own layer

    /// <summary>A Strength cast (+20, LAYER_SPELL_STATS) and a Strength potion (+20,
    /// LAYER_FLAG_Potion) stack to 90 (:3884) - the potion no longer deletes the spell.</summary>
    [Fact]
    public void Potion_StacksWithTheSpell_OnItsOwnLayer()
    {
        var (e, w, c, t, _) = Setup(Def(SpellType.Strength, layer: SpellLayers.Stats));
        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        e.ApplyDirectEffect(t, t, SpellType.Strength, 1000, Potion(w));

        Assert.Equal(90, CombatEngine.EffectiveStr(t));
        var mems = Mems(t, SpellType.Strength);
        Assert.Equal(2, mems.Count);
        Assert.Contains(mems, m => m.EquipLayer == SpellLayers.Stats);
        Assert.Contains(mems, m => m.EquipLayer == SpellLayers.FlagPotion);
    }

    /// <summary>A second potion replaces only the first potion (Spell_Effect_Create clears
    /// its own layer, :2056-2081); the cast keeps its memory.</summary>
    [Fact]
    public void Potion_ReplacesOnlyThePotion()
    {
        var (e, w, c, t, _) = Setup(Def(SpellType.Strength, layer: SpellLayers.Stats));
        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        e.ApplyDirectEffect(t, t, SpellType.Strength, 1000, Potion(w));
        var spellMem = t.FindLayer(SpellLayers.Stats)!;
        var firstPotion = t.FindLayer(SpellLayers.FlagPotion)!;

        e.ApplyDirectEffect(t, t, SpellType.Strength, 1000, Potion(w));

        Assert.Equal(90, CombatEngine.EffectiveStr(t));
        Assert.False(spellMem.IsDeleted);
        Assert.True(firstPotion.IsDeleted);
        Assert.Equal(2, Mems(t, SpellType.Strength).Count);
    }

    /// <summary>Spell_Dispel takes LAYER_SPELL_STATS..LAYER_SPELL_Summon only (:79-104):
    /// the spell goes, the potion's +20 stays (70, not 50). Death's dispel half is the
    /// same; the potion layer goes with the corpse's flag-layer sweep.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispelAndDeathDispel_LeaveThePotion(bool death)
    {
        var (e, w, c, t, _) = Setup(Def(SpellType.Strength, layer: SpellLayers.Stats));
        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        e.ApplyDirectEffect(t, t, SpellType.Strength, 1000, Potion(w));

        if (death)
            e.ClearAllEffectsOnDeath(t);
        else
            e.StripDispellableEffects(t);

        Assert.Equal(70, CombatEngine.EffectiveStr(t));
        Assert.Null(t.FindLayer(SpellLayers.Stats));
        Assert.NotNull(t.FindLayer(SpellLayers.FlagPotion));

        Expire(e);
        Assert.Equal(50, CombatEngine.EffectiveStr(t));
    }

    /// <summary>Both memories survive a save/load on their layers, without re-adding,
    /// and each one's expiry takes back exactly its own +20.</summary>
    [Fact]
    public void PotionAndSpell_SurviveSaveLoad()
    {
        var (e, w, c, t, r) = Setup(Def(SpellType.Strength, layer: SpellLayers.Stats));
        e.ApplyDirectEffect(c, t, SpellType.Strength, 1000);
        e.ApplyDirectEffect(t, t, SpellType.Strength, 1000, Potion(w));

        var (reloaded, engine) = RoundTrip(w, r, t);

        Assert.Equal(90, CombatEngine.EffectiveStr(reloaded));
        Assert.NotNull(reloaded.FindLayer(SpellLayers.Stats));
        Assert.NotNull(reloaded.FindLayer(SpellLayers.FlagPotion));
        Expire(engine);
        Assert.Equal(50, CombatEngine.EffectiveStr(reloaded));
    }

    /// <summary>Spells whose case equips a fixed layer keep it from a potion too
    /// (Reactive Armor, :3896-3897).</summary>
    [Fact]
    public void Potion_FixedLayerSpell_KeepsItsLayer()
    {
        var (e, w, _, t, _) = Setup(Def(SpellType.ReactiveArmor, effect: 200, layer: SpellLayers.Reactive));
        e.ApplyDirectEffect(t, t, SpellType.ReactiveArmor, 1000, Potion(w));
        Assert.Equal(SpellLayers.Reactive, Mem(t, SpellType.ReactiveArmor)!.EquipLayer);
    }

    // ------------------------------------- N05: the elemental ward branches

    private static void ElementalTarget(Character c, Character t)
    {
        Character.CombatFlags = (int)CombatFlags.ElementalEngine;
        c.SetSkill(SkillType.EvalInt, 1000);
        c.SetSkill(SkillType.Meditation, 1000);
        c.SetSkill(SkillType.Inscription, 1000);
        t.ResPhysical = t.ResFire = t.ResCold = t.ResPoison = t.ResEnergy = 50;
        t.SetSkill(SkillType.MagicResistance, 600);
        t.SetSkill(SkillType.Inscription, 0);
        t.Tags.Set(SpellCastingProperties.FasterCasting, "3");
    }

    private static long FasterCasting(Character t) => t.Tags.GetInt(SpellCastingProperties.FasterCasting);

    private static void AssertResists(Character t, int physical, int others)
    {
        Assert.Equal(physical, t.ResPhysical);
        Assert.Equal(others, t.ResFire);
        Assert.Equal(others, t.ResCold);
        Assert.Equal(others, t.ResPoison);
        Assert.Equal(others, t.ResEnergy);
    }

    /// <summary>Reactive Armor under COMBAT_ELEMENTAL_ENGINE: 15 + 1000/200 = 20 onto
    /// physical, 5 off the rest, no reflection (:1393-1404); the removal gives it back.</summary>
    [Fact]
    public void Elemental_ReactiveArmor_ShiftsResists_AndUndoes()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.ReactiveArmor, effect: 200));
        ElementalTarget(c, t);

        e.ApplyDirectEffect(c, t, SpellType.ReactiveArmor, 1000);

        AssertResists(t, 70, 45);
        Assert.False(t.IsStatFlag(StatFlag.Reactive));
        Assert.Equal(0, t.ReactiveArmorPercent);
        Assert.Equal(20, Level(Mem(t, SpellType.ReactiveArmor)!));

        Expire(e);
        AssertResists(t, 50, 50);
    }

    /// <summary>Magic Reflection under the elemental engine: 25 - 1000/200 = 20 off
    /// physical, 10 onto the rest, the reflection flag still on (:1628-1641).</summary>
    [Fact]
    public void Elemental_MagicReflect_ShiftsResists_AndUndoes()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.MagicReflect, effect: 200));
        ElementalTarget(c, t);

        e.ApplyDirectEffect(c, t, SpellType.MagicReflect, 1000);

        AssertResists(t, 30, 60);
        Assert.True(t.IsStatFlag(StatFlag.Reflection));
        Assert.Equal(20, Level(Mem(t, SpellType.MagicReflect)!));

        Expire(e);
        AssertResists(t, 50, 50);
        Assert.False(t.IsStatFlag(StatFlag.Reflection));
    }

    /// <summary>Protection under the elemental engine (:1666-1691): level
    /// min(75, 3000/40) = 75; physical -(15 - 5) = 40; Faster Casting -2 = 1; Magic
    /// Resistance - min(600, 350) = 250; no AR. The removal gives every amount back.</summary>
    [Fact]
    public void Elemental_Protection_Contributions_AndUndo()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.Protection, effect: 200));
        ElementalTarget(c, t);

        e.ApplyDirectEffect(c, t, SpellType.Protection, 1000);

        var mem = Mem(t, SpellType.Protection)!;
        Assert.Equal(75, Level(mem));
        Assert.Equal(40, t.ResPhysical);
        Assert.Equal(50, t.ResFire);
        Assert.Equal(1, FasterCasting(t));
        Assert.Equal(250, t.GetSkill(SkillType.MagicResistance));
        Assert.Equal(0, t.ProtectionArmor);

        Expire(e);
        Assert.Equal(50, t.ResPhysical);
        Assert.Equal(3, FasterCasting(t));
        Assert.Equal(600, t.GetSkill(SkillType.MagicResistance));
    }

    /// <summary>A recast replaces the ward: the old removal and the new add land on the
    /// same values (no compounding).</summary>
    [Fact]
    public void Elemental_Protection_Recast_DoesNotCompound()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.Protection, effect: 200));
        ElementalTarget(c, t);

        e.ApplyDirectEffect(c, t, SpellType.Protection, 1000);
        e.ApplyDirectEffect(c, t, SpellType.Protection, 1000);

        Assert.Single(Mems(t, SpellType.Protection));
        Assert.Equal(40, t.ResPhysical);
        Assert.Equal(1, FasterCasting(t));
        Assert.Equal(250, t.GetSkill(SkillType.MagicResistance));
    }

    /// <summary>The wards' changes are saved character state: a save/load keeps them
    /// without applying them twice, and the loaded memory still undoes them.</summary>
    [Fact]
    public void Elemental_Wards_SurviveSaveLoad()
    {
        var (e, w, c, t, r) = Setup(Def(SpellType.Protection, effect: 200), Def(SpellType.ReactiveArmor, effect: 200));
        ElementalTarget(c, t);
        e.ApplyDirectEffect(c, t, SpellType.Protection, 1000);
        e.ApplyDirectEffect(c, t, SpellType.ReactiveArmor, 1000);

        var (reloaded, engine) = RoundTrip(w, r, t);

        Assert.Equal(60, reloaded.ResPhysical);           // 50 - 10 + 20
        Assert.Equal(45, reloaded.ResFire);
        Assert.Equal(1, FasterCasting(reloaded));
        Assert.Equal(250, reloaded.GetSkill(SkillType.MagicResistance));
        Assert.Equal(0, reloaded.ProtectionArmor);
        Assert.False(reloaded.IsStatFlag(StatFlag.Reactive));

        Expire(engine);
        AssertResists(reloaded, 50, 50);
        Assert.Equal(3, FasterCasting(reloaded));
        Assert.Equal(600, reloaded.GetSkill(SkillType.MagicResistance));
    }

    /// <summary>Dispel removes the wards with their undo.</summary>
    [Fact]
    public void Elemental_Wards_Dispel_Undoes()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.MagicReflect, effect: 200), Def(SpellType.Protection, effect: 200));
        ElementalTarget(c, t);
        e.ApplyDirectEffect(c, t, SpellType.MagicReflect, 1000);
        e.ApplyDirectEffect(c, t, SpellType.Protection, 1000);

        e.StripDispellableEffects(t);

        AssertResists(t, 50, 50);
        Assert.Equal(3, FasterCasting(t));
        Assert.Equal(600, t.GetSkill(SkillType.MagicResistance));
    }

    /// <summary>The removal reads the memory's own level (m_spelllevel, :882): a script
    /// that rewrote MOREY is obeyed.</summary>
    [Fact]
    public void Elemental_ReactiveArmor_UndoReadsTheMemoryLevel()
    {
        var (e, _, c, t, _) = Setup(Def(SpellType.ReactiveArmor, effect: 200));
        ElementalTarget(c, t);
        e.ApplyDirectEffect(c, t, SpellType.ReactiveArmor, 1000);
        var mem = Mem(t, SpellType.ReactiveArmor)!;
        mem.MoreP = new Point3D(mem.MoreP.X, 10, mem.MoreP.Z, mem.MoreP.Map);

        Assert.True(e.RemoveEffectByMemory(mem));

        Assert.Equal(60, t.ResPhysical);
        Assert.Equal(50, t.ResFire);
    }

    /// <summary>SPELLFLAG_NO_ELEMENTALENGINE and the classic mode keep the classic
    /// effects: the reflection flag and the AR ward, resists untouched.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptOutAndClassic_KeepClassicWards(bool elementalWithOptOut)
    {
        var flags = SpellFlag.Good | SpellFlag.TargChar | (elementalWithOptOut ? SpellFlag.NoElementalEngine : 0);
        var (e, _, c, t, _) = Setup(Def(SpellType.ReactiveArmor, effect: 200, flags: flags),
            Def(SpellType.Protection, effect: 200, flags: flags));
        ElementalTarget(c, t);
        if (!elementalWithOptOut)
            Character.CombatFlags = 0;

        e.ApplyDirectEffect(c, t, SpellType.ReactiveArmor, 1000);
        e.ApplyDirectEffect(c, t, SpellType.Protection, 1000);

        Assert.True(t.IsStatFlag(StatFlag.Reactive));
        Assert.Equal(20, t.ReactiveArmorPercent);
        Assert.Equal(200, t.ProtectionArmor);
        AssertResists(t, 50, 50);
        Assert.Equal(3, FasterCasting(t));
        Assert.Equal(600, t.GetSkill(SkillType.MagicResistance));

        Expire(e);
        Assert.False(t.IsStatFlag(StatFlag.Reactive));
        Assert.Equal(0, t.ProtectionArmor);
    }

    // -------------------------------- N11: the definition layer picks the effect

    private const SpellType Custom = (SpellType)400;

    /// <summary>A custom spell defined on a native layer gets that layer's shared effect
    /// (:1197 Invis, :1208 Paralyze, :1143 Night Sight) and its removal (:711, :715, :676).</summary>
    [Theory]
    [InlineData("invis")]
    [InlineData("paralyze")]
    [InlineData("nightsight")]
    public void CustomSpell_DefinitionLayer_SelectsNativeEffect(string kind)
    {
        var (layer, flag) = kind switch
        {
            "invis" => (SpellLayers.Invis, StatFlag.Invisible),
            "paralyze" => (SpellLayers.Paralyze, StatFlag.Freeze),
            _ => (SpellLayers.NightSight, StatFlag.NightSight),
        };
        var (e, _, c, t, _) = Setup(Def(Custom, layer: layer));

        e.ApplyDirectEffect(c, t, Custom, 1000);

        var mem = Mem(t, Custom)!;
        Assert.Equal(layer, mem.EquipLayer);
        Assert.True(t.IsStatFlag(flag));

        Assert.True(e.RemoveEffectByMemory(mem));
        Assert.False(t.IsStatFlag(flag));
    }

    /// <summary>@EffectAdd RETURN 0 keeps the memory but skips the layer effect; RETURN 1
    /// deletes it (:1000-1014).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CustomSpell_EffectAddVerdict_GatesTheLayerEffect(int verdict)
    {
        var (e, _, c, t, _) = Setup(Def(Custom, layer: SpellLayers.Invis));
        e.TriggerDispatcher = Script($"[SPELL 400]\nON=@EffectAdd\nRETURN {verdict}\n");

        e.ApplyDirectEffect(c, t, Custom, 1000);

        Assert.False(t.IsStatFlag(StatFlag.Invisible));
        Assert.Equal(verdict == 0, Mem(t, Custom) != null);
    }

    /// <summary>The memory's runtime layer does not choose the effect: from a potion the
    /// custom Invis spell sits on LAYER_FLAG_Potion and still makes its drinker
    /// invisible; a memory a script moved to another layer is still undone by its
    /// definition's effect.</summary>
    [Fact]
    public void RuntimeLayer_DoesNotOverrideTheDefinitionLayer()
    {
        var (e, w, c, t, _) = Setup(Def(Custom, layer: SpellLayers.Invis));

        e.ApplyDirectEffect(t, t, Custom, 1000, Potion(w));

        var mem = Mem(t, Custom)!;
        Assert.Equal(SpellLayers.FlagPotion, mem.EquipLayer);
        Assert.True(t.IsStatFlag(StatFlag.Invisible));

        Expire(e);
        Assert.False(t.IsStatFlag(StatFlag.Invisible));
        Assert.Null(Mem(t, Custom));
    }

    // ------------------------------------------- N12: the Explosion timer gate

    private static SpellDef ExplosionDef(int duration, Layer layer) =>
        Def(SpellType.Explosion, effect: 20, duration: duration,
            flags: SpellFlag.Damage | SpellFlag.Harm | SpellFlag.TargChar, layer: layer);

    /// <summary>OnSpellEffect's Explosion case (:3960-3964): a positive duration makes
    /// the LAYER_SPELL_Explosion memory whether or not the definition names a layer, and
    /// it deals the second blast when it runs out; a zero duration makes none, even with
    /// LAYER=80.</summary>
    [Theory]
    [InlineData(50, false, true)]
    [InlineData(0, true, false)]
    [InlineData(0, false, false)]
    [InlineData(50, true, true)]
    public void Explosion_DelayedMemory_FollowsDuration(int duration, bool declaredLayer, bool expectMemory)
    {
        var (e, _, c, t, _) = Setup(ExplosionDef(duration, declaredLayer ? SpellLayers.Explosion : Layer.None));

        e.ApplyDirectEffect(c, t, SpellType.Explosion, 1000);
        int firstBlast = 100 - t.Hits;
        Assert.True(firstBlast > 0);

        var mems = Mems(t, SpellType.Explosion);
        Assert.Equal(expectMemory ? 1 : 0, mems.Count);
        Assert.All(mems, m => Assert.Equal(SpellLayers.Explosion, m.EquipLayer));
        Assert.All(mems, m => Assert.True(m.Timeout > 0));

        Expire(e);
        Assert.Equal(100 - (expectMemory ? 2 : 1) * firstBlast, t.Hits);
        Assert.Null(Mem(t, SpellType.Explosion));
    }

    /// <summary>A potion never makes the Explosion timer (!fPotion, :3962).</summary>
    [Fact]
    public void Explosion_FromAPotion_MakesNoTimer()
    {
        var (e, w, c, t, _) = Setup(ExplosionDef(50, SpellLayers.Explosion));
        e.ApplyDirectEffect(c, t, SpellType.Explosion, 1000, Potion(w));
        Assert.Null(Mem(t, SpellType.Explosion));
    }

    /// <summary>A recast never cuts a pending timer short (:2076): both run.</summary>
    [Fact]
    public void Explosion_Recast_StacksTimers()
    {
        var (e, _, c, t, _) = Setup(ExplosionDef(50, Layer.None));
        e.ApplyDirectEffect(c, t, SpellType.Explosion, 1000);
        e.ApplyDirectEffect(c, t, SpellType.Explosion, 1000);
        Assert.Equal(2, Mems(t, SpellType.Explosion).Count);
    }

    /// <summary>A pending Explosion timer survives a save/load and still goes off.</summary>
    [Fact]
    public void Explosion_Timer_SurvivesSaveLoad()
    {
        var (e, w, c, t, r) = Setup(ExplosionDef(50, Layer.None));
        e.ApplyDirectEffect(c, t, SpellType.Explosion, 1000);
        int hits = t.Hits;
        int firstBlast = 100 - hits;

        var (reloaded, engine) = RoundTrip(w, r, t);

        Assert.NotNull(Mem(reloaded, SpellType.Explosion));
        Expire(engine);
        Assert.Equal(hits - firstBlast, reloaded.Hits);
    }
}
