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

// Combat wave C2 — the remaining Source-X trigger LOCAL contracts:
// @HitCheck LOCAL.Recoil_NoRange (per-swing SWING_NORANGE override,
// CCharFight.cpp:1767), @HitMiss LOCAL.Arrow = the live ammo stack UID
// (CCharFight.cpp:2032), and the @Hit LOCAL.ItemDamageChance /
// ItemPoisonReductionChance/Amount weapon-side knobs (CCharFight.cpp:2148).
public class CombatWaveC2ParityTests
{
    private static (GameClient Client, Character Attacker, Character Target) MakeFight(
        int port, GameWorld world, ItemType weaponType, int targetDistX)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.PrivLevel = PrivLevel.GM; // skip LOS so swing-prep is deterministic
        attacker.Str = attacker.Dex = 100;
        attacker.Stam = attacker.MaxStam = 100;
        attacker.SetStatFlag(StatFlag.War);
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, attacker);
        client.BroadcastNearby = (_, _, _, _) => { };

        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D((short)(100 + targetDistX), 100, 0, 0));

        var weapon = world.CreateItem();
        weapon.ItemType = weaponType;
        weapon.BaseId = weaponType == ItemType.WeaponBow ? (ushort)0x13B2 : (ushort)0x0F5E;
        attacker.Equip(weapon, weaponType == ItemType.WeaponBow ? Layer.TwoHanded : Layer.OneHanded);

        attacker.FightTarget = target.Uid;
        attacker.NextAttackTime = 0;
        return (client, attacker, target);
    }

    // ---- @HitCheck LOCAL.Recoil_NoRange ----

    [Fact]
    public void HitCheck_RecoilNoRange_ScriptEnablesOutOfRangeSwing()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_recoil_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_recoil_probe]
            ON=@HitCheck
            TAG.GOTNR=<LOCAL.Recoil_NoRange>
            LOCAL.Recoil_NoRange=1
            """);
        var oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = 0; // SWING_NORANGE globally OFF
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);
            stack.Dispatcher.BuildUsedTriggerCache();

            var world = TestHarness.CreateWorld();
            var (client, attacker, _) = MakeFight(1410, world, ItemType.WeaponSword, targetDistX: 10);
            client.SetEngines(triggerDispatcher: stack.Dispatcher);
            attacker.Events.Add(stack.Resources.ResolveDefName("e_recoil_probe"));

            // Out of melee reach with the flag off: only the script's
            // LOCAL.Recoil_NoRange=1 lets this swing start (windup opens and
            // waits for reach, the SWING_NORANGE semantics per-swing).
            client.TickCombat();

            Assert.True(attacker.TryGetTag("GOTNR", out var nr) && nr == "0"); // seeded from the (off) flag
            Assert.True(attacker.HasPendingHit);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void HitCheck_WithoutRecoilOverride_OutOfRangeSwingStillBlocked()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_recoil_off_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_recoil_noop_probe]
            ON=@HitCheck
            TAG.SAWCHECK=1
            """);
        var oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = 0;
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);
            stack.Dispatcher.BuildUsedTriggerCache();

            var world = TestHarness.CreateWorld();
            var (client, attacker, _) = MakeFight(1411, world, ItemType.WeaponSword, targetDistX: 10);
            client.SetEngines(triggerDispatcher: stack.Dispatcher);
            attacker.Events.Add(stack.Resources.ResolveDefName("e_recoil_noop_probe"));

            // A hooked @HitCheck opens the fast-path gate (the trigger fires,
            // Source-X order), but with Recoil_NoRange left at 0 the range
            // validation still blocks the swing.
            client.TickCombat();

            Assert.True(attacker.TryGetTag("SAWCHECK", out var v) && v == "1");
            Assert.False(attacker.HasPendingHit);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
            File.Delete(tempFile);
        }
    }

    // ---- @HitMiss LOCAL.Arrow ----

    [Fact]
    public void HitMiss_ArrowLocal_IsLiveAmmoStackUid_AndArrowHandledSkipsConsume()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_arrow_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_arrow_probe]
            ON=@HitMiss
            TAG.GOTARROW=<LOCAL.Arrow>
            LOCAL.ArrowHandled=1
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);
            stack.Dispatcher.BuildUsedTriggerCache();

            var world = TestHarness.CreateWorld();
            var (client, attacker, target) = MakeFight(1412, world, ItemType.WeaponBow, targetDistX: 5);
            client.SetEngines(triggerDispatcher: stack.Dispatcher);
            attacker.Events.Add(stack.Resources.ResolveDefName("e_arrow_probe"));
            // GM priv would auto-hit every swing (era-0 roll); a plain player
            // with 0 Archery misses often, exercising the @HitMiss branch.
            attacker.PrivLevel = PrivLevel.Player;

            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            attacker.Equip(pack, Layer.Pack);
            var arrows = world.CreateItem();
            arrows.ItemType = ItemType.WeaponArrow;
            arrows.Amount = 200;
            pack.AddItem(arrows);

            // Swing until a natural miss fires @HitMiss. Each loop resets the
            // swing pacing; hits consume 1 arrow, the scripted miss must NOT
            // (LOCAL.ArrowHandled=1 hands the ammo to the script).
            bool missSeen = false;
            for (int i = 0; i < 300 && !missSeen; i++)
            {
                target.Hits = target.MaxHits;
                attacker.NextAttackTime = 0;
                attacker.SetCombatSwingState(SwingState.Ready);
                int before = arrows.Amount;
                client.TickCombat();
                if (attacker.TryGetTag("GOTARROW", out _))
                {
                    missSeen = true;
                    // The miss spent nothing — ArrowHandled took over the ammo.
                    Assert.Equal(before, arrows.Amount);
                }
            }

            Assert.True(missSeen, "no miss occurred in 300 swings");
            Assert.True(attacker.TryGetTag("GOTARROW", out var got));
            // LOCAL.Arrow carried the LIVE pack stack's UID (Source-X pAmmo),
            // not the old constant 1.
            Assert.Equal(arrows.Uid.Value.ToString(), got);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ---- @Hit LOCAL.ItemPoisonReductionChance / Amount ----

    [Fact]
    public void Hit_PoisonReductionChanceZero_PreservesWeaponPoisonCharges()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_poisonred_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_poisonred_probe]
            ON=@Hit
            TAG.SEENCHANCE=<LOCAL.ItemPoisonReductionChance>
            LOCAL.ItemPoisonReductionChance=0
            """);
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);
            CombatEngine.OnHitDamage = ctx => stack.Dispatcher.RunHitDamageTriggers(ctx);

            var world = TestHarness.CreateWorld();
            var attacker = MakeCombatant(world, 100);
            var target = MakeCombatant(world, 101);
            attacker.Events.Add(stack.Resources.ResolveDefName("e_poisonred_probe"));

            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            sword.SetTag("POISON_SKILL", "1000");
            sword.SetTag("POISON_CHARGES", "5");
            attacker.Equip(sword, Layer.OneHanded);

            Assert.True(ResolveHit(attacker, target, sword) >= 0);

            // The poison delivered but the script's 0% reduction chance kept
            // every charge (default engine behavior spends 1 per delivery).
            Assert.True(attacker.TryGetTag("SEENCHANCE", out var c) && c == "100"); // Source-X seed
            Assert.True(target.IsPoisoned);
            Assert.True(sword.TryGetTag("POISON_CHARGES", out var charges) && charges == "5");
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Hit_DefaultPoisonReduction_SpendsOneCharge()
    {
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null; // engine defaults (100% / 1)

            var world = TestHarness.CreateWorld();
            var attacker = MakeCombatant(world, 100);
            var target = MakeCombatant(world, 101);

            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            sword.SetTag("POISON_SKILL", "1000");
            sword.SetTag("POISON_CHARGES", "5");
            attacker.Equip(sword, Layer.OneHanded);

            Assert.True(ResolveHit(attacker, target, sword) > 0);
            Assert.True(sword.TryGetTag("POISON_CHARGES", out var charges) && charges == "4");
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    // ---- @Hit LOCAL.ItemDamageChance (weapon wear gate) ----

    [Fact]
    public void Hit_ItemDamageChance_GatesWeaponDurabilityWear()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_weapwear_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_wear_never]
            ON=@Hit
            LOCAL.ItemDamageChance=0

            [EVENTS e_wear_always]
            ON=@Hit
            LOCAL.ItemDamageChance=100
            """);
        var savedHook = CombatEngine.OnHitDamage;
        bool savedEnabled = CombatEngine.DurabilityEnabled;
        int savedChance = CombatEngine.DurabilityLossChance;
        int savedMin = CombatEngine.DurabilityLossMin;
        int savedMax = CombatEngine.DurabilityLossMax;
        bool savedBreak = CombatEngine.BreakOnZeroHits;
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);
            CombatEngine.OnHitDamage = ctx => stack.Dispatcher.RunHitDamageTriggers(ctx);
            CombatEngine.DurabilityEnabled = true;
            CombatEngine.DurabilityLossChance = 100;
            CombatEngine.DurabilityLossMin = 1;
            CombatEngine.DurabilityLossMax = 1;
            CombatEngine.BreakOnZeroHits = false;

            var world = TestHarness.CreateWorld();
            var attacker = MakeCombatant(world, 100);
            var target = MakeCombatant(world, 101);

            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            sword.HitsMax = 50;
            sword.HitsCur = 50;
            attacker.Equip(sword, Layer.OneHanded);

            // LOCAL.ItemDamageChance=0: the weapon never wears on a hit.
            attacker.Events.Add(stack.Resources.ResolveDefName("e_wear_never"));
            Assert.True(ResolveHit(attacker, target, sword) >= 0);
            Assert.Equal(50, sword.HitsCur);

            // LOCAL.ItemDamageChance=100: the weapon wears every hit.
            attacker.Events.Clear();
            attacker.Events.Add(stack.Resources.ResolveDefName("e_wear_always"));
            Assert.True(ResolveHit(attacker, target, sword) >= 0);
            Assert.Equal(49, sword.HitsCur);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            CombatEngine.DurabilityEnabled = savedEnabled;
            CombatEngine.DurabilityLossChance = savedChance;
            CombatEngine.DurabilityLossMin = savedMin;
            CombatEngine.DurabilityLossMax = savedMax;
            CombatEngine.BreakOnZeroHits = savedBreak;
            File.Delete(tempFile);
        }
    }

    private static Character MakeCombatant(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 100; ch.Hits = 100;
        ch.SetSkill(SkillType.Swordsmanship, 1000);
        ch.SetSkill(SkillType.Tactics, 1000);
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static int ResolveHit(Character attacker, Character target, Item weapon)
    {
        for (int i = 0; i < 400; i++)
        {
            target.Hits = target.MaxHits;
            target.CurePoison();
            int d = CombatEngine.ResolveAttack(attacker, target, weapon);
            if (d >= 0) return d;
        }
        return -1;
    }
}
