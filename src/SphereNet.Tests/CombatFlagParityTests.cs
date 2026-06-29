using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
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
}
