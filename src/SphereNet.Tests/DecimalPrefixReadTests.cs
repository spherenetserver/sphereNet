using SphereNet.Scripting.Expressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A leading D reads a member as a NUMBER, and does so unconditionally.
///
/// Upstream calls it the shorthand for &lt;EVAL &lt;SOMEVAL&gt;&gt; and the whole
/// body is two lines:
///
///     if ( r_WriteVal(ptcArg, sVal, pSrc) ) {
///         if ( *sVal != '-' ) sVal.FormatLLVal(Str_ToLL(sVal.GetBuffer()).value_or(0));
///
/// (CScriptObj.cpp:543-551). A value starting with '-' is the only one left as
/// written; everything else is converted, and text that is not a number converts to
/// zero. SphereNet converted only when the value happened to begin with a '0' and
/// otherwise handed the raw text back.
///
/// That looks harmless and is not, because the thing scripts do with &lt;dX&gt; is
/// COMPARE it. Sphere's evaluator reads a leading number and stops at the first
/// token that is not an operator — so with text passed through, "68 65 6C 6C 6F == 0"
/// evaluates to 68 and the "== 0" is never reached. The reference pack's IsBlank is
/// built exactly that way (&lt;ASC&gt; of the string, then
/// ELSEIF (&lt;dLOCAL.ASC&gt; == 0)), so it answered "blank" for every non-empty
/// string, and every ISBLANK gate in the pack — the GM page box included — threw
/// away what the player typed.
/// </summary>
public sealed class DecimalPrefixReadTests(ITestOutputHelper output)
{
    private static ExpressionParser Parser() => new()
    {
        VariableResolver = name => name.ToUpperInvariant() switch
        {
            "MORE1" => "08981",                 // hex as written back by the engine
            "HITS" => "45",                     // already decimal
            "DEBT" => "-5",
            "NAME" => "Bob",                    // not a number at all
            "ASC" => "68 65 6C 6C 6F",          // what <ASC hello> answers
            "DISPID" => "0190",                 // a member whose name begins with D
            _ => null
        },
        FunctionResolver = name => name.Trim().ToUpperInvariant() switch
        {
            "DAMTYPES" => "7",                  // a [FUNCTION] that begins with D
            _ => null
        }
    };

    [Theory]
    [InlineData("<dMORE1>", "35201")]   // 08981 hex read back as decimal
    [InlineData("<dHITS>", "45")]
    [InlineData("<dDEBT>", "-5")]       // negative: left exactly as written
    [InlineData("<dNAME>", "0")]        // not a number -> zero, not the text
    [InlineData("<dASC>", "68")]        // leading number, rest ignored
    public void TheDecimalPrefixAlwaysAnswersANumber(string expr, string expected)
    {
        string actual = Parser().EvaluateStr(expr);
        output.WriteLine($"{expr} -> '{actual}'");
        Assert.Equal(expected, actual);
    }

    /// <summary>The prefix must not eat a real member name. This is the trap the
    /// whole D branch is arranged around: try the stripped name, and when that is
    /// not a member the D belonged to the name after all.</summary>
    [Fact]
    public void AMemberThatBeginsWithDIsNotEaten()
    {
        Assert.Equal("0190", Parser().EvaluateStr("<DISPID>"));
        Assert.Equal("7", Parser().EvaluateStr("<DAMTYPES>"));
    }

    /// <summary>The comparison this was really costing: a value that is a number
    /// followed by other tokens must answer the question actually asked.</summary>
    [Fact]
    public void AComparisonAgainstTheValueIsAComparisonAndNotTheLeadingNumber()
    {
        var p = Parser();
        // <dASC> is 68, so "== 0" is false and "== 68" is true. Before the fix the
        // left side stayed "68 65 6C 6C 6F", the evaluator stopped at "65", and BOTH
        // of these came back 68 — truthy — whatever they were compared with.
        Assert.False(p.EvaluateConditional("(" + p.EvaluateStr("<dASC>") + " == 0)"));
        Assert.True(p.EvaluateConditional("(" + p.EvaluateStr("<dASC>") + " == 68)"));
    }
}
