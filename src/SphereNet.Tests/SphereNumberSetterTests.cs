using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// A numeric key accepts a Sphere number, not just a decimal one.
///
/// Upstream reads every one of these through the expression evaluator - GetArgVal /
/// GetArgWVal - so a leading zero means hex, exactly as it does everywhere else in the
/// language. Several setters here used a plain decimal TryParse instead, and a value
/// they could not read was DISCARDED: the assignment returned true and changed nothing.
///
/// That is not a corner case. &lt;DEF.name&gt; answers in hex - a DEF of 65000 reads
/// back as 0FDE8 - so a script doing the ordinary thing
///
///     REF1.MAXAMOUNT=&lt;DEF.sn_player_gold_amount&gt;
///     REF1.AMOUNT=&lt;DEF.sn_player_gold_amount&gt;
///
/// set neither, and the pile stayed at the one coin it was created with.
/// </summary>
public sealed class SphereNumberSetterTests
{
    private static Item Fresh()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var it = world.CreateItem();
        it.BaseId = 0x0EED;
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        return it;
    }

    private static string Read(Item it, string key)
    {
        it.TryGetProperty(key, out string v);
        return v;
    }

    /// <summary>The reported case, end to end: the value a DEF hands back.</summary>
    [Fact]
    public void AmountTakesTheHexADefHandsBack()
    {
        var it = Fresh();
        Assert.True(it.TrySetProperty("AMOUNT", "0FDE8"));   // 65000
        Assert.Equal("65000", Read(it, "AMOUNT"));
    }

    /// <summary>MAXAMOUNT has to take it too, or the cap stays at the global default
    /// and the amount below it is clamped away.</summary>
    [Fact]
    public void MaxAmountTakesItToo()
    {
        var it = Fresh();
        Assert.True(it.TrySetProperty("MAXAMOUNT", "0FDE8"));
        Assert.True(it.TrySetProperty("AMOUNT", "0FDE8"));
        Assert.Equal("65000", Read(it, "AMOUNT"));
    }

    /// <summary>Decimal still means decimal - the leading zero is what marks hex.</summary>
    [Theory]
    [InlineData("AMOUNT", "65000", "65000")]
    [InlineData("AMOUNT", "0FDE8", "65000")]
    [InlineData("AMOUNT", "0x FDE8", "65000")]
    public void DecimalAndHexBothRead(string key, string written, string expected)
    {
        var it = Fresh();
        it.TrySetProperty(key, written.Replace(" ", ""));
        Assert.Equal(expected, Read(it, key));
    }

    /// <summary>The rest of the family that reads a plain number upstream.</summary>
    [Theory]
    [InlineData("MOREX", "07D0", "2000")]   // MOREX is a short, as upstream's m_moreP.m_x is
    [InlineData("MOREY", "064", "100")]
    [InlineData("PRICE", "0C8", "200")]
    [InlineData("QUALITY", "032", "50")]
    [InlineData("HITPOINTS", "010", "16")]
    [InlineData("HITSMAX", "020", "32")]
    public void TheFamilyReadsASphereNumber(string key, string written, string expected)
    {
        var it = Fresh();
        Assert.True(it.TrySetProperty(key, written));
        Assert.Equal(expected, Read(it, key));
    }

    /// <summary>A value nothing can read leaves the key alone rather than zeroing it.</summary>
    [Fact]
    public void RubbishChangesNothing()
    {
        var it = Fresh();
        it.TrySetProperty("AMOUNT", "7");
        it.TrySetProperty("AMOUNT", "not a number");
        Assert.Equal("7", Read(it, "AMOUNT"));
    }
}
