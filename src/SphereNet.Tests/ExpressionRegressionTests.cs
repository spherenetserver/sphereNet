using SphereNet.Scripting.Expressions;

namespace SphereNet.Tests;

public class ExpressionRegressionTests
{
    [Fact]
    public void ExpressionParser_StrReplace_ReplacesLiteralMatches()
    {
        var parser = new ExpressionParser();

        Assert.Equal("iron ingot", parser.EvaluateStr("<STRREPLACE iron ore,ore,ingot>"));
        Assert.Equal("abc", parser.EvaluateStr("<STRREPLACE abc,,x>"));
    }

    [Fact]
    public void ExpressionParser_StrJoin_JoinsArgumentsWithSeparator()
    {
        var parser = new ExpressionParser();

        Assert.Equal("a|b|c", parser.EvaluateStr("<STRJOIN |,a,b,c>"));
        Assert.Equal("one two", parser.EvaluateStr("<STRJOIN( ,one,two)>"));
    }

    [Fact]
    public void ExpressionParser_StrRegex_UsesSafeNonBacktrackingEngine()
    {
        var parser = new ExpressionParser();

        Assert.Equal("1", parser.EvaluateStr("<STRREGEX ^a+$,aaaa>"));
        Assert.Equal("0", parser.EvaluateStr("<STRREGEX (a+)\\1,aaaa>"));
    }

    [Fact]
    public void ExpressionParser_StrRegex_RejectsOversizedInput()
    {
        var parser = new ExpressionParser();
        string longInput = new('a', 4097);

        Assert.Equal("0", parser.EvaluateStr($"<STRREGEX ^a+$,{longInput}>"));
    }

    [Fact]
    public void ExpressionParser_StrRegexNew_ReportsUnsupportedPattern()
    {
        var parser = new ExpressionParser();

        Assert.Equal("-1", parser.EvaluateStr("<STRREGEXNEW 4,aaaa,(a+)\\1>"));
    }

    [Fact]
    public void ExpressionParser_FloatFunctions_PreserveDecimalMath()
    {
        var parser = new ExpressionParser();

        Assert.Equal("1.5", parser.EvaluateStr("<FEVAL 1/2+1>"));
        Assert.Equal("2.75", parser.EvaluateStr("<FLOATVAL (1.5+4)/2>"));
        Assert.Equal("02", parser.EvaluateStr("<FHVAL 2.9>"));
    }

    [Fact]
    public void ExpressionParser_LeadingZeroNumbers_UseSourceXHexConvention()
    {
        var parser = new ExpressionParser();

        Assert.Equal(10, parser.Evaluate("0A".AsSpan()));
        Assert.Equal("10", parser.EvaluateStr("<EVAL 0A>"));
        Assert.Equal("10", parser.EvaluateStr("<FEVAL 0A>"));
    }

    [Fact]
    public void ExpressionParser_RandomR_AcceptsNoSpaceForm()
    {
        var parser = new ExpressionParser();
        for (int i = 0; i < 50; i++)
        {
            Assert.InRange(int.Parse(parser.EvaluateStr("<R5>")), 0, 4);   // no-space form
            Assert.InRange(int.Parse(parser.EvaluateStr("<R 5>")), 0, 4);  // spaced form still works
        }
    }

    [Fact]
    public void ExpressionParser_DPrefix_DoesNotHijackBareProperties()
    {
        var parser = new ExpressionParser();
        parser.VariableResolver = name => name.ToUpperInvariant() switch
        {
            "DISPID" => "i_halberd",
            "DEX" => "75",
            "HITS" => "42", // for the <DHITS> decimal-coercion fallback
            _ => null,
        };

        // Bare 'd' properties resolve to the real property, not a 'd'-stripped
        // name (regression: <dispid> used to become <ispid> -> 0).
        Assert.Equal("i_halberd", parser.EvaluateStr("<dispid>"));
        Assert.Equal("75", parser.EvaluateStr("<DEX>"));
        // The genuine D-force-decimal prefix still works when the full name is
        // unknown (<DHITS> -> decimal of HITS).
        Assert.Equal("42", parser.EvaluateStr("<DHITS>"));
    }

    [Fact]
    public void ExpressionParser_BraceRange_RollsWithinBounds()
    {
        var parser = new ExpressionParser();

        // Equal bounds are deterministic.
        Assert.Equal(5, parser.Evaluate("{5 5}".AsSpan()));
        // A real range stays within [lo,hi].
        for (int i = 0; i < 50; i++)
            Assert.InRange(parser.Evaluate("{3 8}".AsSpan()), 3, 8);
    }

    [Fact]
    public void ExpressionParser_MaxMin_And_Power()
    {
        var parser = new ExpressionParser();

        Assert.Equal(7, parser.Evaluate("MAX(3,7)".AsSpan()));
        Assert.Equal(3, parser.Evaluate("MIN(3,7)".AsSpan()));
        Assert.Equal(256, parser.Evaluate("2@8".AsSpan())); // power operator
    }

    [Fact]
    public void ExpressionParser_Qval_NumericThreeWay()
    {
        var parser = new ExpressionParser();

        // QVAL v1,v2,lt,eq,gt
        Assert.Equal("lt", parser.EvaluateStr("<QVAL 3,7,lt,eq,gt>"));
        Assert.Equal("eq", parser.EvaluateStr("<QVAL 5,5,lt,eq,gt>"));
        Assert.Equal("gt", parser.EvaluateStr("<QVAL 9,2,lt,eq,gt>"));
        // Conditional form still works.
        Assert.Equal("yes", parser.EvaluateStr("<QVAL 1?yes:no>"));
    }
}
