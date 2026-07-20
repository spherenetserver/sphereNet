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
        def.LoadFromKey("DESIRES", "5 0EED, 0F3F 20, 0DF8");

        Assert.Equal(3, def.Desires.Count);
        Assert.Equal(3, def.DesireQtys.Count);
        Assert.Equal(5, def.DesireQtys[0]);
        Assert.Equal(20, def.DesireQtys[1]);
        Assert.Equal(1, def.DesireQtys[2]); // bare entry defaults to 1
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
        world.PlaceCharacter(vendor, new Point3D(1000, 1000, 0, 0));

        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.MaxHits = 100; attacker.Hits = 100;
        world.PlaceCharacter(attacker, new Point3D(1001, 1000, 0, 0));

        // The player struck the vendor: FightTarget assigned + damage memory.
        vendor.FightTarget = attacker.Uid;
        vendor.RecordAttack(attacker.Uid, 5);

        var ai = new NpcAI(world, new SphereConfig());
        var actVendor = typeof(NpcAI).GetMethod("ActVendor",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        actVendor.Invoke(ai, [vendor]);

        // The vendor engaged (kept its target and committed to a swing or a
        // step toward the attacker) instead of ignoring the fight entirely.
        Assert.True(vendor.FightTarget == attacker.Uid,
            "the vendor dropped its assigned fight");
        Assert.True(vendor.HasPendingHit ||
                    vendor.Position.GetDistanceTo(attacker.Position) <= 1,
            "the vendor neither swung nor closed in");
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
        world.PlaceCharacter(animal, new Point3D(1000, 1000, 0, 0));

        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.SetStatFlag(StatFlag.War);
        attacker.MaxHits = 60; attacker.Hits = 60;
        world.PlaceCharacter(attacker, new Point3D(1001, 1000, 0, 0));

        animal.FightTarget = attacker.Uid;
        animal.RecordAttack(attacker.Uid, 5);

        var ai = new NpcAI(world, new SphereConfig());
        var actAnimal = typeof(NpcAI).GetMethod("ActAnimal",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        actAnimal.Invoke(ai, [animal]);

        // Old behavior: the war-mode attacker read as a "threat" and the
        // animal backed away, never fighting its FightTarget.
        Assert.Equal(attacker.Uid, animal.FightTarget);
        Assert.True(animal.HasPendingHit ||
                    animal.Position.GetDistanceTo(attacker.Position) <= 1,
            "the animal fled instead of fighting its attacker");
    }
}
