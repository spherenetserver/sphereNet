using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 266 — region-jail machine. Source-X locates jail cells from data-driven
/// AREADEF regions named "jail" / "jailN" (GetRegionPoint) rather than hardcoded
/// coordinates. SphereNet gains region-driven cell lookup (FindRegionByName /
/// GetJailPoint) while keeping its Freeze confinement + timed auto-release (which
/// already exceed Source-X, whose jail has no timer).
/// </summary>
public sealed class SourceXWave266Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Region JailRegion(string name, int x, int y, int z)
    {
        var r = new Region { Name = name, MapIndex = 0, P = new Point3D((short)x, (short)y, (sbyte)z, 0) };
        r.AddRect((short)(x - 5), (short)(y - 5), (short)(x + 5), (short)(y + 5));
        return r;
    }

    [Fact]
    public void GetJailPoint_ResolvesJailRegionAnchor()
    {
        var world = CreateWorld();
        world.AddRegion(JailRegion("jail", 2000, 2000, 5));

        var p = world.GetJailPoint(0);
        Assert.Equal(2000, p.X);
        Assert.Equal(2000, p.Y);
        Assert.Equal(5, p.Z);
    }

    [Fact]
    public void GetJailPoint_ResolvesNumberedCell_AndFallsBackToBaseJail()
    {
        var world = CreateWorld();
        world.AddRegion(JailRegion("jail", 2000, 2000, 5));
        world.AddRegion(JailRegion("jail2", 2100, 2100, 5));

        // Numbered cell resolves to its own region.
        var cell2 = world.GetJailPoint(2);
        Assert.Equal(2100, cell2.X);
        Assert.Equal(2100, cell2.Y);

        // An undefined numbered cell falls back to the base "jail" region.
        var cell3 = world.GetJailPoint(3);
        Assert.Equal(2000, cell3.X);
        Assert.Equal(2000, cell3.Y);
    }

    [Fact]
    public void GetJailPoint_NoJailRegion_FallsBackToLegacyCoords()
    {
        var world = CreateWorld();
        // No jail AREADEF loaded — the legacy Britain jail coords keep the feature working.
        var p = world.GetJailPoint(0);
        Assert.Equal(1476, p.X);
        Assert.Equal(1604, p.Y);
        Assert.Equal(20, p.Z);
    }

    [Fact]
    public void FindRegionByName_MatchesNameAndDefName_CaseInsensitive()
    {
        var world = CreateWorld();
        var byName = new Region { Name = "jail", MapIndex = 0 };
        byName.AddRect(90, 90, 110, 110);
        world.AddRegion(byName);

        var byDef = new Region { Name = "The Cell", DefName = "a_jail_deep", MapIndex = 0 };
        byDef.AddRect(200, 200, 210, 210);
        world.AddRegion(byDef);

        Assert.Same(byName, world.FindRegionByName("JAIL"));       // by NAME, case-insensitive
        Assert.Same(byDef, world.FindRegionByName("a_jail_deep")); // by DEFNAME
        Assert.Null(world.FindRegionByName("nonexistent"));
    }
}
