using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.AI;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The flow around a swing, against the reference's Fight_Hit / OnTakeDamage /
/// OnHarmedBy:
/// <list type="bullet">
/// <item>the swing weapon is m_uidWeapon, set only for a weapon-typed item (CCharAct.cpp:308-311);</item>
/// <item>a bow with no ammo ends the fight (CCharFight.cpp:1866-1873, :1582-1589);</item>
/// <item>the cast disturb is rolled before the `iDmg &lt;= 0` return (CCharFight.cpp:880-916);</item>
/// <item>a connecting swing cancels only Healing, with its own message (:2203-2208);</item>
/// <item>a pet harmed by its owner deserts from OnHarmedBy (:312-313);</item>
/// <item>a guard deletes a conjured NPC instead of fighting it (:1818-1822);</item>
/// <item>the arrow stays in an NPC's body after any connecting hit (:2210-2218).</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CombatFlowTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Npc(GameWorld world, short x, NpcBrainType brain = NpcBrainType.Monster)
    {
        var ch = world.CreateCharacter();
        ch.NpcBrain = brain;
        ch.Str = ch.Dex = ch.Int = 100;
        ch.MaxHits = ch.Hits = 100;
        ch.MaxStam = ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    private static (GameClient client, Character player, Character target, GameWorld world) FightBench(
        int port, ItemType? weaponType, Layer weaponLayer, short targetX = 101)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.PrivLevel = PrivLevel.GM; // era-0 roll: always hits
        player.Str = player.Dex = 100;
        player.Stam = player.MaxStam = 100;
        player.SetStatFlag(StatFlag.War);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        client.BroadcastNearby = (_, _, _, _) => { };

        var target = world.CreateCharacter();
        target.MaxHits = 30000;
        target.Hits = 30000;
        world.PlaceCharacter(target, new Point3D(targetX, 100, 0, 0));

        if (weaponType != null)
        {
            var item = world.CreateItem();
            item.ItemType = weaponType.Value;
            item.BaseId = 0x13B2;
            player.Equip(item, weaponLayer);
        }
        return (client, player, target, world);
    }

    // ---- m_uidWeapon is weapon-typed only ----

    [Theory]
    [InlineData(ItemType.Spellbook)]
    [InlineData(ItemType.Shield)]
    public void ANonWeaponInHandIsNotTheSwingWeapon(ItemType held)
    {
        var (client, player, target, _) = FightBench(1501, held, Layer.OneHanded);
        var held1 = player.GetEquippedItem(Layer.OneHanded)!;
        player.FightTarget = target.Uid;
        player.NextAttackTime = 0;

        client.TickCombat();

        Assert.True(player.HasPendingHit);
        Assert.True(player.PendingHitWeaponCaptured);
        Assert.False(player.PendingHitWeapon.IsValid);
        Assert.NotEqual(held1.Uid, player.PendingHitWeapon);
    }

    [Fact]
    public void AWeaponInHandStillIsTheSwingWeapon()
    {
        var (client, player, target, _) = FightBench(1502, ItemType.WeaponSword, Layer.OneHanded);
        var sword = player.GetEquippedItem(Layer.OneHanded)!;
        player.FightTarget = target.Uid;
        player.NextAttackTime = 0;

        client.TickCombat();

        Assert.True(player.HasPendingHit);
        Assert.Equal(sword.Uid, player.PendingHitWeapon);
    }

    [Fact]
    public void AnNpcWithASpellbookInHandSwingsUnarmed()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = Npc(world, 100);
        npc.Direction = Direction.East;
        var victim = Npc(world, 101);
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        npc.Equip(book, Layer.OneHanded);
        npc.NextAttackTime = 0;

        var swing = typeof(NpcAI).GetMethod("TrySwingAttack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)swing.Invoke(ai, [npc, victim])!);

        Assert.True(npc.HasPendingHit);
        Assert.False(npc.PendingHitWeapon.IsValid);
    }

    // ---- WAR_SWING_INVALID for a bow with no ammo ----

    [Fact]
    public void ABowWithNoAmmoEndsTheFightWithThatTarget()
    {
        var (client, archer, target, world) = FightBench(1503, ItemType.WeaponBow, Layer.TwoHanded, targetX: 105);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        archer.Equip(pack, Layer.Pack);
        archer.CombatState.AddAttacker(target.Uid);
        archer.FightTarget = target.Uid;
        archer.NextAttackTime = 0;

        client.TickCombat();

        // Fight_Clear(target): the target is dropped from the list and from the fight;
        // war mode is untouched.
        Assert.False(archer.FightTarget.IsValid);
        Assert.Equal(-1, archer.CombatState.IndexOfAttacker(target.Uid));
        Assert.False(archer.HasPendingHit);
        Assert.True(archer.IsInWarMode);
    }

    // ---- disturb before the damage return ----

    private static (SpellEngine engine, Character caster, Character attacker) CastingCaster()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Clumsy,
            Flags = SpellFlag.TargChar,
            ManaCost = 0,
            CastTimeBase = 50,
            InterruptBase = 1000,
            InterruptScale = 0,
        });

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.MaxMana = 100;
        caster.Mana = 100;
        caster.MaxHits = 100;
        caster.Hits = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        caster.Equip(pack, Layer.Pack);
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.More1 = 1u << ((int)SpellType.Clumsy - 1);
        pack.AddItem(book);

        var attacker = world.CreateCharacter();
        world.PlaceCharacter(attacker, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.Clumsy, attacker.Uid, attacker.Position) > 0);
        Assert.True(caster.IsCasting);
        return (engine, caster, attacker);
    }

    [Fact]
    public void ABlowThatArmourAbsorbedEntirelyStillDisturbsTheCast()
    {
        var (engine, caster, attacker) = CastingCaster();

        Assert.True(engine.TryInterruptFromBlow(caster, 0, DamageType.HitSlash, attacker));
        Assert.False(caster.IsCasting);
    }

    [Fact]
    public void ABlowCarryingNoDisturbNeverDisturbs()
    {
        var (engine, caster, attacker) = CastingCaster();

        Assert.False(engine.TryInterruptFromBlow(caster, 12, DamageType.HitSlash | DamageType.NoDisturb, attacker));
        Assert.True(caster.IsCasting);
    }

    [Theory]
    [InlineData(StatFlag.Invul)]
    [InlineData(StatFlag.Stone)]
    public void ABouncedBlowDoesNotDisturb(StatFlag protection)
    {
        // The bounce returns ahead of the disturb (CCharFight.cpp:640-650).
        var (engine, caster, attacker) = CastingCaster();
        caster.SetStatFlag(protection);

        Assert.False(engine.TryInterruptFromBlow(caster, 0, DamageType.HitSlash, attacker));
        Assert.True(caster.IsCasting);
    }

    [Fact]
    public void ABlowThatDidDamageStillDisturbs()
    {
        var (engine, caster, attacker) = CastingCaster();

        Assert.True(engine.TryInterruptFromBlow(caster, 9, DamageType.HitSlash, attacker));
        Assert.False(caster.IsCasting);
    }

    // ---- only Healing is cancelled by a hit ----

    [Fact]
    public void AConnectingBlowCancelsHealingAndTellsTheVictim()
    {
        var world = CreateWorld();
        var victim = Npc(world, 100);
        var saved = Character.SendOwnerMessage;
        var said = new List<string>();
        Character.SendOwnerMessage = (_, text) => said.Add(text);
        try
        {
            victim.BeginSkillPending((int)SkillType.Healing, Environment.TickCount64 + 5000,
                Environment.TickCount64 + 5000, Serial.Invalid, null);

            victim.InterruptHealingOnHit();

            Assert.False(victim.HasActiveSkillPending());
            Assert.Equal(ServerMessages.Get(Msg.HealingInterrupt), Assert.Single(said, s => s == ServerMessages.Get(Msg.HealingInterrupt)));
        }
        finally
        {
            Character.SendOwnerMessage = saved;
        }
    }

    [Fact]
    public void AnotherSkillCarriesOnThroughDamageAndAHit()
    {
        var world = CreateWorld();
        var victim = Npc(world, 100);
        victim.BeginSkillPending((int)SkillType.Mining, Environment.TickCount64 + 5000,
            Environment.TickCount64 + 5000, Serial.Invalid, null);

        victim.Hits -= 10;          // losing hit points alone ends no skill
        victim.InterruptHealingOnHit(); // and a hit cancels Healing only

        Assert.True(victim.HasActiveSkillPending());
        Assert.Equal((int)SkillType.Mining, victim.SkillPendingId);
    }

    // ---- pet desertion lives in OnHarmedBy ----

    private static (Character owner, Character pet) PetBench()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Hits = owner.MaxHits = 100;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pet = Npc(world, 101);
        pet.SetTag("OWNER_UID", owner.Uid.Value.ToString());
        Assert.Equal(owner.Uid, pet.OwnerSerial);
        return (owner, pet);
    }

    [Fact]
    public void APetHarmedByItsOwnerDeserts()
    {
        var oldFlags = Character.CombatFlags;
        var savedDesert = Character.OnPetDesert;
        try
        {
            Character.CombatFlags = 0;
            Character.OnPetDesert = null;
            var (owner, pet) = PetBench();

            pet.OnAttackedBy(owner);

            Assert.False(pet.OwnerSerial.IsValid);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
            Character.OnPetDesert = savedDesert;
        }
    }

    [Fact]
    public void NoPetDesertKeepsTheHarmedPetOwned()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = (int)CombatFlags.NoPetDesert;
            var (owner, pet) = PetBench();

            pet.OnAttackedBy(owner);

            Assert.Equal(owner.Uid, pet.OwnerSerial);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void ThePetDesertTriggerCanKeepTheHarmedPetServing()
    {
        var oldFlags = Character.CombatFlags;
        var savedDesert = Character.OnPetDesert;
        try
        {
            Character.CombatFlags = 0;
            int fired = 0;
            Character.OnPetDesert = (_, _) => { fired++; return true; };
            var (owner, pet) = PetBench();

            pet.OnAttackedBy(owner);

            Assert.Equal(1, fired);
            Assert.Equal(owner.Uid, pet.OwnerSerial);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
            Character.OnPetDesert = savedDesert;
        }
    }

    [Fact]
    public void ABerserkSummonNeverDeserts()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = 0;
            var (owner, pet) = PetBench();
            pet.NpcBrain = NpcBrainType.Berserk;

            pet.OnAttackedBy(owner);

            Assert.Equal(owner.Uid, pet.OwnerSerial);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void OrderingAnAttackOnOnesPetHarmsNothing()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = 0;
            var lf = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1504);
            var owner = world.CreateCharacter();
            owner.IsPlayer = true;
            owner.Hits = owner.MaxHits = 100;
            world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, owner);
            client.BroadcastNearby = (_, _, _, _) => { };
            var pet = world.CreateCharacter();
            pet.Hits = pet.MaxHits = 100;
            world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
            pet.SetTag("OWNER_UID", owner.Uid.Value.ToString());

            client.HandleAttack(pet.Uid.Value);

            Assert.Equal(owner.Uid, pet.OwnerSerial);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    // ---- guards remove conjured NPCs ----

    [Fact]
    public void AGuardDeletesAConjuredCreatureInsteadOfFightingIt()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var guard = Npc(world, 100, NpcBrainType.Guard);
        guard.Direction = Direction.East;
        var summon = Npc(world, 101);
        summon.SetStatFlag(StatFlag.Conjured);
        var swungAt = new List<Character>();
        ai.OnNpcSwingStart = (_, t, _, _, _) => swungAt.Add(t);
        guard.NextAttackTime = 0;

        var swing = typeof(NpcAI).GetMethod("TrySwingAttack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        swing.Invoke(ai, [guard, summon]);

        Assert.True(summon.IsDeleted);
        Assert.Empty(swungAt);
        Assert.False(guard.HasPendingHit);
    }

    [Fact]
    public void AGuardStillFightsAnOrdinaryCreature()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var guard = Npc(world, 100, NpcBrainType.Guard);
        guard.Direction = Direction.East;
        var monster = Npc(world, 101);
        var swungAt = new List<Character>();
        ai.OnNpcSwingStart = (_, t, _, _, _) => swungAt.Add(t);
        guard.NextAttackTime = 0;

        var swing = typeof(NpcAI).GetMethod("TrySwingAttack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)swing.Invoke(ai, [guard, monster])!);

        Assert.False(monster.IsDeleted);
        Assert.Contains(monster, swungAt);
    }

    [Fact]
    public void AMonsterIsNotAGuardAndLeavesAConjuredCreatureAlone()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var monster = Npc(world, 100);
        monster.Direction = Direction.East;
        var summon = Npc(world, 101);
        summon.SetStatFlag(StatFlag.Conjured);
        monster.NextAttackTime = 0;

        var swing = typeof(NpcAI).GetMethod("TrySwingAttack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        swing.Invoke(ai, [monster, summon]);

        Assert.False(summon.IsDeleted);
    }

    // ---- the arrow stays in an NPC's body ----

    [Fact]
    public void ATargetWithNoPackStillKeepsSomeOfTheArrowsThatHitIt()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = 0;
            var (client, archer, target, world) = FightBench(1505, ItemType.WeaponBow, Layer.TwoHanded, targetX: 105);
            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            archer.Equip(pack, Layer.Pack);
            var arrows = world.CreateItem();
            arrows.ItemType = ItemType.WeaponArrow;
            arrows.BaseId = 0x0F3F;
            arrows.Amount = 200;
            pack.AddItem(arrows);
            Assert.Null(target.Backpack);

            archer.FightTarget = target.Uid;
            const int shots = 80;
            int spent = 0;
            for (int i = 0; i < shots; i++)
            {
                target.Hits = target.MaxHits;
                archer.NextAttackTime = 0;
                archer.SetCombatSwingState(SwingState.Ready);
                client.TickCombat();
                archer.SwingHitTime = Environment.TickCount64 - 1;
                client.TickCombat();
                spent = 200 - arrows.Amount;
            }

            // A hit that finds no pack bounces the arrow to the ground at the target's
            // feet: 41% of the connecting shots. 80 shots all missing the roll is a
            // 1-in-10^18 event.
            Assert.True(spent > 0);
            int onGround = world.GetItemsInRange(target.Position, 0)
                .Count(i => i.ItemType == ItemType.WeaponArrow);
            Assert.True(onGround > 0, "no recovered arrow reached the ground");
            Assert.True(onGround < spent, "every hit kept its arrow");
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }
}
