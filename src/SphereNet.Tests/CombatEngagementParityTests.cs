using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Starting a fight, and picking the next one (port plan İŞ-19 / PLAN-402).
///
/// Three things the reference does that this engine did not:
///
/// 1. The attacker list runs BOTH WAYS. Fight_Attack calls Attacker_Add on the
///    character it engages (CCharFight.cpp:1442), so a target is written down before
///    it has hit back. Here the list was only ever filled by damage taken.
///
/// 2. @Attack and @CombatAdd carry the THREAT and the IGNORE flag and read both back
///    (CCharFight.cpp:1433/:1437, CCharAttacker.cpp:38/:45). Ours fired @Attack with
///    no arguments, so a script could refuse a fight but never weight one.
///
/// 3. The next target comes off that list, choosing by threat (with NPC_AI_THREAT) or
///    else by distance (NPC_FightFindBestTarget, CCharNPCAct_Fight.cpp:139/:145).
///    Ours had no threat value at all - it scored a bonus off damage taken, a formula
///    that exists nowhere upstream - and re-scanned the whole sight range instead.
/// </summary>
public sealed class CombatEngagementParityTests : IDisposable
{
    public void Dispose()
    {
        Character.OnAttackTrigger = null;
        Character.OnCombatAdd = null;
        Character.OnHitIgnored = null;
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar(GameWorld world, int x, int y = 100, bool player = false)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.Str = 60; ch.MaxHits = ch.Hits = 60;
        world.PlaceCharacter(ch, new Point3D((short)x, (short)y, 0, 0));
        return ch;
    }

    // ---- 1. engaging writes the target down ------------------------------

    [Fact]
    public void EngagingATargetPutsItOnMyOwnList()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);

        Assert.True(npc.CombatState.BeginFightWith(foe, toldByMaster: false));

        Assert.Single(npc.Attackers);
        Assert.Equal(foe.Uid, npc.Attackers[0].Uid);
        // It is a participant row, not a damage row - nothing has been dealt.
        Assert.Equal(0, npc.Attackers[0].TotalDamage);
    }

    [Fact]
    public void ARepeatedOrderDoesNotReseedTheRow()
    {
        // Upstream returns early when the entry already exists (CCharAttacker.cpp:21),
        // so neither the threat nor the damage total is disturbed by a second order.
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);

        Assert.True(npc.CombatState.BeginFightWith(foe, toldByMaster: true));
        int threat = npc.CombatState.GetAttackerThreat(0);
        npc.FightTarget = foe.Uid;

        Assert.True(npc.CombatState.BeginFightWith(foe, toldByMaster: true));
        Assert.Single(npc.Attackers);
        Assert.Equal(threat, npc.CombatState.GetAttackerThreat(0));
    }

    // ---- 2. the trigger arguments ----------------------------------------

    [Fact]
    public void AttackTriggerCanRewriteTheThreatAndTheIgnoreFlag()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);

        int seenThreat = -1;
        Character.OnAttackTrigger = (self, target, ctx) =>
        {
            seenThreat = ctx.Threat;
            ctx.Threat = 350;          // ARGN1 write-back
            return true;
        };

        Assert.True(npc.CombatState.BeginFightWith(foe, toldByMaster: false));
        Assert.Equal(0, seenThreat);                       // seeded with the engine's own
        Assert.Equal(350, npc.CombatState.GetAttackerThreat(0));
    }

    [Fact]
    public void AttackTriggerCanIgnoreAnAttackerAlreadyOnTheList()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);
        npc.RecordAttack(foe.Uid, 5);          // already a row

        Character.OnAttackTrigger = (_, _, ctx) => { ctx.Ignore = true; return true; };

        // ARGN2 goes back onto the row and the engagement is then refused
        // (Attacker_SetIgnore at CCharFight.cpp:1440, the check at :1446).
        Assert.False(npc.CombatState.BeginFightWith(foe, toldByMaster: false));
        Assert.True(npc.Attackers[0].Ignored);
    }

    [Fact]
    public void IgnoringATargetNotYetOnTheListDoesNothing()
    {
        // Deliberate: upstream writes the flag with Attacker_SetIgnore BEFORE the
        // row exists, so the write lands nowhere and the freshly added row reads
        // back unignored (CCharFight.cpp:1440, then the check at :1446). Kept as
        // upstream has it - a script written against the reference depends on the
        // order of those two lines.
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);

        Character.OnAttackTrigger = (_, _, ctx) => { ctx.Ignore = true; return true; };

        Assert.True(npc.CombatState.BeginFightWith(foe, toldByMaster: false));
        Assert.Single(npc.Attackers);
        Assert.False(npc.Attackers[0].Ignored);
    }

    [Fact]
    public void AttackTriggerReturningOneStopsTheEngagement()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);

        Character.OnAttackTrigger = (_, _, _) => false;   // RETURN 1

        Assert.False(npc.CombatState.BeginFightWith(foe, toldByMaster: false));
        Assert.Empty(npc.Attackers);
    }

    [Fact]
    public void CombatAddCanRewriteTheThreatOrCancelTheAdd()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);

        Character.OnCombatAdd = (_, _, ctx) => { ctx.Threat += 5; return true; };
        Assert.True(npc.CombatState.AddAttacker(foe.Uid, 10));
        Assert.Equal(15, npc.CombatState.GetAttackerThreat(0));

        var other = MakeChar(world, 102);
        Character.OnCombatAdd = (_, _, _) => false;       // RETURN 1
        Assert.False(npc.CombatState.AddAttacker(other.Uid, 10));
        Assert.Single(npc.Attackers);
    }

    // ---- 3. told by the master -------------------------------------------

    [Fact]
    public void AMastersOrderOutbidsEverythingOnTheList()
    {
        var world = CreateWorld();
        var pet = MakeChar(world, 100);
        var bully = MakeChar(world, 101);
        var ordered = MakeChar(world, 102);

        pet.RecordAttack(bully.Uid, 50);
        Assert.True(pet.CombatState.SetAttackerThreat(0, 400));

        Assert.True(pet.CombatState.BeginFightWith(ordered, toldByMaster: true));

        int orderedThreat = pet.CombatState.GetAttackerThreat(
            pet.CombatState.IndexOfAttacker(ordered.Uid));
        Assert.Equal(CharacterCombatState.ThreatToldByMaster + 400, orderedThreat);
        Assert.True(orderedThreat > pet.CombatState.GetAttackerThreat(0));
    }

    [Fact]
    public void APlayerNeverCarriesThreatEvenOnAnOrder()
    {
        var world = CreateWorld();
        var player = MakeChar(world, 100, player: true);
        var foe = MakeChar(world, 101);

        Assert.True(player.CombatState.BeginFightWith(foe, toldByMaster: true));
        Assert.Equal(0, player.CombatState.GetAttackerThreat(0));
    }

    // ---- 4. picking the next target --------------------------------------

    private static NpcAI MakeAi(GameWorld world, bool threatFlag = true)
    {
        var config = new SphereConfig();
        var ai = new NpcAI(world, config);
        var flags = (NpcAIFlags)(uint)config.NpcAi;
        ai.Flags = threatFlag ? flags | NpcAIFlags.Threat : flags & ~NpcAIFlags.Threat;
        return ai;
    }

    [Fact]
    public void TheHighestThreatWinsWhenTheFlagIsOn()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var near = MakeChar(world, 101);       // 1 tile away
        var far = MakeChar(world, 110);        // 10 tiles away

        npc.RecordAttack(near.Uid, 5);
        npc.RecordAttack(far.Uid, 5);
        Assert.True(npc.CombatState.SetAttackerThreat(1, 500));   // the far one matters

        Assert.Same(far, MakeAi(world).FightFindBestTarget(npc));
    }

    [Fact]
    public void TheNearestWinsWhenTheFlagIsOff()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var near = MakeChar(world, 101);
        var far = MakeChar(world, 110);

        npc.RecordAttack(far.Uid, 5);
        npc.RecordAttack(near.Uid, 5);
        Assert.True(npc.CombatState.SetAttackerThreat(0, 500));

        Assert.Same(near, MakeAi(world, threatFlag: false).FightFindBestTarget(npc));
    }

    [Fact]
    public void TheTargetJustLostIsExcluded()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var dying = MakeChar(world, 101);
        var next = MakeChar(world, 105);

        npc.RecordAttack(dying.Uid, 5);
        npc.RecordAttack(next.Uid, 5);

        Assert.Same(next, MakeAi(world).FightFindBestTarget(npc, exclude: dying));
    }

    [Fact]
    public void AnIgnoredRowIsSkipped()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var ignored = MakeChar(world, 101);
        var other = MakeChar(world, 108);

        npc.RecordAttack(ignored.Uid, 5);
        npc.RecordAttack(other.Uid, 5);
        Assert.True(npc.SetAttackerIgnored(ignored.Uid, true));

        Assert.Same(other, MakeAi(world).FightFindBestTarget(npc));

        // ...unless @HitIgnore says otherwise, which is the reference's own escape
        // hatch (CCharNPCAct_Fight.cpp:103) - the nearer one is back in play.
        Character.OnHitIgnored = (_, _) => true;
        Assert.Same(ignored, MakeAi(world).FightFindBestTarget(npc));
    }

    [Fact]
    public void ADeadOrEmptyListLeavesTheCurrentTargetAlone()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var current = MakeChar(world, 101);
        npc.FightTarget = current.Uid;

        Assert.Same(current, MakeAi(world).FightFindBestTarget(npc));
    }

    // ---- 5. the player's own attack packet -------------------------------

    [Fact]
    public void ThePlayersAttackPacketWritesTheTargetOntoTheList()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 8401);

        var me = MakeChar(world, 100, player: true);
        me.PrivLevel = PrivLevel.Player;
        var foe = MakeChar(world, 101, player: true);
        TestHarness.AttachCharacter(client, me);

        client.HandleAttack(foe.Uid.Value);

        Assert.Single(me.Attackers);
        Assert.Equal(foe.Uid, me.Attackers[0].Uid);
    }

    [Fact]
    public void AVetoedAttackLeavesNoTrace()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 8402);

        var me = MakeChar(world, 100, player: true);
        me.PrivLevel = PrivLevel.Player;
        var foe = MakeChar(world, 101, player: true);
        TestHarness.AttachCharacter(client, me);

        Character.OnAttackTrigger = (_, _, _) => false;   // @Attack RETURN 1
        client.HandleAttack(foe.Uid.Value);

        Assert.Empty(me.Attackers);
        Assert.NotEqual(foe.Uid, me.FightTarget);
    }
}
