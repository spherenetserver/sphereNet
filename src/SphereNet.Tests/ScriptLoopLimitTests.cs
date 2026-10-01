using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Script loops run to MAXLOOPTIMES (upstream m_iMaxLoopTimes, default
/// 100000; 0 = no limit), and WHILE numbers its passes in LOCAL._WHILE. The cap was a
/// fixed 512, so a loop over a larger list stopped part-way without a word, and a
/// WHILE that read LOCAL._WHILE never moved on.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptLoopLimitTests
{
    private static string Run(params string[] lines)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var body = new List<ScriptKey>();
        foreach (string l in lines)
        {
            int eq = l.IndexOf('=');
            int sp = l.IndexOf(' ');
            if (eq > 0 && (sp < 0 || eq < sp)) body.Add(new ScriptKey(l[..eq], l[(eq + 1)..]));
            else if (sp > 0) body.Add(new ScriptKey(l[..sp], l[(sp + 1)..]));
            else body.Add(new ScriptKey(l, ""));
        }
        var item = new Item();
        stack.Interpreter.Execute(body, item, null, new TriggerArgs(), new ScriptScope());
        // The stored text (a script read would show a number var in Sphere hex).
        return item.Tags.Get("X") ?? "0";
    }

    [Fact]
    public void AForLoopRunsPastTheOldCap() =>
        Assert.Equal("2000", Run("TAG.X=0", "FOR 2000", "TAG.X=<eval <tag.x>+1>", "ENDFOR"));

    [Fact]
    public void AWhileLoopRunsPastTheOldCap() =>
        Assert.Equal("2000", Run("TAG.X=0", "WHILE (<eval <tag.x>> < 2000)", "TAG.X=<eval <tag.x>+1>", "ENDWHILE"));

    [Fact]
    public void TheConfiguredCapStopsARunawayLoop()
    {
        ScriptScope.DefaultMaxLoopIterations = 50;
        Assert.Equal("50", Run("TAG.X=0", "WHILE (1)", "TAG.X=<eval <tag.x>+1>", "ENDWHILE"));
    }

    [Fact]
    public void WhileNumbersItsPassesFromZero() =>
        // LOCAL._WHILE is a number var: each pass reads in Sphere hex.
        Assert.Equal(":00,01,02,", Run("TAG.X=:", "TAG.N=0", "WHILE (<eval <tag.n>> < 3)",
            "TAG.X=<tag.x><local._while>,", "TAG.N=<eval <tag.n>+1>", "ENDWHILE"));
}
