using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scheduling;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every creature the NPC timer wheel runs has to leave a timer behind, and a
/// creature a player walks up to has to come round and look.
///
/// Field report (a staff member running through a dungeon): some creatures turned on
/// him at once, some late, some not at all, while the NPC budget reported actions
/// running ten seconds to three minutes late with nothing ever deferred. The oldest
/// wait grew by exactly the length of the report window, window after window: one
/// creature due on every tick with a timer frozen in the past. That creature was the
/// staff member's own ridden mount - the tick returned early for it without setting
/// a new timer, the wheel kept it (a ridden mount is carried, not asleep), and the
/// past due time was clamped to the next slot, so it came due again 100 ms later,
/// for as long as he rode.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcScheduleCadenceTests
{
    private readonly ITestOutputHelper _out;
    public NpcScheduleCadenceTests(ITestOutputHelper output) => _out = output;

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static Character OnlinePlayer(GameWorld world, Point3D at)
    {
        var p = world.CreateCharacter();
        p.BodyId = 0x0190;
        p.IsPlayer = true;
        p.IsOnline = true;
        p.Str = 50;
        p.Hits = p.MaxHits = 100;
        p.Karma = 0;
        world.PlaceCharacter(p, at);
        world.AddOnlinePlayer(p);
        return p;
    }

    private static Character Monster(GameWorld world, Point3D at)
    {
        var m = world.CreateCharacter();
        m.BodyId = 0x0032; // skeleton
        m.NpcBrain = NpcBrainType.Monster;
        m.Karma = -1500;   // evil (Noto_IsEvil: a monster below zero karma)
        m.Str = 60; m.Dex = 60; m.Int = 30;
        m.Hits = m.MaxHits = 60;
        m.Stam = m.MaxStam = 60;
        world.PlaceCharacter(m, at);
        return m;
    }

    /// <summary>Run the wheel the way the server does, one 100 ms tick at a time, and
    /// count how often <paramref name="watched"/> came due.</summary>
    private static int RunTicks(TimerWheel wheel, NpcAI ai, GameWorld world, long start, int ticks,
        Character watched, Func<bool>? stopWhen = null)
    {
        int due = 0;
        for (int i = 1; i <= ticks; i++)
        {
            long now = start + i * 100L;
            SphereNet.Server.Program.RunDueNpcs(wheel, now,
                npc => { if (ReferenceEquals(npc, watched)) due++; ai.OnTickAction(npc); },
                npc => SphereNet.Server.Program.ShouldStayScheduled(world, npc));
            if (stopWhen?.Invoke() == true)
                break;
        }
        return due;
    }

    [Fact]
    public void ARiddenMount_LeavesATimerBehind_AndIsNotDueOnEveryTick()
    {
        var world = World();
        var ai = new NpcAI(world, new SphereConfig());
        OnlinePlayer(world, new Point3D(1000, 1000, 0, 0));
        world.OnTick(); // light up the sector window

        var mount = world.CreateCharacter();
        mount.BodyId = 0x00C8;
        mount.Hits = mount.MaxHits = 50;
        mount.SetStatFlag(StatFlag.Ridden);
        world.PlaceCharacter(mount, new Point3D(1000, 1000, 0, 0));

        long start = Environment.TickCount64;
        // Mounted two minutes ago: its last timer is long gone.
        mount.NextNpcActionTime = start - 120_000;
        var wheel = new TimerWheel(start - 1000);
        wheel.Schedule(mount, start);

        int due = RunTicks(wheel, ai, world, start, 30, mount);
        _out.WriteLine($"ridden mount came due {due} times in 3 s, next timer +{mount.NextNpcActionTime - Environment.TickCount64} ms");

        // Once, then not again for the re-check interval - not 30 times.
        Assert.Equal(1, due);
        Assert.True(mount.NextNpcActionTime > Environment.TickCount64);
        // It stays in the wheel: a carried mount is not asleep, and stepping off it
        // must find it scheduled.
        Assert.True(SphereNet.Server.Program.ShouldStayScheduled(world, mount));
        Assert.Equal(1, wheel.Count);
    }

    [Fact]
    public void ARiddenMount_DoesNotReportMinutesOfLatenessToTheNpcBudget()
    {
        var world = World();
        var ai = new NpcAI(world, new SphereConfig());
        OnlinePlayer(world, new Point3D(1000, 1000, 0, 0));
        world.OnTick();

        var mount = world.CreateCharacter();
        mount.BodyId = 0x00C8;
        mount.SetStatFlag(StatFlag.Ridden);
        world.PlaceCharacter(mount, new Point3D(1000, 1000, 0, 0));
        long start = Environment.TickCount64;
        mount.NextNpcActionTime = start - 60_000;
        var wheel = new TimerWheel(start - 1000);
        wheel.Schedule(mount, start);

        SphereNet.Server.Program.NpcBudgetStats.Reset();
        try
        {
            RunTicks(wheel, ai, world, start, 30, mount);
            var s = SphereNet.Server.Program.NpcBudgetStats;
            _out.WriteLine($"due={s.Due} samples={s.LatenessSamples} oldest={s.OldestWaitMs}ms");
            // The one stale timer it carried in is reported once; it is not re-reported
            // every tick, growing by 100 ms each time, which is what kept p99 overflowed.
            Assert.Equal(1, s.Due);
            Assert.Equal(1, s.LatenessSamples);
        }
        finally
        {
            SphereNet.Server.Program.NpcBudgetStats.Reset();
        }
    }

    [Fact]
    public void AStatue_LeavesATimerBehind()
    {
        var world = World();
        var ai = new NpcAI(world, new SphereConfig());
        OnlinePlayer(world, new Point3D(1000, 1000, 0, 0));
        world.OnTick();

        var statue = Monster(world, new Point3D(1003, 1000, 0, 0));
        statue.CanMask = (ulong)CanFlags.C_Statue; // XOR onto the default can flags
        Assert.True((SphereNet.Game.Definitions.CharDefHelper.GetCanFlags(statue) & CanFlags.C_Statue) != 0);

        long start = Environment.TickCount64;
        statue.NextNpcActionTime = start - 30_000;
        var wheel = new TimerWheel(start - 1000);
        wheel.Schedule(statue, start);

        int due = RunTicks(wheel, ai, world, start, 30, statue);
        Assert.Equal(1, due);
        // A statue never acts (CAN_C_STATUE skips NPC_OnTickAction upstream).
        Assert.False(statue.FightTarget.IsValid);
    }

    [Fact]
    public void TheMulticoreDecision_GivesARiddenMountAFreshTimer()
    {
        var world = World();
        var ai = new NpcAI(world, new SphereConfig());
        OnlinePlayer(world, new Point3D(1000, 1000, 0, 0));
        world.OnTick();

        var mount = world.CreateCharacter();
        mount.SetStatFlag(StatFlag.Ridden);
        world.PlaceCharacter(mount, new Point3D(1000, 1000, 0, 0));
        long now = Environment.TickCount64;
        mount.NextNpcActionTime = now - 90_000;

        var decision = ai.BuildDecision(mount, now);
        // It used to answer "nothing to do", which left the stale timer in place and
        // the reschedule put the mount straight back into the next slot.
        Assert.NotNull(decision);
        ai.ApplyDecision(decision!.Value);
        Assert.True(mount.NextNpcActionTime > now);
        Assert.Equal(now + NpcAI.IdleCreatureRecheckMs, mount.NextNpcActionTime);
    }

    [Fact]
    public void ACreatureWokenWithItsSector_TurnsOnAPassingPlayerPromptly()
    {
        // The whole path a dungeon run takes: a sleeping area with a creature whose
        // last timer is long past, a player walking into range, the sector wake, and
        // the wheel running at the tick rate. The creature must acquire the player
        // within the wake spread plus one idle re-tick, not "eventually".
        var world = World();
        var ai = new NpcAI(world, new SphereConfig());
        var monster = Monster(world, new Point3D(2000, 2000, 0, 0));
        long start = Environment.TickCount64;
        monster.NextNpcActionTime = start - 176_500; // the figure the report showed
        world.OnTick();
        Assert.False(world.IsInActiveArea(0, monster.X, monster.Y));

        var player = OnlinePlayer(world, new Point3D(2004, 2000, 0, 0));
        world.OnTick();
        var woken = world.NewlyActiveSectors;
        Assert.Contains(world.GetSector(monster.Position), woken);

        var wheel = new TimerWheel(start);
        SphereNet.Server.Program.WakeSectorNpcs(wheel, woken, start);
        // A wake clears the stale timer, so the budget sees no lateness for it.
        Assert.Equal(0, monster.NextNpcActionTime);

        SphereNet.Server.Program.NpcBudgetStats.Reset();
        try
        {
            int ticks = 0;
            RunTicks(wheel, ai, world, start, 40, monster,
                () => { ticks++; return monster.FightTarget.IsValid; });
            _out.WriteLine($"acquired after {ticks} ticks, late_max={SphereNet.Server.Program.NpcBudgetStats.LatenessMaxMs}ms");

            Assert.Equal(player.Uid, monster.FightTarget);
            // Wake spread is under a second; the first action looks around.
            Assert.True(ticks <= 10, $"took {ticks} ticks");
            Assert.True(SphereNet.Server.Program.NpcBudgetStats.LatenessMaxMs < 1000);
        }
        finally
        {
            SphereNet.Server.Program.NpcBudgetStats.Reset();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStaffMember_InOrOutOfGmMode_IsAttackedUnlessInvulnerable(bool gmMode)
    {
        // Upstream has no GM-mode immunity for creatures: NPC_LookAround,
        // NPC_GetHostilityLevelToward and Fight_Attack never look at PRIV_GM. What
        // keeps them off a staff member is STATF_INVUL (Fight_IsAttackableState),
        // which the login sets (CClient::Setup_Start) and INVUL clears.
        var world = World();
        var ai = new NpcAI(world, new SphereConfig());
        var staff = OnlinePlayer(world, new Point3D(1000, 1000, 0, 0));
        staff.PrivLevel = PrivLevel.Owner;
        Assert.True(staff.TrySetProperty("GM", gmMode ? "1" : "0"));
        Assert.Equal(gmMode, staff.IsGmMode);
        var monster = Monster(world, new Point3D(1002, 1000, 0, 0));
        world.OnTick();

        monster.NextNpcActionTime = 0;
        ai.OnTickAction(monster);
        Assert.Equal(staff.Uid, monster.FightTarget);

        // Invulnerable: the creature lets go and does not pick the staff member again.
        staff.SetStatFlag(StatFlag.Invul);
        monster.NextNpcActionTime = 0;
        ai.OnTickAction(monster);
        Assert.False(monster.FightTarget.IsValid);

        var other = Monster(world, new Point3D(998, 1000, 0, 0));
        other.NextNpcActionTime = 0;
        ai.OnTickAction(other);
        Assert.False(other.FightTarget.IsValid);
    }

    [Fact]
    public void ARiddenMount_DoesNotMakeItsSectorLookComplex()
    {
        // CSector::GetCharComplexity counts the active characters; a ridden mount is
        // out of the world upstream (Horse_Mount: SetDisconnected). Counting it shrank
        // every look-around in the sector to a quarter of its range once a crowded
        // sector crossed half of MAXCOMPLEXITY.
        var world = World();
        var at = new Point3D(1000, 1000, 0, 0);
        OnlinePlayer(world, at);
        var mount = world.CreateCharacter();
        mount.SetStatFlag(StatFlag.Ridden);
        world.PlaceCharacter(mount, at);

        var sector = world.GetSector(at)!;
        Assert.Equal(1, sector.GetCharComplexity());
        Assert.Equal(1, sector.CharComplexity);
    }
}
