using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("GlobalConfigSerial")]
public class SpellParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void SpellEngine_CastDone_FizzlesOnFailedSkillCheck()
    {
        Character.ManaLossFail = false;
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Clumsy,
            Flags = SpellFlag.TargChar | SpellFlag.Curse,
            ManaCost = 10,
            CastTimeBase = 1,
        });
        // Far past the bell curve's zero-chance tail so the fizzle is
        // deterministic (at delta -1000 the curve still succeeds ~0.5%).
        registry.Get(SpellType.Clumsy)!.SkillReq[SkillType.Magery] = 3000;

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 0);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.Clumsy, target.Uid, target.Position) > 0);
        Assert.False(engine.CastDone(caster));
        Assert.Equal(100, caster.Mana);
        Assert.False(caster.IsCasting);
    }

    [Fact]
    public void SpellEngine_TickCastTimer_CompletesNpcCast()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Heal,
            Flags = SpellFlag.TargChar | SpellFlag.Heal,
            ManaCost = 0,
            CastTimeBase = 5,
            EffectBase = 20,
            EffectScale = 20,
        });

        var npc = world.CreateCharacter();
        npc.MaxMana = 100;
        npc.Mana = 100;
        npc.Hits = 10;
        npc.MaxHits = 100;
        npc.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        int castMs = engine.CastStart(npc, SpellType.Heal, npc.Uid, npc.Position);
        Assert.True(castMs > 0);
        npc.SetCastTimerEnd(Environment.TickCount64 - 1);

        Assert.False(engine.TickCastTimer(npc));
        Assert.False(npc.IsCasting);
        Assert.True(npc.Hits > 10);
    }

    [Fact]
    public void SpellEngine_CastStart_AllowsNpcCastersWithWeapon()
    {
        Character.EquippedCastEnabled = false;
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Heal,
            Flags = SpellFlag.TargChar | SpellFlag.Heal,
            ManaCost = 0,
            CastTimeBase = 5,
            EffectBase = 20,
            EffectScale = 20,
        });

        var npc = world.CreateCharacter();
        npc.MaxMana = 100;
        npc.Mana = 100;
        npc.SetSkill(SkillType.Magery, 2000);
        var staff = world.CreateItem();
        staff.ItemType = ItemType.WeaponMaceStaff;
        npc.Equip(staff, Layer.OneHanded);
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(npc, SpellType.Heal, npc.Uid, npc.Position) > 0);
    }

    [Fact]
    public void SpellEngine_CastStart_BlocksRecallWhenMagicFlagSet()
    {
        Character.MagicFlags = (int)MagicConfigFlags.DisableRecall;
        try
        {
            var world = CreateWorld();
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Recall,
                Flags = SpellFlag.TargObj,
                ManaCost = 0,
                CastTimeBase = 1
            });

            var caster = world.CreateCharacter();
            caster.MaxMana = 100;
            caster.Mana = 100;
            world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

            var rune = world.CreateItem();
            world.PlaceItem(rune, new Point3D(101, 100, 0, 0));

            var engine = new SpellEngine(world, registry);
            Assert.Equal(-1, engine.CastStart(caster, SpellType.Recall, rune.Uid, rune.Position));
        }
        finally
        {
            Character.MagicFlags = 0;
        }
    }

    [Fact]
    public void SpellEngine_Heal_FlagsHelperCriminalWhenHelpingCriminal()
    {
        Character.HelpingCriminalsIsACrimeEnabled = true;
        Character.CriminalTimerSeconds = 180;
        try
        {
            var world = CreateWorld();
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Heal,
                Flags = SpellFlag.TargChar | SpellFlag.Heal,
                ManaCost = 0,
                CastTimeBase = 1,
                EffectBase = 15,
                EffectScale = 15,
            });

            var healer = world.CreateCharacter();
            healer.IsPlayer = true;
            healer.MaxMana = 100;
            healer.Mana = 100;
            healer.SetSkill(SkillType.Magery, 2000);

            // Satisfy the spellbook requirement with a wielded book (the
            // global flag is shared with parallel test collections).
            var healBook = world.CreateItem();
            healBook.ItemType = ItemType.Spellbook;
            healBook.More1 = 1u << ((int)SpellType.Heal - 1);
            healer.Equip(healBook, Layer.OneHanded);
            world.PlaceCharacter(healer, new Point3D(100, 100, 0, 0));

            var criminal = world.CreateCharacter();
            criminal.IsPlayer = true;
            criminal.MakeCriminal();
            criminal.Hits = 10;
            criminal.MaxHits = 100;
            world.PlaceCharacter(criminal, new Point3D(101, 100, 0, 0));

            var engine = new SpellEngine(world, registry);
            Assert.True(engine.CastStart(healer, SpellType.Heal, criminal.Uid, criminal.Position) > 0);
            Assert.True(engine.CastDone(healer));
            Assert.True(healer.IsFlaggedAsCriminal);
        }
        finally
        {
            Character.HelpingCriminalsIsACrimeEnabled = false;
        }
    }

    [Fact]
    public void SpellEngine_CastStart_RevealsHiddenCaster()
    {
        Character.MagicFlags = 0;
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.NightSight,
            ManaCost = 0,
            CastTimeBase = 1
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        caster.SetStatFlag(StatFlag.Hidden);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.NightSight, caster.Uid, caster.Position) > 0);
        Assert.False(caster.IsStatFlag(StatFlag.Hidden));
    }
}
