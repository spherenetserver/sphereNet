using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Does a dragon actually breathe in a fight? The pack's c_dragon is body 0x0C with
/// NPC=brain_monster. Source-X breathes only for brain_dragon
/// (CCharNPCAct_Fight.cpp:286); SphereNet deliberately also lets dragon bodies
/// breathe, because packs keep brain_monster on their dragons. Full stamina,
/// range 1-8 and line of sight are the upstream gates.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DragonBreathDecisionTests
{
    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private static (GameWorld, NpcAI, SphereNet.Game.Objects.Characters.Character dragon,
        SphereNet.Game.Objects.Characters.Character target) Fight(ushort body, NpcBrainType brain, int distance, bool fullStamina = true)
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var dragon = world.CreateCharacter();
        dragon.NpcBrain = brain;
        dragon.BodyId = body;
        dragon.Str = 200;
        dragon.Hits = dragon.MaxHits = 500;
        dragon.MaxStam = 100;
        dragon.Stam = (short)(fullStamina ? 100 : 50);
        world.PlaceCharacter(dragon, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Hits = target.MaxHits = 500;
        world.PlaceCharacter(target, new Point3D((short)(100 + distance), 100, 0, 0));
        return (world, ai, dragon, target);
    }

    [Fact]
    public void ThePacksDragonBreathesAtAPlayerThreeTilesAway()
    {
        var (_, ai, dragon, target) = Fight(0x0C, NpcBrainType.Monster, 3);
        int breaths = 0, damage = 0;
        ai.OnNpcBreath = (_, t, dmg) => { breaths++; damage = dmg; Assert.Same(target, t); };

        Invoke(ai, "ActFight", dragon, target, 100);

        Assert.Equal(1, breaths);
        Assert.True(damage > 0);
    }

    [Theory]
    [InlineData(9, true)]    // out of breath range
    [InlineData(3, false)]   // not at full stamina
    public void NoBreathOutsideTheUpstreamGates(int distance, bool fullStamina)
    {
        var (_, ai, dragon, target) = Fight(0x0C, NpcBrainType.Monster, distance, fullStamina);
        int breaths = 0;
        ai.OnNpcBreath = (_, _, _) => breaths++;

        Invoke(ai, "ActFight", dragon, target, 100);

        Assert.Equal(0, breaths);
    }

    [Fact]
    public void AnOrdinaryMonsterDoesNotBreathe()
    {
        var (_, ai, orc, target) = Fight(0x11, NpcBrainType.Monster, 3);
        int breaths = 0;
        ai.OnNpcBreath = (_, _, _) => breaths++;

        Invoke(ai, "ActFight", orc, target, 100);

        Assert.Equal(0, breaths);
    }
}
