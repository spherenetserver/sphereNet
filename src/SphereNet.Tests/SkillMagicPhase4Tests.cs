using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;

namespace SphereNet.Tests;

[Collection("GlobalConfigSerial")]
public class SkillMagicPhase4Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ActiveSkillEngine_Stealth_LeavesTheStepBudgetToTheScript()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        // GM auto-pass makes the success deterministic.
        ch.PrivLevel = PrivLevel.GM;
        ch.SetSkill(SkillType.Stealth, 3000);
        ch.SetStatFlag(StatFlag.Hidden);
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var sink = new RecordingSkillSink(ch, world);
        Assert.True(ActiveSkillEngine.Stealth(sink));
        // SKILL_STEALTH has no engine stage (CCharSkill.cpp:3674): the step budget is
        // STEPSTEALTH, which the pack's [SKILL 47] @Success sets
        // (skills/skill47_stealth.scp). This used to expect an engine-made 10.
        Assert.Equal(0, ch.StepStealth);
        Assert.True(ch.IsStatFlag(StatFlag.Hidden));
        Assert.False(ch.IsStatFlag(StatFlag.Invisible));
    }

    [Fact]
    public void MovementEngine_StealthStep_DecrementsAndReveals()
    {
        var world = CreateWorld();
        var move = new MovementEngine(world);
        var ch = world.CreateCharacter();
        ch.SetStatFlag(StatFlag.Hidden);
        ch.SetStatFlag(StatFlag.Invisible);
        ch.StepStealth = 1;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.True(move.TryMove(ch, Direction.East, running: false, sequence: 1));
        Assert.Equal(0, ch.StepStealth);
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void SpellEngine_GateTravel_AlwaysOpensTwoLinkedTelepads()
    {
        // Spell_CreateGate (CCharSpell.cpp:250-339) always opens BOTH ends as linked
        // IT_TELEPAD gates; the one-way moongate plus an opt-in return gate was
        // invented.
        var world = CreateWorld();
        Character.MagicFlags = 0;
        try
        {
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.GateTravel,
                ManaCost = 0,
                CastTimeBase = 1,
            });

            var caster = world.CreateCharacter();
            caster.MaxMana = 100;
            caster.Mana = 100;
            caster.SetSkill(SkillType.Magery, 2000);
            world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

            var rune = world.CreateItem();
            rune.SetRuneMark(new Point3D(200, 210, 5, 0));
            world.PlaceItem(rune, new Point3D(101, 100, 0, 0));

            var engine = new SpellEngine(world, registry);
            Assert.True(engine.CastStart(caster, SpellType.GateTravel, rune.Uid, rune.Position) > 0);
            Assert.True(engine.CastDone(caster));

            Item? here = null, there = null;
            foreach (var item in world.GetItemsInRange(new Point3D(100, 100, 0, 0), 2))
                if (item.ItemType == ItemType.Telepad) here = item;
            foreach (var item in world.GetItemsInRange(new Point3D(200, 210, 5, 0), 2))
                if (item.ItemType == ItemType.Telepad) there = item;

            Assert.NotNull(here);
            Assert.NotNull(there);
            Assert.Equal(there!.Uid, here!.Link);
            Assert.Equal(here.Uid, there.Link);
            Assert.Equal(200, here.MoreP.X);          // leads to the mark
            Assert.Equal(100, there.MoreP.X);         // and back to the caster
            Assert.True(here.IsAttr(ObjAttributes.Move_Never));
        }
        finally
        {
            Character.MagicFlags = 0;
        }
    }

    [Fact]
    public void SpellEngine_OutdoorSpell_IsNotBlockedUnderground()
    {
        // Source-X has no outdoor-only spell list: REGION_FLAG_UNDERGROUND only sets
        // STATF_INDOORS (CCharAct.cpp:4831). The old underground refusal was invented.
        var world = CreateWorld();
        var dungeon = new Region { Name = "dungeon_test" };
        dungeon.Flags = RegionFlag.Underground;
        dungeon.AddRect(90, 90, 110, 110);
        world.AddRegion(dungeon);

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Flamestrike,
            Flags = SpellFlag.TargChar | SpellFlag.Damage,
            ManaCost = 0,
            CastTimeBase = 1,
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.Flamestrike, target.Uid, target.Position) > 0);
    }

    [Fact]
    public void SpellEngine_RevertPolymorphOnDeath_RestoresBodyWhenFlagSet()
    {
        var world = CreateWorld();
        Character.MagicFlags = (int)MagicConfigFlags.PolymorphRevertDeath;
        try
        {
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Polymorph,
                ManaCost = 0,
                CastTimeBase = 1,
                DurationBase = 300,
            });

            var caster = world.CreateCharacter();
            caster.MaxMana = 100;
            caster.Mana = 100;
            caster.SetSkill(SkillType.Magery, 2000);
            caster.BodyId = 0x0190;
            world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

            var engine = new SpellEngine(world, registry);
            // The form is the menu pick (m_atMagery.m_uiSummonID) - no random body.
            caster.SetTag("POLY_SELECT", "51");
            Assert.True(engine.CastStart(caster, SpellType.Polymorph, caster.Uid, caster.Position) > 0);
            Assert.True(engine.CastDone(caster));
            Assert.NotEqual(0x0190, caster.BodyId);

            engine.RevertPolymorphOnDeath(caster);
            Assert.Equal(0x0190, caster.BodyId);
            Assert.False(caster.IsStatFlag(StatFlag.Polymorph));
        }
        finally
        {
            Character.MagicFlags = 0;
        }
    }

    [Fact]
    public void ActiveSkillEngine_RepairItem_RestoresHits()
    {
        var world = CreateWorld();
        var smith = world.CreateCharacter();
        smith.SetSkill(SkillType.Tinkering, 2000);
        // Arms Lore identifies the piece before any work begins, and the work needs
        // an anvil within two tiles (Source-X Use_Repair, CCharUse.cpp:764/781).
        smith.SetSkill(SkillType.ArmsLore, 1000);
        world.PlaceCharacter(smith, new Point3D(100, 100, 0, 0));

        var anvil = world.CreateItem();
        anvil.ItemType = ItemType.Anvil;
        world.PlaceItem(anvil, new Point3D(100, 100, 0, 0));

        var item = world.CreateItem();
        item.BaseId = 0x13BB;
        item.SetTag("HITSMAX", "50");
        item.SetTag("HITS", "10");
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));

        var sink = new RecordingSkillSink(smith, world);
        Assert.True(ActiveSkillEngine.RepairItem(sink, item));
        Assert.True(item.GetHitsCur() > 10);
    }

    private sealed class RecordingSkillSink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public Random Random { get; } = new(7);
        public GameWorld World { get; } = world;
        public void SysMessage(string text) { }
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }
}
