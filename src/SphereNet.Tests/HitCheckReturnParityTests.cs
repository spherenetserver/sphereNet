using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// @HitCheck's return is a NUMBER, and the number is the point (port plan İŞ-20 /
/// PLAN-402, swing-state leg).
///
/// The reference reads the trigger's return as a war swing state and acts on it
/// (CCharFight.cpp:1770-1779): RETURN 1 means "the state is in ARGN1", RETURN -1 means
/// the target is invalid, RETURN -2 means take my edits and run the hardcoded path
/// anyway. This engine treated any true return as a forced miss, which inverts the
/// contract - the reference pack's own combat override answers
/// <c>argn1 SWING_READY / return 1</c> to mean "hold, not yet"
/// (Scripts-X-main/_incomplete/combat_override.scp:49), and every one of those holds
/// would have been rendered as a swing and a miss.
///
/// Reading it at all needed the raw RETURN value to reach the engine: a true/false
/// result cannot carry -1 or -2.
/// </summary>
public sealed class HitCheckReturnParityTests : IDisposable
{
    private readonly string _script;

    public HitCheckReturnParityTests()
    {
        _script = Path.Combine(Path.GetTempPath(), $"sphnet_hitcheck_{Guid.NewGuid():N}.scp");
    }

    public void Dispose()
    {
        try { File.Delete(_script); } catch (IOException) { }
    }

    private (GameClient Client, Character Attacker, Character Target) Setup(string body, int port)
    {
        // The probe also records @HitMiss: whether the miss fired is what
        // separates "the script held the swing" from "the engine rendered a miss",
        // and both leave the target at full health.
        File.WriteAllText(_script, $"""
            [EVENTS e_hitcheck_probe]
            ON=@HitCheck
            {body}

            ON=@HitMiss
            TAG.MISSED=1
            """);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(_script);
        stack.Dispatcher.BuildUsedTriggerCache();

        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.PrivLevel = PrivLevel.GM;      // skip LOS so the swing prep is deterministic
        attacker.Str = attacker.Dex = 100;
        attacker.Stam = attacker.MaxStam = 100;
        attacker.SetStatFlag(StatFlag.War);
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, attacker);
        client.BroadcastNearby = (_, _, _, _) => { };

        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));   // in melee reach

        var weapon = world.CreateItem();
        weapon.ItemType = ItemType.WeaponSword;
        weapon.BaseId = 0x0F5E;
        attacker.Equip(weapon, Layer.OneHanded);

        client.SetEngines(triggerDispatcher: stack.Dispatcher);
        attacker.Events.Add(stack.Resources.ResolveDefName("e_hitcheck_probe"));
        attacker.FightTarget = target.Uid;
        attacker.NextAttackTime = 0;
        return (client, attacker, target);
    }

    // ---- RETURN 1: ARGN1 is the state -------------------------------------

    [Fact]
    public void ReturnOneWithSwingReadyHoldsTheSwingInsteadOfMissing()
    {
        var (client, attacker, target) = Setup("""
            ARGN1=1
            RETURN 1
            """, 1450);

        client.TickCombat();

        // Held: no swing, no MISS, nothing spent, the fight stands.
        Assert.False(attacker.HasPendingHit);
        Assert.False(attacker.TryGetTag("MISSED", out _));
        Assert.Equal(100, target.Hits);
        Assert.Equal(target.Uid, attacker.FightTarget);
        Assert.Equal("1", Read(attacker, "SWING"));
    }

    [Fact]
    public void ReturnOneWithSwingEquippingSpendsTheSwing()
    {
        var (client, attacker, target) = Setup("""
            ARGN1=0
            RETURN 1
            """, 1451);

        long before = Environment.TickCount64;
        client.TickCombat();

        // The script says the swing was made: no hit lands and no miss is
        // rendered either, but the recoil runs.
        Assert.Equal(100, target.Hits);
        Assert.False(attacker.HasPendingHit);
        Assert.False(attacker.TryGetTag("MISSED", out _));
        Assert.True(attacker.NextAttackTime > before);
        Assert.Equal(target.Uid, attacker.FightTarget);
    }

    // ---- RETURN -1: the target is invalid ---------------------------------

    [Fact]
    public void ReturnMinusOneStopsFightingThatTarget()
    {
        var (client, attacker, target) = Setup("RETURN -1", 1452);

        client.TickCombat();

        Assert.False(attacker.FightTarget.IsValid);
        Assert.False(attacker.TryGetTag("MISSED", out _));
        Assert.Equal(100, target.Hits);
    }

    // ---- RETURN -2: run the hardcoded path anyway --------------------------

    [Fact]
    public void ReturnMinusTwoKeepsTheHardcodedPath()
    {
        // -2 is a TRUE return by the true/false reading, so without the numeric
        // return reaching the engine this would have been read as a veto.
        var (client, attacker, target) = Setup("""
            TAG.SAW=1
            RETURN -2
            """, 1453);

        client.TickCombat();

        Assert.True(attacker.TryGetTag("SAW", out string? saw) && saw == "1");
        Assert.True(attacker.FightTarget.IsValid);
        // The swing went through the ordinary path: it either landed or missed.
        // Being HELD (no damage and no miss) is the failure this catches.
        Assert.True(target.Hits < 100 || attacker.TryGetTag("MISSED", out _),
            "the swing was held instead of resolving");
    }

    [Fact]
    public void NoReturnRunsTheHardcodedPath()
    {
        var (client, attacker, target) = Setup("TAG.SAW=1", 1454);

        client.TickCombat();

        Assert.True(attacker.TryGetTag("SAW", out string? saw) && saw == "1");
        Assert.True(attacker.FightTarget.IsValid);
        Assert.True(target.Hits < 100 || attacker.TryGetTag("MISSED", out _),
            "the swing was held instead of resolving");
    }

    // ---- the SWING property the live pack reads ----------------------------

    [Fact]
    public void SwingIsReadableUnderTheReferencesOwnName()
    {
        // The live pack's player-info dialog prints <SWING> and
        // <DEF.war_swing.<SWING>> (dialogs/sphere_dialogs_prop.scp:183); only the
        // SphereNet-invented SWINGSTATE name used to answer.
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();

        ch.SetCombatSwingState(SwingState.Swinging);
        Assert.Equal("2", Read(ch, "SWING"));
        Assert.Equal(Read(ch, "SWINGSTATE"), Read(ch, "SWING"));
    }

    [Fact]
    public void SwingAcceptsOnlyTheReferencesRange()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();

        Assert.True(ch.TrySetProperty("SWING", "2"));
        Assert.Equal(SwingState.Swinging, ch.CombatSwingState);
        Assert.True(ch.TrySetProperty("SWING", "-1"));
        Assert.Equal(SwingState.Invalid, ch.CombatSwingState);
        Assert.True(ch.TrySetProperty("SWING", "0"));
        Assert.Equal(SwingState.Equipping, ch.CombatSwingState);

        // Out of the -1..2 band the reference refuses the write rather than
        // parking a nonsense state (CChar.cpp:4041).
        Assert.False(ch.TrySetProperty("SWING", "3"));
        Assert.False(ch.TrySetProperty("SWING", "-2"));
        Assert.Equal(SwingState.Equipping, ch.CombatSwingState);
    }

    // ---- the same contract when an NPC swings ------------------------------

    private static (SphereNet.Game.AI.NpcAI Ai, Character Npc, Character Target) NpcFight()
    {
        var world = TestHarness.CreateWorld();
        var ai = new SphereNet.Game.AI.NpcAI(world, new SphereNet.Core.Configuration.SphereConfig());

        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Monster;
        npc.Str = npc.Dex = 100;
        npc.Stam = npc.MaxStam = 100;
        npc.Hits = npc.MaxHits = 100;
        npc.NextNpcActionTime = 0;
        npc.NextAttackTime = 0;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.IsOnline = true;
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        world.AddOnlinePlayer(target);
        world.OnTick();

        npc.FightTarget = target.Uid;
        return (ai, npc, target);
    }

    [Fact]
    public void AnNpcSwingReadsTheSameReturnContract()
    {
        // A script must not see one meaning of RETURN 1 when it swings and another
        // when its pet does: the NPC path used to read any true return as a miss.
        var (ai, npc, target) = NpcFight();
        int missesReported = 0;
        ai.OnNpcAttack = (_, _, _, damage, _) =>
        {
            if (damage == SphereNet.Game.Combat.CombatEngine.AttackMiss) missesReported++;
        };
        ai.OnNpcHitCheck = (attacker, _, _, noRange) =>
            new SphereNet.Game.AI.NpcAI.NpcHitCheckOutcome(
                1, true, (int)SwingState.Ready, noRange);

        ai.OnTickAction(npc);

        Assert.Equal(0, missesReported);
        Assert.Equal(100, target.Hits);
        Assert.Equal(target.Uid, npc.FightTarget);
    }

    [Fact]
    public void AnNpcDropsTheTargetOnMinusOne()
    {
        var (ai, npc, target) = NpcFight();
        ai.OnNpcHitCheck = (_, _, _, noRange) =>
            new SphereNet.Game.AI.NpcAI.NpcHitCheckOutcome(-1, true, 0, noRange);

        ai.OnTickAction(npc);

        Assert.Equal(100, target.Hits);
        Assert.NotEqual(target.Uid, npc.FightTarget);
    }

    private static string Read(Character ch, string key)
    {
        Assert.True(ch.TryGetProperty(key, out string value), $"{key} did not resolve");
        return value;
    }
}
