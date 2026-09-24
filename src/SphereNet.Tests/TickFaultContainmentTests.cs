using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A fault in one object's tick is reported and contained to that object
/// (upstream ticks every object inside its own exception block). An item @Timer that
/// threw used to leave the due-timer pass, and the timers already taken off the queue
/// behind it were lost for good.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TickFaultContainmentTests
{
    [Fact]
    public void OneFailingTimerDoesNotCostTheOthersTheirs()
    {
        Sector.SleepDelayMs = 0;
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        Item.ResolveWorld = () => world;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;

        var fired = new List<string>();
        var reported = new List<string>();
        TickFaults.Log = (message, _) => reported.Add(message);
        Item.OnTimerExpired = item =>
        {
            fired.Add(item.Name);
            if (item.Name == "bad") throw new InvalidOperationException("@Timer threw");
            return TriggerResult.True;
        };

        var items = new List<Item>();
        foreach (string name in new[] { "a", "bad", "b", "c" })
        {
            var it = world.CreateItem();
            it.Name = name;
            it.BaseId = 0x0EED;
            world.PlaceItem(it, new Point3D(100, 100, 0, 0));
            it.SetTimeout(Environment.TickCount64 - 1000);
            items.Add(it);
        }

        world.OnTick();

        Assert.Equal(4, fired.Count);
        Assert.Contains("a", fired);
        Assert.Contains("b", fired);
        Assert.Contains("c", fired);
        Assert.Single(reported);
        Assert.Contains("bad", reported[0]);
        Assert.False(items[1].IsDeleted);
    }
}
