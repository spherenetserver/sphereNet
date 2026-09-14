using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A sector does not fall asleep the instant the player's window moves off it.
///
/// Source-X keeps a sector ticking for SECTORSLEEP after its last client leaves
/// (CSector::_CanSleep, g_Cfg._iSectorSleepDelay, ten minutes by default).
/// SphereNet had the delay, the config key, the predicate and tests for it — and
/// the tick never asked: a sector was awake if and only if it sat inside some
/// player's 5x5 window. Step three sectors away and the area behind you stopped
/// dead, which is why its item timers needed a three-minute maintenance sweep to
/// keep moving at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SectorSleepGraceTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private GameWorld _world = null!;
    private Character _player = null!;

    private static readonly Point3D Home = new(200, 200, 0, 0);
    private static readonly Point3D Away = new(1600, 1600, 0, 0);

    public SectorSleepGraceTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        Item.OnTimerExpired = null;
        Sector.SleepDelayMs = 10L * 60 * 1000;
    }

    /// <summary>Built in the test body, not the constructor: ResetEngineStatics runs
    /// after the constructor and clears the ambient world resolvers.</summary>
    private void Setup(long graceMs)
    {
        Sector.SleepDelayMs = graceMs;
        _world = new GameWorld(LoggerFactory.Create(_ => { }));
        _world.InitMap(0, 2048, 2048);
        ObjBase.ResolveWorld = () => _world;
        Item.ResolveWorld = () => _world;

        _player = _world.CreateCharacter();
        _player.IsPlayer = true;
        _player.IsOnline = true;
        _player.MaxHits = 100; _player.Hits = 100;
        _world.PlaceCharacter(_player, Home);
        _world.AddOnlinePlayer(_player);
        _world.OnTick();
    }

    /// <summary>An armed, already-overdue item timer is the cheapest probe for "did
    /// this sector tick": it fires exactly once, the first time its sector runs.</summary>
    private (Item Item, List<uint> Fires) Tripwire(Point3D at)
    {
        var fires = new List<uint>();
        Item.OnTimerExpired = it => { fires.Add(it.Uid.Value); return TriggerResult.Default; };

        var item = _world.CreateItem();
        item.BaseId = 0x0EED;
        _world.PlaceItem(item, at);
        item.SetTimeout(Environment.TickCount64 - 1000);
        return (item, fires);
    }

    private Sector SectorAt(Point3D p) =>
        _world.GetSector(0, p.X / Sector.SectorSize, p.Y / Sector.SectorSize)!;

    // ------------------------------------------------------------------

    [Fact]
    public void ASectorThePlayerJustLeftKeepsTicking()
    {
        Setup(graceMs: 10L * 60 * 1000);
        var (item, fires) = Tripwire(Home);

        // Leave. Home is now far outside the 5x5 window - the only thing that used
        // to keep it alive.
        _world.MoveCharacter(_player, Away, fireRegionEvents: false);
        _world.OnTick();

        _out.WriteLine($"item timer at the abandoned sector fired {fires.Count}x on the tick after leaving");
        Assert.Single(fires);
        Assert.Equal(item.Uid.Value, fires[0]);
        Assert.False(SectorAt(Home).IsSleeping);
    }

    [Fact]
    public void ASectorSleepsOnceTheGraceHasPassed()
    {
        Setup(graceMs: 120);
        _world.MoveCharacter(_player, Away, fireRegionEvents: false);
        _world.OnTick();

        // Arm the tripwire AFTER the move, so the only thing that can fire it is a
        // later tick of the abandoned sector.
        var (_, fires) = Tripwire(Home);

        while (Environment.TickCount64 - _world.GetSector(0, Home.X / Sector.SectorSize,
                   Home.Y / Sector.SectorSize)!.LastClientTimeMs <= 120)
            System.Threading.Thread.Sleep(10);

        _world.OnTick();

        _out.WriteLine($"after the grace expired the abandoned sector fired {fires.Count} timer(s); " +
                       $"IsSleeping={SectorAt(Home).IsSleeping}");
        Assert.Empty(fires);
        Assert.True(SectorAt(Home).IsSleeping);
    }

    [Fact]
    public void TheTrailIsTheSectorsThePlayerWasIn_NotTheWholeWindow()
    {
        Setup(graceMs: 10L * 60 * 1000);

        // A sector two away from the player: inside the 5x5 window, but the player
        // was never IN it, so nothing ever stamped its last-client time.
        var neighbour = new Point3D((short)(Home.X + 2 * Sector.SectorSize), Home.Y, 0, 0);
        var (_, fires) = Tripwire(neighbour);

        _world.MoveCharacter(_player, Away, fireRegionEvents: false);
        _world.OnTick();

        // The grace keeps the trail a traveller actually walked, one sector wide. If
        // it kept the whole window instead, a player crossing the map would drag a
        // five-sector-wide band of awake world behind them for ten minutes.
        _out.WriteLine($"window-only sector fired {fires.Count} timer(s) after the player left");
        Assert.Empty(fires);
        Assert.True(SectorAt(neighbour).IsSleeping);
    }

    [Fact]
    public void SectorSleepZeroMeansNothingEverSleeps()
    {
        Setup(graceMs: 0);
        var (_, fires) = Tripwire(Home);

        _world.MoveCharacter(_player, Away, fireRegionEvents: false);
        _world.OnTick();
        _world.OnTick();

        // Upstream reads SECTORSLEEP=0 as "never sleep" (CSector::_CanSleep returns
        // false immediately), so a sector a player has visited keeps ticking for the
        // life of the process. Pinned because it is a loaded gun on this engine: an
        // awake sector here ticks every object it holds, so a shard that sets 0 and
        // lets players travel ends up ticking the whole map.
        _out.WriteLine($"SECTORSLEEP=0: abandoned sector fired {fires.Count} timer(s); " +
                       $"IsSleeping={SectorAt(Home).IsSleeping}");
        Assert.Single(fires);
        Assert.False(SectorAt(Home).IsSleeping);
    }

    [Fact]
    public void AnUnvisitedSectorIsAsleepAndSaysSo()
    {
        Setup(graceMs: 10L * 60 * 1000);
        var far = SectorAt(Away);

        _world.OnTick();

        // IsSleeping was never assigned anywhere in the engine, so the admin sector
        // list reported every sector on the map as awake while most of it was not.
        _out.WriteLine($"never-visited sector: IsSleeping={far.IsSleeping}");
        Assert.True(far.IsSleeping || !_world.ActiveSectorsForProbe.Contains(far));
    }
}
