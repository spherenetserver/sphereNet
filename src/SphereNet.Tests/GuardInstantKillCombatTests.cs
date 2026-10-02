using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// GUARDSINSTANTKILL reaches the swing itself, not only the guard's approach: a
/// guard NPC's chance to hit is 100, its damage UINT16_MAX and its swing one tick
/// (Source-X CResourceCalc.cpp:44/147, CCharFight.cpp:1206). Without it a guard
/// fought a strong creature with its scripted DAM and could lose.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GuardInstantKillCombatTests
{
    private static Character Fighter(GameWorld world, int x, bool player)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 1000; ch.Hits = 1000;
        ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static Character Guard(GameWorld world, int x)
    {
        var guard = Fighter(world, x, player: false);
        guard.NpcBrain = NpcBrainType.Guard;
        guard.TrySetProperty("DAM", "1,2");
        return guard;
    }

    [Fact]
    public void AnInstantKillGuardAlwaysLandsForMaximumDamage()
    {
        var world = TestHarness.CreateWorld();
        var guard = Guard(world, 100);
        var beast = Fighter(world, 101, player: false);
        beast.SetSkill(SkillType.Wrestling, 1000);
        beast.SetSkill(SkillType.Tactics, 1000);

        Assert.Equal(100, CombatEngine.CalcHitChance(guard, beast));
        Assert.Equal((ushort.MaxValue, ushort.MaxValue), CombatEngine.CalcWeaponDamage(guard, null));
        Assert.Equal(100, CombatEngine.GetSwingDelayMs(guard, null));
        for (int i = 0; i < 200; i++)
        {
            beast.Hits = beast.MaxHits;
            Assert.True(CombatEngine.ResolveAttack(guard, beast, null, CombatFlags.None, 0, 0, 0, out _) >= 0);
        }
    }

    [Fact]
    public void WithoutInstantKillAGuardFightsByTheNormalRules()
    {
        var world = TestHarness.CreateWorld();
        var guard = Guard(world, 100);
        var beast = Fighter(world, 101, player: false);
        Character.GuardsInstantKill = false;

        Assert.Equal((1, 2), CombatEngine.CalcWeaponDamage(guard, null));
        Assert.NotEqual(100, CombatEngine.GetSwingDelayMs(guard, null));
    }

    [Fact]
    public void APlayerIsNeverAnInstantKillGuard()
    {
        var world = TestHarness.CreateWorld();
        var player = Fighter(world, 100, player: true);
        player.NpcBrain = NpcBrainType.Guard;

        Assert.False(CombatEngine.IsInstantKillGuard(player));
    }
}
