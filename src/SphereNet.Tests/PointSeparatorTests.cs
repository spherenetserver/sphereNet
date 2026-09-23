using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A point separates with comma, space or tab (CPointBase::Read, " ,\t").
/// The worldgen decoration writes NEW.P=6082 1450 5 and, for Trammel, a fourth map
/// field; the P setter split on commas only, so every decoration item stayed at
/// 0,0,0,0.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PointSeparatorTests
{
    [Theory]
    [InlineData("6082 1450 5", 6082, 1450, 5, 0)]
    [InlineData("6082 1450 5 1", 6082, 1450, 5, 1)]
    [InlineData("6082\t1450\t-5", 6082, 1450, -5, 0)]
    [InlineData("6082, 1450, 5, 1", 6082, 1450, 5, 1)]
    [InlineData("6082,1450,5,1", 6082, 1450, 5, 1)]
    public void TryParseAcceptsCommaSpaceAndTab(string text, int x, int y, int z, int map)
    {
        Assert.True(Point3D.TryParse(text, out var p));
        Assert.Equal(new Point3D((short)x, (short)y, (sbyte)z, (byte)map), p);
    }

    [Fact]
    public void AnItemTakesASpaceSeparatedP()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 7168, 4096);
        world.InitMap(1, 7168, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var item = world.CreateItem();

        Assert.True(item.TrySetProperty("P", "6082 1450 5"));
        Assert.Equal(new Point3D(6082, 1450, 5, 0), item.Position);

        Assert.True(item.TrySetProperty("P", "6082 1449 5 1"));
        Assert.Equal(new Point3D(6082, 1449, 5, 1), item.Position);
    }
}
