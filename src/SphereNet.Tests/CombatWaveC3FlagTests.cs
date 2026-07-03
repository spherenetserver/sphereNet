using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Combat wave C3 — behavior for the COMBATFLAGS that existed only as enum
// values: ALLOWHITFROMSHIP (Fight_CanHit ship boundary, CCharFight.cpp:1725),
// DCLICKSELF_UNMOUNTS (Cmd_Use_Obj, CClientEvent.cpp:2368), NOPETDESERT
// (OnHarmedBy, CCharFight.cpp:312) and ATTACK_NOAGGREIVED (OnAttackedBy,
// CCharFight.cpp:349).
public class CombatWaveC3FlagTests
{
    private static Character MakeChar(GameWorld world, int x, int y, bool player = true)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.Hits = ch.MaxHits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, (short)y, 0, 0));
        return ch;
    }

    // ---- COMBAT_ALLOWHITFROMSHIP ----

    [Fact]
    public void ShipBoundaryCombat_BlockedUnlessAllowHitFromShip()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            var world = TestHarness.CreateWorld();
            var ship = new SphereNet.Game.World.Regions.Region
            { Name = "ship_test", Flags = RegionFlag.Ship, MapIndex = 0 };
            ship.AddRect(90, 90, 105, 110);
            world.AddRegion(ship);

            var aboard = MakeChar(world, 100, 100);  // inside the ship region
            var ashore = MakeChar(world, 120, 100);  // outside

            // Across the ship boundary: blocked without the flag, either way.
            Character.CombatFlags = 0;
            Assert.True(CombatHelper.IsCombatBlockedByRegion(world, aboard, ashore));
            Assert.True(CombatHelper.IsCombatBlockedByRegion(world, ashore, aboard));

            // COMBAT_ALLOWHITFROMSHIP opens it.
            Character.CombatFlags = (int)CombatFlags.AllowHitFromShip;
            Assert.False(CombatHelper.IsCombatBlockedByRegion(world, aboard, ashore));
            Assert.False(CombatHelper.IsCombatBlockedByRegion(world, ashore, aboard));

            // Both on the SAME ship may always fight (no flag needed).
            Character.CombatFlags = 0;
            var shipmate = MakeChar(world, 101, 100);
            Assert.False(CombatHelper.IsCombatBlockedByRegion(world, aboard, shipmate));
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    // ---- COMBAT_DCLICKSELF_UNMOUNTS ----

    [Fact]
    public void DClickSelfKeepsMount_OnlyMidFightWithoutTheFlag()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            var world = TestHarness.CreateWorld();
            var rider = MakeChar(world, 100, 100);
            var foe = MakeChar(world, 101, 100);

            // In war with an active fight and no flag: keep the mount (paperdoll).
            Character.CombatFlags = 0;
            rider.SetStatFlag(StatFlag.War);
            rider.FightTarget = foe.Uid;
            Assert.True(CombatHelper.DClickSelfKeepsMount(rider));

            // The flag opts into the unmount even mid-fight.
            Character.CombatFlags = (int)CombatFlags.DClickSelfUnmounts;
            Assert.False(CombatHelper.DClickSelfKeepsMount(rider));

            // Out of combat the dclick always unmounts.
            Character.CombatFlags = 0;
            rider.FightTarget = Core.Types.Serial.Invalid;
            Assert.False(CombatHelper.DClickSelfKeepsMount(rider));
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    // ---- COMBAT_NOPETDESERT ----

    [Fact]
    public void AttackOwnPet_Deserts_UnlessNoPetDesert()
    {
        var oldFlags = Character.CombatFlags;
        var savedDesert = Character.OnPetDesert;
        var loggerFactory = LoggerFactory.Create(_ => { });
        try
        {
            Character.OnPetDesert = null;
            var world = TestHarness.CreateWorld();
            var accounts = new AccountManager(loggerFactory);

            var client = TestHarness.CreateClient(loggerFactory, world, accounts, 1420);
            var owner = MakeChar(world, 100, 100);
            TestHarness.AttachCharacter(client, owner);

            var pet = MakeChar(world, 101, 100, player: false);
            pet.SetTag("OWNER_UID", owner.Uid.Value.ToString());
            Assert.Equal(owner.Uid, pet.OwnerSerial);

            // COMBAT_NOPETDESERT: friendly fire allowed, the pet stays owned.
            Character.CombatFlags = (int)CombatFlags.NoPetDesert;
            client.HandleAttack(pet.Uid.Value);
            Assert.Equal(owner.Uid, pet.OwnerSerial);

            // Without the flag the pet deserts its owner on the attack.
            Character.CombatFlags = 0;
            client.HandleAttack(pet.Uid.Value);
            Assert.False(pet.OwnerSerial.IsValid);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
            Character.OnPetDesert = savedDesert;
        }
    }

    // ---- COMBAT_ATTACK_NOAGGREIVED ----

    [Fact]
    public void AttackNoAggreived_SkipsTheCriminalFlagOnAttack()
    {
        var oldFlags = Character.CombatFlags;
        var loggerFactory = LoggerFactory.Create(_ => { });
        try
        {
            var world = TestHarness.CreateWorld();
            var accounts = new AccountManager(loggerFactory);

            // With the flag: starting a fight against an innocent does NOT
            // mark the attacker criminal (old sphere behaviour).
            Character.CombatFlags = (int)CombatFlags.AttackNoAggreived;
            var clientA = TestHarness.CreateClient(loggerFactory, world, accounts, 1421);
            var attackerA = MakeChar(world, 100, 100);
            TestHarness.AttachCharacter(clientA, attackerA);
            var victimA = MakeChar(world, 101, 100);
            clientA.HandleAttack(victimA.Uid.Value);
            Assert.False(attackerA.IsStatFlag(StatFlag.Criminal));

            // Control: without the flag the same attack flags criminal.
            Character.CombatFlags = 0;
            var clientB = TestHarness.CreateClient(loggerFactory, world, accounts, 1422);
            var attackerB = MakeChar(world, 100, 102);
            TestHarness.AttachCharacter(clientB, attackerB);
            var victimB = MakeChar(world, 101, 102);
            clientB.HandleAttack(victimB.Uid.Value);
            Assert.True(attackerB.IsStatFlag(StatFlag.Criminal));
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }
}
