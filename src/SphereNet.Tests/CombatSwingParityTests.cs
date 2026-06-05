using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Network.Packets;

namespace SphereNet.Tests;

public class CombatSwingParityTests
{
    [Fact]
    public void WeaponEquip_UpdatesSourceXSwingStateAndFirstHitFlagControlsWait()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var accounts = new AccountManager(loggerFactory);
            var client = TestHarness.CreateClient(loggerFactory, world, accounts, 1201);
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, player);

            Character.CombatFlags = 0;
            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            sword.Amount = 1;
            sword.ContainedIn = player.Uid;

            client.HandleItemEquip(sword.Uid.Value, (byte)Layer.OneHanded, player.Uid.Value);

            Assert.Equal(SwingState.Equipping, player.CombatSwingState);
            Assert.True(player.NextAttackTime > Environment.TickCount64);
            Assert.True(player.TryGetProperty("SWINGSTATE.NAME", out var stateName));
            Assert.Equal(nameof(SwingState.Equipping), stateName);

            Character.CombatFlags = (int)CombatFlags.FirstHitInstant;
            var instantSword = world.CreateItem();
            instantSword.ItemType = ItemType.WeaponSword;
            instantSword.BaseId = 0x0F5E;
            instantSword.Amount = 1;
            instantSword.ContainedIn = player.Uid;

            client.HandleItemEquip(instantSword.Uid.Value, (byte)Layer.OneHanded, player.Uid.Value);

            Assert.Equal(SwingState.Ready, player.CombatSwingState);
            Assert.True(player.NextAttackTime <= Environment.TickCount64);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void ArcherySwing_ConsumesAmmoAndBroadcastsSphere56xProjectile()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = (int)CombatFlags.FirstHitInstant;

            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var accounts = new AccountManager(loggerFactory);
            var client = TestHarness.CreateClient(loggerFactory, world, accounts, 1202);

            var archer = world.CreateCharacter();
            archer.IsPlayer = true;
            archer.Dex = 100;
            archer.Stam = 100;
            archer.SetSkill(SkillType.Archery, 1200);
            archer.SetSkill(SkillType.Tactics, 1200);
            archer.SetStatFlag(StatFlag.War);
            world.PlaceCharacter(archer, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, archer);

            var target = world.CreateCharacter();
            target.Hits = target.MaxHits = 100;
            target.SetSkill(SkillType.Wrestling, 0);
            world.PlaceCharacter(target, new Point3D(105, 100, 0, 0));

            var bow = world.CreateItem();
            bow.ItemType = ItemType.WeaponBow;
            bow.BaseId = 0x13B2;
            archer.Equip(bow, Layer.TwoHanded);

            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            archer.Backpack = pack;

            var arrows = world.CreateItem();
            arrows.BaseId = 0x0F3F;
            arrows.ItemType = ItemType.WeaponArrow;
            arrows.Amount = 2;
            pack.AddItem(arrows);

            var broadcasts = new List<PacketBuffer>();
            client.BroadcastNearby = (_, _, packet, _) => broadcasts.Add(packet.Build());

            archer.FightTarget = target.Uid;
            archer.NextAttackTime = 0;

            client.TickCombat();

            Assert.Equal(1, arrows.Amount);
            Assert.Equal(SwingState.Swinging, archer.CombatSwingState);
            Assert.Contains(broadcasts, packet => IsArrowProjectile(packet));
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void HandleAttack_AcceptedTarget_SendsSwingAndAttackOk()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = (int)CombatFlags.FirstHitInstant;

            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var accounts = new AccountManager(loggerFactory);
            var client = TestHarness.CreateClient(loggerFactory, world, accounts, 1203);

            var attacker = world.CreateCharacter();
            attacker.IsPlayer = true;
            attacker.Str = attacker.Dex = attacker.Int = 100;
            attacker.Hits = attacker.MaxHits = 100;
            attacker.Stam = attacker.MaxStam = 100;
            attacker.SetSkill(SkillType.Swordsmanship, 1200);
            attacker.SetSkill(SkillType.Tactics, 1200);
            world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, attacker);

            var target = world.CreateCharacter();
            target.IsPlayer = true;
            target.Hits = target.MaxHits = 100;
            target.SetSkill(SkillType.Wrestling, 0);
            world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

            client.HandleAttack(target.Uid.Value);

            var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
            Assert.Contains(packets, p => p.Span.Length > 0 && p.Span[0] == 0x72);
            Assert.Contains(packets, p => IsAttackResponse(p, target.Uid.Value));
            Assert.Contains(packets, p => IsSwing(p, attacker.Uid.Value, target.Uid.Value));
            Assert.Equal(target.Uid, attacker.FightTarget);
            Assert.True(attacker.IsInWarMode);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void HandleAttack_SelfTarget_SendsRefuseWithoutWarmode()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = TestHarness.CreateClient(loggerFactory, world, accounts, 1204);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        client.HandleAttack(player.Uid.Value);

        var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
        Assert.Contains(packets, p => IsAttackResponse(p, 0));
        Assert.DoesNotContain(packets, p => p.Span.Length > 0 && p.Span[0] == 0x72);
        Assert.False(player.IsInWarMode);
        Assert.False(player.FightTarget.IsValid);
    }

    private static bool IsArrowProjectile(PacketBuffer packet)
    {
        var span = packet.Span;
        if (span.Length < 12 || span[0] != 0x70 || span[1] != 0)
            return false;

        ushort effectId = (ushort)((span[10] << 8) | span[11]);
        return effectId == 0x0F3F;
    }

    private static bool IsAttackResponse(PacketBuffer packet, uint targetSerial)
    {
        var span = packet.Span;
        return span.Length == 5 && span[0] == 0xAA && ReadUInt32(span, 1) == targetSerial;
    }

    private static bool IsSwing(PacketBuffer packet, uint attackerSerial, uint defenderSerial)
    {
        var span = packet.Span;
        return span.Length == 10
            && span[0] == 0x2F
            && span[1] == 0
            && ReadUInt32(span, 2) == attackerSerial
            && ReadUInt32(span, 6) == defenderSerial;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset) =>
        ((uint)span[offset] << 24) |
        ((uint)span[offset + 1] << 16) |
        ((uint)span[offset + 2] << 8) |
        span[offset + 3];
}
