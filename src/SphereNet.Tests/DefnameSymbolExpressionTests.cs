using SphereNet.Scripting.Expressions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A bare symbol that starts with D is still a symbol (CExpression.cpp:
/// 1221-1237 looks it up in VAR/RESDEF/DEF; the d prefix lives only in r_WriteVal,
/// CScriptObj.cpp:542), and RESOURCETYPE sees the resource behind a decimal wrapper
/// (CScriptObj.cpp:607-612). The pack's worldgen leans on both: IF (&lt;LOCAL.SPAWN_ARRAY&gt;)
/// for lists starting with Dog / DeepSeaSerpent, and
/// IF (&lt;RESOURCETYPE &lt;dARGS&gt;&gt; == &lt;DEF.RES_ITEMDEF&gt;) in the spawner timer.</summary>
public sealed class DefnameSymbolExpressionTests
{
    private static ExpressionParser Make()
    {
        var p = new ExpressionParser();
        p.VariableResolver = name =>
            name.Equals("ARGS", StringComparison.OrdinalIgnoreCase) ? "i_reag_black_pearl"
            : name.Equals("COUNT", StringComparison.OrdinalIgnoreCase) ? "012"
            : null;
        p.ResourceValueResolver = name =>
            name.Equals("Dog", StringComparison.OrdinalIgnoreCase) ? 217
            : name.Equals("Kraken", StringComparison.OrdinalIgnoreCase) ? 77
            : name.Equals("i_reag_black_pearl", StringComparison.OrdinalIgnoreCase) ? 3962
            : null;
        p.ResourceTypeIndexResolver = (arg, wantIndex) =>
            arg.Equals("i_reag_black_pearl", StringComparison.OrdinalIgnoreCase)
                ? (wantIndex ? "0f7a" : "0f")
                : "00";
        return p;
    }

    [Fact]
    public void ABareSymbolStartingWithDIsLookedUpLikeAnyOther()
    {
        var p = Make();
        Assert.Equal(77, p.Evaluate("Kraken"));
        Assert.Equal(217, p.Evaluate("Dog"));
    }

    [Fact]
    public void TheDecimalPrefixStillConvertsAKnownVariable()
    {
        var p = Make();
        Assert.Equal("18", p.ResolveAngleBrackets("<dCOUNT>")); // 012 is hex in a script
    }

    [Fact]
    public void ResourceTypeSeesTheNameBehindADecimalWrapper()
    {
        var p = Make();
        Assert.Equal("0f", p.ResolveAngleBrackets("<RESOURCETYPE <dARGS>>"));
        Assert.Equal("0f", p.ResolveAngleBrackets("<RESOURCETYPE <EVAL i_reag_black_pearl>>"));
        Assert.Equal("0f", p.ResolveAngleBrackets("<RESOURCETYPE i_reag_black_pearl>"));
    }
}
