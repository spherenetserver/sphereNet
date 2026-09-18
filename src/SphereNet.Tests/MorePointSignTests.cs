using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// MOREP keeps a negative coordinate, and MOREX reads it back negative.
///
/// Upstream stores the point's x and y as signed shorts and writes them back with
/// FormatVal (CItem.cpp:3456/2929), so -1 survives the round trip. The packs use -1 as
/// "unset" rather than as a place: a wand ships with MOREP=-1,-1 and its @ClientTooltip
/// asks IF (&lt;MOREX&gt; != -1) before printing a charge count, and a potion keg asks
/// IF (0 &gt;= &lt;MOREX&gt;) before refusing an empty bottle.
///
/// If the coordinate is stored unsigned, -1 reads back as 65535: both tests then answer
/// the opposite of what they were written to answer.
/// </summary>
public sealed class MorePointSignTests
{
    private static Item Fresh()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var it = world.CreateItem();
        world.PlaceItem(it, new SphereNet.Core.Types.Point3D(10, 20, 0, 0));
        return it;
    }

    private static string Read(Item it, string key)
    {
        it.TryGetProperty(key, out string v);
        return v;
    }

    /// <summary>The form the packs write.</summary>
    [Fact]
    public void MorepKeepsMinusOne()
    {
        var it = Fresh();
        Assert.True(it.TrySetProperty("MOREP", "-1,-1"));
        Assert.Equal("-1", Read(it, "MOREX"));
        Assert.Equal("-1", Read(it, "MOREY"));
    }

    /// <summary>And the component write on its own (GetArgSVal, CItem.cpp:3473).</summary>
    [Fact]
    public void MorexTakesANegativeDirectly()
    {
        var it = Fresh();
        Assert.True(it.TrySetProperty("MOREX", "-1"));
        Assert.Equal("-1", Read(it, "MOREX"));
    }

    /// <summary>The guard the wand's tooltip is built on: unset must not look set.</summary>
    [Fact]
    public void TheUnsetGuardAnswersUnset()
    {
        var it = Fresh();
        it.TrySetProperty("MOREP", "-1,-1");
        Assert.True(new SphereNet.Scripting.Expressions.ExpressionParser()
            .EvaluateConditional($"{Read(it, "MOREX")} == -1"));
    }

    /// <summary>A positive point still round-trips, and MOREP is the item's own store,
    /// not its position.</summary>
    [Fact]
    public void APositivePointStillRoundTrips()
    {
        var it = Fresh();
        it.TrySetProperty("MOREP", "1234,2345,5");
        Assert.Equal("1234", Read(it, "MOREX"));
        Assert.Equal("2345", Read(it, "MOREY"));
        Assert.Equal("5", Read(it, "MOREZ"));
        Assert.Equal(10, it.X);   // position untouched
    }
}
