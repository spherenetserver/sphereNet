using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

public class SkillDelayTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void SkillEngine_GetSkillDelayMs_ReadsSkillDefDelay()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_skill_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [SKILL 11]
            DELAY=110
            """);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        Assert.Equal(11_000, SkillEngine.GetSkillDelayMs(SkillType.Healing));
    }

    [Fact]
    public void Character_ClearActiveSkillPending_ClearsTags()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.SetTag("SKILL_PENDING_ID", "17");
        ch.SetTag("SKILL_DELAY_END", "99999");

        int id = ch.ClearActiveSkillPending();
        Assert.Equal(17, id);
        Assert.False(ch.HasActiveSkillPending());
    }

    [Fact]
    public void ActiveSkillEngine_Peacemaking_ClearsNpcWarMode()
    {
        var world = CreateWorld();
        var bard = world.CreateCharacter();
        bard.SetSkill(SkillType.Musicianship, 900);
        bard.SetSkill(SkillType.Peacemaking, 900);
        world.PlaceCharacter(bard, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        bard.Backpack = pack;
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        pack.AddItem(lute);

        var wolf = world.CreateCharacter();
        wolf.NpcBrain = NpcBrainType.Animal;
        wolf.SetStatFlag(StatFlag.War);
        wolf.FightTarget = bard.Uid;
        world.PlaceCharacter(wolf, new Point3D(101, 100, 0, 0));

        var sink = new RecordingSkillSink(bard, world);
        Assert.True(ActiveSkillEngine.Peacemaking(sink, wolf));
        Assert.False(wolf.IsStatFlag(StatFlag.War));
        Assert.False(wolf.FightTarget.IsValid);
    }

    [Fact]
    public void SpellEngine_Polymorph_RestoresBodyOnExpiry()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Polymorph,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 5,
            DurationScale = 5,
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 1000);
        caster.BodyId = 0x0190;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.Polymorph, caster.Uid, caster.Position) > 0);
        Assert.True(engine.CastDone(caster));
        Assert.NotEqual(0x0190, caster.BodyId);

        engine.ProcessExpirations(Environment.TickCount64 + 60_000);
        Assert.Equal(0x0190, caster.BodyId);
    }

    private sealed class RecordingSkillSink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public Random Random { get; } = new(1);
        public GameWorld World { get; } = world;
        public void SysMessage(string text) { }
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public SphereNet.Game.Objects.Items.Item? FindBackpackItem(ItemType type)
        {
            var pack = Self.Backpack;
            if (pack == null) return null;
            foreach (var it in pack.Contents)
                if (it.ItemType == type) return it;
            return null;
        }
        public void ConsumeAmount(SphereNet.Game.Objects.Items.Item item, ushort amount = 1) { }
        public void DeliverItem(SphereNet.Game.Objects.Items.Item item) { }
    }
}
