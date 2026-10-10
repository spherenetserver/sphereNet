using SphereNet.Scripting.Expressions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A value keyword ends at the first character that is not a letter, digit or '_'
/// (Str_CmpHeadI_Table, sstring.cpp:840), and what follows is the expression
/// (CScriptObj.cpp:724). &lt;eval(&lt;p.z&gt;+15)&gt; is EVAL of "(&lt;p.z&gt;+15)".
/// Only a space ended the keyword here, so the parenthesised form read as nothing - and
/// a pack's Teleport check, IF (&lt;dtargp.z&gt; &gt;= &lt;eval(&lt;p.z&gt;+15)&gt;), compared against an
/// empty value and refused every cast.
/// </summary>
public sealed class ValueKeywordParenTests
{
    private static ExpressionParser Parser() => new()
    {
        VariableResolver = name => name.ToUpperInvariant() switch
        {
            "P.Z" => "5",
            "TARGP.Z" => "0",
            _ => null
        }
    };

    [Theory]
    [InlineData("<eval(<p.z>+15)>", "20")]
    [InlineData("<eval (<p.z>+15)>", "20")]
    [InlineData("<eval <p.z>+15>", "20")]
    [InlineData("<eval(1+2)*3>", "9")]       // the whole rest is the expression
    [InlineData("<hval(255)>", "0ff")]
    [InlineData("<hval 255>", "0ff")]
    public void AValueKeywordEndsAtAParenthesisAsWellAsASpace(string expr, string expected) =>
        Assert.Equal(expected, Parser().EvaluateStr(expr));

    [Fact]
    public void TheTeleportHeightCheckComparesAgainstANumber()
    {
        var p = Parser();
        string cond = "(" + p.EvaluateStr("<dtargp.z>") + " >= " + p.EvaluateStr("<eval(<p.z>+15)>") + ")";
        Assert.False(p.EvaluateConditional(cond));
    }
}
