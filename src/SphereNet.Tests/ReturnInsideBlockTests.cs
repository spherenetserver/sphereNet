using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// RETURN reports its value from inside a block, not just at the top level.
///
/// ExecuteIf set scope.IsReturning and the return VALUE but left its out-parameter at
/// the TriggerResult.Default it was initialised with, so
///
///     IF &lt;condition&gt;
///        RETURN 1
///     ENDIF
///
/// stopped the block and then reported nothing. That shape is how every conditional
/// veto in every pack is written - @EquipTest refusing a piece of gear, @DClick
/// swallowing a use, @Buy cancelling a sale - and each of them came back as "no
/// opinion", which the dispatcher reads as "carry on". Only IF was affected: the
/// loops run their body through Execute, which maps the result.
///
/// The same hole ran through the rest of IF's inner statement switch. BREAK and
/// CONTINUE had no case either, so they fell to ExecuteLine, which does not know
/// them - and "IF &lt;cond&gt; BREAK ENDIF" is the only way to leave a loop early, so
/// every such loop ran to its end. DORAND and DOSWITCH execute the single line they
/// pick, and a lookup table written as a DOSWITCH of RETURNs is the ordinary way to
/// write one; the value went nowhere and the function came back blank.
/// </summary>
public sealed class ReturnInsideBlockTests
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
    public void ATopLevelReturnStillReportsItsValue()
    {
        Assert.Equal((TriggerResult.True, "a"), Run("TAG.X=a", "RETURN 1"));
    }

    [Fact]
    public void AReturnInsideAnIfReportsItsValue()
    {
        // The taken branch wins, and nothing after ENDIF runs.
        Assert.Equal((TriggerResult.True, "a"),
            Run("IF (1)", "TAG.X=a", "RETURN 1", "ENDIF", "TAG.X=b", "RETURN 0"));

        // With no trailing lines at all.
        Assert.Equal((TriggerResult.True, "a"),
            Run("IF (1)", "TAG.X=a", "RETURN 1", "ENDIF"));

        // And through a condition a script would really write.
        Assert.Equal((TriggerResult.True, "a"),
            Run("IF (<EVAL 2>&02)", "TAG.X=a", "RETURN 1", "ENDIF", "TAG.X=b", "RETURN 0"));
    }

    [Fact]
    public void AnUntakenBranchFallsThroughAsBefore()
    {
        Assert.Equal((TriggerResult.Default, "b"),
            Run("IF (0)", "TAG.X=a", "RETURN 1", "ENDIF", "TAG.X=b", "RETURN 0"));
    }

    /// <summary>RETURN 0 means "no opinion" at every level, the way the top-level
    /// mapping has always read it - the fix must not turn it into a veto.</summary>
    [Fact]
    public void AZeroReturnIsStillNoOpinion()
    {
        Assert.Equal((TriggerResult.Default, "a"),
            Run("IF (1)", "TAG.X=a", "RETURN 0", "ENDIF"));
    }

    /// <summary>An ELSE branch reports too.</summary>
    [Fact]
    public void AReturnInAnElseBranchReportsItsValue()
    {
        Assert.Equal((TriggerResult.True, "b"),
            Run("IF (0)", "TAG.X=a", "RETURN 0", "ELSE", "TAG.X=b", "RETURN 1", "ENDIF"));
    }

    /// <summary>And one nested two blocks deep.</summary>
    [Fact]
    public void AReturnNestedTwoDeepReportsItsValue()
    {
        Assert.Equal((TriggerResult.True, "c"),
            Run("IF (1)", "IF (1)", "TAG.X=c", "RETURN 1", "ENDIF", "ENDIF", "TAG.X=d", "RETURN 0"));
    }

    /// <summary>BREAK inside an IF leaves the loop. There is no other way to write a
    /// conditional loop exit.</summary>
    [Fact]
    public void ABreakInsideAnIfLeavesTheLoop()
    {
        // Writes 1 and 2, then the condition fires on 3 and the loop stops.
        Assert.Equal((TriggerResult.Default, "2"),
            Run("FOR 1 5", "IF (<eval <dlocal._for>> > 2)", "BREAK", "ENDIF",
                "TAG.X=<eval <dlocal._for>>", "ENDFOR", "RETURN 0"));

        // And on the first pass, before anything is written.
        Assert.Equal((TriggerResult.Default, "0"),
            Run("FOR 1 5", "IF (1)", "BREAK", "ENDIF",
                "TAG.X=<eval <dlocal._for>>", "ENDFOR", "RETURN 0"));
    }

    /// <summary>CONTINUE inside an IF skips the rest of the iteration.</summary>
    [Fact]
    public void AContinueInsideAnIfSkipsTheRestOfTheIteration()
    {
        Assert.Equal((TriggerResult.Default, "0"),
            Run("FOR 1 3", "IF (1)", "CONTINUE", "ENDIF", "TAG.X=reached", "ENDFOR", "RETURN 0"));

        // The untaken branch leaves the iteration alone.
        Assert.Equal((TriggerResult.Default, "reached"),
            Run("FOR 1 3", "IF (0)", "CONTINUE", "ENDIF", "TAG.X=reached", "ENDFOR", "RETURN 0"));
    }

    /// <summary>A BREAK outside any loop is not an exit - the statement is simply
    /// ignored, the way the top-level case has always treated it.</summary>
    [Fact]
    public void ABreakOutsideALoopIsIgnored()
    {
        Assert.Equal((TriggerResult.True, "a"),
            Run("IF (1)", "BREAK", "TAG.X=a", "RETURN 1", "ENDIF"));
    }

    /// <summary>The line DORAND or DOSWITCH picks can be a RETURN, and a lookup table
    /// written as a DOSWITCH of them is how the reference pack's pre-AOS weapon hue
    /// table is written.</summary>
    [Fact]
    public void ThePickedLineCanBeAReturn()
    {
        Assert.Equal((TriggerResult.True, "0"), Run("DOSWITCH 1", "RETURN 0", "RETURN 77", "ENDDO"));
        Assert.Equal((TriggerResult.True, "0"), Run("DORAND 1", "RETURN 77", "ENDDO"));
    }

    /// <summary>And the VALUE it returns is what a function hands back - the point of
    /// the table. A numeric RETURN stores the evaluated number, so the pack's
    /// "return 0a37" comes back as the 2615 that leading-zero hex means.</summary>
    [Fact]
    public void ThePickedReturnCarriesItsValue()
    {
        Assert.Equal("2615", ValueOf("DOSWITCH 1", "RETURN 0", "RETURN 0a37", "RETURN 07be", "ENDDO"));
        Assert.Equal("1982", ValueOf("DOSWITCH 2", "RETURN 0", "RETURN 0a37", "RETURN 07be", "ENDDO"));
        Assert.Equal("77", ValueOf("DORAND 1", "RETURN 77", "ENDDO"));
        // A string RETURN keeps its text, the way a [FUNCTION] returning a name does.
        Assert.Equal("chosen", ValueOf("DOSWITCH 0", "RETURN chosen", "ENDDO"));
    }

    private static string ValueOf(params string[] lines)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var body = new List<ScriptKey>();
        foreach (string l in lines)
        {
            int sp = l.IndexOf(' ');
            body.Add(sp > 0 ? new ScriptKey(l[..sp], l[(sp + 1)..]) : new ScriptKey(l, ""));
        }
        var scope = new ScriptScope();
        stack.Interpreter.Execute(body, new Item(), null, new TriggerArgs(), scope);
        return scope.ReturnValue ?? "";
    }
}
