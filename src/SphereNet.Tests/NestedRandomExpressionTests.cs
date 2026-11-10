using SphereNet.Scripting.Expressions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Nested short-random operands (<c>&lt;R&lt;x&gt;&gt;</c>, <c>&lt;R&lt;a&gt;,&lt;b&gt;&gt;</c>)
/// arrive at the R shortcut as <c>R3</c> / <c>R3,7</c> once the inner brackets are
/// expanded (CScriptObj.cpp:563); the live pack writes them in its dungeon-chest odds
/// test, the spawner timer and the shuriken damage.</summary>
public sealed class NestedRandomExpressionTests
{
    private static ExpressionParser Make()
    {
        var p = new ExpressionParser();
        p.VariableResolver = name => name.Equals("X", StringComparison.OrdinalIgnoreCase) ? "3"
            : name.Equals("Y", StringComparison.OrdinalIgnoreCase) ? "7" : null;
        return p;
    }

    [Fact]
    public void NestedSingleOperandIsARandBelow()
    {
        var p = Make();
        for (int i = 0; i < 50; i++)
        {
            long v = long.Parse(p.ResolveAngleBrackets("<R<X>>"));
            Assert.InRange(v, 0, 2);
        }
    }

    [Fact]
    public void NestedRangeIsInclusive()
    {
        var p = Make();
        for (int i = 0; i < 50; i++)
        {
            long v = long.Parse(p.ResolveAngleBrackets("<R<X>,<Y>>"));
            Assert.InRange(v, 3, 7);
        }
    }

    [Fact]
    public void FormatMinutesIsLeftToThePacksFunction()
    {
        var p = Make();
        p.FunctionResolver = name => name.StartsWith("FormatMinutes", StringComparison.OrdinalIgnoreCase)
            ? "pack-answer" : null;
        Assert.Equal("pack-answer", p.ResolveAngleBrackets("<FormatMinutes 100000>"));
    }
}
