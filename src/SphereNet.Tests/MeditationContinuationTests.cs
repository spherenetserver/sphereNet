using System.Buffers.Binary;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class MeditationContinuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientMeditationRepeatsOnSkillTimerAndCompletesAtFullMana(bool armor)
    {
        WithClient((client, player) =>
        {
            if (armor)
            {
                var chest = SphereNet.Game.Objects.ObjBase.ResolveWorld!().CreateItem();
                chest.ItemType = ItemType.Armor;
                player.Equip(chest, Layer.Chest);
            }
            client.HandleUseSkill(46);
            Assert.True(player.HasActiveSkillPending());
            Assert.Equal(2, player.Mana);
            player.OnTick(); // Disabled passive mana regen must not disable active meditation.
            Assert.Equal(2, player.Mana);
            for (short expected = 3; expected <= 5; expected++)
            {
                player.ContinueSkillPending(0);
                client.TickPendingSkill();
                Assert.Equal(expected, player.Mana);
                Assert.True(player.HasActiveSkillPending());
                Assert.True(player.IsStatFlag(StatFlag.Meditation));
                Assert.True(player.SkillDelayEnd > Environment.TickCount64);
            }
            player.ContinueSkillPending(0);
            client.TickPendingSkill();
            Assert.False(player.HasActiveSkillPending());
            Assert.False(player.IsStatFlag(StatFlag.Meditation));
            TestHarness.ClearQueuedPackets(client.NetState);
            client.Combat.TickStatUpdate();
            var mana = Assert.Single(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0xA2);
            Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(mana.Span[7..]));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MeditationHonorsActionEffectAcrossContinuations(int amount)
    {
        WithClient((client, player) =>
        {
            player.MaxMana = 20;
            client.HandleUseSkill(46);
            player.ActionEffect = amount;
            for (int i = 1; i <= 2; i++)
            {
                player.ContinueSkillPending(0);
                client.TickPendingSkill();
                Assert.Equal(2 + amount * i, player.Mana);
                Assert.Equal(amount, player.ActionEffect);
            }
        });
    }

    [Fact]
    public void MeditationGrantsExperienceOnlyWhenTheManaCycleCompletes()
    {
        var previous = SkillEngine.OnSkillGainCheck;
        try
        {
            int credits = 0;
            SkillEngine.OnSkillGainCheck = (Character ch, SkillType skill, ref int chance, ref int cap) =>
            {
                if (skill == SkillType.Meditation) credits++;
                return false;
            };
            WithClient((client, player) =>
            {
                client.HandleUseSkill(46);
                for (int i = 0; i < 3; i++)
                {
                    player.ContinueSkillPending(0);
                    client.TickPendingSkill();
                    Assert.Equal(0, credits);
                }
                player.ContinueSkillPending(0);
                client.TickPendingSkill();
                Assert.Equal(1, credits);
            });
        }
        finally { SkillEngine.OnSkillGainCheck = previous; }
    }

    [Fact]
    public void FailedStartGivesNoManaAndLeavesNoPendingSkill()
    {
        WithClient((client, player) =>
        {
            Character.OnSkillUseQuick = (_, _, _, _) => 0;
            client.HandleUseSkill(46);
            player.ContinueSkillPending(0);
            client.TickPendingSkill();
            Assert.Equal(2, player.Mana);
            Assert.False(player.HasActiveSkillPending());
            Assert.False(player.IsStatFlag(StatFlag.Meditation));
        });
    }

    [Fact]
    public void DamageStopsMeditationAndDoesNotRestartOnNextTick()
    {
        WithClient((client, player) =>
        {
            client.HandleUseSkill(46);
            player.ContinueSkillPending(0);
            client.TickPendingSkill();
            player.Hits--;
            Assert.False(player.HasActiveSkillPending());
            Assert.False(player.IsStatFlag(StatFlag.Meditation));
            client.TickPendingSkill();
            Assert.Equal(3, player.Mana);
        });
    }

    private static void WithClient(Action<SphereNet.Game.Clients.GameClient, Character> action)
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"meditation_{Guid.NewGuid():N}.scp");
        var previous = Character.OnSkillUseQuick;
        try
        {
            File.WriteAllText(path, "[SKILL 46]\nKEY=Meditation\nDELAY=2.0,1.0\nADV_RATE=0\n");
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
            var world = TestHarness.CreateWorld();
            using var logs = TestHarness.CreateLoggerFactory();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 8997);
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            player.MaxHits = 50; player.Hits = 50;
            player.MaxMana = 5; player.Mana = 2;
            player.SetSkill(SkillType.Meditation, 1000);
            Assert.True(player.TrySetProperty("REGENMANA", "-1"));
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
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
