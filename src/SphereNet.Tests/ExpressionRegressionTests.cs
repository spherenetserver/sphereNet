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
}
