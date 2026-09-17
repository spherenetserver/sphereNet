using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// A block nested inside an IF is still that block.
///
/// ExecuteIf carried a PARTIAL COPY of the statement switch Execute uses: IF, FOR and
/// WHILE had cases in it, and the other eighteen block kinds did not. Anything not
/// named fell through to ExecuteLine as an ordinary statement, so the block structure
/// was simply ignored - a DORAND ran EVERY option instead of one, a DOSWITCH ran every
/// entry, and an object loop ran its body once, on whatever object the line happened
/// to be running on, instead of iterating.
///
/// The shipped packs nest 4817 of these inside an IF, and 4754 are DORAND: the guarded
/// random barkline of a town NPC. Every one of them said all of its lines at once.
///
/// Both dispatchers now share one method, which is the actual repair - a partial copy
/// of a switch is a bug that regrows.
/// </summary>
public sealed class NestedBlockDispatchTests
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

    /// <summary>The 4754-line case. Whichever option is picked, exactly ONE runs, so
    /// the total is 1 or 10 - never 11.</summary>
    [Fact]
    public void ADorandInsideAnIfRunsExactlyOneOption()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var (_, tag) = Run("TAG.X=0", "IF (1)", "DORAND 2",
                "TAG.X=<eval <tag.x>+1>", "TAG.X=<eval <tag.x>+10>", "ENDDO", "ENDIF", "RETURN 0");
            Assert.True(tag is "1" or "10", $"one option, not both - got {tag}");
        }
    }

    /// <summary>Bare and nested agree, which is the whole point.</summary>
    [Fact]
    public void ADoswitchInsideAnIfRunsOnlyItsEntry()
    {
        Assert.Equal((TriggerResult.Default, "10"),
            Run("TAG.X=0", "IF (1)", "DOSWITCH 1",
                "TAG.X=<eval <tag.x>+1>", "TAG.X=<eval <tag.x>+10>", "ENDDO", "ENDIF", "RETURN 0"));

        Assert.Equal((TriggerResult.Default, "1"),
            Run("TAG.X=0", "IF (1)", "DOSWITCH 0",
                "TAG.X=<eval <tag.x>+1>", "TAG.X=<eval <tag.x>+10>", "ENDDO", "ENDIF", "RETURN 0"));
    }

    /// <summary>An object loop nested in an IF iterates - over nothing here, which is
    /// exactly what the same loop does at the top level. It used to run its body once
    /// regardless.</summary>
    [Fact]
    public void AnObjectLoopInsideAnIfIteratesTheSameWayItDoesBare()
    {
        var bare = Run("TAG.X=0", "FORCHARS 2", "TAG.X=<eval <tag.x>+1>", "ENDFOR", "RETURN 0");
        var nested = Run("TAG.X=0", "IF (1)", "FORCHARS 2", "TAG.X=<eval <tag.x>+1>", "ENDFOR",
            "ENDIF", "RETURN 0");
        Assert.Equal(bare, nested);
    }

    /// <summary>A block in an UNTAKEN branch stays untaken - the skip path has to keep
    /// understanding the same block kinds.</summary>
    [Fact]
    public void ABlockInAnUntakenBranchDoesNotRun()
    {
        Assert.Equal((TriggerResult.Default, "0"),
            Run("TAG.X=0", "IF (0)", "DORAND 2",
                "TAG.X=<eval <tag.x>+1>", "TAG.X=<eval <tag.x>+10>", "ENDDO", "ENDIF", "RETURN 0"));
    }

    /// <summary>And the nesting goes both ways round.</summary>
    [Fact]
    public void AnIfInsideALoopInsideAnIfStillWorks()
    {
        Assert.Equal((TriggerResult.Default, "3"),
            Run("TAG.X=0", "IF (1)", "FOR 1 3", "IF (1)", "TAG.X=<eval <tag.x>+1>", "ENDIF",
                "ENDFOR", "ENDIF", "RETURN 0"));
    }

    /// <summary>Every object loop closes with ENDFOR, so the dispatcher, the block-end
    /// search and the skip path have to agree on which commands are one.
    ///
    /// They did not: FORCHARLAYER and FORCHARMEMORYTYPE were dispatched as loops but
    /// missing from IsForVariant, which both of the other two consult. An ENDFOR
    /// belonging to one of them closed the loop AROUND it instead - the outer body
    /// ended early and its tail ran once, outside the loop, rather than once per pass.
    /// The three now read from one predicate.</summary>
    [Theory]
    [InlineData("FORCHARS")]
    [InlineData("FORITEMS")]
    [InlineData("FOROBJS")]
    [InlineData("FORCONT")]
    [InlineData("FORCONTID")]
    [InlineData("FORCONTTYPE")]
    [InlineData("FORPLAYERS")]
    [InlineData("FORCLIENTS")]
    [InlineData("FORINSTANCES")]
    [InlineData("FORCHARLAYER")]
    [InlineData("FORCHARMEMORYTYPE")]
    public void AnObjectLoopNestedInALoopClosesItsOwnEndfor(string loop)
    {
        // The inner loop walks nothing here, so only the tail counts: once per pass of
        // the outer FOR, three passes.
        Assert.Equal((TriggerResult.Default, "3"),
            Run("TAG.X=0", "FOR 1 3",
                $"{loop} 2", "TAG.X=<eval <tag.x>+100>", "ENDFOR",
                "TAG.X=<eval <tag.x>+1>", "ENDFOR", "RETURN 0"));
    }

    /// <summary>And the same nesting inside an untaken IF stays untaken - that is the
    /// skip path reading the same list.</summary>
    [Theory]
    [InlineData("FORCHARLAYER")]
    [InlineData("FORCHARMEMORYTYPE")]
    public void AnObjectLoopInAnUntakenBranchIsSkippedWhole(string loop)
    {
        Assert.Equal((TriggerResult.Default, "0"),
            Run("TAG.X=0", "IF (0)",
                $"{loop} 2", "TAG.X=<eval <tag.x>+100>", "ENDFOR",
                "TAG.X=<eval <tag.x>+1>",
                "ENDIF", "RETURN 0"));
    }
}
