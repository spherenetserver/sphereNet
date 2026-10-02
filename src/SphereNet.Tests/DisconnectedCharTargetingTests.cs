using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Combat;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A logged-out player's body stays in the world, but Source-X keeps it on the
/// sector's disconnected list (CSector.cpp:194-250), out of reach of everything that
/// picks a character to fight or to look at:
/// <list type="bullet">
/// <item>Fight_CanHit answers WAR_SWING_INVALID when either side IsDisconnected()
/// (CCharFight.cpp:1692), and Fight_HitTry then clears the fight (:1582-1588).</item>
/// <item>CWorldSearch reads the disconnected list only under AllShow
/// (CWorldSearch.cpp:271-275), so NPC_LookAround (CCharNPCAct.cpp:1166), the
/// NPC_FightCast friend search (CCharNPCAct_Magic.cpp:324) and CallGuards
/// (CCharFight.cpp:192) never see a logged-out player.</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DisconnectedCharTargetingTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, short x, bool online)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.IsOnline = online;
        ch.Str = ch.Dex = ch.Int = 100;
        ch.MaxHits = ch.Hits = 100;
        ch.MaxStam = ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
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

    // ---- Fight_CanHit: either side disconnected is an invalid swing ----

    [Fact]
    public void ASwingIsAbortedWhenEitherSideHasLoggedOut()
    {
        var world = CreateWorld();
        var attacker = Player(world, 100, online: true);
        var target = Player(world, 101, online: true);
        long now = Environment.TickCount64;

        Assert.False(CombatHelper.IsInvalidSwingParticipant(target, asTarget: true));

        target.IsOnline = false;
        Assert.True(CombatHelper.IsInvalidSwingParticipant(target, asTarget: true));
        Assert.Equal(CombatHelper.SwingPrepResult.Abort, CombatHelper.ValidateSwingPrep(
            world, attacker, target, null, PrivLevel.Player, now, (_, _) => true).Result);
        Assert.Equal(CombatHelper.HitTimeDecision.Drop, CombatHelper.EvaluateHitTime(
            world, attacker, target, null, PrivLevel.Player, now, now, (_, _) => true));

        target.IsOnline = true;
        attacker.IsOnline = false;
        Assert.True(CombatHelper.IsInvalidSwingParticipant(attacker, asTarget: false));
        Assert.Equal(CombatHelper.SwingPrepResult.Abort, CombatHelper.ValidateSwingPrep(
            world, attacker, target, null, PrivLevel.Player, now, (_, _) => true).Result);
    }

    [Fact]
    public void ALingeringClientIsStillAValidTarget()
    {
        // The body of a client that just dropped lingers in the world, connected,
        // until the linger time runs out (CClient::CharDisconnect); only then is it
        // disconnected.
        var world = CreateWorld();
        var target = Player(world, 101, online: false);
        target.SetTag("CLIENT_LINGER_UNTIL", (Environment.TickCount64 + 60_000).ToString());

        Assert.False(target.IsLoggedOut);
        Assert.False(CombatHelper.IsInvalidSwingParticipant(target, asTarget: true));
    }

    [Fact]
    public void AnNpcDoesNotSwingAtAPlayerWhoLoggedOut()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = Npc(world, 100);
        npc.Direction = Direction.East;
        var player = Player(world, 101, online: false);
        var swungAt = new List<Character>();
        ai.OnNpcSwingStart = (_, t, _, _, _) => swungAt.Add(t);
        npc.NextAttackTime = 0;

        var swing = typeof(NpcAI).GetMethod("TrySwingAttack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False((bool)swing.Invoke(ai, [npc, player])!);
        Assert.Empty(swungAt);

        player.IsOnline = true; // the same swing at a connected player goes out
        Assert.True((bool)swing.Invoke(ai, [npc, player])!);
        Assert.Contains(player, swungAt);
    }

    [Fact]
    public void APlayerStopsFightingATargetThatLoggedOut()
    {
        // Fight_HitTry on WAR_SWING_INVALID: Fight_Clear(pCharTarg) (CCharFight.cpp:1584).
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1290);
        var attacker = Player(world, 100, online: true);
        attacker.SetStatFlag(StatFlag.War);
        TestHarness.AttachCharacter(client, attacker);
        client.BroadcastNearby = (_, _, _, _) => { };
        var target = Player(world, 101, online: true);
        attacker.FightTarget = target.Uid;
        attacker.NextAttackTime = long.MaxValue; // recoiling: the fight is only held
        client.TickCombat();
        Assert.Equal(target.Uid, attacker.FightTarget);

        target.IsOnline = false;
        attacker.NextAttackTime = 0;
        client.TickCombat();

        Assert.False(attacker.FightTarget.IsValid);
    }

    // ---- CWorldSearch: no disconnected chars in the look-around ----

    [Fact]
    public void ATownsmanDoesNotSeeALoggedOutCriminal()
    {
        var world = CreateWorld();
        var region = new Region { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        region.AddRect(0, 0, 6000, 4000);
        world.AddRegion(region);
        var ai = new NpcAI(world, new SphereConfig());
        var said = new List<string>();
        ai.OnNpcSay = (_, text) => said.Add(text);
        ai.OnWitnessCrime = (_, _) => true;
        var townsman = Npc(world, 100, NpcBrainType.Human);
        townsman.DSpeech.Add(new ResourceId(ResType.Speech, 1));
        var villain = Player(world, 102, online: false);
        villain.SetStatFlag(StatFlag.Criminal);

        var look = typeof(NpcAI).GetMethod("LookAroundTown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 300; i++)
            look.Invoke(ai, [townsman, false]);
        Assert.Empty(said);

        villain.IsOnline = true;
        for (int i = 0; i < 300 && said.Count == 0; i++)
            look.Invoke(ai, [townsman, false]);
        Assert.Equal(ServerMessages.Get(Msg.NpcGenericSeecrim), Assert.Single(said));
    }

    [Fact]
    public void SeeNewPlayerIsNotFiredForALoggedOutPlayer()
    {
        var saved = Character.OnNpcSeeNewPlayer;
        try
        {
            var world = CreateWorld();
            var ai = new NpcAI(world, new SphereConfig());
            var npc = Npc(world, 100, NpcBrainType.Human);
            var gone = Player(world, 102, online: false);
            var here = Player(world, 104, online: true);
            var seen = new List<Character>();
            Character.OnNpcSeeNewPlayer = (_, p) => { seen.Add(p); return false; };

            var look = typeof(NpcAI).GetMethod("LookForNewPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int i = 0; i < 200; i++)
                look.Invoke(ai, [npc]);

            Assert.Contains(here, seen);
            Assert.DoesNotContain(gone, seen);
        }
        finally
        {
            Character.OnNpcSeeNewPlayer = saved;
        }
    }

    [Fact]
    public void TheFightCastFriendListSkipsALoggedOutPlayer()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var caster = Npc(world, 100);
        var enemy = Npc(world, 105);
        var gone = Player(world, 101, online: false);
        var here = Player(world, 102, online: true);
        gone.Memory_AddObjTypes(enemy.Uid, MemoryType.Fight);
        here.Memory_AddObjTypes(enemy.Uid, MemoryType.Fight);

        var friends = new List<Character>();
        typeof(NpcAI).GetMethod("CollectFightFriends", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, [caster, enemy, friends]);

        Assert.Contains(here, friends);
        Assert.DoesNotContain(gone, friends);
    }

    [Fact]
    public void CallingTheGuardsDoesNotReportALoggedOutCriminal()
    {
        var world = CreateWorld();
        var caller = Player(world, 100, online: true);
        var gone = Player(world, 102, online: false);
        var here = Player(world, 104, online: true);
        gone.SetStatFlag(StatFlag.Criminal);
        here.SetStatFlag(StatFlag.Criminal);

        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var program = typeof(SphereNet.Server.Program);
        var worldField = program.GetField("_world", flags)!;
        var previous = worldField.GetValue(null);
        worldField.SetValue(null, world);
        try
        {
            var found = (List<Character>)program.GetMethod("FindAllGuardTargets", flags)!
                .Invoke(null, [caller])!;
            Assert.Contains(here, found);
            Assert.DoesNotContain(gone, found);
        }
        finally
        {
            worldField.SetValue(null, previous);
        }
    }
}
