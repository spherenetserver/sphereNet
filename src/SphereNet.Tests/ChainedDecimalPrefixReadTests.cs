using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// A D in front of a read asks for the value in decimal: Source-X strips it, reads the
/// rest, and converts with Str_ToLL unless the text starts with '-'
/// (CScriptObj::r_WriteVal, CScriptObj.cpp:543-551). The top-level &lt;dctag0.x&gt; had
/// that; a chained &lt;src.dctag0.x&gt; reached the object's own CTAG branch with the D
/// still on and came back in Sphere hex. A pack that spliced it into SQL unquoted sent
/// "WHERE page_id = 0ca3", which MySQL reads as a column name.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ChainedDecimalPrefixReadTests
{
    [Theory]
    [InlineData("0ca3", "3235")]
    [InlineData("3235", "3235")]
    [InlineData("0", "0")]
    [InlineData("00", "0")]
    [InlineData("", "0")]
    [InlineData("abc", "0")]
    [InlineData("12abc", "12")]
    [InlineData("  0ff", "255")]
    [InlineData("-5", "-5")]
    [InlineData("-0ff", "-0ff")]
    [InlineData("0ffffffff", "4294967295")]
    [InlineData("99999999999999999999", "0")]
    public void ADecimalReadConvertsAsStrToLLDoes(string text, string expected) =>
        Assert.Equal(expected, ScriptNumber.ToDecimalReading(text));

    [Fact]
    public void AChainedDctagReadIsDecimalAndThePlainOneIsNot()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        // `src.ctag0.secilen_page <dlocal.argn>` stores a number var.
        ch.CTags.SetStr("secilen_page", false, "3235");

        Assert.True(ch.TryGetProperty("CTAG0.secilen_page", out string plain));
        Assert.Equal("0ca3", plain);
        Assert.True(ch.TryGetProperty("DCTAG0.secilen_page", out string dec));
        Assert.Equal("3235", dec);
        Assert.True(ch.TryGetProperty("DCTAG0.missing", out string missing));
        Assert.Equal("0", missing);
    }

    [Fact]
    public void AChainedDtagReadIsDecimal()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var item = world.CreateItem();
        item.Tags.SetStr("count", false, "255");

        Assert.True(item.TryGetProperty("TAG0.count", out string plain));
        Assert.Equal("0ff", plain);
        Assert.True(item.TryGetProperty("DTAG0.count", out string dec));
        Assert.Equal("255", dec);
        Assert.True(item.TryGetProperty("DTAG.count", out string dec2));
        Assert.Equal("255", dec2);
    }
}
