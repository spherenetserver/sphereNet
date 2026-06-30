using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

// Crafting/gathering parity (wiki/11.txt remainder):
//  - weight-based CanCarry so a gathered/crafted item bounces to the ground when
//    it would overload the actor;
//  - partial resource-node regen (a vein slowly refills over time, not only after
//    full depletion).
public class CraftGatherRemainingTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void CanCarry_GatesByWeight()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 10;
        Assert.Equal(75, ch.MaxWeight); // Str*7/2 + 40

        // Default item weight is 1 tenth/unit in-test, so Amount scales the weight.
        var light = world.CreateItem();
        light.Amount = 100; // 10 stones — fits the 75-stone cap
        Assert.True(ch.CanCarry(light));

        var heavy = world.CreateItem();
        heavy.Amount = 800; // 80 stones — over the cap
        Assert.False(ch.CanCarry(heavy));
    }

    [Fact]
    public void RegenMarker_PartiallyRefillsPoolOverTime_CappedAtMax()
    {
        var world = CreateWorld();
        var resDef = new RegionResourceDef(default) { Regen = 10 }; // 10s to fully refill

        long now = Environment.TickCount64;

        // max 5, regen 10s -> 2s per unit. Depleted to 1, last gather 4s ago -> +2.
        var marker = world.CreateItem();
        marker.SetTag("RES_POOL", "1");
        marker.SetTag("RES_MAX", "5");
        marker.SetTag("RES_LAST", (now - 4000).ToString());
        GatheringEngine.RegenMarker(marker, resDef, now);
        Assert.Equal(3, GatheringEngine.GetPool(marker)); // 1 + 2

        // 12s elapsed -> +6 units, but capped at the original 5.
        var marker2 = world.CreateItem();
        marker2.SetTag("RES_POOL", "1");
        marker2.SetTag("RES_MAX", "5");
        marker2.SetTag("RES_LAST", (now - 12000).ToString());
        GatheringEngine.RegenMarker(marker2, resDef, now);
        Assert.Equal(5, GatheringEngine.GetPool(marker2)); // capped

        // A full node never over-fills.
        var full = world.CreateItem();
        full.SetTag("RES_POOL", "5");
        full.SetTag("RES_MAX", "5");
        full.SetTag("RES_LAST", (now - 60000).ToString());
        GatheringEngine.RegenMarker(full, resDef, now);
        Assert.Equal(5, GatheringEngine.GetPool(full));
    }
}
