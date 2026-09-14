using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using SphereNet.Scripting.Definitions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What "the timer fires on schedule" actually means per timer type, with nobody
/// nearby (review finding B7).
///
/// Sector sleeping means a far-away sector does not tick. Storing a deadline as an
/// absolute wall-clock time is not the same thing as running the callback at that
/// deadline: the deadline does not drift, but the CALL can still be late, and how
/// late depends on which path owns the timer. These tests measure each path rather
/// than asserting the general claim, because the general claim is not true of every
/// path and the documentation has to say which.
///
/// An active sector window is 5x5 sectors of 64 tiles around each player, so a
/// sleeping sector is at least ~128 tiles away — far outside the 18-tile view range.
/// Nothing in a sleeping sector is observable while it is late; it becomes
/// observable when a player arrives, which is also when the sector wakes.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SleepingSectorTimerContractTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private GameWorld _world = null!;
    private Character _player = null!;

    private static readonly Point3D Near = new(100, 100, 0, 0);   // player's own sector
    private static readonly Point3D Far = new(1200, 1200, 0, 0);  // >> 5x5 window away

    public SleepingSectorTimerContractTests(ITestOutputHelper output) => _out = output;

    /// <summary>Build the world INSIDE the test body: ResetEngineStatics runs after
    /// the class constructor, so a world wired up there loses its ambient resolvers
    /// before the first line of the test. Two of these tests measure registries that
    /// are populated through those resolvers, and with them null the registrations
    /// silently do not happen — the tests would have reported the engine losing
    /// timers it does not lose.</summary>
    private void Setup()
    {
        _world = new GameWorld(LoggerFactory.Create(_ => { }));
        _world.InitMap(0, 2048, 2048);
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

    public void Dispose()
    {
        Item.OnTimerExpired = null;
        if (_world != null) _world.TimerFExpired = null;
    }

    private Item GroundItem(Point3D at, long overdueByMs = 1000)
    {
        var item = _world.CreateItem();
        item.BaseId = 0x0EED;
        _world.PlaceItem(item, at);
        item.SetTimeout(Environment.TickCount64 - overdueByMs);
        return item;
    }

    /// <summary>Count timer callbacks per item, so a test can say WHICH item fired
    /// rather than that something did.</summary>
    private Dictionary<uint, int> CountTimerFires()
    {
        var fires = new Dictionary<uint, int>();
        Item.OnTimerExpired = it =>
        {
            fires[it.Uid.Value] = fires.GetValueOrDefault(it.Uid.Value) + 1;
            return TriggerResult.Default;
        };
        return fires;
    }

    private static int Fired(Dictionary<uint, int> fires, Item item) =>
        fires.GetValueOrDefault(item.Uid.Value);

    /// <summary>Register an ITEMDEF for this test run (the loader's table is the
    /// engine's only source for def-level flags).</summary>
    private static void DefineItem(int baseId, Action<ItemDef> shape)
    {
        var def = new ItemDef(new ResourceId(ResType.ItemDef, baseId));
        shape(def);
        var table = (Dictionary<int, ItemDef>)typeof(SphereNet.Game.Definitions.DefinitionLoader)
            .GetField("_itemDefs", System.Reflection.BindingFlags.Static |
                                   System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null)!;
        table[baseId] = def;
    }

    // ------------------------------------------------------------------

    [Fact]
    public void AnArmedTimerNextToThePlayerRunsOnTheNextTick()
    {
        Setup();
        var fires = CountTimerFires();
        var item = GroundItem(Near);

        _world.OnTick();

        _out.WriteLine($"active sector: fired {Fired(fires, item)}x after one tick");
        Assert.Equal(1, Fired(fires, item));
    }

    [Fact]
    public void AnArmedTimerInASleepingSectorRunsOnTheNextTick()
    {
        Setup();
        var fires = CountTimerFires();
        var item = GroundItem(Far);

        _world.OnTick();

        // This used to wait for the three-minute maintenance sweep, because the
        // sector's item list WAS the mechanism and a sleeping sector has no tick.
        // Armed item timers are in the world's due queue now, so where the item lies
        // stopped mattering: the deadline is what is waited on.
        _out.WriteLine($"sleeping sector: fired {Fired(fires, item)}x after one tick");
        Assert.Equal(1, Fired(fires, item));
    }

    [Fact]
    public void TheMaintenanceSweepNoLongerRunsTimersAtAll()
    {
        Setup();
        var fires = CountTimerFires();
        var item = GroundItem(Far);

        // The sweep used to be how a remote item timer ran; it now prunes deleted
        // entries and carries the uid-recycling cadence, and nothing else. Leaving it
        // able to fire timers would put the polling back and blur the contract - an
        // item would run from its deadline, or from a sweep, whichever came first.
        _world.TickSleepingMaintenance(Environment.TickCount64 + 180_000);

        _out.WriteLine($"after a full sweep, with no world tick: fired {Fired(fires, item)}x");
        Assert.Equal(0, Fired(fires, item));

        _world.OnTick();
        Assert.Equal(1, Fired(fires, item));
    }

    [Fact]
    public void ATimerOnAnItemOffTheGroundIsExactWhereverItIs()
    {
        Setup();
        var fires = CountTimerFires();

        var pack = _world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        _world.PlaceItem(pack, Far);

        var contained = _world.CreateItem();
        contained.BaseId = 0x0EED;
        pack.AddItem(contained);
        contained.SetTimeout(Environment.TickCount64 - 1000);

        _world.OnTick();

        // Items that are not on the ground belong to no sector list, so they are
        // pumped from a world-level registry every tick. That registry does not ask
        // whether the sector is asleep — which is exactly the "exact-time timers run
        // in a due queue independent of sector sleep" shape, already in place for
        // this class of timer.
        _out.WriteLine($"contained item in a sleeping sector: fired {Fired(fires, contained)}x after one tick");
        Assert.Equal(1, Fired(fires, contained));
    }

    [Fact]
    public void TimerFIsExactInASleepingSector()
    {
        Setup();
        var fired = new List<string>();
        _world.TimerFExpired = (_, entry) => fired.Add(entry.FunctionName);

        var item = _world.CreateItem();
        item.BaseId = 0x0EED;
        _world.PlaceItem(item, Far);
        item.AddTimerF(0, "f_far_away", "");

        _world.OnTick();

        // TIMERF is held in a world-level, globally due-ordered list, so it is exact
        // no matter where the object sits.
        _out.WriteLine($"TIMERF in a sleeping sector: {fired.Count} call(s) after one tick");
        Assert.Single(fired);
    }

    [Fact]
    public void ACharacterInASleepingSectorDoesNotTickUntilThePlayerArrives()
    {
        Setup();
        var npc = _world.CreateCharacter();
        npc.BaseId = 0x000C;
        npc.NpcBrain = NpcBrainType.Monster;
        npc.MaxHits = 50; npc.Hits = 50;
        npc.TrySetProperty("REGENHITS", "0");   // regen every tick, so one tick shows
        npc.Food = 10;
        npc.Hits = 1;
        _world.PlaceCharacter(npc, Far);

        for (int i = 0; i < 20; i++)
            _world.OnTick();

        // The maintenance sweep processes items only: characters in a sleeping sector
        // do not tick at all, at any interval. This is the deliberate part of sector
        // sleeping — an idle world costs nothing because nobody is thinking — and it
        // is also the reason "timers fire on schedule" cannot be said unqualified.
        _out.WriteLine($"sleeping NPC after 20 world ticks: {npc.Hits} hp (started at 1)");
        Assert.Equal(1, npc.Hits);

        // Walking a player into range wakes the sector on the very next tick.
        _world.PlaceCharacter(_player, new Point3D(Far.X, Far.Y, 0, 0));
        _world.OnTick();

        _out.WriteLine($"after the player arrives: {npc.Hits} hp");
        Assert.True(npc.Hits > 1, "the NPC should tick once its sector wakes");
    }

    [Fact]
    public void AnOverdueTimerDoesNotWaitForAnybodyToArrive()
    {
        Setup();
        var fires = CountTimerFires();
        var item = GroundItem(Far, overdueByMs: 600_000);   // ten minutes past due

        _world.OnTick();

        // The deadline is absolute and the queue is world-level, so a timer that is
        // overdue runs at once and exactly once - no player has to walk into the
        // sector to collect it, and walking in later collects nothing extra.
        Assert.Equal(1, Fired(fires, item));

        _world.PlaceCharacter(_player, new Point3D(Far.X, Far.Y, 0, 0));
        _world.OnTick();

        _out.WriteLine($"ten-minute-overdue timer: fired {Fired(fires, item)}x in total");
        Assert.Equal(1, Fired(fires, item));
    }

    [Fact]
    public void ANoSleepSectorTicksWithNobodyNearby()
    {
        Setup();
        var fires = CountTimerFires();
        var item = GroundItem(Far);

        var sector = _world.GetSector(0, Far.X / Sector.SectorSize, Far.Y / Sector.SectorSize);
        Assert.NotNull(sector);
        sector!.Flags |= SectorFlag.NoSleep;

        _world.OnTick();

        // SECF_NoSleep is the per-sector escape hatch: a shard that needs a remote
        // area to keep running exactly can say so, and then the ordinary tick covers
        // it with no maintenance delay at all.
        _out.WriteLine($"NOSLEEP sector: fired {Fired(fires, item)}x after one tick");
        Assert.Equal(1, Fired(fires, item));
    }

    [Fact]
    public void AnItemThatDeclaresItNeverSleepsKeepsItsTimerExact()
    {
        Setup();
        var fires = CountTimerFires();

        // CAN=O_NOSLEEP on the ITEMDEF. Source-X reads the same flag in
        // CObjBase::_TickableStateOverride to keep the object in the ticking list
        // when its sector sleeps; SphereNet defined the flag and read it nowhere,
        // so this was the one thing a shard could not ask for.
        const int NoSleepId = 0x0EEE;
        DefineItem(NoSleepId, def => def.Can = CanFlags.O_NoSleep);

        var item = _world.CreateItem();
        item.BaseId = NoSleepId;
        _world.PlaceItem(item, Far);
        item.SetTimeout(Environment.TickCount64 - 1000);

        var ordinary = GroundItem(Far);   // same sleeping sector, no flag

        _world.OnTick();

        _out.WriteLine($"NOSLEEP item: fired {Fired(fires, item)}x; " +
                       $"ordinary item beside it: fired {Fired(fires, ordinary)}x");

        // Both fire now. CAN=O_NOSLEEP bought a ground item an exact timer when the
        // sector tick was the mechanism; with every armed timer in the world's due
        // queue there is nothing left for it to buy, and the honest thing is to say
        // so rather than keep a flag that appears to do something. It still means
        // what upstream means for the rest of an object's ticking, and SECF_NoSleep
        // still governs whether the SECTOR runs its characters.
        Assert.Equal(1, Fired(fires, item));
        Assert.Equal(1, Fired(fires, ordinary));
    }

    [Fact]
    public void ANeverSleepingItemStaysRegisteredAcrossRepeatedTimers()
    {
        Setup();
        var fires = CountTimerFires();

        const int NoSleepId = 0x0EEF;
        DefineItem(NoSleepId, def => def.Can = CanFlags.O_NoSleep);

        var item = _world.CreateItem();
        item.BaseId = NoSleepId;
        _world.PlaceItem(item, Far);

        // Three rounds of arm-and-fire. The registry entry is consumed when the
        // timer runs, so a re-arming @Timer has to put it back - the same path a
        // worn item uses. Getting this wrong would fire once and then go quiet,
        // which is the failure that is hardest to notice.
        for (int round = 0; round < 3; round++)
        {
            item.SetTimeout(Environment.TickCount64 - 1);
            _world.OnTick();
        }

        _out.WriteLine($"NOSLEEP item over three re-arms: fired {Fired(fires, item)}x");
        Assert.Equal(3, Fired(fires, item));
    }

    [Fact]
    public void ANeverSleepingItemSurvivesTheTicksBeforeItsDeadline()
    {
        Setup();
        var fires = CountTimerFires();

        const int NoSleepId = 0x0EF0;
        DefineItem(NoSleepId, def => def.Can = CanFlags.O_NoSleep);

        var item = _world.CreateItem();
        item.BaseId = NoSleepId;
        _world.PlaceItem(item, Far);
        long deadline = Environment.TickCount64 + 300;
        item.SetTimeout(deadline);

        // The pump retires an entry once it is no longer its business. A ground item
        // normally IS somebody else's business - its sector's - so the retire test
        // has to know about the flag as well as the registration does. Get only one
        // of the two right and a timer armed for the FUTURE is registered, retired on
        // the very next tick, and never fires at all: silent, and only for the items
        // a shard marked as the ones that must not be.
        for (int i = 0; i < 20; i++)
            _world.OnTick();

        Assert.Equal(0, Fired(fires, item));     // not due yet, and still on the books

        while (Environment.TickCount64 < deadline)
            System.Threading.Thread.Sleep(10);
        _world.OnTick();

        _out.WriteLine($"NOSLEEP item armed 300ms ahead: fired {Fired(fires, item)}x " +
                       $"after 20 early ticks");
        Assert.Equal(1, Fired(fires, item));
    }

    /// <summary>The timer contract in ARCHITECTURE.md quotes four numbers. They are
    /// the engine's own constants, so they have to be read from the engine and not
    /// from memory: the previous text said timers "fire on schedule with no drift",
    /// which was true of the deadlines and not of the callbacks.</summary>
    [Fact]
    public void TheDocumentedDelaysAreTheEnginesOwnNumbers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "docs")))
            root = root.Parent;
        // Reuses the suite's existing gate vocabulary: this test reads the engine's
        // own source files for the constants it pins.
        if (Gate.MissingValue(_out, "engine source", root)) return;

        string doc = File.ReadAllText(Path.Combine(root!.FullName, "docs", "ARCHITECTURE.md"));
        string world = File.ReadAllText(Path.Combine(root.FullName, "src",
            "SphereNet.Game", "World", "GameWorld.cs"));
        string tick = File.ReadAllText(Path.Combine(root.FullName, "src",
            "SphereNet.Server", "Program.Tick.cs"));

        var claims = new (string Doc, string Source, string Where)[]
        {
            ("next tick, wherever it is", "TickItemTimers(currentTime)", "GameWorld"),
            ("drained **256** per tick", "CollectDueDecay(now, 256", "Program.Tick"),
            ("audit every **60 s**", "DecayAuditIntervalMs = 60_000", "GameWorld"),
        };

        foreach (var (docText, source, where) in claims)
        {
            Assert.True(doc.Contains(docText),
                $"ARCHITECTURE.md no longer states: {docText}");
            string haystack = where == "GameWorld" ? world : tick;
            Assert.True(haystack.Contains(source),
                $"{where} no longer contains '{source}' — the documented delay " +
                $"'{docText}' is now a claim about nothing");
        }

        _out.WriteLine($"{claims.Length} documented delays still match the engine");
    }

    [Fact]
    public void DecayIsDueOrderedAndBoundedPerTickWhereverTheItemIs()
    {
        Setup();
        var due = new List<Item>();
        for (int i = 0; i < 300; i++)
        {
            var item = _world.CreateItem();
            item.BaseId = 0x0EED;
            _world.PlaceItem(item, new Point3D((short)(1200 + i % 40), (short)(1200 + i / 40), 0, 0));
            item.SetDecayAt(Environment.TickCount64 - 1000);
            due.Add(item);
        }

        var buffer = new List<Item>();
        _world.CollectDueDecay(Environment.TickCount64, 256, buffer);

        // Decay never waited for the sleeping-sector sweep; it used to have its own
        // pass every five seconds, which walked EVERY ground item in the world to
        // find the expired ones (11 ms at 300,000 items, to find nothing). It is a
        // due-ordered queue now, so the check runs every tick and costs what is due.
        // The cap still bounds one tick's work - the rest are simply the front of the
        // next tick rather than the back of another full scan.
        _out.WriteLine($"{due.Count} expired items, one tick collected {buffer.Count}, " +
                       $"{_world.DecayQueueCount} still queued");
        Assert.Equal(256, buffer.Count);

        var rest = new List<Item>();
        _world.CollectDueDecay(Environment.TickCount64, 256, rest);
        Assert.Equal(44, rest.Count);
    }
}
