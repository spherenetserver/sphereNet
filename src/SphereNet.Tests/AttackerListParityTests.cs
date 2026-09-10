using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The ATTACKER.* script surface (port plan İŞ-19 / PLAN-402, target-change leg).
///
/// Measured from the pack side rather than from the enum: the LIVE script pack asks
/// for <c>&lt;ATTACKER.0&gt;</c>, <c>&lt;ATTACKER.LAST.DAM&gt;</c> and
/// <c>&lt;ATTACKER.MAX.DAM&gt;</c> in its own player-info dialog
/// (dialogs/sphere_dialogs_prop.scp:530/537/541) and writes <c>ATTACKER.CLEAR</c> in
/// functions/sphere_functions.scp:246. None of the four resolved: the engine only
/// answered LAST and MAX as bare uids and only accepted <c>n.IGNORE</c> on the way in,
/// so the dialog printed nothing and the clear was a no-op.
///
/// The reference resolves a SELECTOR (a number, MAX, LAST) to an index and then reads
/// or writes a field on that row (CChar.cpp:2414 / :3733).
/// </summary>
public sealed class AttackerListParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar(GameWorld world, int x, bool player = false)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static string Get(Character ch, string key)
    {
        Assert.True(ch.TryGetProperty(key, out string value), $"{key} did not resolve");
        return value;
    }

    private static uint ParseUid(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.Length > 1 && s[0] == '0') s = s[1..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v) ? v : 0;
    }

    // ---- what the live pack actually asks for -----------------------------

    [Fact]
    public void ABareIndexAnswersWithTheAttackersUid()
    {
        var world = CreateWorld();
        var victim = MakeChar(world, 100);
        var attacker = MakeChar(world, 101);
        victim.RecordAttack(attacker.Uid, 7);

        Assert.Equal(attacker.Uid.Value, ParseUid(Get(victim, "ATTACKER.0")));
        Assert.Equal(attacker.Uid.Value, ParseUid(Get(victim, "ATTACKER.0.UID")));
    }

    [Fact]
    public void MaxAndLastCarryAFieldAfterThem()
    {
        var world = CreateWorld();
        var victim = MakeChar(world, 100);
        var heavy = MakeChar(world, 101);
        var recent = MakeChar(world, 102);

        victim.RecordAttack(heavy.Uid, 100);
        victim.RecordAttack(recent.Uid, 10);
        // The wall clock ticks in ~15ms steps here, so the two hits can land on
        // the same stamp. Age the first one deliberately instead of sleeping.
        Assert.True(victim.TrySetProperty("ATTACKER.0.ELAPSED", "30"));

        Assert.Equal(heavy.Uid.Value, ParseUid(Get(victim, "ATTACKER.MAX")));
        Assert.Equal("100", Get(victim, "ATTACKER.MAX.DAM"));
        Assert.Equal(recent.Uid.Value, ParseUid(Get(victim, "ATTACKER.LAST")));
        Assert.Equal("10", Get(victim, "ATTACKER.LAST.DAM"));
    }

    [Fact]
    public void ClearEmptiesTheWholeList()
    {
        var world = CreateWorld();
        var victim = MakeChar(world, 100);
        victim.RecordAttack(MakeChar(world, 101).Uid, 5);
        victim.RecordAttack(MakeChar(world, 102).Uid, 5);
        Assert.Equal("2", Get(victim, "ATTACKER"));

        Assert.True(victim.TrySetProperty("ATTACKER.CLEAR", "1"));
        Assert.Equal("0", Get(victim, "ATTACKER"));
    }

    [Fact]
    public void IdAnswersTheSlotACharacterHolds()
    {
        var world = CreateWorld();
        var victim = MakeChar(world, 100);
        var first = MakeChar(world, 101);
        var second = MakeChar(world, 102);
        var stranger = MakeChar(world, 103);

        victim.RecordAttack(first.Uid, 5);
        victim.RecordAttack(second.Uid, 5);

        Assert.Equal("0", Get(victim, $"ATTACKER.ID 0{first.Uid.Value:X}"));
        Assert.Equal("1", Get(victim, $"ATTACKER.ID 0{second.Uid.Value:X}"));
        Assert.Equal("-1", Get(victim, $"ATTACKER.ID 0{stranger.Uid.Value:X}"));
    }

    // ---- the rest of the reference's key set -------------------------------

    [Fact]
    public void TargetReadsAndWritesTheFightTarget()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);

        Assert.Equal("-1", Get(npc, "ATTACKER.TARGET"));
        Assert.True(npc.TrySetProperty("ATTACKER.TARGET", $"0{foe.Uid.Value:X}"));
        Assert.Equal(foe.Uid, npc.FightTarget);
        Assert.Equal(foe.Uid.Value, ParseUid(Get(npc, "ATTACKER.TARGET")));

        // Pointing at myself is refused and clears the target (CChar.cpp:3770).
        Assert.False(npc.TrySetProperty("ATTACKER.TARGET", $"0{npc.Uid.Value:X}"));
        Assert.False(npc.FightTarget.IsValid);
    }

    [Fact]
    public void PerRowFieldsAreWritable()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var foe = MakeChar(world, 101);
        npc.RecordAttack(foe.Uid, 20);

        Assert.True(npc.TrySetProperty("ATTACKER.0.DAM", "77"));
        Assert.Equal("77", Get(npc, "ATTACKER.0.DAM"));

        Assert.True(npc.TrySetProperty("ATTACKER.0.ELAPSED", "30"));
        Assert.Equal("30", Get(npc, "ATTACKER.0.ELAPSED"));

        Assert.True(npc.TrySetProperty("ATTACKER.0.THREAT", "42"));
        Assert.Equal("42", Get(npc, "ATTACKER.0.THREAT"));

        Assert.True(npc.TrySetProperty("ATTACKER.0.DELETE", "1"));
        Assert.Equal("0", Get(npc, "ATTACKER"));
    }

    [Fact]
    public void DeleteTakesTheAttackersUid()
    {
        var world = CreateWorld();
        var npc = MakeChar(world, 100);
        var a1 = MakeChar(world, 101);
        var a2 = MakeChar(world, 102);
        npc.RecordAttack(a1.Uid, 5);
        npc.RecordAttack(a2.Uid, 5);

        Assert.True(npc.TrySetProperty("ATTACKER.DELETE", $"0{a1.Uid.Value:X}"));
        Assert.Equal("1", Get(npc, "ATTACKER"));
        Assert.Equal(a2.Uid.Value, ParseUid(Get(npc, "ATTACKER.0")));
    }

    [Fact]
    public void AThreatWriteIsRefusedOnAPlayer()
    {
        // Threat exists to steer an NPC's choice of target, so the reference
        // returns before writing one on a player (CCharAttacker.cpp:205).
        var world = CreateWorld();
        var player = MakeChar(world, 100, player: true);
        player.RecordAttack(MakeChar(world, 101).Uid, 5);

        Assert.False(player.TrySetProperty("ATTACKER.0.THREAT", "900"));
        Assert.Equal("0", Get(player, "ATTACKER.0.THREAT"));
    }

    // ---- the list's shape --------------------------------------------------

    [Fact]
    public void RepeatedHitsDoNotRenumberTheList()
    {
        // ATTACKER.n is the handle a script holds between two lines. Moving the
        // entry that just took a hit to the end made every index below it point at
        // somebody else; the reference only ever appends (Attacker_Add).
        var world = CreateWorld();
        var victim = MakeChar(world, 100);
        var first = MakeChar(world, 101);
        var second = MakeChar(world, 102);

        victim.RecordAttack(first.Uid, 5);
        victim.RecordAttack(second.Uid, 5);
        Assert.True(victim.TrySetProperty("ATTACKER.1.ELAPSED", "30"));
        victim.RecordAttack(first.Uid, 5);   // the older attacker hits again

        Assert.Equal(first.Uid.Value, ParseUid(Get(victim, "ATTACKER.0")));
        Assert.Equal(second.Uid.Value, ParseUid(Get(victim, "ATTACKER.1")));
        // ...and LAST still follows the most recent hit, not the insertion order.
        Assert.Equal(first.Uid.Value, ParseUid(Get(victim, "ATTACKER.LAST")));
    }
}
