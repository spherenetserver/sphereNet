using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class NpcCombatHomeBoundaryTests
{
    private static void Act(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ai, args);

    private static (SphereNet.Game.World.GameWorld World, NpcAI Ai, Character Npc, Character Target) Stage()
    {
        var world = TestHarness.CreateWorld();
        world.InitMap(1, 6144, 4096);
        var ai = new NpcAI(world, new SphereConfig { MapViewRadar = 18 }) { Flags = NpcAIFlags.None };
        var npc = world.CreateCharacter();
        npc.BodyId = 0xcf;
        npc.NpcBrain = NpcBrainType.Monster;
        npc.Str = 150; npc.Dex = 100; npc.Int = 10;
        npc.Hits = npc.MaxHits; npc.Stam = npc.MaxStam;
        npc.Home = new(20, 100, 0, 0);
        npc.HomeDist = 5;
        world.PlaceCharacter(npc, new(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true; target.BodyId = 0x190;
        target.Str = 50; target.Hits = target.MaxHits;
        world.PlaceCharacter(target, new(108, 100, 0, 0));
        npc.FightTarget = target.Uid;
        return (world, ai, npc, target);
    }

    [Theory]
    [InlineData(80, 0)]
    [InlineData(80, 50)]
    [InlineData(20, 0)]
    [InlineData(20, 50)]
    [InlineData(20, 1000)]
    public void VisibleArcherIsPursuedOutsideSpawnRadiusWithoutHomeTeleport(short homeX, int lostTeleport)
    {
        var (_, ai, npc, target) = Stage();
        npc.Home = new(homeX, 100, 0, 0);
        NpcAI.LostNpcTeleport = lostTeleport;
        int teleports = 0;
        Character.OnNpcLostTeleport = (_, _) => { teleports++; return false; };

        Act(ai, "ActFight", npc, target, 100);

        Assert.Equal(target.Uid, npc.FightTarget);
        Assert.Equal(new Point3D(101, 100, 0, 0), npc.Position);
        Assert.Equal(0, teleports);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void PursuitLimitUsesDistanceToTargetRatherThanHome(short targetDistance, bool follows)
    {
        var (world, _, npc, target) = Stage();
        var ai = new NpcAI(world, new SphereConfig { MapViewRadar = 8 });
        world.PlaceCharacter(target, new((short)(100 + targetDistance), 100, 0, 0));
        Act(ai, "ActFight", npc, target, 100);
        Assert.Equal(follows ? target.Uid : Serial.Invalid, npc.FightTarget);
        Assert.Equal(follows ? 101 : 100, npc.X);
    }

    [Fact]
    public void NormalNpcTicksKeepFollowingAnArcherAwayFromSpawn()
    {
        var (world, ai, npc, target) = Stage();
        target.IsOnline = true;
        world.AddOnlinePlayer(target);
        world.OnTick();
        for (short i = 0; i < 5; i++)
        {
            world.MoveCharacter(target, new((short)(108 + i), 100, 0, 0));
            npc.NextNpcActionTime = 0;
            ai.OnTickAction(npc);
            Assert.Equal(target.Uid, npc.FightTarget);
            Assert.Equal(101 + i, npc.X);
        }
    }

    [Fact]
    public void InvalidHomeDoesNotFireLostTeleport()
    {
        var (_, ai, npc, _) = Stage();
        npc.Home = new(20, 100, 0, 250);
        NpcAI.LostNpcTeleport = 50;
        int calls = 0;
        Character.OnNpcLostTeleport = (_, _) => { calls++; return false; };
        Act(ai, "WanderHome", npc);
        Assert.Equal(new Point3D(100, 100, 0, 0), npc.Position);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0, false, 99, 0)]
    [InlineData(50, false, 20, 1)]
    [InlineData(50, true, 99, 1)]
    [InlineData(80, false, 99, 0)]
    [InlineData(1000, false, 99, 0)]
    public void IdleHomeReturnHonorsSettingThresholdAndScriptVeto(int setting, bool veto, int expectedX, int expectedCalls)
    {
        var (_, ai, npc, _) = Stage();
        npc.FightTarget = Serial.Invalid;
        NpcAI.LostNpcTeleport = setting;
        int calls = 0;
        Character.OnNpcLostTeleport = (_, distance) =>
        {
            calls++;
            Assert.Equal(80, distance);
            return veto;
        };
        // The leash is NPC_Act_GoHome's (CCharNPCAct.cpp:1547-1571): the NPC is heading home.
        ai.SetIdleMode(npc, NpcAI.IdleMode.GoHome);
        Act(ai, "WanderHome", npc);
        Assert.Equal(expectedX, npc.X);
        Assert.Equal(expectedCalls, calls);
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(50, true, 0)]
    [InlineData(50, false, 1)]
    public void DifferentMapDoesNotBypassLostTeleportSetting(int setting, bool veto, byte expectedMap)
    {
        var (_, ai, npc, _) = Stage();
        npc.FightTarget = Serial.Invalid;
        npc.Home = new(20, 100, 0, 1);
        NpcAI.LostNpcTeleport = setting;
        int calls = 0;
        Character.OnNpcLostTeleport = (_, distance) =>
        {
            calls++;
            Assert.Equal(short.MaxValue, distance);
            return veto;
        };
        ai.SetIdleMode(npc, NpcAI.IdleMode.GoHome);
        Act(ai, "WanderHome", npc);
        Assert.Equal(expectedMap, npc.MapIndex);
        Assert.Equal(setting == 0 ? 0 : 1, calls);
    }

    [Fact]
    public void WideHomeRangeDoesNotTriggerLostTeleport()
    {
        var (_, ai, npc, _) = Stage();
        npc.FightTarget = Serial.Invalid;
        npc.HomeDist = 100;
        NpcAI.LostNpcTeleport = 50;
        int calls = 0;
        Character.OnNpcLostTeleport = (_, _) => { calls++; return false; };
        Act(ai, "WanderHome", npc);
        Assert.Equal(0, calls);
        Assert.InRange(npc.Position.GetDistanceTo(new(100, 100, 0, 0)), 0, 1);
    }
}
