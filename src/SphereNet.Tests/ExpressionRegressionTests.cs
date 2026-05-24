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
}
