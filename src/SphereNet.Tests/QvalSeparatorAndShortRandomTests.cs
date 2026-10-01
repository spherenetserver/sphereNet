using SphereNet.Scripting.Expressions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Two expression-reader contracts from the reference:
///
/// QVAL splits its condition and branches with EvaluateConditionalQval_ParseArg
/// (CExpression.cpp:2311): it steps over a nested &lt;QVAL ...&gt; that opens before the
/// separator, then hands the rest to Str_Parse (CExpression.cpp:137), which ignores a
/// separator inside "quotes" or inside ( ), { } and [ ].
///
/// The short random &lt;R&gt; form (CScriptObj.cpp:563) accepts a digit or a '-' after the
/// R, reads "min[,max]", answers min when min &gt;= max, and with no number at all is
/// the range 0..999.
/// </summary>
public sealed class QvalSeparatorAndShortRandomTests
{
    private static ExpressionParser Parser(List<string>? asked = null) => new()
    {
        VariableResolver = name =>
        {
            asked?.Add(name.ToUpperInvariant());
            return name.ToUpperInvariant() switch
            {
                "YES" => "1",
                "NO" => "0",
                "TAKEN" => "taken",
                "SKIPPED" => "skipped",
                _ => null
            };
        }
    };

    [Fact]
    public void QuotedColonInTheTrueBranchIsNotTheSeparator()
    {
        var p = Parser();
        Assert.Equal("\"a:b\"", p.EvaluateStr("<QVAL 1 ? \"a:b\" : no>"));
        Assert.Equal("no", p.EvaluateStr("<QVAL 0 ? \"a:b\" : no>"));
    }

    [Fact]
    public void QuotedQuestionMarkIsNotTheConditionSeparator()
    {
        var p = Parser();
        Assert.Equal("\"x?y\"", p.EvaluateStr("<QVAL 1 ? \"x?y\" : n>"));
        Assert.Equal("\"c?d:e\"", p.EvaluateStr("<QVAL 0 ? \"a?b\" : \"c?d:e\">"));
    }

    [Fact]
    public void ParenthesisedColonIsNotTheSeparator()
    {
        var p = Parser();
        Assert.Equal("(a:b)", p.EvaluateStr("<QVAL 1 ? (a:b) : c>"));
        Assert.Equal("c", p.EvaluateStr("<QVAL 0 ? (a:b) : c>"));
    }

    [Fact]
    public void NestedQvalSeparatorsBelongToTheNestedStatement()
    {
        var p = Parser();
        Assert.Equal("b", p.EvaluateStr("<QVAL 1 ? <QVAL 0 ? a : b> : c>"));
        Assert.Equal("c", p.EvaluateStr("<QVAL 0 ? <QVAL 0 ? a : b> : c>"));
        Assert.Equal("a", p.EvaluateStr("<QVAL 0 ? x : <QVAL 1 ? a : b>>"));
        Assert.Equal("MATCH", p.EvaluateStr("<QVAL <QVAL 5?1:0>==1 ? MATCH : NOMATCH>"));
        Assert.Equal("\"q:r\"", p.EvaluateStr("<QVAL 1 ? <QVAL 1 ? \"q:r\" : s> : t>"));
    }

    [Fact]
    public void UnselectedBranchIsStillNotEvaluated()
    {
        var asked = new List<string>();
        var p = Parser(asked);
        Assert.Equal("taken", p.EvaluateStr("<QVAL (<YES>)?<TAKEN>:\"<SKIPPED>:z\">"));
        Assert.DoesNotContain("SKIPPED", asked);

        asked.Clear();
        Assert.Equal("skipped", p.EvaluateStr("<QVAL (<NO>)?\"<TAKEN>:z\":<SKIPPED>>"));
        Assert.DoesNotContain("TAKEN", asked);
    }

    [Fact]
    public void ShortRandomWithEqualNegativeEndsAnswersThatValue()
    {
        var p = Parser();
        Assert.Equal("-7", p.EvaluateStr("<R-7,-7>"));
        Assert.Equal("-1", p.EvaluateStr("<R-1,-1>"));
    }

    [Fact]
    public void ShortRandomWithMixedSignsStaysInTheInclusiveRange()
    {
        var p = Parser();
        bool sawNegative = false, sawPositive = false;
        for (int i = 0; i < 400; i++)
        {
            long v = long.Parse(p.EvaluateStr("<R-7,7>"));
            Assert.InRange(v, -7, 7);
            sawNegative |= v < 0; sawPositive |= v > 0;

            Assert.InRange(long.Parse(p.EvaluateStr("<R-3,-1>")), -3, -1);
        }
        Assert.True(sawNegative && sawPositive);
    }

    [Fact]
    public void ShortRandomSingleNegativeOrZeroBoundAnswersZero()
    {
        var p = Parser();
        // min = 0, max = bound - 1 <= min: the reference answers min.
        Assert.Equal("0", p.EvaluateStr("<R-5>"));
        Assert.Equal("0", p.EvaluateStr("<R0>"));
        Assert.Equal("0", p.EvaluateStr("<R1>"));
    }

    [Fact]
    public void ShortRandomExistingFormsKeepWorking()
    {
        var p = Parser();
        Assert.Equal("7", p.EvaluateStr("<R7,7>"));
        // min >= max answers min - there is no swap on this path.
        Assert.Equal("15", p.EvaluateStr("<R15,3>"));
        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(int.Parse(p.EvaluateStr("<R7>")), 0, 6);
            // A leading zero is hex: 010 = 16.
            Assert.InRange(int.Parse(p.EvaluateStr("<R010>")), 0, 15);
            Assert.InRange(int.Parse(p.EvaluateStr("<R3,15>")), 3, 15);
        }
    }

    [Fact]
    public void BareShortRandomIsZeroToNineHundredNinetyNine()
    {
        var p = Parser();
        long max = long.MinValue;
        for (int i = 0; i < 2000; i++)
        {
            long v = long.Parse(p.EvaluateStr("<R>"));
            Assert.InRange(v, 0, 999);
            max = Math.Max(max, v);
        }
        Assert.True(max >= 900, $"max seen {max}");
    }

    [Fact]
    public void BareRResolvedByAName_TakesPrecedenceOverTheRandomFallback()
    {
        var p = new ExpressionParser
        {
            VariableResolver = name => name.Equals("R", StringComparison.OrdinalIgnoreCase) ? "named" : null
        };
        Assert.Equal("named", p.EvaluateStr("<R>"));
    }
}
