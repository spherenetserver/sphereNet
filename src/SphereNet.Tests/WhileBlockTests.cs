using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// WHILE, held down across the shapes the block repairs touched.
///
/// WHILE was one of the three the IF dispatcher already knew, so none of those bugs
/// reached it - but that is a claim worth a test rather than an assertion, since the
/// same repairs moved the ground under every block: the shared dispatcher, the three
/// scope flags, and the object-loop list that FindBlockEnd reads.
/// </summary>
public sealed class WhileBlockTests
{
    private static (TriggerResult Result, string Tag) Run(params string[] lines)
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
        var r = stack.Interpreter.Execute(body, item, null, new TriggerArgs(), new ScriptScope());
        item.TryGetProperty("TAG.X", out string v);
        return (r, v);
    }

    [Fact]
    public void ItCountsToItsConditionBareAndNestedAlike()
    {
        Assert.Equal((TriggerResult.Default, "3"),
            Run("TAG.X=0", "WHILE (<eval <tag.x>> < 3)", "TAG.X=<eval <tag.x>+1>", "ENDWHILE", "RETURN 0"));

        Assert.Equal((TriggerResult.Default, "3"),
            Run("TAG.X=0", "IF (1)", "WHILE (<eval <tag.x>> < 3)", "TAG.X=<eval <tag.x>+1>",
                "ENDWHILE", "ENDIF", "RETURN 0"));
    }

    [Fact]
    public void AnUntakenBranchSkipsTheWholeLoop()
    {
        Assert.Equal((TriggerResult.Default, "0"),
            Run("TAG.X=0", "IF (0)", "WHILE (<eval <tag.x>> < 3)", "TAG.X=<eval <tag.x>+1>",
                "ENDWHILE", "ENDIF", "RETURN 0"));
    }

    /// <summary>The conditional exit - without it the only thing stopping this loop is
    /// the iteration cap.</summary>
    [Fact]
    public void ABreakInsideAnIfLeavesTheLoop()
    {
        Assert.Equal((TriggerResult.Default, "3"),
            Run("TAG.X=0", "WHILE (1)", "TAG.X=<eval <tag.x>+1>",
                "IF (<eval <tag.x>> > 2)", "BREAK", "ENDIF", "ENDWHILE", "RETURN 0"));
    }

    [Fact]
    public void AContinueInsideAnIfSkipsTheRestOfThePass()
    {
        Assert.Equal((TriggerResult.Default, "3"),
            Run("TAG.X=0", "WHILE (<eval <tag.x>> < 3)", "TAG.X=<eval <tag.x>+1>",
                "IF (1)", "CONTINUE", "ENDIF", "TAG.X=99", "ENDWHILE", "RETURN 0"));
    }

    [Fact]
    public void AReturnLeavesTheLoopAndReportsItsValue()
    {
        Assert.Equal((TriggerResult.True, "7"),
            Run("TAG.X=0", "WHILE (1)", "TAG.X=7", "RETURN 1", "ENDWHILE", "RETURN 0"));
    }

    /// <summary>An object loop inside it closes its own ENDFOR, so the ENDWHILE still
    /// belongs to the WHILE.</summary>
    [Fact]
    public void AnObjectLoopInsideItDoesNotStealTheEnd()
    {
        Assert.Equal((TriggerResult.Default, "3"),
            Run("TAG.X=0", "WHILE (<eval <tag.x>> < 3)", "TAG.X=<eval <tag.x>+1>",
                "FORCHARLAYER 2", "TAG.X=<eval <tag.x>+100>", "ENDFOR", "ENDWHILE", "RETURN 0"));
    }

    [Fact]
    public void ANestedWhileClosesItsOwnEnd()
    {
        // Inner runs to 3, then the outer adds one per pass until 6.
        Assert.Equal((TriggerResult.Default, "6"),
            Run("TAG.X=0", "WHILE (<eval <tag.x>> < 6)",
                "WHILE (<eval <tag.x>> < 3)", "TAG.X=<eval <tag.x>+1>", "ENDWHILE",
                "TAG.X=<eval <tag.x>+1>", "ENDWHILE", "RETURN 0"));
    }
}
