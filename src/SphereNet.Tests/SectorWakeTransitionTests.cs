using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scheduling;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What the transitions cost: the cases the timer-delay contract left as inferences
/// rather than measurements (review work item D04, the open half of B7).
///
/// The contract table says what a timer's delay is while nobody is nearby. It does not
/// say what happens at the moments the answer CHANGES — a player teleporting into an
/// empty part of the world, walking back and forth over a sector boundary, logging out,
/// changing map — nor what a pet or a summon does while its sector sleeps, which was
/// reasoned about (they hang off memory items, so they should be exact) and never
/// measured. An inference is not a measurement, and the two differ here: a pet does not
/// sleep because of what it IS, not because of where its timer lives.
///
/// The last test measures the one number the acceptance asks for and nothing reported:
/// how much AI a wake piles into a single tick.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SectorWakeTransitionTests
{
    private readonly ITestOutputHelper _out;
    public SectorWakeTransitionTests(ITestOutputHelper output) => _out = output;

    private static readonly Point3D Near = new(100, 100, 0, 0);
    private static readonly Point3D Far = new(1200, 1200, 0, 0);

    private GameWorld _world = null!;
    private Character _player = null!;

    /// <summary>Built inside the test body, not the constructor: ResetEngineStatics
    /// runs between the two and would take the ambient resolvers with it.</summary>
    private void Setup()
    {
        _world = new GameWorld(LoggerFactory.Create(_ => { }));
        _world.InitMap(0, 2048, 2048);
        _world.InitMap(1, 2048, 2048);
        ObjBase.ResolveWorld = () => _world;
        Item.ResolveWorld = () => _world;

        _player = _world.CreateCharacter();
        _player.IsPlayer = true;
        _player.IsOnline = true;
        _player.MaxHits = 100; _player.Hits = 100;
        _world.PlaceCharacter(_player, Near);
        _world.AddOnlinePlayer(_player);
        _world.OnTick();
    }

    /// <summary>A creature that shows whether its sector ticked: one hit point per
    /// tick while its sector is awake, nothing while it sleeps.</summary>
    private Character Regenerating(Point3D at)
    {
        var npc = _world.CreateCharacter();
        npc.BaseId = 0x000C;
        npc.NpcBrain = NpcBrainType.Monster;
        npc.MaxHits = 50; npc.Hits = 1;
        npc.TrySetProperty("REGENHITS", "0");
        npc.Food = 10;
        _world.PlaceCharacter(npc, at);
        return npc;
    }

    private bool SectorAwake(Point3D at)
    {
        var sector = _world.GetSector(at.Map, at.X / Sector.SectorSize, at.Y / Sector.SectorSize);
        return sector != null && _world.ActiveSectorsForProbe.Contains(sector);
    }

    // ---- arriving --------------------------------------------------------

    [Fact]
    public void ATeleportWakesTheDestinationOnTheVeryNextTick()
    {
        Setup();
        var npc = Regenerating(Far);
        for (int i = 0; i < 5; i++) _world.OnTick();
        Assert.Equal(1, npc.Hits);      // asleep, as the contract says

        // A teleport is a position change with no walk in between: nothing crosses the
        // sectors along the way, so the destination has to wake on the position alone.
        _world.PlaceCharacter(_player, Far);
        _world.OnTick();

        _out.WriteLine($"after one tick at the destination: {npc.Hits} hp");
        Assert.True(npc.Hits > 1, "the destination sector did not wake on the tick after the teleport");
        Assert.True(SectorAwake(Far));
    }

    [Fact]
    public void TheSectorLeftBehindKeepsTickingForItsGracePeriod()
    {
        Setup();
        Regenerating(Near);
        _world.PlaceCharacter(_player, Far);
        for (int i = 0; i < 5; i++) _world.OnTick();

        // SECTORSLEEP (ten minutes by default) is measured from the last time a client
        // was in the sector, so the part of the world a player just left does not stop
        // the instant they walk out of the window - which is the whole point of the
        // grace, and what makes walking back into it free. The active set is the probe:
        // hit-point regen runs on its own wall clock, and several ticks inside one
        // millisecond produce one gain however awake the sector is.
        var neverVisited = new Point3D(300, 1800, 0, 0);
        _out.WriteLine($"left behind awake={SectorAwake(Near)} never-visited awake={SectorAwake(neverVisited)}");
        Assert.True(SectorAwake(Near), "the sector behind the player stopped immediately");
        Assert.False(SectorAwake(neverVisited), "a sector nobody has been near was ticking");
    }

    [Fact]
    public void WalkingBackAndForthOverASectorBoundaryDoesNotChurn()
    {
        Setup();
        // Straddle a sector boundary: one step each way, ten times.
        int boundary = Sector.SectorSize * 3;
        var left = new Point3D((short)(boundary - 1), 100, 0, 0);
        var right = new Point3D((short)boundary, 100, 0, 0);
        Regenerating(left);

        int sleepTransitions = 0;
        bool wasAwake = true;
        for (int i = 0; i < 10; i++)
        {
            _world.PlaceCharacter(_player, i % 2 == 0 ? right : left);
            _world.OnTick();
            bool awake = SectorAwake(left);
            if (awake != wasAwake) sleepTransitions++;
            wasAwake = awake;
        }

        // Both sides are inside the 5x5 window anyway; the point of the measurement is
        // that a player pacing a boundary does not make a sector flap between states,
        // which would show up as creatures stuttering in and out of thinking.
        _out.WriteLine($"boundary pacing: {sleepTransitions} sleep/wake transitions, " +
                       $"left sector awake={SectorAwake(left)}");
        Assert.Equal(0, sleepTransitions);
        Assert.True(SectorAwake(left));
    }

    // ---- leaving ---------------------------------------------------------

    [Fact]
    public void AMapChangeWakesTheNewMapAndLeavesTheOldOneToItsGrace()
    {
        Setup();
        var sameSpotOnMapOne = new Point3D(100, 100, 0, 1);
        var onMapOne = Regenerating(sameSpotOnMapOne);
        _world.OnTick();

        // The same coordinates on another map are a different sector, so the creature
        // there is asleep while the player stands on map 0.
        Assert.False(SectorAwake(sameSpotOnMapOne));
        Assert.Equal(1, onMapOne.Hits);

        _world.PlaceCharacter(_player, sameSpotOnMapOne);
        _world.OnTick();

        _out.WriteLine($"after the map change: map1 npc {onMapOne.Hits} hp, " +
                       $"map0 sector awake={SectorAwake(Near)}");
        Assert.True(onMapOne.Hits > 1, "the new map's sector did not wake");
        Assert.True(SectorAwake(sameSpotOnMapOne));
        // And the map the player left keeps its grace like any sector walked out of.
        Assert.True(SectorAwake(Near));
    }

    [Fact]
    public void ALingeringClientStillHoldsItsSectorAwake()
    {
        Setup();
        var npc = Regenerating(Near);

        // Link loss, not logout: the character stays in the world while the client
        // lingers, and anything that was happening around them keeps happening -
        // otherwise a dropped connection would freeze the fight they were in.
        _player.IsOnline = false;
        _player.SetTag("CLIENT_LINGER_UNTIL", (Environment.TickCount64 + 60_000).ToString());
        Assert.True(_player.IsClientLingering);
        int before = npc.Hits;
        _world.OnTick();

        _out.WriteLine($"while lingering: {before} -> {npc.Hits} hp");
        Assert.True(npc.Hits > before);
        Assert.True(SectorAwake(Near));
    }

    // ---- what never sleeps ----------------------------------------------

    [Fact]
    public void APetKeepsItsScheduleWhereverItIsAndAWildCreatureDoesNot()
    {
        Setup();
        var wild = Regenerating(Far);

        var pet = _world.CreateCharacter();
        pet.BaseId = 0x000C;
        pet.NpcBrain = NpcBrainType.Animal;
        pet.MaxHits = 50; pet.Hits = 50;
        _world.PlaceCharacter(pet, Far);
        pet.TryAssignOwnership(_player, _player, summoned: false, enforceFollowerCap: false);
        Assert.True(pet.NpcMaster.IsValid, "the pet was not given a master");

        // This is the rule the AI schedule is built on, measured rather than inferred:
        // a pet stays in the wheel because of WHAT IT IS, not because of where its
        // timer lives. That is what makes a summon's expiry and a pet's loyalty tick
        // exact in a sleeping sector - and it is also why a creature that stops being
        // a pet in a far sector stops being ticked at all.
        Assert.True(SphereNet.Server.Program.ShouldStayScheduled(_world, pet),
            "a pet in a sleeping sector was dropped from the schedule");
        Assert.False(SphereNet.Server.Program.ShouldStayScheduled(_world, wild),
            "a wild creature in a sleeping sector was kept in the schedule");

        // Releasing it makes it a wild creature in a sleeping sector, and it stops
        // being thought about like any other - worth knowing, because its pet timers
        // (loyalty, food, a summon's expiry) stop with it.
        pet.ClearOwnership(clearFriends: true);
        _out.WriteLine($"released pet in a sleeping sector stays scheduled: " +
                       $"{SphereNet.Server.Program.ShouldStayScheduled(_world, pet)}");
        Assert.False(SphereNet.Server.Program.ShouldStayScheduled(_world, pet));
    }

    [Fact]
    public void ASummonExpiresOnTimeWhileItsSectorSleeps()
    {
        Setup();
        var summon = _world.CreateCharacter();
        summon.BaseId = 0x000C;
        summon.NpcBrain = NpcBrainType.Monster;
        summon.MaxHits = 50; summon.Hits = 50;
        _world.PlaceCharacter(summon, Far);
        summon.TryAssignOwnership(_player, _player, summoned: true, enforceFollowerCap: false);
        summon.SetTag("SUMMON_DURATION", "300");
        summon.SetTag("SUMMON_EXPIRE_TICK", (1_000_000L + 30_000).ToString());

        // A summon is a pet as far as the schedule is concerned, so its expiry is
        // reached by the AI tick at its deadline rather than whenever somebody
        // happens to walk past. Both halves are asserted: it is still scheduled, and
        // the check it is scheduled FOR answers at the right moment.
        Assert.True(SphereNet.Server.Program.ShouldStayScheduled(_world, summon));
        Assert.False(summon.TickPetOwnershipTimers(1_000_000L + 29_999));
        Assert.True(summon.TickPetOwnershipTimers(1_000_000L + 30_000));
    }

    // ---- the cost of waking up ------------------------------------------

    [Fact]
    public void ASectorFullOfCreaturesDoesNotLandInOneTick()
    {
        Setup();
        const int Count = 240;
        // All of them inside ONE sector: a sector is 64 tiles, so an 8x30 block of
        // creatures starting on a sector boundary stays in it.
        int sx = Far.X / Sector.SectorSize, sy = Far.Y / Sector.SectorSize;
        short baseX = (short)(sx * Sector.SectorSize), baseY = (short)(sy * Sector.SectorSize);
        var sector = _world.GetSector(0, sx, sy)!;
        for (int i = 0; i < Count; i++)
        {
            var npc = _world.CreateCharacter();
            npc.BaseId = 0x000C;
            npc.NpcBrain = NpcBrainType.Monster;
            npc.MaxHits = 50; npc.Hits = 50;
            _world.PlaceCharacter(npc, new Point3D((short)(baseX + (i % 8)), (short)(baseY + (i / 8)), 0, 0));
        }
        Assert.Equal(Count, sector.Characters.Count(c => !c.IsPlayer));

        long now = 5_000_000;
        var wheel = new TimerWheel(now);
        SphereNet.Server.Program.WakeSectorNpcs(wheel, new[] { sector }, now);

        // Walk the wheel 100 ms at a time - the tick rate - and record the worst tick.
        var perTick = new List<int>();
        for (long t = now + 100; t <= now + 1200; t += 100)
            perTick.Add(wheel.Advance(t).Count);

        int woken = perTick.Sum();
        int worst = perTick.Max();
        _out.WriteLine($"{Count} creatures woken: {woken} scheduled, worst tick {worst}, per tick [{string.Join(",", perTick)}]");

        // Every one of them wakes, and none is forgotten.
        Assert.Equal(Count, woken);

        // The spread is the uid modulo 800 ms, so creatures with CONSECUTIVE uids -
        // a sector filled by one spawner, or by a world load - are one millisecond
        // apart and occupy as many 100 ms ticks as they are hundreds. 240 of them
        // therefore land across about three ticks rather than eight, and the number
        // worth knowing is the worst one: it has to stay well inside the 500-per-tick
        // AI budget, which is what stops a wake from becoming a stall.
        Assert.True(worst < 500 / 2,
            $"a wake piled {worst} creatures into one tick, close to the {500} per-tick budget");
        Assert.True(perTick.Count(n => n > 0) >= 2, "the wake was not spread over more than one tick");
    }
    // ---- what SECTORSLEEP=0 actually does --------------------------------

    [Fact]
    public void DisablingSectorSleepKeepsEveryVisitedSectorAwakeForever()
    {
        Setup();
        long restore = Sector.SleepDelayMs;
        try
        {
            // "SECTORSLEEP ... 0 disables sector sleeping" is what the ini says, and
            // CanSleep is a faithful port of upstream's predicate: delay 0 means never
            // sleep. But upstream ticks EVERY sector and lets the predicate decide,
            // while the tick set here is built from the 5x5 window around each player
            // and the predicate is only ever asked about sectors that were active last
            // tick. With 0, those are never allowed to sleep, so the set only grows.
            Sector.SleepDelayMs = 0;

            var counts = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                _world.PlaceCharacter(_player, new Point3D((short)(200 + i * Sector.SectorSize * 3), 200, 0, 0));
                _world.OnTick();
                counts.Add(_world.ActiveSectorsForProbe.Count);
            }

            _out.WriteLine($"SECTORSLEEP=0, a player crossing 8 areas: active sectors {string.Join(" -> ", counts)}");

            // Two things are true at once here, and neither is what the ini promises:
            // a sector nobody has visited is still not ticking (so sleeping is not
            // disabled), and the ticking set never shrinks (so a long-running shard
            // pays for every sector any player has ever walked through).
            Assert.True(counts[^1] > counts[0], "the active set did not grow");
            for (int i = 1; i < counts.Count; i++)
                Assert.True(counts[i] >= counts[i - 1], "the active set shrank; the grace is bounded after all");
            Assert.False(SectorAwake(new Point3D(1800, 1800, 0, 0)),
                "an unvisited sector was ticking, so sleeping really is disabled");
        }
        finally
        {
            Sector.SleepDelayMs = restore;
        }
    }
}
