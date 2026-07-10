using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class NpcAiAuditRegressionTests
{
    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private static T PrivateField<T>(NpcAI ai, string name) =>
        (T)typeof(NpcAI).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ai)!;

    private static GameWorld CreateWorld(bool secondMap = false)
    {
        var world = TestHarness.CreateWorld();
        if (secondMap)
            world.InitMap(1, 6144, 4096);
        return world;
    }

    [Fact]
    public void Sight_UsesVisualRangeAndZeroKeepsDefault()
    {
        var npc = new Character { VisualRange = 7, Int = 300 };
        Assert.Equal(7, NpcAI.GetNpcSight(npc));

        npc.VisualRange = 0;
        Assert.Equal(18, NpcAI.GetNpcSight(npc));
    }

    [Theory]
    [InlineData(0x31A, 0x31F)]
    [InlineData(0xC5, 0x58B)]
    [InlineData(0x46, 0x48)]
    [InlineData(0x302, 0x305)]
    [InlineData(0x30D, 0x327)]
    [InlineData(0x325, 0x328)]
    public void AllyGroups_IncludeSourceXModernFamilies(int first, int second)
    {
        Assert.Equal(NpcAI.GetAllyGroup((ushort)first), NpcAI.GetAllyGroup((ushort)second));
    }

    [Fact]
    public void PlayableBodies_IncludeElfAndGargoyleForms()
    {
        Assert.True(NpcAI.IsPlayableBody(0x25D));
        Assert.True(NpcAI.IsPlayableBody(0x260));
        Assert.True(NpcAI.IsPlayableBody(0x29A));
        Assert.True(NpcAI.IsPlayableBody(0x2B7));
        Assert.False(NpcAI.IsPlayableBody(0x04));
        Assert.NotEqual(NpcAI.GetAllyGroup(0x31A), NpcAI.GetAllyGroup(0xC5));
    }

    [Fact]
    public void TargetAcquisition_DenseAlliedCrowdCannotHideEnemyByInsertionOrder()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Monster;
        npc.BodyId = 0x11;
        npc.Hits = npc.MaxHits = 100;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        // Created before the allies: Sector enumerates in reverse insertion
        // order, so the former first-40 cap never reached this valid target.
        var enemy = world.CreateCharacter();
        enemy.IsPlayer = true;
        enemy.IsOnline = true;
        enemy.BodyId = 0x190;
        enemy.Hits = enemy.MaxHits = 100;
        world.PlaceCharacter(enemy, new Point3D(101, 100, 0, 0));
        world.AddOnlinePlayer(enemy);

        for (int i = 0; i < 45; i++)
        {
            var ally = world.CreateCharacter();
            ally.NpcBrain = NpcBrainType.Monster;
            ally.BodyId = npc.BodyId;
            ally.Hits = ally.MaxHits = 100;
            world.PlaceCharacter(ally, new Point3D(110, 100, 0, 0));
        }

        world.OnTick();
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);

        Assert.Equal(enemy.Uid, npc.FightTarget);
    }

    [Fact]
    public void ForcedTarget_AcceptsSphereStyleHexSerialTag()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = world.CreateCharacter();
        npc.Hits = npc.MaxHits = 100;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        npc.SetTag("CONSTANT_FOCUS", $"0{target.Uid.Value:X8}");

        bool engaged = (bool)Invoke(ai, "TryForcedTarget", npc)!;

        Assert.True(engaged);
        Assert.Equal(target.Uid, npc.FightTarget);
    }

    [Fact]
    public void PendingSwing_BlocksBreathAndMovementUntilHitPhase()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var dragon = world.CreateCharacter();
        dragon.NpcBrain = NpcBrainType.Dragon;
        dragon.BodyId = 0x0C;
        dragon.Str = 200;
        dragon.Hits = dragon.MaxHits = 500;
        dragon.Stam = dragon.MaxStam = 100;
        world.PlaceCharacter(dragon, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Hits = target.MaxHits = 500;
        world.PlaceCharacter(target, new Point3D(103, 100, 0, 0));

        long now = Environment.TickCount64;
        dragon.BeginSwingWindup(now, 60_000, 60_000, target.Uid, now + 120_000);
        Point3D before = dragon.Position;
        bool breathed = false;
        ai.OnNpcBreath = (_, _, _) => breathed = true;

        Invoke(ai, "ActFight", dragon, target, 100);

        Assert.True(dragon.HasPendingHit);
        Assert.Equal(before, dragon.Position);
        Assert.False(breathed);
    }

    [Fact]
    public void NonInstantGuard_PumpsItsPendingHitPhase()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig { GuardsInstantKill = false });
        var guard = world.CreateCharacter();
        guard.Hits = guard.MaxHits = 100;
        guard.Stam = guard.MaxStam = 100;
        world.PlaceCharacter(guard, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        long now = Environment.TickCount64;
        guard.BeginSwingWindup(now - 1_000, 0, 1_000, target.Uid, now + 2_000);
        Invoke(ai, "GuardEngage", guard, target);

        Assert.False(guard.HasPendingHit);
    }

    [Fact]
    public void FleeCast_ConsumesTheTickWithoutSecondHealOrMovement()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var caster = world.CreateCharacter();
        caster.Int = 50;
        caster.Mana = caster.MaxMana = 100;
        caster.Hits = 10;
        caster.MaxHits = 100;
        caster.FleeStepsCurrent = 11; // increments to 12: kite + heal cadence overlap
        caster.FleeStepsMax = 20;
        caster.NpcSpellAdd(SpellType.MagicArrow);
        caster.NpcSpellAdd(SpellType.GreaterHeal);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(103, 100, 0, 0));

        int casts = 0;
        ai.OnNpcCastSpell = (_, _, _) => casts++;
        Point3D before = caster.Position;
        Invoke(ai, "ActFlee", caster, target);

        Assert.Equal(1, casts);
        Assert.Equal(before, caster.Position);
    }

    [Fact]
    public void ReversedThrowRanges_AreNormalizedWithoutCrashingTick()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var thrower = world.CreateCharacter();
        thrower.NpcBrain = NpcBrainType.Monster;
        thrower.Dex = 100;
        thrower.Hits = thrower.MaxHits = 100;
        thrower.Stam = thrower.MaxStam = 100;
        thrower.SetTag("THROWOBJ", "1");
        thrower.SetTag("THROWRANGE", "9,2");
        thrower.SetTag("THROWDAM", "20,10");
        world.PlaceCharacter(thrower, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(103, 100, 0, 0));

        int? thrownDamage = null;
        ai.OnNpcThrow = (_, _, damage) => thrownDamage = damage;
        Invoke(ai, "ActFight", thrower, target, 100);

        Assert.InRange(thrownDamage ?? -1, 10, 20);
    }

    [Fact]
    public void BowConsumesArrowButNeverEarlierBoltStack()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var archer = world.CreateCharacter();
        archer.PrivLevel = PrivLevel.GM; // deterministic connecting hit; NPC misses keep their ammo
        archer.Hits = archer.MaxHits = 100;
        archer.Stam = archer.MaxStam = 100;
        world.PlaceCharacter(archer, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        archer.Equip(pack, Layer.Pack);
        var bolts = world.CreateItem();
        bolts.ItemType = ItemType.WeaponBolt;
        bolts.Amount = 3;
        pack.AddItem(bolts);
        var arrows = world.CreateItem();
        arrows.ItemType = ItemType.WeaponArrow;
        arrows.Amount = 3;
        pack.AddItem(arrows);

        var bow = world.CreateItem();
        bow.ItemType = ItemType.WeaponBow;
        archer.Equip(bow, Layer.TwoHanded);

        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(102, 100, 0, 0));

        long now = Environment.TickCount64;
        archer.BeginSwingWindup(now, 0, 1_000, target.Uid, now + 2_000);
        Invoke(ai, "ResolveNpcHit", archer, now);

        Assert.Equal((ushort)3, bolts.Amount);
        Assert.Equal((ushort)2, arrows.Amount);
    }

    [Fact]
    public void ReactiveArmorKill_IsCreditedToTheDefender()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var attacker = world.CreateCharacter();
        attacker.PrivLevel = PrivLevel.GM; // deterministic hit
        attacker.Str = 100;
        attacker.Hits = 1;
        attacker.MaxHits = 100;
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        var defender = world.CreateCharacter();
        defender.Hits = defender.MaxHits = 1_000;
        defender.SetStatFlag(StatFlag.Reactive);
        world.PlaceCharacter(defender, new Point3D(101, 100, 0, 0));

        Character? creditedKiller = null;
        Character? deadVictim = null;
        ai.OnNpcKill = (killer, victim) =>
        {
            creditedKiller = killer;
            deadVictim = victim;
        };
        long now = Environment.TickCount64;
        attacker.BeginSwingWindup(now, 0, 1_000, defender.Uid, now + 2_000);
        Invoke(ai, "ResolveNpcHit", attacker, now);

        Assert.Same(defender, creditedKiller);
        Assert.Same(attacker, deadVictim);
    }

    [Fact]
    public void RejectedSpellStart_ReturnsFalseAndClearsComboState()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig())
        {
            OnNpcTryStartSpellCast = (_, _, _) => false,
        };
        var caster = world.CreateCharacter();
        var target = world.CreateCharacter();
        caster.SetTag("COMBO_STEP", "2");
        caster.SetTag("COMBO_TARGET", target.Uid.Value.ToString());

        bool started = (bool)Invoke(
            ai, "CastViaTrigger", caster, target, SpellType.Fireball, true)!;

        Assert.False(started);
        Assert.False(caster.TryGetTag("COMBO_STEP", out _));
        Assert.False(caster.TryGetTag("COMBO_TARGET", out _));
    }

    [Fact]
    public void BeneficialFallbackSpellTargetsCasterNotEnemy()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig())
        {
            ResolveNpcSpellFlags = _ => SpellFlag.Good | SpellFlag.Bless | SpellFlag.TargChar,
        };
        var caster = world.CreateCharacter();
        caster.Hits = caster.MaxHits = 100;
        caster.NpcSpellAdd(SpellType.Bless);
        var enemy = world.CreateCharacter();
        enemy.Hits = enemy.MaxHits = 100;

        var (spell, castTarget) = ai.ChooseBestSpell(caster, enemy, 3);

        Assert.Equal(SpellType.Bless, spell);
        Assert.Same(caster, castTarget);
    }

    [Fact]
    public void MoveTowardAndPetFollow_DoNotTeleportAcrossMaps()
    {
        var world = CreateWorld(secondMap: true);
        var ai = new NpcAI(world, new SphereConfig());
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Hits = owner.MaxHits = 100;
        world.PlaceCharacter(owner, new Point3D(200, 200, 0, 1));

        var pet = world.CreateCharacter();
        pet.Hits = pet.MaxHits = 100;
        pet.PetAIMode = PetAIMode.Follow;
        Assert.True(pet.TryAssignOwnership(owner));
        world.PlaceCharacter(pet, new Point3D(100, 100, 0, 0));

        Point3D before = pet.Position;
        Invoke(ai, "MoveToward", pet, owner.Position, true);
        Assert.Equal(before, pet.Position);

        pet.NextNpcActionTime = 0;
        ai.OnTickAction(pet);
        Assert.Equal(before, pet.Position);
    }

    [Fact]
    public void PathBackoff_IsInvalidatedWhenDestinationChanges()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig())
        {
            Flags = NpcAIFlags.Path | NpcAIFlags.PersistentPath,
        };
        var npc = world.CreateCharacter();
        npc.Int = 300;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        var blocker = world.CreateCharacter();
        blocker.Hits = blocker.MaxHits = 100;
        world.PlaceCharacter(blocker, new Point3D(101, 100, 0, 0));

        var goals = PrivateField<Dictionary<uint, Point3D>>(ai, "_pathGoal");
        var backoffs = PrivateField<Dictionary<uint, long>>(ai, "_nextPathfindMs");
        goals[npc.Uid.Value] = new Point3D(100, 120, 0, 0);
        backoffs[npc.Uid.Value] = long.MaxValue;
        var newGoal = new Point3D(110, 100, 0, 0);

        Invoke(ai, "MoveToward", npc, newGoal, true);

        Assert.Equal(newGoal, goals[npc.Uid.Value]);
        Assert.NotEqual(long.MaxValue, backoffs[npc.Uid.Value]);
    }

    [Fact]
    public void GuardGoHome_PreservesTheHomeFacet()
    {
        var world = CreateWorld(secondMap: true);
        var ai = new NpcAI(world, new SphereConfig());
        var guard = world.CreateCharacter();
        guard.NpcBrain = NpcBrainType.Guard;
        guard.Home = new Point3D(200, 200, 0, 1);
        world.PlaceCharacter(guard, new Point3D(100, 100, 0, 0));

        Invoke(ai, "ActGuard", guard);

        Assert.Equal((byte)1, guard.MapIndex);
        Assert.Equal(guard.Home, guard.Position);
    }

    [Fact]
    public void WitnessMemory_SuppressesRepeatedGuardCalls()
    {
        var world = CreateWorld();
        var region = new SphereNet.Game.World.Regions.Region
        { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        region.AddRect(90, 90, 110, 110);
        world.AddRegion(region);
        var ai = new NpcAI(world, new SphereConfig());
        var witness = world.CreateCharacter();
        world.PlaceCharacter(witness, new Point3D(100, 100, 0, 0));
        var criminal = world.CreateCharacter();
        criminal.IsPlayer = true;
        criminal.Hits = criminal.MaxHits = 100;
        criminal.SetStatFlag(StatFlag.Criminal);
        world.PlaceCharacter(criminal, new Point3D(101, 100, 0, 0));
        witness.Memory_AddObjTypes(criminal.Uid, MemoryType.SawCrime);

        int reports = 0;
        ai.OnWitnessCrime = (_, _) => reports++;
        for (int i = 0; i < 500; i++)
            Invoke(ai, "CheckWitnessCrime", witness);

        Assert.Equal(0, reports);
    }

    [Fact]
    public void NewCharactersDefaultToUnlimitedHomeDistance()
    {
        var npc = new Character();
        Assert.Equal(Character.UnlimitedHomeDistance, npc.HomeDist);
    }
}
