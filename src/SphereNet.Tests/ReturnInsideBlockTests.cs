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
}
