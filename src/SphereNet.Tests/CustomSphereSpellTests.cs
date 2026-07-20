using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit findings (wiki/test.txt follow-up 2): SPELLFLAG_HARM alone drives the
/// criminal marking and Magic Reflect bounce (finding 1 — pack Poison/Paralyze/
/// Mana Vampire carry HARM without DAMAGE/CURSE), the [SPELL n] ON=@Select
/// stage fires from CastStart for every entry point (finding 2), Animated
/// Weapon summons its classic body (finding 3), and the Sphere custom spells
/// 1000+ have native behaviors instead of mana-burning no-ops (finding 4).
/// </summary>
[Collection("VendorStateSerial")]
public sealed class CustomSphereSpellTests
{
    private static (GameWorld World, SpellEngine Engine, Character Caster) Setup(params SpellDef[] defs)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        foreach (var def in defs)
            registry.Register(def);
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (world, engine, caster);
    }

    private static SpellDef HarmOnlyPoisonDef() => new()
    {
        Id = SpellType.Poison,
        Name = "Poison",
        // As the pack defines it: SPELLFLAG_HARM without DAMAGE/CURSE.
        Flags = SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Tick,
        DurationBase = 150,
    };

    [Fact]
    public void HarmOnlySpell_OnInnocentPlayer_MarksCasterCriminal()
    {
        var (world, engine, caster) = Setup(HarmOnlyPoisonDef());
        caster.PrivLevel = PrivLevel.Player;

        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));

        Assert.False(caster.IsFlaggedAsCriminal);
        engine.ApplyDirectEffect(caster, victim, SpellType.Poison, 500);

        // Source-X keys the crime off SPELLFLAG_HARM (OnAttackedBy in
        // OnSpellEffect); Damage||Curse alone let Poison casters stay blue.
        Assert.True(caster.IsFlaggedAsCriminal,
            "poisoning an innocent player did not flag the caster criminal");
        Assert.True(victim.IsPoisoned);
    }

    [Fact]
    public void HarmOnlySpell_IsBouncedByMagicReflect()
    {
        var (world, engine, caster) = Setup(HarmOnlyPoisonDef());

        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.SetStatFlag(StatFlag.Reflection);
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));

        engine.ApplyDirectEffect(caster, victim, SpellType.Poison, 500);

        // The single-use Reflection charge bounced the HARM spell back.
        Assert.False(victim.IsPoisoned, "reflected poison still landed on the victim");
        Assert.True(caster.IsPoisoned, "reflected poison did not land on the caster");
        Assert.False(victim.IsStatFlag(StatFlag.Reflection), "the reflect charge was not consumed");
    }

    [Fact]
    public void SelectTrigger_Return1_CancelsCastStart()
    {
        // A poly-only school spell whose pack body is JUST ON=@Select (the
        // Scripts-X Reaper Form shape). The section stage must fire from
        // CastStart — the engine choke point every entry point (client,
        // scroll, wand, NPC, console) funnels through — and RETURN 1 cancels.
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-select-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "spell.scp");
        File.WriteAllText(path, "[SPELL 609]\nNAME=Reaper Form\nON=@Select\nRETURN 1\n");

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(path);

        var (_, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.ReaperForm,
            Name = "Reaper Form",
            Flags = SpellFlag.Good | SpellFlag.Poly,
            HasScriptedStages = true,
        });
        engine.TriggerDispatcher = stack.Dispatcher;

        Assert.Equal(-1, engine.CastStart(caster, SpellType.ReaperForm,
            caster.Uid, caster.Position));
    }

    [Fact]
    public void AnimatedWeapon_SummonsItsClassicBody()
    {
        var (world, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.AnimatedWeapon,
            Name = "Animated Weapon",
            Flags = SpellFlag.TargXYZ | SpellFlag.Summon,
            DurationBase = 1200,
        });

        Assert.True(engine.CastStart(caster, SpellType.AnimatedWeapon, caster.Uid,
            new Point3D(102, 100, 0, 0)) > 0);
        Assert.True(engine.CastDone(caster));

        var weapon = world.GetCharsInRange(caster.Position, 5)
            .FirstOrDefault(c => c != caster && c.IsSummoned);
        Assert.NotNull(weapon);
        Assert.Equal((ushort)0x02B4, weapon!.BodyId); // CREID_ANIMATED_WEAPON
    }

    [Fact]
    public void RefreshRestoreMana_RefillTheirStats()
    {
        var (_, engine, caster) = Setup(
            new SpellDef { Id = SpellType.Refresh, Name = "Refresh", EffectBase = 30, DurationBase = 0 },
            new SpellDef { Id = SpellType.Restore, Name = "Restore", EffectBase = 30, DurationBase = 0 },
            new SpellDef { Id = SpellType.Mana, Name = "Mana", EffectBase = 30, DurationBase = 0 });
        caster.MaxStam = 100; caster.Stam = 10;
        caster.MaxHits = 100; caster.Hits = 10;
        caster.MaxMana = 100; caster.Mana = 10;

        engine.ApplyDirectEffect(caster, caster, SpellType.Refresh, 500);
        Assert.True(caster.Stam > 10, "Refresh did not restore stamina");

        short stamAfterRefresh = caster.Stam;
        engine.ApplyDirectEffect(caster, caster, SpellType.Restore, 500);
        Assert.True(caster.Hits > 10, "Restore did not restore hits");
        Assert.True(caster.Stam > stamAfterRefresh || caster.Stam == caster.MaxStam,
            "Restore did not restore stamina");

        engine.ApplyDirectEffect(caster, caster, SpellType.Mana, 500);
        Assert.True(caster.Mana > 10, "Mana did not restore mana");
    }

    [Fact]
    public void StoneSpell_PetrifiesForTheDuration_AndTranceBoostsMeditation()
    {
        var (world, engine, caster) = Setup(
            new SpellDef
            {
                Id = SpellType.Stone,
                Name = "Stone",
                Flags = SpellFlag.TargChar | SpellFlag.Harm,
                DurationBase = 1200,
            },
            new SpellDef
            {
                Id = SpellType.Trance,
                Name = "Trance",
                EffectBase = 100,
                DurationBase = 1200,
            });

        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));

        engine.ApplyDirectEffect(caster, victim, SpellType.Stone, 500);
        Assert.True(victim.IsStatFlag(StatFlag.Stone), "Stone did not petrify");

        ushort medBefore = caster.GetSkill(SkillType.Meditation);
        engine.ApplyDirectEffect(caster, caster, SpellType.Trance, 500);
        Assert.True(caster.GetSkill(SkillType.Meditation) > medBefore,
            "Trance did not raise Meditation");

        // Both are timed effects: expiry reverts flag and skill exactly.
        engine.ProcessExpirations(Environment.TickCount64 + 30L * 60 * 1000);
        Assert.False(victim.IsStatFlag(StatFlag.Stone), "petrify never wore off");
        Assert.Equal(medBefore, caster.GetSkill(SkillType.Meditation));
    }

    [Fact]
    public void Steelskin_GrantsTimedArmor()
    {
        var (_, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.Steelskin,
            Name = "Steelskin",
            EffectBase = 20,
            DurationBase = 1200,
        });

        int armorBefore = caster.ProtectionArmor;
        engine.ApplyDirectEffect(caster, caster, SpellType.Steelskin, 500);
        Assert.True(caster.ProtectionArmor > armorBefore, "Steelskin granted no armor");

        engine.ProcessExpirations(Environment.TickCount64 + 30L * 60 * 1000);
        Assert.Equal(armorBefore, caster.ProtectionArmor);
    }

    [Fact]
    public void BoneArmor_ConsumesSkeletonCorpse_ButNotOtherCorpses()
    {
        var (world, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.BoneArmor,
            Name = "Bone Armor",
            Flags = SpellFlag.TargObj,
        });
        var removed = new List<Item>();
        engine.OnItemRemoved = i => { removed.Add(i); world.RemoveItem(i); };

        var wolfCorpse = world.CreateItem();
        wolfCorpse.ItemType = ItemType.Corpse;
        wolfCorpse.Amount = 0x00E1; // a wolf — "the body stirs" and survives
        world.PlaceItem(wolfCorpse, new Point3D(101, 100, 0, 0));

        caster.BeginCast(SpellType.BoneArmor, wolfCorpse.Uid, wolfCorpse.Position);
        Assert.True(engine.CastDone(caster));
        Assert.Empty(removed);

        var skeletonCorpse = world.CreateItem();
        skeletonCorpse.ItemType = ItemType.Corpse;
        skeletonCorpse.Amount = 0x0032; // CREID_SKELETON
        world.PlaceItem(skeletonCorpse, new Point3D(101, 100, 0, 0));

        caster.BeginCast(SpellType.BoneArmor, skeletonCorpse.Uid, skeletonCorpse.Position);
        Assert.True(engine.CastDone(caster));
        Assert.Contains(skeletonCorpse, removed); // the skeleton was consumed
    }

    [Fact]
    public void BoneArmor_NeverConsumesARemoteOrContainedCorpse()
    {
        var (world, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.BoneArmor,
            Name = "Bone Armor",
            Flags = SpellFlag.TargObj,
        });
        var removed = new List<Item>();
        engine.OnItemRemoved = i => { removed.Add(i); world.RemoveItem(i); };

        // A skeleton corpse far outside casting range (known UID exploit).
        var farCorpse = world.CreateItem();
        farCorpse.ItemType = ItemType.Corpse;
        farCorpse.Amount = 0x0032;
        world.PlaceItem(farCorpse, new Point3D(300, 300, 0, 0));

        caster.BeginCast(SpellType.BoneArmor, farCorpse.Uid, farCorpse.Position);
        Assert.True(engine.CastDone(caster));
        Assert.Empty(removed);

        // A skeleton corpse inside a container (Source-X: top-level only).
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));
        var packedCorpse = world.CreateItem();
        packedCorpse.ItemType = ItemType.Corpse;
        packedCorpse.Amount = 0x0032;
        Assert.True(chest.TryAddItem(packedCorpse));

        caster.BeginCast(SpellType.BoneArmor, packedCorpse.Uid, chest.Position);
        Assert.True(engine.CastDone(caster));
        Assert.Empty(removed);
    }

    [Fact]
    public void ReaperAndStoneForm_FirstCast_AppliesAndRevertsTheFormBody()
    {
        var (_, engine, caster) = Setup(
            new SpellDef
            {
                Id = SpellType.ReaperForm,
                Name = "Reaper Form",
                Flags = SpellFlag.Good | SpellFlag.Poly,
                DurationBase = 0, // as the pack ships it — form holds
            },
            new SpellDef
            {
                Id = SpellType.StoneForm,
                Name = "Stone Form",
                Flags = SpellFlag.Good | SpellFlag.Poly,
                DurationBase = 600, // 60 s
            });
        ushort originalBody = caster.BodyId;

        // Reaper Form: body 0xE6 (CREID_REAPER_FORM), holds past the 30 s
        // engine floor because the pack duration is 0 (reference: no timer).
        engine.ApplyDirectEffect(caster, caster, SpellType.ReaperForm, 500);
        Assert.Equal((ushort)0x00E6, caster.BodyId);
        Assert.True(caster.IsStatFlag(StatFlag.Polymorph));
        engine.ProcessExpirations(Environment.TickCount64 + 30L * 60 * 1000);
        Assert.Equal((ushort)0x00E6, caster.BodyId);

        // Re-cast to Stone Form replaces the poly layer (0x2C1), and its
        // timed duration reverts to the original body.
        engine.ApplyDirectEffect(caster, caster, SpellType.StoneForm, 500);
        Assert.Equal((ushort)0x02C1, caster.BodyId);
        engine.ProcessExpirations(Environment.TickCount64 + 30L * 60 * 1000);
        Assert.Equal(originalBody, caster.BodyId);
        Assert.False(caster.IsStatFlag(StatFlag.Polymorph));
    }

    [Fact]
    public void MonsterForm_PolymorphsIntoAMonsterBody()
    {
        var (_, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.MonsterForm,
            Name = "Monster Form",
            Flags = SpellFlag.Harm | SpellFlag.PlayerOnly,
            DurationBase = 1200,
        });
        ushort originalBody = caster.BodyId;

        engine.ApplyDirectEffect(caster, caster, SpellType.MonsterForm, 500);

        Assert.NotEqual(originalBody, caster.BodyId);
        Assert.Contains(caster.BodyId, new[]
        {
            (ushort)0x0002, (ushort)0x0004, (ushort)0x0011,
            (ushort)0x0021, (ushort)0x0036,
        });
        Assert.True(caster.IsStatFlag(StatFlag.Polymorph));
    }

    [Fact]
    public void Shrink_KillsAConjuredCreature()
    {
        var (world, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.Shrink,
            Name = "Shrink",
            Flags = SpellFlag.TargChar | SpellFlag.Harm,
        });

        var summon = world.CreateCharacter();
        summon.SetStatFlag(StatFlag.Conjured);
        summon.MaxHits = 50;
        summon.Hits = 50;
        world.PlaceCharacter(summon, new Point3D(101, 100, 0, 0));

        engine.ApplyDirectEffect(caster, summon, SpellType.Shrink, 500);

        // Source-X NPC_Shrink zeroes a conjured creature's STR — it dies
        // instead of yielding a figurine.
        Assert.True(summon.IsDead || summon.Hits <= 0,
            "the conjured creature survived the shrink");
    }

    [Fact]
    public void ReaperForm_ScriptToggle_RemovesTheFormViaItsMemory()
    {
        // The Scripts-X @Select toggle runs FINDID.<RUNE_ITEM>.REMOVE while
        // polymorphed: FINDID must see the hidden spell-memory item (whose
        // graphic IS the rune id) and removing it must revert the effect
        // (Source-X: deleting the IT_SPELL memory runs Spell_Effect_Remove).
        var (_, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.ReaperForm,
            Name = "Reaper Form",
            Flags = SpellFlag.Good | SpellFlag.Poly,
            DurationBase = 0,
            RuneItemId = 0x2D59, // i_scroll_reaper_form
        });
        Character.SpellMemoryEffectRemover = engine.RemoveEffectByMemory;
        ushort originalBody = caster.BodyId;

        engine.ApplyDirectEffect(caster, caster, SpellType.ReaperForm, 500);
        Assert.Equal((ushort)0x00E6, caster.BodyId);
        Assert.Contains(caster.Memories, m => !m.IsDeleted && m.BaseId == 0x2D59);

        Assert.True(caster.TryExecuteCommand("FINDID.02D59.REMOVE", "", null!));

        Assert.Equal(originalBody, caster.BodyId);
        Assert.False(caster.IsStatFlag(StatFlag.Polymorph));
        Assert.DoesNotContain(caster.Memories, m => !m.IsDeleted && m.BaseId == 0x2D59);
    }

    [Fact]
    public void Hallucination_Tick_PlaysClientOnlySound_AndRefreshesView()
    {
        var (_, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.Hallucination,
            Name = "Hallucination",
            Flags = SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Curse,
            DurationBase = 1200,
        });
        var sounds = new List<ushort>();
        Character? refreshed = null;
        engine.OnPlaySoundTo = (_, snd) => sounds.Add(snd);
        engine.OnViewRefresh = ch => refreshed = ch;

        engine.ApplyDirectEffect(caster, caster, SpellType.Hallucination, 500);
        Assert.True(caster.IsStatFlag(StatFlag.Hallucinating));

        // First trip tick lands within 15-30 s.
        engine.ProcessExpirations(Environment.TickCount64 + 31_000);
        Assert.NotEmpty(sounds);
        Assert.All(sounds, s => Assert.Contains(s, new[] { (ushort)0x0243, (ushort)0x0244 }));
        Assert.Same(caster, refreshed);
    }

    [Fact]
    public void Alcohol_DrainsStaminaAndManaPerTick()
    {
        var (_, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.Ale,
            Name = "Ale",
            DurationBase = 600,
        });
        caster.MaxStam = 100; caster.Stam = 50;
        caster.MaxMana = 100; caster.Mana = 50;

        engine.ApplyDirectEffect(caster, caster, SpellType.Ale, 500);

        // Advance past one 5 s drunk tick: one stamina and one mana drained
        // (Source-X Spell_Equip_OnTick Stat_AddVal DEX/INT).
        engine.ProcessExpirations(Environment.TickCount64 + 6000);
        Assert.True(caster.Stam < 50, "the drunk tick drained no stamina");
        Assert.True(caster.Mana < 50, "the drunk tick drained no mana");
    }
}
