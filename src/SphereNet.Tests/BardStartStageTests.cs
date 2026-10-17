using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>The bard skills play their instrument at START, when the skill begins
/// (Skill_Musicianship / Skill_Peacemaking / Skill_Enticement / Skill_Provocation,
/// CCharSkill.cpp:1787, 1830, 1956, 2071); the skill's own roll comes after the DELAY
/// with the difficulty START answered. Everything used to run after the DELAY.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BardStartStageTests
{
    /// <summary>The tune is the START: the Musicianship roll happens when Peacemaking
    /// is used, the Peacemaking roll after the DELAY, and the tune is not played again.</summary>
    [Fact]
    public void PeacemakingPlaysAtStart_AndRollsAfterTheDelay()
    {
        var rolls = new List<SkillType>();
        WithClient((client, player) =>
        {
            Character.OnSkillUseQuick = (_, skill, _, _) => { rolls.Add((SkillType)skill); return 1; };
            client.HandleUseSkill((int)SkillType.Peacemaking);
            Assert.True(player.HasActiveSkillPending());
            Assert.Equal([SkillType.Musicianship], rolls);

            player.ContinueSkillPending(0);
            client.TickPendingSkill();
            Assert.False(player.HasActiveSkillPending());
            Assert.Equal([SkillType.Musicianship, SkillType.Peacemaking], rolls);
        }, withLute: true);
    }

    /// <summary>No instrument is said when the skill is used. With @Start in use the
    /// skill ends there quietly; with no start trigger it waits out the DELAY and then
    /// fails (Skill_Done, CCharSkill.cpp:3927), running @Fail.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoInstrumentIsSaidAtOnce(bool startHooked)
    {
        string script = "ON=@Fail\nSRC.TAG.FAILED=1\n" + (startHooked ? "ON=@Start\nSRC.TAG.STARTED=1\n" : "");
        WithClient((client, player) =>
        {
            TestHarness.ClearQueuedPackets(client.NetState);
            client.HandleUseSkill((int)SkillType.Peacemaking);
            Assert.Contains(TestHarness.GetQueuedPackets(client.NetState),
                p => System.Text.Encoding.ASCII.GetString(p.Span).Contains(
                    ServerMessages.Get(Msg.MusicanshipNotool)) ||
                    System.Text.Encoding.BigEndianUnicode.GetString(p.Span).Contains(
                    ServerMessages.Get(Msg.MusicanshipNotool)));
            Assert.Equal(!startHooked, player.HasActiveSkillPending());
            if (!startHooked)
            {
                Assert.False(player.TryGetTag("FAILED", out _));
                player.ContinueSkillPending(0);
                client.TickPendingSkill();
            }
            Assert.Equal(startHooked, !player.TryGetTag("FAILED", out _));
        }, withLute: false, triggers: script);
    }

    /// <summary>START's answer is ACTDIFF (m_Act_Difficulty) in @Start: a pack that
    /// writes ACTDIFF 0 there - as the Sphere 56T custom-version pack does for
    /// musicianship - keeps a refused start going (CCharSkill.cpp:4447, 4515).</summary>
    [Fact]
    public void TheStartTriggerRewritesTheStartAnswer()
    {
        WithClient((client, player) =>
        {
            client.HandleUseSkill((int)SkillType.Peacemaking);
            Assert.Equal("1", player.TryGetTag("SEEN", out string? seen) ? seen : null);
            Assert.True(player.HasActiveSkillPending());
            player.ContinueSkillPending(0);
            client.TickPendingSkill();
            Assert.False(player.TryGetTag("FAILED", out _));
        }, withLute: false, triggers: "ON=@Fail\nSRC.TAG.FAILED=1\nON=@Start\nIF <ACTDIFF> < 0\nSRC.TAG.SEEN=1\nENDIF\nACTDIFF 0\n");
    }

    /// <summary>Musicianship plays ACT, the instrument double-clicked (Use_Item
    /// IT_MUSICAL sets it, CCharUse.cpp:1739-1745; Skill_Musicianship :1787), not the
    /// first one in the pack.</summary>
    [Fact]
    public void MusicianshipPlaysTheInstrumentItWasStartedOn()
    {
        var world = TestHarness.CreateWorld();
        var bard = world.CreateCharacter();
        world.PlaceCharacter(bard, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        bard.Backpack = pack;
        var drum = world.CreateItem();
        drum.ItemType = ItemType.Musical;
        drum.TData1 = 0x38;
        pack.AddItem(drum);
        var harp = world.CreateItem();
        harp.ItemType = ItemType.Musical;
        harp.TData1 = 0x45;
        pack.AddItem(harp);
        var sink = new Sink(bard, world);
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        try
        {
            Assert.True(ActiveSkillEngine.BardStart(sink, SkillType.Musicianship, harp, null) >= 0);
            Assert.Equal([(ushort)0x45], sink.Sounds);
        }
        finally { Character.OnSkillUseQuick = null; }
    }

    /// <summary>Enticement asks only that the bard sees the creature (Skill_Enticement,
    /// CCharSkill.cpp:1914-1916, CChar::CanSee): no line of sight and no 8-tile reach.</summary>
    [Fact]
    public void EnticementReachesAnythingInSight()
    {
        var world = TestHarness.CreateWorld();
        var bard = world.CreateCharacter();
        bard.SetSkill(SkillType.Musicianship, 1000);
        bard.SetSkill(SkillType.Enticement, 1000);
        world.PlaceCharacter(bard, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        bard.Backpack = pack;
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        pack.AddItem(lute);
        var near = world.CreateCharacter();
        near.NpcBrain = NpcBrainType.Animal;
        world.PlaceCharacter(near, new Point3D(112, 100, 0, 0));
        var far = world.CreateCharacter();
        far.NpcBrain = NpcBrainType.Animal;
        world.PlaceCharacter(far, new Point3D((short)(100 + bard.VisualRange + 1), 100, 0, 0));
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        try
        {
            var sink = new Sink(bard, world);
            Assert.True(ActiveSkillEngine.Enticement(sink, near));
            Assert.Equal(bard.Position, near.ActP);
            Assert.False(ActiveSkillEngine.Enticement(sink, far));
            Assert.True(ActiveSkillEngine.BardStart(sink, SkillType.Enticement, far, null) < 0);
        }
        finally { Character.OnSkillUseQuick = null; }
    }

    /// <summary>A provocation whose START came back bound to fail turns the provoked
    /// creature on the bard after the DELAY (the FAIL stage, CCharSkill.cpp:2092-2096).</summary>
    [Fact]
    public void AProvocationBoundToFailTurnsTheCreatureOnTheBard()
    {
        var world = TestHarness.CreateWorld();
        var bard = world.CreateCharacter();
        world.PlaceCharacter(bard, new Point3D(100, 100, 0, 0));
        var ogre = world.CreateCharacter();
        ogre.NpcBrain = NpcBrainType.Monster;
        world.PlaceCharacter(ogre, new Point3D(103, 100, 0, 0));
        var deer = world.CreateCharacter();
        deer.NpcBrain = NpcBrainType.Animal;
        world.PlaceCharacter(deer, new Point3D(105, 100, 0, 0));

        Assert.False(ActiveSkillEngine.Provocation(new Sink(bard, world), ogre, deer, startDifficulty: -1));
        Assert.Equal(bard.Uid, ogre.FightTarget);
    }

    /// <summary>A provoked creature remembers the fight it was set on (Attacker_Add's
    /// Memory_Fight_Start) and its target remembers being irritated (Fight_Attack);
    /// with MONSTERFIGHT off the memory is what keeps one monster on another instead
    /// of fleeing it.</summary>
    [Fact]
    public void AProvokedMonsterRemembersTheFight()
    {
        var world = TestHarness.CreateWorld();
        var bard = world.CreateCharacter();
        bard.SetSkill(SkillType.Provocation, 1000);
        bard.SetSkill(SkillType.Musicianship, 1000);
        world.PlaceCharacter(bard, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        bard.Backpack = pack;
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        pack.AddItem(lute);
        var orc = world.CreateCharacter();
        orc.NpcBrain = NpcBrainType.Monster;
        orc.BodyId = 0x11;
        orc.Karma = -500;
        world.PlaceCharacter(orc, new Point3D(103, 100, 0, 0));
        var ettin = world.CreateCharacter();
        ettin.NpcBrain = NpcBrainType.Monster;
        ettin.BodyId = 0x02;
        ettin.Karma = -500;
        world.PlaceCharacter(ettin, new Point3D(104, 100, 0, 0));
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        try
        {
            Assert.True(ActiveSkillEngine.Provocation(new Sink(bard, world), orc, ettin));
            Assert.Equal(ettin.Uid, orc.FightTarget);
            Assert.NotNull(orc.Memory_FindObjTypes(ettin.Uid, MemoryType.Fight));
            Assert.NotNull(ettin.Memory_FindObjTypes(orc.Uid, MemoryType.IrritatedBy));

            // The orc's next fight tick stays on the ettin rather than running from it.
            bard.IsPlayer = true; bard.IsOnline = true; world.AddOnlinePlayer(bard); world.OnTick();
            var ai = new SphereNet.Game.AI.NpcAI(world, new SphereNet.Core.Configuration.SphereConfig());
            orc.Hits = orc.MaxHits = 100;
            ettin.Hits = ettin.MaxHits = 100;
            orc.NextNpcActionTime = 0;
            ai.OnTickAction(orc);
            Assert.Equal(ettin.Uid, orc.FightTarget);
            Assert.Equal(0, orc.FleeStepsCurrent);
        }
        finally { Character.OnSkillUseQuick = null; }
    }

    private sealed class Sink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(3);
        public List<ushort> Sounds { get; } = [];
        public void SysMessage(string text) { }
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) => Sounds.Add(soundId);
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) =>
            Self.Backpack?.Contents.FirstOrDefault(i => i.ItemType == type);
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private static void WithClient(Action<SphereNet.Game.Clients.GameClient, Character> action,
        bool withLute, string triggers = "")
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"bard_{Guid.NewGuid():N}.scp");
        var previous = Character.OnSkillUseQuick;
        try
        {
            File.WriteAllText(path, "[SKILL 9]\nKEY=Peacemaking\nDELAY=2.0\nADV_RATE=0\n" + triggers +
                "\n[SKILL 29]\nKEY=Musicianship\nDELAY=2.0\nADV_RATE=0\n");
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
            runtime.Dispatcher.BuildUsedTriggerCache();
            var world = TestHarness.CreateWorld();
            using var logs = TestHarness.CreateLoggerFactory();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 8996);
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            player.MaxHits = 50; player.Hits = 50;
            player.SetSkill(SkillType.Peacemaking, 1000);
            player.SetSkill(SkillType.Musicianship, 1000);
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            player.Backpack = pack;
            if (withLute)
            {
                var lute = world.CreateItem();
                lute.ItemType = ItemType.Musical;
                pack.AddItem(lute);
            }
            var npc = world.CreateCharacter();
            npc.NpcBrain = NpcBrainType.Animal;
            world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: runtime.Dispatcher);
            Character.OnSkillUseQuick = (_, _, _, _) => 1;
            action(client, player);
        }
        finally
        {
            Character.OnSkillUseQuick = previous;
            File.Delete(path);
        }
    }
}
