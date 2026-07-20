using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

// Audit round 7 (wiki/test.txt): the brain_animal_trainer alias resolves to
// the Stable brain, DESIRES entries keep their want quantity (Source-X
// NPC_WantThisItem returns GetResQty, never a flat 100), and non-monster
// brains defend themselves when a FightTarget is assigned by an attack.
public class NpcAiRound7Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void BrainAlias_AnimalTrainer_MapsToStable()
    {
        var world = CreateWorld();
        var npc = world.CreateCharacter();

        // The Scripts-X stablemaster @Create runs NPC=brain_animal_trainer;
        // the def value is 7 = NpcBrainType.Stable. The name-mismatch used
        // to fail the parse and leave the pre-spawn Monster fallback.
        Assert.True(npc.TrySetProperty("NPC", "brain_animal_trainer"));
        Assert.Equal(NpcBrainType.Stable, npc.NpcBrain);
    }

    [Fact]
    public void DesireList_KeepsPerEntryQuantities()
    {
        var def = new CharDef(new ResourceId(ResType.CharDef, 0x21));

        // "qty defname", "defname qty" and bare forms all parse; the qty is
        // the want score (bare = 1). Numeric ids avoid the def-name table.
        def.LoadFromKey("DESIRES", "5 0EED, 0F3F 20, 0DF8, 999 0E76");

        Assert.Equal(4, def.Desires.Count);
        Assert.Equal(4, def.DesireQtys.Count);
        Assert.Equal(5, def.DesireQtys[0]);
        Assert.Equal(20, def.DesireQtys[1]);
        Assert.Equal(1, def.DesireQtys[2]);   // bare entry defaults to 1
        Assert.Equal(999, def.DesireQtys[3]); // Source-X keeps the raw qty
                                              // (Scripts-X DESIRES=999 t_corpse)
    }

    [Fact]
    public void DesireList_TypesRegionAliases_AsRegions()
    {
        var def = new CharDef(new ResourceId(ResType.CharDef, 0x22));

        // Scripts-X uses region preferences inside DESIRES (r_caves,
        // r_ruins, ~167 lines) — they must never hash as fake itemdefs.
        def.LoadFromKey("DESIRES", "r_caves, 0EED");

        Assert.Equal(2, def.Desires.Count);
        Assert.Equal(ResType.RegionType, def.Desires[0].Type);
        Assert.Equal(ResType.ItemDef, def.Desires[1].Type);
    }

    [Fact]
    public void ServiceNpc_DefendsItself_WhenAttacked()
    {
        var world = CreateWorld();
        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        vendor.BodyId = 0x0190;
        vendor.Str = 50; vendor.Dex = 50; vendor.Int = 50;
        vendor.MaxHits = 50; vendor.Hits = 50;
        vendor.MaxStam = 50; vendor.Stam = 50; // spawn seeds Stam=Dex; a swing needs stamina
        vendor.Direction = Direction.East;     // facing the attacker (swing prep gate)
        world.PlaceCharacter(vendor, new Point3D(1000, 1000, 0, 0));

        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.MaxHits = 100; attacker.Hits = 100;
        world.PlaceCharacter(attacker, new Point3D(1001, 1000, 0, 0));

        // The player struck the vendor: FightTarget assigned + damage memory.
        vendor.FightTarget = attacker.Uid;
        vendor.RecordAttack(attacker.Uid, 5);

        var ai = new NpcAI(world, new SphereConfig());
        var swungAt = new List<Character>();
        ai.OnNpcAttack = (_, target, _, _, _) => swungAt.Add(target);
        var actVendor = typeof(NpcAI).GetMethod("ActVendor",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        actVendor.Invoke(ai, [vendor]);

        // DISCRIMINATING observable (audit round 8): the attacker starts
        // adjacent, so only a RESOLVED SWING (hit or miss) proves the vendor
        // actually fought back — a distance check passed even when the old
        // code did nothing.
        Assert.Equal(attacker.Uid, vendor.FightTarget);
        Assert.Contains(attacker, swungAt);
    }

    [Fact]
    public void Animal_FightsItsAttacker_InsteadOfOnlyFleeing()
    {
        var world = CreateWorld();
        var animal = world.CreateCharacter();
        animal.NpcBrain = NpcBrainType.Animal;
        animal.BodyId = 0x00E1;
        animal.Str = 60; animal.Dex = 60; animal.Int = 10;
        animal.MaxHits = 60; animal.Hits = 60;
        animal.MaxStam = 60; animal.Stam = 60; // a swing needs stamina
        animal.Direction = Direction.East;     // facing the attacker (swing prep gate)
        world.PlaceCharacter(animal, new Point3D(1000, 1000, 0, 0));

        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.SetStatFlag(StatFlag.War);
        attacker.MaxHits = 60; attacker.Hits = 60;
        world.PlaceCharacter(attacker, new Point3D(1001, 1000, 0, 0));

        animal.FightTarget = attacker.Uid;
        animal.RecordAttack(attacker.Uid, 5);

        var ai = new NpcAI(world, new SphereConfig());
        var swungAt = new List<Character>();
        ai.OnNpcAttack = (_, target, _, _, _) => swungAt.Add(target);
        var actAnimal = typeof(NpcAI).GetMethod("ActAnimal",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        actAnimal.Invoke(ai, [animal]);

        // Old behavior: the war-mode attacker read as a "threat" and the
        // animal backed away, never fighting its FightTarget. Only a
        // RESOLVED swing proves the fight-back (audit round 8: the
        // adjacent-start distance check passed even with no action).
        Assert.Equal(attacker.Uid, animal.FightTarget);
        Assert.Contains(attacker, swungAt);
    }

    [Fact]
    public void FreshSpawn_NpcFood_SeededFed_SoWantIsCalm()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(1000, 1000, 0, 0));

        var bread = world.CreateItem();
        bread.ItemType = ItemType.Food;
        world.PlaceItem(bread, new Point3D(1001, 1000, 0, 0));

        // Starving (the old fresh-spawn state): certain want.
        npc.NpcFood = 0;
        Assert.Equal(100, ai.GetWantScore(npc, bread));

        // Fed (the new spawn seed, 50/60): hunger want drops to ~17.
        npc.NpcFood = 50;
        int fedWant = ai.GetWantScore(npc, bread);
        Assert.InRange(fedWant, 1, 25);
    }

    [Fact]
    public void Healer_Alignment_IsThreeWay()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var actHealer = typeof(NpcAI).GetMethod("ActHealer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var healer = world.CreateCharacter();
        healer.NpcBrain = NpcBrainType.Healer;
        world.PlaceCharacter(healer, new Point3D(1000, 1000, 0, 0));

        var ghost = world.CreateCharacter();
        ghost.IsPlayer = true;
        ghost.MakeCriminal();
        ghost.Kill();
        ghost.SetStatFlag(StatFlag.War); // manifesting ghost
        world.PlaceCharacter(ghost, new Point3D(1001, 1000, 0, 0));

        var served = new List<Character>();
        ai.OnHealerAction = (_, target, _) => served.Add(target);

        // GOOD healer refuses the criminal ghost.
        healer.Karma = 5000;
        actHealer.Invoke(ai, [healer]);
        Assert.Empty(served);

        // EVIL healer serves it.
        healer.Karma = -5000;
        actHealer.Invoke(ai, [healer]);
        Assert.Contains(ghost, served);

        // NEUTRAL healer (zero karma) serves everyone too.
        served.Clear();
        healer.Karma = 0;
        actHealer.Invoke(ai, [healer]);
        Assert.Contains(ghost, served);
    }
}
