using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A save taken from inside a TIMERF callback (port plan İŞ-63 / PLAN-205).
///
/// PLAN-205 lists five TIMERF situations, and four are already covered: ordering
/// across objects, ordering within one due time, re-arming from inside a callback,
/// cancellation, and a source with no client. The fifth is this one - a callback
/// that saves the world while the due list is still being walked.
///
/// It is the one with a silent failure mode. The dispatcher could reasonably take
/// every due job off its object before running any of them, and that would be
/// invisible until a script saved from inside a callback: the jobs not yet run
/// would already be off their objects, so they would not reach the file, and a
/// restart would lose work a shard had scheduled. The dispatcher instead removes
/// one job immediately before running it, and the comment at GameWorld.TickTimerF
/// says so - but nothing measured it.
/// </summary>
public sealed class DelayedCallSaveDuringCallbackTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public DelayedCallSaveDuringCallbackTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_tfs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Timed(GameWorld world, short x, string name)
    {
        var it = world.CreateItem();
        it.BaseId = 0x0EED;
        it.Name = name;
        world.PlaceItem(it, new Point3D(x, 100, 0, 0));
        return it;
    }

    private void Save(GameWorld world) =>
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, _dir);

    /// <summary>Every TIMERF line the save wrote, across all its files.</summary>
    private string[] SavedTimerFLines() =>
        Directory.EnumerateFiles(_dir, "*.scp")
            .SelectMany(File.ReadAllLines)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("TIMERF", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    [Fact]
    public void ASaveTakenInsideACallbackStillHoldsTheJobsThatHaveNotRun()
    {
        var world = NewWorld();
        long now = Environment.TickCount64;

        var first = Timed(world, 100, "first");
        var second = Timed(world, 101, "second");
        var third = Timed(world, 102, "third");

        // All three due at once, in a known order.
        first.AddTimerF(0, "f_first", "");
        second.AddTimerF(0, "f_second", "");
        third.AddTimerF(0, "f_third", "");

        int ran = 0;
        world.TimerFExpired = (obj, entry) =>
        {
            ran++;
            // The first callback takes a save, exactly as a script doing SERV.SAVE
            // from a delayed function would.
            if (ran == 1) Save(world);
        };

        TestHarness.PumpTimerF(world, now);

        var lines = SavedTimerFLines();
        foreach (string l in lines) _out.WriteLine(l);
        _out.WriteLine($"callbacks run: {ran}, TIMERF lines in the save: {lines.Length}");

        // The save happened while the first job was running, so the two that had not
        // run yet still belonged to their objects and had to reach the file. Draining
        // the whole due list up front would have written none of them, and a restart
        // would silently lose scheduled work.
        Assert.Equal(3, ran);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.Contains("f_second", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("f_third", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheJobThatIsRunningIsNotWrittenTwice()
    {
        var world = NewWorld();
        long now = Environment.TickCount64;

        var only = Timed(world, 100, "only");
        only.AddTimerF(0, "f_only", "");

        world.TimerFExpired = (obj, entry) => Save(world);
        TestHarness.PumpTimerF(world, now);

        var lines = SavedTimerFLines();
        _out.WriteLine($"TIMERF lines in the save: {lines.Length}");

        // The other half of the same rule: a job is taken off its object immediately
        // BEFORE it runs, so a save from inside its own callback must not write it
        // again - restoring it would run the same work twice after a restart.
        Assert.Empty(lines);
    }

    [Fact]
    public void AJobAddedByTheCallbackReachesTheSameSave()
    {
        var world = NewWorld();
        long now = Environment.TickCount64;

        var obj = Timed(world, 100, "obj");
        obj.AddTimerF(0, "f_start", "");

        world.TimerFExpired = (o, e) =>
        {
            // Re-arming from inside a callback is covered elsewhere; what matters here
            // is that a save taken afterwards in the same callback sees the new job,
            // since by then it really does belong to the object.
            o.AddTimerF(60_000, "f_again", "");
            Save(world);
        };
        TestHarness.PumpTimerF(world, now);

        var lines = SavedTimerFLines();
        foreach (string l in lines) _out.WriteLine(l);

        Assert.Single(lines);
        Assert.Contains("f_again", lines[0], StringComparison.OrdinalIgnoreCase);
    }
}
