using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// An armed decay deadline reaches the queue that makes it happen.
///
/// The deadline and the registration are set in one place, so they cannot normally
/// disagree - except that the registration goes through the ambient world hook, and a
/// caller running before that hook is wired sets the deadline and registers nothing. The
/// item then sits armed and unqueued until the once-a-minute audit sweeps it up, which is
/// how a shard reported a dozen ground items whose "deadlines were set without reaching
/// the registration door" on the tick it started.
///
/// The world's own repair pass was one such caller: it arms decay from inside GameWorld
/// but reached for the global hook to register it. It registers on itself now, and a
/// deadline armed with no world at all says so at the moment it happens rather than a
/// minute later with no caller left to name.
/// </summary>
public sealed class DecayRegistrationTests
{
    private static SphereNet.Game.World.GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Grounded(SphereNet.Game.World.GameWorld world)
    {
        var it = world.CreateItem();
        it.BaseId = 0x09D1;
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        return it;
    }

    /// <summary>The ordinary case: arming registers.</summary>
    [Fact]
    public void ArmingRegistersTheDeadline()
    {
        var world = World();
        int before = world.DecayQueueCount;

        Grounded(world).SetDecayAt(Environment.TickCount64 + 60_000);

        Assert.Equal(before + 1, world.DecayQueueCount);
    }

    /// <summary>The world's repair pass arms decay on a flagged ground item, and that
    /// arming has to be registered too - it used to depend on a hook that may not be
    /// wired when the pass runs at boot.</summary>
    [Fact]
    public void TheRepairPassRegistersWhatItArms()
    {
        var world = World();
        var it = Grounded(world);
        it.SetAttr(ObjAttributes.Decay);
        Assert.Equal(0, it.DecayTime);

        // The hook is deliberately unavailable, which is the boot-time condition.
        Item.ResolveWorld = null;
        int before = world.DecayQueueCount;

        world.GarbageCollection();

        Assert.True(it.DecayTime > 0, "the repair pass arms a flagged ground item");
        Assert.Equal(before + 1, world.DecayQueueCount);
    }

    /// <summary>And when there is genuinely no world, the loss is reported at the moment
    /// it happens rather than left for the audit a minute later.</summary>
    [Fact]
    public void ALostRegistrationIsReportedImmediately()
    {
        var world = World();
        var it = Grounded(world);

        Item.ResetDecayRegistrationWarning();
        Item? reported = null;
        Item.OnDecayRegistrationLost = lost => reported = lost;
        Item.ResolveWorld = null;

        it.SetDecayAt(Environment.TickCount64 + 60_000);

        Assert.Same(it, reported);
        Assert.True(it.DecayTime > 0, "the deadline is still set - only the queue missed it");
        Item.OnDecayRegistrationLost = null;
    }

    /// <summary>The report is once per process: a load that arms thousands must not bury
    /// the line that matters.</summary>
    [Fact]
    public void TheReportDoesNotRepeat()
    {
        var world = World();
        var a = Grounded(world);
        var b = Grounded(world);

        Item.ResetDecayRegistrationWarning();
        int reports = 0;
        Item.OnDecayRegistrationLost = _ => reports++;
        Item.ResolveWorld = null;

        a.SetDecayAt(Environment.TickCount64 + 60_000);
        b.SetDecayAt(Environment.TickCount64 + 60_000);

        Assert.Equal(1, reports);
        Item.OnDecayRegistrationLost = null;
    }

    /// <summary>Clearing a deadline reports nothing - there is nothing to register.</summary>
    [Fact]
    public void ClearingReportsNothing()
    {
        var world = World();
        var it = Grounded(world);

        Item.ResetDecayRegistrationWarning();
        int reports = 0;
        Item.OnDecayRegistrationLost = _ => reports++;
        Item.ResolveWorld = null;

        it.ClearDecay();

        Assert.Equal(0, reports);
        Item.OnDecayRegistrationLost = null;
    }
}
