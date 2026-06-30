using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Behavioral parity for the Source-X COMBATFLAGS that gate the swing path:
/// NODIRCHANGE, ARCHERYCANMOVE and PARALYZE_CANSWING. These were defined in the
/// CombatFlags enum but had no effect on the engine.
/// </summary>
public class CombatFlagParityTests
{
    private static (GameClient Client, Character Attacker, Character Target) MakeMeleeFight(
        int port, GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.PrivLevel = PrivLevel.GM; // skip LOS so swing-prep is deterministic
        attacker.Str = attacker.Dex = 100;
        attacker.Stam = attacker.MaxStam = 100;
        attacker.SetSkill(SkillType.Swordsmanship, 1000);
        attacker.SetSkill(SkillType.Tactics, 1000);
        attacker.SetStatFlag(StatFlag.War);
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, attacker);
        client.BroadcastNearby = (_, _, _, _) => { };

        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0)); // 1 tile East

        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        sword.BaseId = 0x0F5E;
        attacker.Equip(sword, Layer.OneHanded);

        attacker.FightTarget = target.Uid;
        attacker.NextAttackTime = 0;
        return (client, attacker, target);
    }

    [Fact]
    public void ArcheryCanMove_BypassesMovementSettleDelay()
    {
        var oldFlags = Character.CombatFlags;
        var oldDelay = Character.CombatArcheryMovementDelay;
        var oldMin = Character.ArcheryMinDist;
        var oldMax = Character.ArcheryMaxDist;
        try
        {
            Character.ArcheryMinDist = 1;
            Character.ArcheryMaxDist = 12;
            Character.CombatArcheryMovementDelay = 500;

            var world = TestHarness.CreateWorld();
            var attacker = world.CreateCharacter();
            world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
            var target = world.CreateCharacter();
            world.PlaceCharacter(target, new Point3D(105, 100, 0, 0));
            var bow = new Item { ItemType = ItemType.WeaponBow, BaseId = 0x13B2 };

            attacker.LastMoveTick = Environment.TickCount64;
            long now = attacker.LastMoveTick + 100; // 100ms into the 500ms settle

            // Without the flag the recent move blocks the shot.
            Character.CombatFlags = 0;
            var blocked = CombatHelper.ValidateSwingPrep(
                world, attacker, target, bow, PrivLevel.GM, now, (_, _) => true);
            Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, blocked.Result);

            // With COMBAT_ARCHERYCANMOVE the settle delay is ignored.
            Character.CombatFlags = (int)CombatFlags.ArcheryCanMove;
            var ready = CombatHelper.ValidateSwingPrep(
                world, attacker, target, bow, PrivLevel.GM, now, (_, _) => true);
            Assert.Equal(CombatHelper.SwingPrepResult.Ready, ready.Result);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
            Character.CombatArcheryMovementDelay = oldDelay;
            Character.ArcheryMinDist = oldMin;
            Character.ArcheryMaxDist = oldMax;
        }
    }

    [Fact]
    public void ParalyzeCanSwing_FrozenAttackerSwingsOnlyWithFlag()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            // Without the flag a frozen (paralyzed) attacker cannot swing.
            Character.CombatFlags = 0;
            var (c1, a1, _) = MakeMeleeFight(1301, TestHarness.CreateWorld());
            a1.SetStatFlag(StatFlag.Freeze);
            c1.TickCombat();
            Assert.NotEqual(SwingState.Swinging, a1.CombatSwingState);

            // With COMBAT_PARALYZE_CANSWING the frozen attacker still swings.
            Character.CombatFlags = (int)CombatFlags.ParalyzeCanSwing;
            var (c2, a2, _) = MakeMeleeFight(1302, TestHarness.CreateWorld());
            a2.SetStatFlag(StatFlag.Freeze);
            c2.TickCombat();
            Assert.Equal(SwingState.Swinging, a2.CombatSwingState);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void AttackingInnocentInWilderness_FlagsCriminalRegardlessOfRegion()
    {
        var oldEnabled = Character.AttackingIsACrimeEnabled;
        try
        {
            Character.AttackingIsACrimeEnabled = true;
            var world = TestHarness.CreateWorld(); // plain map: no guarded region
            var lf = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1320);
            client.BroadcastNearby = (_, _, _, _) => { };

            var attacker = world.CreateCharacter();
            attacker.IsPlayer = true;
            attacker.PrivLevel = PrivLevel.Player;
            attacker.Str = attacker.Dex = 100;
            attacker.Stam = attacker.MaxStam = 100;
            attacker.SetStatFlag(StatFlag.War);
            world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, attacker);

            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            attacker.Equip(sword, Layer.OneHanded);

            var victim = world.CreateCharacter();
            victim.IsPlayer = true;
            victim.Hits = victim.MaxHits = 100;
            world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0)); // innocent blue, adjacent

            Assert.False(attacker.IsCriminal);

            client.HandleAttack(victim.Uid.Value);

            // ServUO Mobile.CriminalAction sets Criminal=true unconditionally; only the
            // guard RESPONSE is region-gated. Attacking an innocent in the open (no
            // guarded region) must still flag the attacker grey.
            Assert.True(attacker.IsCriminal);
        }
        finally
        {
            Character.AttackingIsACrimeEnabled = oldEnabled;
        }
    }

    [Fact]
    public void NoDirChange_PreservesFacingDuringSwing()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            // Without the flag the attacker turns to face the target (East).
            Character.CombatFlags = 0;
            var (c1, a1, _) = MakeMeleeFight(1303, TestHarness.CreateWorld());
            a1.Direction = Direction.North;
            c1.TickCombat();
            Assert.Equal(SwingState.Swinging, a1.CombatSwingState); // swing happened
            Assert.Equal(Direction.East, a1.Direction);            // and it rotated

            // With COMBAT_NODIRCHANGE the swing still happens but facing is kept.
            Character.CombatFlags = (int)CombatFlags.NoDirChange;
            var (c2, a2, _) = MakeMeleeFight(1304, TestHarness.CreateWorld());
            a2.Direction = Direction.North;
            c2.TickCombat();
            Assert.Equal(SwingState.Swinging, a2.CombatSwingState);
            Assert.Equal(Direction.North, a2.Direction);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    // ---- Two-phase swing (windup -> hit): STAYINRANGE / SWING_NORANGE / PREHIT ----

    [Fact]
    public void StayInRange_MovedOutBeforeHit_Misses()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            // STAYINRANGE opens a windup window and re-checks reach when the hit lands.
            Character.CombatFlags = (int)CombatFlags.StayInRange;
            var world = TestHarness.CreateWorld();
            var (client, attacker, target) = MakeMeleeFight(1310, world);

            // First tick starts the swing windup — the hit is deferred, no damage yet.
            client.TickCombat();
            Assert.True(attacker.HasPendingHit);
            Assert.Equal(100, target.Hits);

            // The target steps out of reach before the windup elapses → the hit misses.
            world.MoveCharacter(target, new Point3D(120, 100, 0, 0));
            attacker.SwingHitTime = Environment.TickCount64 - 1; // force the windup elapsed
            client.TickCombat();

            Assert.False(attacker.HasPendingHit); // resolved (as a miss)
            Assert.Equal(100, target.Hits);       // ResolveAttack never ran — no damage
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void SwingNoRange_StartsOutOfRange_WaitsThenResolvesInRange()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            // SWING_NORANGE lets the swing START out of range; the hit waits for reach.
            Character.CombatFlags = (int)CombatFlags.SwingNoRange;
            var world = TestHarness.CreateWorld();
            var (client, attacker, target) = MakeMeleeFight(1311, world);
            world.MoveCharacter(target, new Point3D(110, 100, 0, 0)); // out of melee reach

            // The swing starts despite being out of range.
            client.TickCombat();
            Assert.True(attacker.HasPendingHit);

            // Windup elapses but the target is still out of reach → the hit WAITS.
            attacker.SwingHitTime = Environment.TickCount64 - 1;
            client.TickCombat();
            Assert.True(attacker.HasPendingHit); // still pending, waiting for range

            // Target closes to melee range → the pending hit resolves.
            world.MoveCharacter(target, new Point3D(101, 100, 0, 0));
            attacker.SwingHitTime = Environment.TickCount64 - 1;
            client.TickCombat();
            Assert.False(attacker.HasPendingHit); // resolved now that it is in range
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void AnimHitSmooth_SetsSwingAnimationFrameDelay()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            var world = TestHarness.CreateWorld();
            var (client, attacker, _) = MakeMeleeFight(1313, world);
            var packets = new List<PacketBuffer>();
            client.BroadcastNearby = (_, _, packet, _) => packets.Add(packet.Build());

            // Without the flag the 0x6E swing animation uses the default (0) delay.
            Character.CombatFlags = 0;
            attacker.NextAttackTime = 0;
            attacker.SetCombatSwingState(SwingState.Ready);
            client.TickCombat();
            var plain = packets.FirstOrDefault(p => IsSwingAnim(p, attacker.Uid.Value));
            Assert.NotNull(plain);
            Assert.Equal(0, SwingAnimDelay(plain!));

            // With COMBAT_ANIM_HIT_SMOOTH the swing animation carries a non-zero
            // per-frame delay (paced to the swing time).
            packets.Clear();
            Character.CombatFlags = (int)CombatFlags.AnimHitSmooth;
            attacker.NextAttackTime = 0;
            attacker.SetCombatSwingState(SwingState.Ready);
            client.TickCombat();
            var smooth = packets.FirstOrDefault(p => IsSwingAnim(p, attacker.Uid.Value));
            Assert.NotNull(smooth);
            Assert.True(SwingAnimDelay(smooth!) > 0);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    // 0x6E PacketAnimation: id(1) + serial(4) + action(2) + frameCount(2) +
    // repeatCount(2) + forward(1) + repeat(1) + delay(1) = 14 bytes.
    private static bool IsSwingAnim(PacketBuffer p, uint serial)
    {
        var s = p.Span;
        return s.Length == 14 && s[0] == 0x6E &&
            (((uint)s[1] << 24) | ((uint)s[2] << 16) | ((uint)s[3] << 8) | s[4]) == serial;
    }

    private static byte SwingAnimDelay(PacketBuffer p) => p.Span[13];

    [Fact]
    public void PreHit_ResolvesAtomically_EvenWithStayInRange()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            // PREHIT forces the hit to land at swing start (atomic) and disables the
            // STAYINRANGE / SWING_NORANGE windup deferral.
            Character.CombatFlags = (int)(CombatFlags.PreHit | CombatFlags.StayInRange);
            var world = TestHarness.CreateWorld();
            var (client, attacker, _) = MakeMeleeFight(1312, world);

            client.TickCombat();

            // The hit resolved in the same tick — no pending hit lingers.
            Assert.False(attacker.HasPendingHit);
            Assert.Equal(SwingState.Swinging, attacker.CombatSwingState);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }
}
