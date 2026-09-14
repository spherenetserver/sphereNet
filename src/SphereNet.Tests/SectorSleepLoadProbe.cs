using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
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
/// What a full shard costs per world tick, and what a sector-sleep grace period
/// would add to it.
///
/// Sector sleeping decides how much of the world ticks. Source-X keeps a sector
/// awake for SECTORSLEEP (10 minutes by default) after its last client leaves;
/// SphereNet sleeps the moment a player is three sectors away. Adopting the grace
/// makes the world more correct - no three-minute maintenance delay anywhere a
/// player has recently been - and it costs CPU, because an awake sector here ticks
/// every object it holds rather than only the objects whose timer is due.
///
/// This probe measures that cost instead of estimating it: a Britannia-sized map
/// with 500 online players, 50,000 NPCs and 300,000 ground items, ticked standing
/// still and then walking.
///
/// It is OFF by default (SPHERENET_LOADPROBE=1 to run): building the world takes
/// seconds and hundreds of megabytes, and the suite has to stay fast. It measures
/// the WORLD tick only - not the network loop, not NPC AI scheduling - because
/// that is the part the sleep policy governs.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SectorSleepLoadProbe
{
    private readonly ITestOutputHelper _out;
    public SectorSleepLoadProbe(ITestOutputHelper output) => _out = output;

    private const int MapWidth = 6144;      // Britannia
    private const int MapHeight = 4096;     // 96 x 64 = 6144 sectors

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : fallback;

    [Fact]
    public void FiveHundredPlayersFiftyThousandNpcsThreeHundredThousandItems()
    {
        if (Environment.GetEnvironmentVariable("SPHERENET_LOADPROBE") != "1")
        {
            _out.WriteLine("SKIPPED: set SPHERENET_LOADPROBE=1 to run this probe.");
            return;
        }

        int players = Env("SPHERENET_LOADPROBE_PLAYERS", 500);
        int npcs = Env("SPHERENET_LOADPROBE_NPCS", 50_000);
        int items = Env("SPHERENET_LOADPROBE_ITEMS", 300_000);
        int ticks = Env("SPHERENET_LOADPROBE_TICKS", 200);
        int walkTicks = Env("SPHERENET_LOADPROBE_WALKTICKS", 600);

        // The sleep grace under test. 1 ms reproduces the old policy (a sector sleeps
        // as soon as the window moves off it) in the same process and the same
        // phase order, so the two runs are comparable.
        Sector.SleepDelayMs = Env("SPHERENET_LOADPROBE_GRACE_MS", (int)Sector.SleepDelayMs);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, MapWidth, MapHeight);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        long memBefore = GC.GetTotalMemory(true);
        var build = Stopwatch.StartNew();
        var playerList = Build(world, players, npcs, items);
        build.Stop();
        long memAfter = GC.GetTotalMemory(true);

        _out.WriteLine($"world: {players} players, {npcs} npcs, {items} items on " +
                       $"{MapWidth}x{MapHeight} ({MapWidth / Sector.SectorSize}x{MapHeight / Sector.SectorSize} = " +
                       $"{(MapWidth / Sector.SectorSize) * (MapHeight / Sector.SectorSize)} sectors)");
        _out.WriteLine($"build: {build.ElapsedMilliseconds} ms, " +
                       $"heap {(memAfter - memBefore) / (1024 * 1024)} MB " +
                       $"({(memAfter - memBefore) / Math.Max(1, players + npcs + items)} B/object)");

        // What the grace actually buys is TRAIL: the sectors a traveller has been
        // in and left behind, still ticking. The probe runs its ticks back to back,
        // so a hundred ticks is a second of wall clock rather than ten seconds of
        // game time - which means the grace in milliseconds is the wrong dial to
        // turn here. Trail length is the right one, and it converts:
        //
        //     mounted speed 8 tiles/s -> 7.5 sectors of trail per minute of grace
        //     SECTORSLEEP=1  (the shipped ini)  ->  ~8 sectors per player
        //     SECTORSLEEP=10 (Source-X default) ->  ~75 sectors per player
        //
        // So: walk the players that far with the grace held open, then measure the
        // world standing still. That is the steady-state cost of the trail.
        Cluster(playerList, world, Env("SPHERENET_LOADPROBE_CLUSTERS", 10));
        Report("towns/no-trail", Measure(world, ticks, walk: null), world);

        Walk(world, playerList, 60);          // ~8 sectors each: SECTORSLEEP=1
        Report("towns/trail-1min", Measure(world, ticks, walk: null), world);

        Walk(world, playerList, 540);         // ~75 sectors each: SECTORSLEEP=10
        Report("towns/trail-10min", Measure(world, ticks, walk: null), world);

        // And the worst case for comparison: everybody scattered, which claims more
        // window than the map has sectors whatever the grace is.
        Spread(playerList, world);
        Report("spread/no-trail", Measure(world, ticks, walk: null), world);

        _out.WriteLine($"sleep grace: {Sector.SleepDelayMs} ms, " +
                       $"policy consulted by the tick: {GameWorld.SleepPolicyActive}");
    }

    // ------------------------------------------------------------------

    private static List<Character> Build(GameWorld world, int players, int npcs, int items)
    {
        var rng = new Random(20260914);
        var playerList = new List<Character>(players);

        // Players spread over the map rather than clustered in towns: it is the
        // expensive shape, and the one that says whether 500 is survivable at all.
        for (int i = 0; i < players; i++)
        {
            var p = world.CreateCharacter();
            p.IsPlayer = true;
            p.IsOnline = true;
            p.MaxHits = 100; p.Hits = 100;
            p.Name = "p" + i;
            world.PlaceCharacter(p, Spread(rng, i, players));
            world.AddOnlinePlayer(p);
            playerList.Add(p);
        }

        for (int i = 0; i < npcs; i++)
        {
            var npc = world.CreateCharacter();
            npc.BaseId = 0x000C;
            npc.MaxHits = 50; npc.Hits = 50;
            world.PlaceCharacter(npc, Random(rng));
        }

        for (int i = 0; i < items; i++)
        {
            var it = world.CreateItem();
            it.BaseId = 0x0EED;
            world.PlaceItem(it, Random(rng));
        }

        return playerList;
    }

    /// <summary>Put the players into a few towns - the shape a live shard has.</summary>
    private static void Cluster(List<Character> players, GameWorld world, int towns)
    {
        var rng = new Random(4242);
        var centres = new List<Point3D>();
        for (int i = 0; i < towns; i++)
            centres.Add(new Point3D((short)(400 + i * (MapWidth - 800) / Math.Max(1, towns)),
                                    (short)(400 + (i % 3) * 900), 0, 0));

        for (int i = 0; i < players.Count; i++)
        {
            var c = centres[i % centres.Count];
            var pos = new Point3D((short)Math.Clamp(c.X + rng.Next(-40, 40), 1, MapWidth - 2),
                                  (short)Math.Clamp(c.Y + rng.Next(-40, 40), 1, MapHeight - 2), 0, 0);
            world.MoveCharacter(players[i], pos, fireRegionEvents: false);
        }
    }

    /// <summary>Walk every player east for N ticks at mounted speed, laying trail.</summary>
    private static void Walk(GameWorld world, List<Character> players, int ticks)
    {
        for (int t = 0; t < ticks; t++)
        {
            foreach (var p in players)
            {
                short nx = (short)(p.X + 8);
                if (nx >= MapWidth - 2) nx = 16;
                world.MoveCharacter(p, new Point3D(nx, p.Y, 0, 0), fireRegionEvents: false);
            }
            world.OnTick();
        }
    }

    /// <summary>Scatter the players evenly over the map - the worst case.</summary>
    private static void Spread(List<Character> players, GameWorld world)
    {
        var rng = new Random(7);
        for (int i = 0; i < players.Count; i++)
            world.MoveCharacter(players[i], Spread(rng, i, players.Count), fireRegionEvents: false);
    }

    private static Point3D Spread(Random rng, int i, int total)
    {
        int cols = (int)Math.Ceiling(Math.Sqrt(total));
        int cell = i % cols, row = i / cols;
        short x = (short)Math.Min(MapWidth - 2, 16 + cell * (MapWidth / Math.Max(1, cols)));
        short y = (short)Math.Min(MapHeight - 2, 16 + row * (MapHeight / Math.Max(1, cols)));
        return new Point3D(x, y, 0, 0);
    }

    private static Point3D Random(Random rng) =>
        new((short)rng.Next(1, MapWidth - 1), (short)rng.Next(1, MapHeight - 1), 0, 0);

    private sealed record Phase(double[] TickMs, int AwakeSectors, int AwakeChars, int AwakeItems);

    private static Phase Measure(GameWorld world, int ticks, List<Character>? walk)
    {
        var samples = new double[ticks];
        var sw = new Stopwatch();
        for (int t = 0; t < ticks; t++)
        {
            if (walk != null)
            {
                foreach (var p in walk)
                {
                    short nx = (short)(p.X + 8);
                    if (nx >= MapWidth - 2) nx = 16;
                    world.MoveCharacter(p, new Point3D(nx, p.Y, 0, 0), fireRegionEvents: false);
                }
            }

            sw.Restart();
            world.OnTick();
            sw.Stop();
            samples[t] = sw.Elapsed.TotalMilliseconds;
        }

        int chars = 0, items = 0;
        foreach (var s in world.ActiveSectorsForProbe)
        {
            chars += s.CharacterCount;
            items += s.ItemCount;
        }
        return new Phase(samples, world.ActiveSectorsForProbe.Count, chars, items);
    }

    private void Report(string name, Phase p, GameWorld world)
    {
        var sorted = p.TickMs.OrderBy(x => x).ToArray();
        double p50 = sorted[sorted.Length / 2];
        double p95 = sorted[(int)(sorted.Length * 0.95)];
        double max = sorted[^1];
        double mean = p.TickMs.Average();

        _out.WriteLine(
            $"{name,-9} ticks={p.TickMs.Length,4}  p50={p50,7:F2} ms  p95={p95,7:F2} ms  " +
            $"max={max,7:F2} ms  mean={mean,7:F2} ms  awake sectors={p.AwakeSectors,5}  " +
            $"chars={p.AwakeChars,6}  items={p.AwakeItems,7}");

        // A 100 ms server tick is the budget (TICKS_PER_SEC=10). Anything past that
        // and the shard is behind before the network loop has run.
        _out.WriteLine($"{name,-9} budget: p95 is {p95 / 100.0 * 100:F1}% of the 100 ms tick");
    }
}
