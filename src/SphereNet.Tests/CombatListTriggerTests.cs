using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the newly-wired combat-list triggers (@CombatAdd, @CombatDelete,
// @CombatEnd). They hang off the Character attacker-list choke points:
// RecordAttack (new attacker -> @CombatAdd) and Attacker_Delete (removal ->
// @CombatDelete, and @CombatEnd when the list empties). The hooks are driven
// directly here, the same way Program.EngineWiring routes them into the
// dispatcher; ResetEngineStatics nulls them between tests.
public class CombatListTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void RecordAttack_NewAttacker_FiresCombatAddOncePerAttacker()
    {
        var world = CreateWorld();
        var defender = MakeChar(world, 100);
        var attacker = MakeChar(world, 101);

        var added = new List<uint>();
        Character.OnCombatAdd = (self, uid) => { if (self == defender) added.Add(uid.Value); };

        defender.RecordAttack(attacker.Uid, 10);
        defender.RecordAttack(attacker.Uid, 5); // same attacker again -> no new add

        Assert.Equal([attacker.Uid.Value], added);
    }

    [Fact]
    public void AttackerDelete_FiresCombatDelete_AndCombatEndWhenListEmpties()
    {
        var world = CreateWorld();
        var defender = MakeChar(world, 100);
        var a1 = MakeChar(world, 101);
        var a2 = MakeChar(world, 102);

        var deleted = new List<uint>();
        int combatEnds = 0;
        Character.OnCombatDelete = (self, uid) => { if (self == defender) deleted.Add(uid.Value); };
        Character.OnCombatEnd = self => { if (self == defender) combatEnds++; };

        defender.RecordAttack(a1.Uid, 10);
        defender.RecordAttack(a2.Uid, 10);

        defender.Attacker_Delete(a1.Uid);
        Assert.Equal([a1.Uid.Value], deleted);
        Assert.Equal(0, combatEnds); // a2 still in the list

        defender.Attacker_Delete(a2.Uid);
        Assert.Equal([a1.Uid.Value, a2.Uid.Value], deleted);
        Assert.Equal(1, combatEnds); // list now empty -> combat ended
    }

    [Fact]
    public void AttackerDelete_UnknownUid_FiresNothing()
    {
        var world = CreateWorld();
        var defender = MakeChar(world, 100);
        var ghost = MakeChar(world, 101);

        int deletes = 0, ends = 0;
        Character.OnCombatDelete = (_, _) => deletes++;
        Character.OnCombatEnd = _ => ends++;

        defender.Attacker_Delete(ghost.Uid); // never recorded

        Assert.Equal(0, deletes);
        Assert.Equal(0, ends);
    }
}
