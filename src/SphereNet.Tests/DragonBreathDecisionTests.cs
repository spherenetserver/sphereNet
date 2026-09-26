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
/// (CCharNPCAct_Fight.cpp:286), and that is the default; NPCAIEXTRAS CombatExtras
/// also lets dragon bodies breathe, for packs that keep brain_monster on their
/// dragons. Full stamina, range 1-8 and line of sight are the upstream gates.
///
/// The breath is a two-stage NPC skill (Skill_Act_Breath, CCharSkill.cpp:3279-3346):
/// the fight action only STARTS it (stomp, -10 stamina, a three second timer), and
/// the fire comes out when the timer ends - so every test here fights once, steps
/// the AI clock past the wind-up and fights again.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DragonBreathDecisionTests
{
    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private sealed class Clock { public long Now = 1_000_000; }

    private static (GameWorld, NpcAI, SphereNet.Game.Objects.Characters.Character dragon,
        SphereNet.Game.Objects.Characters.Character target, Clock clock) Fight(ushort body, NpcBrainType brain, int distance, bool fullStamina = true)
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var clock = new Clock();
        ai.NowMs = () => clock.Now;
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
        return (world, ai, dragon, target, clock);
    }

    /// <summary>One fight action, then another once the wind-up is over.</summary>
    private static void FightThroughWindup(NpcAI ai, Clock clock,
        SphereNet.Game.Objects.Characters.Character npc, SphereNet.Game.Objects.Characters.Character target)
    {
        Invoke(ai, "ActFight", npc, target, 100);
        clock.Now += NpcAI.SpecialWindupMs;
        Invoke(ai, "ActFight", npc, target, 100);
    }

    [Fact]
    public void ThePacksDragonBreathesAtAPlayerThreeTilesAway()
    {
        var (_, ai, dragon, target, clock) = Fight(0x0C, NpcBrainType.Monster, 3);
        ai.Extras |= NpcAiExtraFlags.CombatExtras; // dragon body with brain_monster
        int breaths = 0, damage = 0;
        ai.OnNpcBreath = (_, t, dmg) => { breaths++; damage = dmg; Assert.Same(target, t); };

        FightThroughWindup(ai, clock, dragon, target);

        Assert.Equal(1, breaths);
        Assert.True(damage > 0);
    }

    [Theory]
    [InlineData(9, true)]    // out of breath range
    [InlineData(3, false)]   // not at full stamina
    public void NoBreathOutsideTheUpstreamGates(int distance, bool fullStamina)
    {
        var (_, ai, dragon, target, clock) = Fight(0x0C, NpcBrainType.Monster, distance, fullStamina);
        int breaths = 0;
        ai.OnNpcBreath = (_, _, _) => breaths++;

        FightThroughWindup(ai, clock, dragon, target);

        Assert.Equal(0, breaths);
    }

    [Fact]
    public void ByDefaultOnlyTheDragonBrainBreathes()
    {
        // Source-X NPC_Act_Fight: m_Brain == NPCBRAIN_DRAGON (CCharNPCAct_Fight.cpp:286).
        var (_, ai, monsterDragon, target, clock) = Fight(0x0C, NpcBrainType.Monster, 3);
        int breaths = 0;
        ai.OnNpcBreath = (_, _, _) => breaths++;
        FightThroughWindup(ai, clock, monsterDragon, target);
        Assert.Equal(0, breaths);

        var (_, ai2, brainDragon, target2, clock2) = Fight(0x3B, NpcBrainType.Dragon, 3);
        ai2.OnNpcBreath = (_, _, _) => breaths++;
        FightThroughWindup(ai2, clock2, brainDragon, target2);
        Assert.Equal(1, breaths);
        Assert.Equal(90, brainDragon.Stam); // Skill_Act_Breath: -10 stamina
    }

    [Fact]
    public void TheBreathWaitsThreeSecondsAndThenNeedsTheTargetInSight()
    {
        // START: stamina spent, nothing burns yet, and the NPC does nothing else.
        var (world, ai, dragon, target, clock) = Fight(0x3B, NpcBrainType.Dragon, 3);
        int breaths = 0;
        ai.OnNpcBreath = (_, _, _) => breaths++;
        Invoke(ai, "ActFight", dragon, target, 100);
        Assert.Equal(0, breaths);
        Assert.Equal(90, dragon.Stam);
        Assert.Equal(NpcAI.NpcSpecialKind.Breath, ai.PendingSpecial(dragon));
        Assert.Equal(target.Uid, dragon.FightTarget);

        clock.Now += NpcAI.SpecialWindupMs - 1;
        dragon.Stam = dragon.MaxStam;
        Invoke(ai, "ActFight", dragon, target, 100);
        Assert.Equal(0, breaths);                 // still winding up
        Assert.Equal(100, dragon.Stam);           // and no second breath started

        clock.Now += 1;
        Invoke(ai, "ActFight", dragon, target, 100);
        Assert.Equal(1, breaths);
        Assert.Equal(NpcAI.NpcSpecialKind.None, ai.PendingSpecial(dragon));

        // SUCCESS re-checks the line of sight (CCharSkill.cpp:3290): a wall between
        // them when the timer ends, and nothing comes out.
        var (world2, ai2, dragon2, target2, clock2) = Fight(0x3B, NpcBrainType.Dragon, 3);
        const ushort WallTile = 0x0080;
        var md = new SphereNet.MapData.MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(WallTile, new SphereNet.MapData.Tiles.ItemTileData
        { Flags = SphereNet.MapData.Tiles.TileFlag.Wall | SphereNet.MapData.Tiles.TileFlag.Impassable, Height = 20, Name = "wall" });
        world2.MapData = md;
        int breaths2 = 0;
        ai2.OnNpcBreath = (_, _, _) => breaths2++;
        Assert.True(world2.CanSeeLOS(dragon2.Position, target2.Position));
        Invoke(ai2, "ActFight", dragon2, target2, 100);
        Assert.Equal(NpcAI.NpcSpecialKind.Breath, ai2.PendingSpecial(dragon2));
        md.AddSyntheticStatic(0, 102, 100, WallTile, 0);
        Assert.False(world2.CanSeeLOS(dragon2.Position, target2.Position));
        clock2.Now += NpcAI.SpecialWindupMs;
        Invoke(ai2, "ActFight", dragon2, target2, 100);
        Assert.Equal(0, breaths2);
        Assert.Equal(NpcAI.NpcSpecialKind.None, ai2.PendingSpecial(dragon2));
    }

    [Fact]
    public void BreathHasNoCooldownByDefault_ButCombatExtrasAddsOne()
    {
        var (_, ai, dragon, target, clock) = Fight(0x3B, NpcBrainType.Dragon, 3);
        int breaths = 0;
        ai.OnNpcBreath = (_, _, _) => breaths++;
        FightThroughWindup(ai, clock, dragon, target);
        dragon.Stam = dragon.MaxStam;
        FightThroughWindup(ai, clock, dragon, target);
        Assert.Equal(2, breaths); // the stamina gate spaces breaths, nothing else
        Assert.False(dragon.TryGetTag("BREATH_CD", out _));

        var (_, ai2, dragon2, target2, clock2) = Fight(0x3B, NpcBrainType.Dragon, 3);
        ai2.Extras |= NpcAiExtraFlags.CombatExtras;
        int breaths2 = 0;
        ai2.OnNpcBreath = (_, _, _) => breaths2++;
        FightThroughWindup(ai2, clock2, dragon2, target2);
        dragon2.Stam = dragon2.MaxStam;
        FightThroughWindup(ai2, clock2, dragon2, target2);
        Assert.Equal(1, breaths2); // three second cooldown after the breath, kept in memory
        Assert.False(dragon2.TryGetTag("BREATH_CD", out _));
    }

    [Fact]
    public void AnOrdinaryMonsterDoesNotBreathe()
    {
        var (_, ai, orc, target, clock) = Fight(0x11, NpcBrainType.Monster, 3);
        int breaths = 0;
        ai.OnNpcBreath = (_, _, _) => breaths++;

        FightThroughWindup(ai, clock, orc, target);

        Assert.Equal(0, breaths);
    }
}
