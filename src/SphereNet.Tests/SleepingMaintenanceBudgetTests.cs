using SphereNet.Core.Types;
using SphereNet.Game.World.Sectors;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Batch 6 (E5): sleeping-sector maintenance must be spread across ticks with a per-tick
/// budget and a resume cursor instead of processing the whole world in one tick every
/// interval. These prove the sweep (a) respects the maintenance-calls budget per tick and
/// (b) still visits every eligible sleeping sector exactly once before completing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SleepingMaintenanceBudgetTests
{
    private const int EligibleSectors = 5;

    private static SphereNet.Game.World.GameWorld WorldWithSleepingItems()
    {
        var world = TestHarness.CreateWorld();
        // One item per distinct sector (64 tiles apart in X). No online players, so no
        // sector is active — every one of these is a sleeping, item-bearing sector.
        for (int i = 0; i < EligibleSectors; i++)
        {
            var it = world.CreateItem();
            Assert.True(world.PlaceItem(it, new Point3D((short)(50 + i * Sector.SectorSize), 50, 0, 0)));
        }
        return world;
    }

    [Fact]
    public void Sweep_SpreadsAcrossTicks_AndVisitsEveryEligibleSectorOnce()
    {
        var world = WorldWithSleepingItems();
        world.MaintenanceCallsPerTick = 1;              // force spreading: <=1 maintenance/tick
        world.MaintenanceExaminePerTick = int.MaxValue; // isolate the calls budget

        const long now = 10_000_000; // well past the 3-minute interval → arms immediately
        int ticks = 0;
        world.TickSleepingMaintenance(now); // arm + first slice
        ticks++;
        while (world.MaintenanceSweepActive && ticks < 10_000)
        {
            world.TickSleepingMaintenance(now); // same timestamp → does not re-arm mid-sweep
            ticks++;
        }

        Assert.False(world.MaintenanceSweepActive);
        // Every eligible sector maintained exactly once across the whole sweep.
        Assert.Equal(EligibleSectors, world.MaintenanceCallsThisSweep);
        // With a budget of 1 call/tick and 5 eligible sectors, it cannot have finished in
        // a single tick — proving the work was spread rather than done all at once.
        Assert.True(ticks >= EligibleSectors);
    }

    [Fact]
    public void Sweep_CompletesInOneTick_WhenBudgetIsAmple()
    {
        var world = WorldWithSleepingItems();
        world.MaintenanceCallsPerTick = 1000;
        world.MaintenanceExaminePerTick = int.MaxValue;

        const long now = 10_000_000;
        world.TickSleepingMaintenance(now);

        Assert.False(world.MaintenanceSweepActive);
        Assert.Equal(EligibleSectors, world.MaintenanceCallsThisSweep);
    }
}
