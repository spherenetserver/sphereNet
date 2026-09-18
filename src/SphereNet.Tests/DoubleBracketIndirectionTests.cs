using SphereNet.Scripting.Expressions;

namespace SphereNet.Tests;

/// <summary>
/// &lt;&lt;X&gt;&gt; reads the key NAMED BY X, not the text of X.
///
/// Upstream gets this from the recursion in ParseScriptText: an open bracket recurses
/// into what follows, so the inner brackets resolve first and the OUTER bracket is then
/// looked up with whatever they produced (CExpression.cpp:2590-2598).
///
/// The shipped packs lean on it to walk a table by index - every skill loop writes
/// &lt;&lt;SERV.SKILL.&lt;DLOCAL._FOR&gt;.KEY&gt;&gt;: the inner half names the skill and the
/// outer half reads that skill off the character. Without the second pass the loop
/// compares a skill's NAME against a number, which is never true, so the whole body is
/// skipped for all 58 iterations.
/// </summary>
public sealed class DoubleBracketIndirectionTests
{
    /// <summary>A parser whose variables are a two-level table: the index names the
    /// key, and the key has the value.</summary>
    private static ExpressionParser Table() => new()
    {
        VariableResolver = name => name.ToUpperInvariant() switch
        {
            "SERV.SKILL.0.KEY" => "ALCHEMY",
            "SERV.SKILL.1.KEY" => "ANATOMY",
            "ALCHEMY" => "421",
            "ANATOMY" => "17",
            "IDX" => "1",
            _ => null
        }
    };

    [Fact]
    public void TheOuterBracketReadsTheKeyTheInnerOneNamed()
        => Assert.Equal("421", Table().EvaluateStr("<<SERV.SKILL.0.KEY>>"));

    /// <summary>The form the packs actually write: the index is itself a bracket, so
    /// three levels have to collapse in the right order.</summary>
    [Fact]
    public void TheIndexMayItselfBeABracket()
        => Assert.Equal("17", Table().EvaluateStr("<<SERV.SKILL.<IDX>.KEY>>"));

    /// <summary>A single bracket still means the plain read - the second pass must not
    /// happen on its own.</summary>
    [Fact]
    public void OneBracketIsStillOneRead()
        => Assert.Equal("ALCHEMY", Table().EvaluateStr("<SERV.SKILL.0.KEY>"));

    /// <summary>And it composes with surrounding text, which is how the loop's
    /// comparison is written.</summary>
    [Fact]
    public void ItComposesWithText()
        => Assert.Equal("skill=421.", Table().EvaluateStr("skill=<<SERV.SKILL.0.KEY>>."));

    /// <summary>Inside an expression &lt;&lt; is still the shift operator
    /// (CExpression.cpp:1378), so the indirection only applies when the second bracket
    /// opens an identifier. "1 &lt;&lt; 3" must not become a lookup.</summary>
    [Fact]
    public void ShiftIsStillShift()
    {
        Assert.Equal("8", Table().EvaluateStr("<EVAL 1<<3>"));
        Assert.Equal("a << 3", Table().EvaluateStr("a << 3"));
    }

    /// <summary>A name the table does not have reads empty, not the literal
    /// brackets.</summary>
    [Fact]
    public void AnUnknownInnerNameReadsEmpty()
        => Assert.Equal("", Table().EvaluateStr("<<SERV.SKILL.9.KEY>>"));
}
