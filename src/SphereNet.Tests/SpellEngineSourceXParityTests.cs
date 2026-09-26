using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// SpellEngine behaviour that used to be invented, pinned to what Source-X does
/// (CCharSpell.cpp): damage bonuses, Mind Blast, field shape, the @SpellSuccess /
/// @SpellFail contracts, durations, mana drain, resist scope, mana on failure,
/// field touch, Incognito, Dispel and the smaller defaults.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellEngineSourceXParityTests
{
    private static (GameWorld World, SpellEngine Engine, Character Caster, Character Target)
        Setup(params SpellDef[] defs) => Setup(null, defs);

    private static (GameWorld World, SpellEngine Engine, Character Caster, Character Target)
        Setup(ScriptRuntimeStack? stack, params SpellDef[] defs)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var registry = new SpellRegistry();
        foreach (var d in defs)
            registry.Register(d);
        var engine = new SpellEngine(world, registry) { TriggerDispatcher = stack?.Dispatcher };

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.Str = 100; caster.MaxHits = 100; caster.Hits = 100;
        caster.Int = 100; caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Str = 100; target.MaxHits = 100; target.Hits = 100;
        target.Dex = 50; target.Int = 50; target.MaxMana = 50; target.Mana = 50;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (world, engine, caster, target);
    }

    private static (ScriptRuntimeStack Stack, string Path) Scripts(string text)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sxmagic-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, text);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(path);
        return (stack, path);
    }

    private static SpellDef Flat(SpellType id, SpellFlag flags, int effect = 0, int durationTenths = 0,
        ushort mana = 0) => new()
    {
        Id = id,
        Name = id.ToString(),
        Flags = flags,
        ManaCost = mana,
        CastTimeBase = 1,
        EffectBase = effect,
        EffectScale = effect,
        DurationBase = durationTenths,
        DurationScale = durationTenths,
    };

    // --- 1: spell damage bonuses only under MAGICF_OSIFORMULAS ---------------

    [Fact]
    public void EvalIntAddsNothingWithoutOsiFormulas()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.Harm, SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage, effect: 20));
        caster.SetSkill(SkillType.EvalInt, 1000);
        Character.MagicFlags = 0;

        engine.ApplyDirectEffect(caster, target, SpellType.Harm, 500);

        Assert.Equal(80, target.Hits);
    }

    [Fact]
    public void OsiSpellDamageBonusFollowsSourceX()
    {
        var (_, _, caster, target) = Setup();
        caster.SetSkill(SkillType.EvalInt, 1000);       // x (3000/1000 + 1) = x4
        caster.SetSkill(SkillType.Inscription, 1000);   // +10 %
        caster.Int = 100;                               // +10 %
        caster.SetTag("INCREASESPELLDAM", "30");        // 15 cap in PvP

        // 10 * 4 = 40, then +35 % = 54.
        Assert.Equal(54, SpellEngine.ApplyOsiSpellDamageBonus(caster, target, 10));

        target.IsPlayer = false;                        // no PvP cap: +50 %
        Assert.Equal(60, SpellEngine.ApplyOsiSpellDamageBonus(caster, target, 10));
    }

    // --- 2: Mind Blast hands the INT difference over as the skill level -------

    [Fact]
    public void MindBlastRunsTheEffectCurveAtHalfTheIntDifference()
    {
        var def = new SpellDef
        {
            Id = SpellType.MindBlast, Name = "Mind Blast", CastTimeBase = 1,
            Flags = SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage,
            EffectBase = 0, EffectScale = 1000,          // effect == randomized level
        };
        var (_, engine, caster, target) = Setup(def);
        caster.Int = 100; target.Int = 20;              // level = min(40, 100/2)

        Assert.True(engine.CastStart(caster, SpellType.MindBlast, target.Uid, target.Position) > 0);
        Assert.True(engine.CastDone(caster));

        // Level 40 randomizes to 20..39 (:3631) and Mind Blast is cold damage.
        Assert.InRange(100 - target.Hits, 20, 39);
    }

    [Fact]
    public void AReboundingMindBlastHarmsNoOneWithoutCanHarmSelf()
    {
        var def = new SpellDef
        {
            Id = SpellType.MindBlast, Name = "Mind Blast", CastTimeBase = 1,
            Flags = SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage,
            EffectBase = 0, EffectScale = 1000,
        };
        var (_, engine, caster, target) = Setup(def);
        Character.MagicFlags = 0;
        caster.Int = 10; target.Int = 90;

        Assert.True(engine.CastStart(caster, SpellType.MindBlast, target.Uid, target.Position) > 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(100, caster.Hits);
        Assert.Equal(100, target.Hits);
    }

    // --- 4: fields are 3 wide unless @Success says otherwise ------------------

    private static int FieldSegments(GameWorld world) =>
        world.GetAllObjects().OfType<Item>().Count(i => !i.IsDeleted && i.TryGetTag("FIELD_SPELL", out _));

    [Fact]
    public void AFieldIsThreeTilesWideByDefault()
    {
        var (world, engine, caster, _) = Setup(Flat(SpellType.FireField,
            SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Damage | SpellFlag.Field, durationTenths: 600));

        Assert.True(engine.CastStart(caster, SpellType.FireField, Serial.Invalid, new Point3D(105, 100, 0, 0)) > 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(3, FieldSegments(world));
    }

    [Fact]
    public void SuccessLocalFieldWidthWidensTheField()
    {
        var (stack, path) = Scripts($"[SPELL {(int)SpellType.FireField}]\nON=@Success\nlocal.FieldWidth=5\n");
        try
        {
            var (world, engine, caster, _) = Setup(stack, Flat(SpellType.FireField,
                SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Damage | SpellFlag.Field, durationTenths: 600));

            Assert.True(engine.CastStart(caster, SpellType.FireField, Serial.Invalid, new Point3D(105, 100, 0, 0)) > 0);
            Assert.True(engine.CastDone(caster));

            Assert.Equal(5, FieldSegments(world));
        }
        finally { File.Delete(path); }
    }

    // --- 6/7: Reactive Armor and Protection --------------------------------------

    [Fact]
    public void ReactiveArmorDoesNotReflectSpellDamage()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.Harm, SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage, effect: 20));
        target.SetStatFlag(StatFlag.Reactive);
        target.ReactiveArmorPercent = 50;

        engine.ApplyDirectEffect(caster, target, SpellType.Harm, 500);

        Assert.Equal(80, target.Hits);
        Assert.Equal(100, caster.Hits);
    }

    [Fact]
    public void ProtectionOnlyAddsArmor()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.Protection, SpellFlag.TargChar | SpellFlag.Good, effect: 15, durationTenths: 600));

        engine.ApplyDirectEffect(caster, target, SpellType.Protection, 500);

        Assert.Equal(15, target.ProtectionArmor);
        Assert.False(target.IsStatFlag(StatFlag.ArcherCanMove));
    }

    // --- 8: SPELLFLAG_SCRIPTED does nothing native on a character --------------

    [Fact]
    public void AScriptedSpellHasNoNativeEffectOnACharacter()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.Strength, SpellFlag.TargChar | SpellFlag.Scripted, effect: 10, durationTenths: 600));

        engine.ApplyDirectEffect(caster, target, SpellType.Strength, 500);

        Assert.Equal(100, target.Str);
    }

    // --- 9: @Success runs before the cast is paid, @Fail prices a failure -------

    [Fact]
    public void SuccessReturnOneAbortsBeforeAnythingIsPaid()
    {
        var (stack, path) = Scripts($"[SPELL {(int)SpellType.Heal}]\nON=@Success\nRETURN 1\n");
        try
        {
            var (_, engine, caster, target) = Setup(stack,
                Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 30, mana: 10));
            target.Hits = 10;

            Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
            Assert.False(engine.CastDone(caster));

            Assert.Equal(100, caster.Mana);
            Assert.Equal(10, target.Hits);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SuccessLocalDurationIsTheEffectsDuration()
    {
        var (stack, path) = Scripts($"[SPELL {(int)SpellType.Strength}]\nON=@Success\nlocal.Duration=50\n");
        try
        {
            var (_, engine, caster, target) = Setup(stack,
                Flat(SpellType.Strength, SpellFlag.TargChar | SpellFlag.Good, effect: 10, durationTenths: 6000));

            Assert.True(engine.CastStart(caster, SpellType.Strength, target.Uid, target.Position) > 0);
            Assert.True(engine.CastDone(caster));
            Assert.Equal(110, target.Str);

            // 5 s, not the 10 minutes of the def.
            engine.ProcessExpirations(Environment.TickCount64 + 6_000);
            Assert.Equal(100, target.Str);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailArgn2IsTheManaAFailedCastLoses()
    {
        var (stack, path) = Scripts($"[SPELL {(int)SpellType.Heal}]\nON=@Fail\nARGN2=3\n");
        bool saved = Character.ManaLossAbort;
        try
        {
            Character.ManaLossAbort = true;
            var (_, engine, caster, target) = Setup(stack,
                Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 30, mana: 20));
            caster.PrivLevel = PrivLevel.Player;   // a failure is priced only below GM
            caster.IsPlayer = false;               // an NPC needs no spellbook

            Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
            target.SetStatFlag(StatFlag.Dead);          // the cast aborts at completion
            Assert.False(engine.CastDone(caster));

            Assert.Equal(97, caster.Mana);
        }
        finally
        {
            Character.ManaLossAbort = saved;
            File.Delete(path);
        }
    }

    // --- 11: durations -----------------------------------------------------------

    [Fact]
    public void OsiDurationFormulasReplaceTheCurve()
    {
        var (_, _, caster, target) = Setup();
        caster.SetSkill(SkillType.Magery, 1000);
        caster.SetSkill(SkillType.SpiritSpeak, 1000);
        var protection = Flat(SpellType.Protection, SpellFlag.TargChar, durationTenths: 1234);
        var paralyze = Flat(SpellType.Paralyze, SpellFlag.TargChar, durationTenths: 1234);
        var bloodOath = Flat(SpellType.BloodOath, SpellFlag.TargChar, durationTenths: 1234);

        Character.MagicFlags = 0;
        Assert.Equal(1234, SpellEngine.GetSpellDuration(protection, 1000, caster, target));
        // Necromancy always uses its formula: 8 + (1000 - 0)/80 = 20 s.
        Assert.Equal(200, SpellEngine.GetSpellDuration(bloodOath, 1000, caster, target));

        Character.MagicFlags = (int)MagicConfigFlags.OsiFormulas;
        Assert.Equal(2000, SpellEngine.GetSpellDuration(protection, 1000, caster, target));  // 1000*2/10
        Assert.Equal(270, SpellEngine.GetSpellDuration(paralyze, 1000, caster, target));     // 7 + 1000/50
        Character.MagicFlags = 0;
    }

    [Fact]
    public void AZeroDurationEffectHasNoTimer()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.Paralyze, SpellFlag.TargChar | SpellFlag.Harm, durationTenths: 0));

        engine.ApplyDirectEffect(caster, target, SpellType.Paralyze, 500);
        Assert.True(target.IsStatFlag(StatFlag.Freeze));

        // No 30 s floor: it is still there a day later.
        engine.ProcessExpirations(Environment.TickCount64 + 86_400_000);
        Assert.True(target.IsStatFlag(StatFlag.Freeze));
    }

    // --- 12: Mana Drain / Mana Vampire -----------------------------------------

    [Fact]
    public void ManaDrainTakesAndGivesBackButFeedsNoOne()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.ManaDrain, SpellFlag.TargChar | SpellFlag.Harm, effect: 20, durationTenths: 50));
        Character.MagicFlags = 0;
        caster.Mana = 10;

        engine.ApplyDirectEffect(caster, target, SpellType.ManaDrain, 500);
        Assert.Equal(30, target.Mana);
        Assert.Equal(10, caster.Mana);

        engine.ProcessExpirations(Environment.TickCount64 + 6_000);
        Assert.Equal(50, target.Mana);
    }

    [Fact]
    public void ManaVampireMovesAllTheManaWithoutOsiFormulas()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.ManaVampire, SpellFlag.TargChar | SpellFlag.Harm, effect: 5));
        Character.MagicFlags = 0;
        caster.Mana = 10;

        engine.ApplyDirectEffect(caster, target, SpellType.ManaVampire, 500);

        Assert.Equal(0, target.Mana);
        Assert.Equal(60, caster.Mana);
    }

    // --- 13: the resist roll reduces damage only ---------------------------------

    [Fact]
    public void ResistReducesDamageButNotACurse()
    {
        var (stack, path) = Scripts(
            $"[SPELL {(int)SpellType.Weaken}]\nON=@Effect\nlocal.Resist=50\n" +
            $"[SPELL {(int)SpellType.Harm}]\nON=@Effect\nlocal.Resist=50\n");
        try
        {
            var (_, engine, caster, target) = Setup(stack,
                Flat(SpellType.Weaken, SpellFlag.TargChar | SpellFlag.Harm, effect: 10, durationTenths: 600),
                Flat(SpellType.Harm, SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage, effect: 40));
            Character.MagicFlags = 0;

            engine.ApplyDirectEffect(caster, target, SpellType.Weaken, 500);
            Assert.Equal(90, target.Str);                // the whole 10

            engine.ApplyDirectEffect(caster, target, SpellType.Harm, 500);
            Assert.Equal(target.MaxHits - 20, target.Hits);   // 40 halved
        }
        finally { File.Delete(path); }
    }

    // --- 14: mana on success and on failure ------------------------------------

    [Fact]
    public void ASuccessfulCastPaysTheFullCostWhateverManaLossPercentSays()
    {
        int savedPct = Character.ManaLossPercent;
        try
        {
            Character.ManaLossPercent = 50;
            var (_, engine, caster, target) = Setup(
                Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 1, mana: 20));
            // A GM caster: no fizzle roll, no book or reagent gate - and still pays.
            Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
            Assert.True(engine.CastDone(caster));

            Assert.Equal(80, caster.Mana);
        }
        finally { Character.ManaLossPercent = savedPct; }
    }

    [Fact]
    public void AnAbortedCastLosesThePercentOfTheLoweredCost()
    {
        int savedPct = Character.ManaLossPercent;
        bool savedAbort = Character.ManaLossAbort;
        try
        {
            Character.ManaLossPercent = 50;
            Character.ManaLossAbort = true;
            var (_, engine, caster, target) = Setup(
                Flat(SpellType.Heal, SpellFlag.TargChar | SpellFlag.Heal, effect: 1, mana: 20));
            caster.PrivLevel = PrivLevel.Player;   // a failure is priced only below GM
            caster.IsPlayer = false;               // an NPC needs no spellbook
            caster.SetTag("LOWERMANACOST", "50");        // 20 -> 10

            Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) > 0);
            target.SetStatFlag(StatFlag.Dead);
            Assert.False(engine.CastDone(caster));

            Assert.Equal(95, caster.Mana);               // 50 % of 10
        }
        finally
        {
            Character.ManaLossPercent = savedPct;
            Character.ManaLossAbort = savedAbort;
        }
    }

    // --- 15: a paralyze field paralyzes by the spell, not a fixed 300 ------------

    [Fact]
    public void AParalyzeFieldTouchUsesTheFieldsOwnDuration()
    {
        var (world, engine, caster, target) = Setup(
            Flat(SpellType.ParalyzeField, SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Field, durationTenths: 100));
        Character.MagicFlags = 0;
        var field = world.CreateItem();
        field.ItemType = ItemType.Spell;
        field.MoreP = new Point3D((short)SpellType.ParalyzeField, 500, 0, 0);
        field.Link = caster.Uid;
        world.PlaceItem(field, target.Position);

        Assert.Equal(FieldTouchResult.SpellHit, engine.ApplyFieldTouch(target, field));
        Assert.True(target.IsStatFlag(StatFlag.Freeze));

        engine.ProcessExpirations(Environment.TickCount64 + 11_000);
        Assert.False(target.IsStatFlag(StatFlag.Freeze));
    }

    // --- 16: Incognito re-hues skin and hair and puts them back -------------------

    [Fact]
    public void IncognitoRandomizesSkinAndHairAndRestoresThem()
    {
        var (world, engine, caster, target) = Setup(
            Flat(SpellType.Incognito, SpellFlag.Good, durationTenths: 100));
        target.BodyId = 0x0190;
        target.Hue = new Color(0x83EA);
        var hair = world.CreateItem();
        hair.BaseId = 0x203B;
        hair.Hue = new Color(0x0450);
        Assert.True(target.Equip(hair, Layer.Hair));

        engine.ApplyDirectEffect(caster, target, SpellType.Incognito, 500);

        Assert.True(target.IsStatFlag(StatFlag.Incognito));
        ushort skin = target.Hue;
        Assert.InRange(skin & 0x7FFF, 0x03EA, 0x0422);
        Assert.Equal(0x8000, skin & 0x8000);
        Assert.InRange((ushort)hair.Hue, 0x044E, 0x04AD);

        engine.ProcessExpirations(Environment.TickCount64 + 11_000);
        Assert.Equal((ushort)0x83EA, (ushort)target.Hue);
        Assert.Equal((ushort)0x0450, (ushort)hair.Hue);
        Assert.False(target.IsStatFlag(StatFlag.Incognito));
    }

    // --- 21: Dispel clears the spell layers only ---------------------------------

    [Fact]
    public void DispelLeavesManaDrainButRemovesBless()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.Bless, SpellFlag.TargChar | SpellFlag.Good, effect: 5, durationTenths: 600),
            Flat(SpellType.ManaDrain, SpellFlag.TargChar | SpellFlag.Harm, effect: 20, durationTenths: 600),
            Flat(SpellType.Dispel, SpellFlag.TargChar));
        Character.MagicFlags = 0;

        engine.ApplyDirectEffect(caster, target, SpellType.Bless, 500);
        engine.ApplyDirectEffect(caster, target, SpellType.ManaDrain, 500);
        Assert.Equal(105, target.Str);
        Assert.Equal(30, target.Mana);

        engine.ApplyDirectEffect(caster, target, SpellType.Dispel, 500);

        Assert.Equal(100, target.Str);   // LAYER_SPELL_STATS: gone
        Assert.Equal(30, target.Mana);   // LAYER_SPELL_Mana_Drain: untouched
    }

    // --- 22: smaller defaults --------------------------------------------------------

    [Fact]
    public void MagicArrowIsFireDamage()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.MagicArrow, SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage, effect: 20));
        target.ResFire = 50;

        engine.ApplyDirectEffect(caster, target, SpellType.MagicArrow, 500);

        Assert.Equal(90, target.Hits);
    }

    [Fact]
    public void AnEmptyCastTimeIsTheOneTenthFloorAndASingleValueCurveIsConstant()
    {
        Assert.Equal(1, new SpellDef { Id = SpellType.Heal }.GetCastTime(0));
        var def = new SpellDef { Id = SpellType.Bless, DurationBase = 600, EffectBase = 7 };
        Assert.Equal(600, def.GetDuration(1000));
        Assert.Equal(7, def.GetEffect(1000));
    }

    [Fact]
    public void TranceAddsItsEffectWithoutAFloor()
    {
        var (_, engine, caster, target) = Setup(
            Flat(SpellType.Trance, SpellFlag.Good, effect: 3, durationTenths: 600));
        target.SetSkill(SkillType.Meditation, 500);

        engine.ApplyDirectEffect(caster, target, SpellType.Trance, 500);

        Assert.Equal(503, target.GetSkill(SkillType.Meditation));
    }
}
