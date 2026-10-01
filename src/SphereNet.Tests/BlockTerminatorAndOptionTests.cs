using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// Block structure follows Source-X CScriptObj::OnTriggerRun.
///
/// * Every terminator - END, ENDDO, ENDFOR, ENDIF, ENDRAND, ENDSWITCH, ENDWHILE - ends
///   whatever block is open: they all return TRIGRET_ENDIF from the section run
///   (CScriptObj.cpp:2393), so they are interchangeable. Nesting is counted by the
///   statements that open a section (IF, BEGIN, DORAND, DOSWITCH, WHILE, FOR and the
///   object loops), never by matching names.
/// * A DORAND / DOSWITCH option is ONE statement (TRIGRUN_SINGLE_TRUE/FALSE): a plain
///   line, or a whole IF..ENDIF, BEGIN..END, loop or nested DORAND/DOSWITCH block.
/// * DORAND N picks rand(N) - the script's N, not the number of lines.
/// * A DOSWITCH index past the last option, or negative, runs nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BlockTerminatorAndOptionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_blk_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private (TriggerResult Result, Item Item, ScriptScope Scope) Run(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "f.scp");
        File.WriteAllText(file,
            "[FUNCTION f_blk_mark]" + Nl +
            "TAG.CALLED=<eval <tag.called>+1>" + Nl);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(file);

        var body = new List<ScriptKey>();
        foreach (string raw in lines)
        {
            string l = raw.Trim();
            int eq = l.IndexOf('=');
            int sp = l.IndexOf(' ');
            if (eq > 0 && (sp < 0 || eq < sp)) body.Add(new ScriptKey(l[..eq], l[(eq + 1)..]));
            else if (sp > 0) body.Add(new ScriptKey(l[..sp], l[(sp + 1)..]));
            else body.Add(new ScriptKey(l, ""));
        }

        var item = new Item();
        var scope = new ScriptScope();
        var r = stack.Interpreter.Execute(body, item, null, new TriggerArgs(), scope);
        return (r, item, scope);
    }

    // The stored value: a script READ of a number var answers in Sphere hex ("01").
    private static string Tag(Item item, string name) => item.TagValue(name);

    public static readonly string[] Terminators =
        ["END", "ENDDO", "ENDFOR", "ENDIF", "ENDRAND", "ENDSWITCH", "ENDWHILE"];

    public static IEnumerable<object[]> EveryBlockWithEveryTerminator()
    {
        string[] kinds = ["IF", "BEGIN", "DORAND", "DOSWITCH", "FOR", "WHILE", "FORCHARS"];
        foreach (string k in kinds)
            foreach (string t in Terminators)
                yield return [k, t];
    }

    private static string[] Opener(string kind) => kind switch
    {
        "IF" => ["IF (1)"],
        "BEGIN" => ["BEGIN"],
        "DORAND" => ["DORAND 1"],
        "DOSWITCH" => ["DOSWITCH 0"],
        "FOR" => ["FOR 2"],
        "WHILE" => ["TAG.W=0", "WHILE (<tag.w> < 2)", "TAG.W=<eval <tag.w>+1>"],
        "FORCHARS" => ["FORCHARS 2"],
        _ => throw new ArgumentException(kind),
    };

    /// <summary>The line after the terminator runs exactly once, and the body runs as
    /// many times as the block says - whichever of the seven keywords closes it.</summary>
    [Theory]
    [MemberData(nameof(EveryBlockWithEveryTerminator))]
    public void EveryTerminatorClosesEveryBlockAndTheNextLineRunsOnce(string kind, string terminator)
    {
        var lines = new List<string> { "TAG.BODY=0", "TAG.AFTER=0" };
        lines.AddRange(Opener(kind));
        lines.Add("TAG.BODY=<eval <tag.body>+1>");
        lines.Add(terminator);
        lines.Add("TAG.AFTER=<eval <tag.after>+1>");

        var (_, item, _) = Run([.. lines]);

        string expectedBody = kind switch
        {
            "FOR" or "WHILE" => "2",
            "FORCHARS" => "0", // no source, nothing to walk
            _ => "1",
        };
        Assert.Equal(expectedBody, Tag(item, "BODY"));
        Assert.Equal("1", Tag(item, "AFTER"));
    }

    /// <summary>A block skipped in an untaken IF branch is skipped up to whichever
    /// terminator closes it, and the IF's own ELSE is still found.</summary>
    [Theory]
    [MemberData(nameof(EveryBlockWithEveryTerminator))]
    public void EveryTerminatorClosesASkippedBlock(string kind, string terminator)
    {
        var lines = new List<string> { "TAG.BODY=0", "TAG.ELSE=0", "TAG.AFTER=0", "IF (0)" };
        lines.AddRange(Opener(kind));
        lines.Add("TAG.BODY=<eval <tag.body>+1>");
        lines.Add(terminator);
        lines.Add("TAG.BODY=<eval <tag.body>+100>");
        lines.Add("ELSE");
        lines.Add("TAG.ELSE=<eval <tag.else>+1>");
        lines.Add("ENDIF");
        lines.Add("TAG.AFTER=<eval <tag.after>+1>");

        var (_, item, _) = Run([.. lines]);
        Assert.Equal("0", Tag(item, "BODY"));
        Assert.Equal("1", Tag(item, "ELSE"));
        Assert.Equal("1", Tag(item, "AFTER"));
    }

    /// <summary>Mixed nesting, each block closed by a different keyword.</summary>
    [Fact]
    public void MixedNestedBlocksCountDepthByOpenersNotByNames()
    {
        var (_, item, _) = Run(
            "TAG.N=0", "TAG.AFTER=0",
            "FOR 2",
            "  IF (1)",
            "    DOSWITCH 1",
            "      TAG.N=<eval <tag.n>+1000>",
            "      BEGIN",
            "        TAG.W=0",
            "        WHILE (<tag.w> < 2)",
            "          TAG.W=<eval <tag.w>+1>",
            "          TAG.N=<eval <tag.n>+1>",
            "        ENDWHILE",
            "      ENDRAND",
            "      TAG.N=<eval <tag.n>+1000>",
            "    ENDSWITCH",
            "  END",
            "ENDDO",
            "TAG.AFTER=<eval <tag.after>+1>");
        Assert.Equal("4", Tag(item, "N"));
        Assert.Equal("1", Tag(item, "AFTER"));
    }

    /// <summary>A terminator that closes nothing ends the section run in Source-X
    /// (TRIGRET_ENDIF from the top-level OnTriggerRun), so the lines after it do not
    /// run. It used to be stepped over.</summary>
    [Theory]
    [InlineData("END")]
    [InlineData("ENDIF")]
    [InlineData("ENDSWITCH")]
    public void AStrayTopLevelTerminatorEndsTheScript(string terminator)
    {
        var (_, item, _) = Run("TAG.A=1", "TAG.B=0", terminator, "TAG.B=1");
        Assert.Equal("1", Tag(item, "A"));
        Assert.Equal("0", Tag(item, "B"));
    }

    /// <summary>BREAK as the picked option leaves the enclosing loop.</summary>
    [Fact]
    public void ABreakOptionLeavesTheEnclosingLoop()
    {
        var (_, item, _) = Run(
            "TAG.N=0", "TAG.AFTER=0",
            "FOR 3",
            "DOSWITCH 1", "TAG.N=<eval <tag.n>+100>", "BREAK", "ENDSWITCH",
            "TAG.N=<eval <tag.n>+1>",
            "ENDFOR",
            "TAG.AFTER=1");
        Assert.Equal("0", Tag(item, "N"));
        Assert.Equal("1", Tag(item, "AFTER"));
    }

    // ---------------------------------------------------------------- DOSWITCH

    /// <summary>The audit's case: options that are BEGIN blocks.</summary>
    [Fact]
    public void DoswitchPicksAWholeBeginBlock()
    {
        string[] Script(int idx) =>
        [
            "TAG.PICK=none", "TAG.AFTER=0",
            $"DOSWITCH {idx}",
            "BEGIN", "TAG.PICK=first", "TAG.FIRST2=1", "END",
            "BEGIN", "TAG.PICK=second", "END",
            "ENDDO",
            "TAG.AFTER=<eval <tag.after>+1>",
        ];

        var (_, a, _) = Run(Script(1));
        Assert.Equal("second", Tag(a, "PICK"));
        Assert.Equal("1", Tag(a, "AFTER"));

        var (_, b, _) = Run(Script(0));
        Assert.Equal("first", Tag(b, "PICK"));
        Assert.Equal("1", Tag(b, "FIRST2"));
        Assert.Equal("1", Tag(b, "AFTER"));
    }

    /// <summary>An IF..ELSE..ENDIF is one option, however many lines it has.</summary>
    [Fact]
    public void DoswitchCountsAnIfBlockAsOneOption()
    {
        string[] Script(int idx) =>
        [
            "TAG.PICK=none",
            $"DOSWITCH {idx}",
            "IF (0)", "TAG.PICK=if", "ELSE", "TAG.PICK=else", "TAG.PICK2=else2", "ENDIF",
            "TAG.PICK=second",
            "ENDDO",
        ];

        var (_, a, _) = Run(Script(0));
        Assert.Equal("else", Tag(a, "PICK"));
        Assert.Equal("else2", Tag(a, "PICK2"));

        var (_, b, _) = Run(Script(1));
        Assert.Equal("second", Tag(b, "PICK"));
    }

    /// <summary>A nested DOSWITCH is one option of the outer one.</summary>
    [Fact]
    public void DoswitchCountsANestedDoswitchAsOneOption()
    {
        var (_, item, _) = Run(
            "TAG.PICK=none",
            "DOSWITCH 1",
            "DOSWITCH 1", "TAG.PICK=inner0", "TAG.PICK=inner1", "ENDDO",
            "TAG.PICK=outer1",
            "TAG.PICK=outer2",
            "ENDDO");
        Assert.Equal("outer1", Tag(item, "PICK"));

        var (_, item2, _) = Run(
            "TAG.PICK=none",
            "DOSWITCH 0",
            "DOSWITCH 1", "TAG.PICK=inner0", "TAG.PICK=inner1", "ENDSWITCH",
            "TAG.PICK=outer1",
            "ENDDO");
        Assert.Equal("inner1", Tag(item2, "PICK"));
    }

    /// <summary>Loops are one option each.</summary>
    [Fact]
    public void DoswitchCountsLoopsAsOneOption()
    {
        var (_, item, _) = Run(
            "TAG.N=0", "TAG.PICK=none",
            "DOSWITCH 2",
            "FOR 3", "TAG.N=<eval <tag.n>+1>", "ENDFOR",
            "WHILE (0)", "TAG.N=<eval <tag.n>+100>", "ENDWHILE",
            "TAG.PICK=third",
            "ENDDO");
        Assert.Equal("third", Tag(item, "PICK"));
        Assert.Equal("0", Tag(item, "N"));

        var (_, item2, _) = Run(
            "TAG.N=0",
            "DOSWITCH 0",
            "FOR 3", "TAG.N=<eval <tag.n>+1>", "ENDFOR",
            "TAG.N=100",
            "ENDDO");
        Assert.Equal("3", Tag(item2, "N"));
    }

    /// <summary>A RETURN option ends the script with its value; options after a
    /// multi-line block are still counted correctly.</summary>
    [Fact]
    public void DoswitchReturnOptionAfterABlock()
    {
        var (r, item, scope) = Run(
            "TAG.AFTER=0",
            "DOSWITCH 1",
            "BEGIN", "RETURN 5", "END",
            "RETURN 7",
            "RETURN 9",
            "ENDDO",
            "TAG.AFTER=1");
        Assert.Equal(TriggerResult.True, r);
        Assert.Equal("7", scope.ReturnValue);
        Assert.Equal("0", Tag(item, "AFTER"));

        var (r2, _, scope2) = Run("DOSWITCH 0", "BEGIN", "TAG.X=1", "RETURN 5", "END", "RETURN 7", "ENDDO");
        Assert.Equal(TriggerResult.True, r2);
        Assert.Equal("5", scope2.ReturnValue);
    }

    /// <summary>A CALL option after a block option.</summary>
    [Fact]
    public void DoswitchCallOptionAfterABlock()
    {
        var (_, item, _) = Run(
            "TAG.CALLED=0",
            "DOSWITCH 1",
            "BEGIN", "TAG.CALLED=100", "END",
            "CALL f_blk_mark",
            "ENDDO");
        Assert.Equal("1", Tag(item, "CALLED"));
    }

    /// <summary>Source-X counts down from the index and runs an option only when the
    /// counter is exactly 0, so an index past the end or below zero runs nothing and
    /// the script continues after the block.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(-1)]
    public void DoswitchOutOfRangeRunsNothing(int idx)
    {
        var (_, item, _) = Run(
            "TAG.PICK=none", "TAG.AFTER=0",
            $"DOSWITCH {idx}",
            "TAG.PICK=first",
            "BEGIN", "TAG.PICK=second", "END",
            "ENDDO",
            "TAG.AFTER=<eval <tag.after>+1>");
        Assert.Equal("none", Tag(item, "PICK"));
        Assert.Equal("1", Tag(item, "AFTER"));
    }

    // ---------------------------------------------------------------- DORAND

    /// <summary>The audit's case: DORAND 1 always picks the first option.</summary>
    [Fact]
    public void DorandUsesTheScriptCountNotTheLineCount()
    {
        for (int n = 0; n < 64; n++)
        {
            var (_, item, _) = Run("DORAND 1", "TAG.PICK=first", "TAG.PICK=second", "ENDDO");
            Assert.Equal("first", Tag(item, "PICK"));
        }
    }

    /// <summary>DORAND N with block options picks only among the first N options and
    /// eventually picks each of them.</summary>
    [Fact]
    public void DorandPicksOnlyAmongTheFirstNBlockOptions()
    {
        var seen = new HashSet<string>();
        for (int n = 0; n < 300; n++)
        {
            var (_, item, _) = Run(
                "TAG.PICK=none", "TAG.AFTER=0",
                "DORAND 3",
                "BEGIN", "TAG.PICK=a", "END",
                "IF (1)", "TAG.PICK=b", "ENDIF",
                "DOSWITCH 1", "TAG.PICK=x", "TAG.PICK=c", "ENDSWITCH",
                "TAG.PICK=d",
                "TAG.PICK=e",
                "ENDRAND",
                "TAG.AFTER=<eval <tag.after>+1>");
            string pick = Tag(item, "PICK");
            Assert.Contains(pick, new[] { "a", "b", "c" });
            Assert.Equal("1", Tag(item, "AFTER"));
            seen.Add(pick);
        }
        Assert.Equal(3, seen.Count);
    }

    /// <summary>DORAND 0 picks option 0 (CSRand::GetLLVal returns 0 below 2).</summary>
    [Fact]
    public void DorandZeroPicksTheFirstOption()
    {
        var (_, item, _) = Run("DORAND 0", "TAG.PICK=first", "TAG.PICK=second", "ENDDO");
        Assert.Equal("first", Tag(item, "PICK"));
    }

    /// <summary>RETURN and CALL as the picked option.</summary>
    [Fact]
    public void DorandReturnAndCallOptions()
    {
        var (r, item, scope) = Run("TAG.AFTER=0", "DORAND 1", "RETURN 3", "RETURN 4", "ENDDO", "TAG.AFTER=1");
        Assert.Equal(TriggerResult.True, r);
        Assert.Equal("3", scope.ReturnValue);
        Assert.Equal("0", Tag(item, "AFTER"));

        var (_, item2, _) = Run("TAG.CALLED=0", "DORAND 1", "CALL f_blk_mark", "TAG.CALLED=50", "ENDDO");
        Assert.Equal("1", Tag(item2, "CALLED"));
    }
}
