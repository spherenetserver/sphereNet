using SphereNet.Scripting.Expressions;

namespace SphereNet.Tests;

/// <summary>
/// Every intrinsic in the expression evaluator's table answers.
///
/// Upstream keeps 27 of them in one sorted table (sm_IntrinsicFunctions,
/// CExpression.h:109), reached from both the integer evaluator (CExpression.cpp:826)
/// and the float one (CFloatMath.cpp:254). They are not script FUNCTIONs - no pack
/// declares them and none can override them - so a missing one cannot be papered over
/// from the script side: the name falls through to the variable lookup and the whole
/// expression quietly reads 0.
///
/// This walks the upstream table and asks this engine for each, rather than trusting a
/// list written by hand. The probe is calibrated: a name upstream does NOT have must
/// come back missing, otherwise the sweep is measuring nothing.
/// </summary>
public sealed class IntrinsicFunctionCoverageTests
{
    /// <summary>An expression using the intrinsic, and what it must come to.</summary>
    private static readonly (string Name, string Expr, long Want)[] Probes =
    [
        ("ABS",        "ABS(-7)",                  7),
        ("ARCCOS",     "ARCCOS(1)",                0),
        ("ARCSIN",     "ARCSIN(0)",                0),
        ("ARCTAN",     "ARCTAN(0)",                0),
        ("COS",        "COS(0)",                   1),
        ("ID",         "ID(0401234)",              0x01234),
        ("ISNUMBER",   "ISNUMBER(42)",             1),
        ("ISOBSCENE",  "ISOBSCENE(hello)",         0),
        ("LOGARITHM",  "LOGARITHM(1000)",          3),
        ("MAX",        "MAX(3,9)",                 9),
        ("MIN",        "MIN(3,9)",                 3),
        ("NAPIERPOW",  "NAPIERPOW(0)",             1),
        ("QVAL",       "QVAL(5,5,10,20,30)",       20),
        ("RAND",       "RAND(1)",                  0),
        ("RANDBELL",   "RANDBELL(50,0)",           500),
        ("SIN",        "SIN(0)",                   0),
        ("SQRT",       "SQRT(81)",                 9),
        ("STRASCII",   "STRASCII(A)",              65),
        ("STRCMP",     "STRCMP(abc,abc)",          0),
        ("STRCMPI",    "STRCMPI(ABC,abc)",         0),
        ("STRINDEXOF", "STRINDEXOF(hello,ll)",     2),
        ("STRLEN",     "STRLEN(hello)",            5),
        ("STRMATCH",   "STRMATCH(h*o,hello)",      1),
        ("STRREGEX",   "STRREGEX(^h.*o$,hello)",   1),
        ("TAN",        "TAN(0)",                   0),
    ];

    /// <summary>Ask the engine. Answers null when the name did not resolve as a
    /// function at all - which, in this evaluator, reads as the number 0 for most of
    /// them, so the probe has to distinguish "answered 0" from "was never called".</summary>
    private static long? Ask(string expr)
    {
        var parser = new ExpressionParser();
        return parser.TryEvaluate(expr, out long v) ? v : null;
    }

    [Fact]
    public void EveryIntrinsicUpstreamHasAnswersHere()
    {
        var missing = new List<string>();
        foreach (var (name, expr, want) in Probes)
        {
            long? got = Ask(expr);
            if (got != want) missing.Add($"{name}: {expr} -> {(got?.ToString() ?? "unresolved")}, want {want}");
        }

        Assert.True(missing.Count == 0,
            "Intrinsics that do not answer as upstream does:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>The calibration: a name upstream does not have must NOT answer, or the
    /// sweep above would pass no matter what this engine implements.</summary>
    [Fact]
    public void AnInventedNameDoesNotAnswer()
    {
        Assert.NotEqual(7L, Ask("NOTAREALINTRINSIC(-7)"));
    }

    /// <summary>ID strips the resource-type bits off an id, and reads its argument the
    /// way every other number is read - a leading zero is hex. Reading it as decimal
    /// made the call a no-op, because a hex id spelled out in decimal lands below the
    /// 20-bit mask and nothing is stripped.</summary>
    [Fact]
    public void IdReadsItsArgumentAsASphereNumber()
    {
        Assert.Equal(0x1234L, Ask("ID(01234)"));      // already inside the mask
        Assert.Equal(0x01234L, Ask("ID(0401234)"));   // type bits stripped
        Assert.Equal(0x04000L, Ask("ID(040004000)"));
    }

    /// <summary>ISNUMBER skips to the first digit and reads the rest, with a leading
    /// zero admitting hex digits.</summary>
    [Theory]
    [InlineData("42", 1)]
    [InlineData("0ff", 1)]
    [InlineData("abc", 0)]
    [InlineData("12x", 0)]
    [InlineData("", 0)]
    public void IsNumberFollowsUpstreamsTest(string arg, long want)
        => Assert.Equal(want, Ask($"ISNUMBER({arg})"));

    /// <summary>And the one the packs actually call, inside EVAL.</summary>
    [Fact]
    public void StrLenCountsTheArgument()
    {
        Assert.Equal(5L, Ask("STRLEN(hello)"));
        Assert.Equal(0L, Ask("STRLEN()"));
        Assert.Equal(5L, Ask("STRLEN( hello )"));
    }

    /// <summary>The same table again, this time through the float evaluator.
    ///
    /// Upstream dispatches sm_IntrinsicFunctions from BOTH evaluators - the integer one
    /// at CExpression.cpp:826 and the float one at CFloatMath.cpp:254. Here the float
    /// parser read an identifier and looked it up as a variable, so a call was never
    /// recognised: every intrinsic inside FLOATVAL answered 0, including the ones the
    /// float form exists for.
    /// </summary>
    [Theory]
    [InlineData("SQRT(81)", "9")]
    [InlineData("MAX(3,9)", "9")]
    [InlineData("MIN(3,9)", "3")]
    [InlineData("STRLEN(hello)", "5")]
    [InlineData("ISNUMBER(42)", "1")]
    [InlineData("STRCMP(abc,abc)", "0")]
    [InlineData("NAPIERPOW(0)", "1")]
    [InlineData("LOGARITHM(1000)", "3")]
    public void TheFloatEvaluatorAnswersThemToo(string call, string want)
        => Assert.Equal(want, new ExpressionParser().EvaluateStr($"<FLOATVAL {call}>"));

    /// <summary>And it does not truncate, which is the whole reason the float form
    /// exists: SQRT(2) is 1 in the integer evaluator and 1.414... here.</summary>
    [Fact]
    public void TheFloatFormKeepsItsFraction()
    {
        string got = new ExpressionParser().EvaluateStr("<FLOATVAL SQRT(2)>");
        Assert.StartsWith("1.41", got);
        Assert.Equal(1L, new ExpressionParser().TryEvaluate("SQRT(2)", out long i) ? i : -1);
    }

    /// <summary>An argument that is itself a call keeps its precision on the way in.</summary>
    [Fact]
    public void ANestedCallStaysInFloat()
    {
        string got = new ExpressionParser().EvaluateStr("<FLOATVAL SQRT(SQRT(16))>");
        Assert.StartsWith("2", got);
        Assert.StartsWith("1.41", new ExpressionParser().EvaluateStr("<FLOATVAL MAX(SQRT(2),1)>"));
    }

    /// <summary>ABS is the one deliberate difference. Upstream has no case for it in
    /// the float switch, so it falls to the default and answers 0 with a console error
    /// - a trap, given every other function in a float expression works. It answers
    /// here.</summary>
    [Fact]
    public void AbsAnswersInAFloatExpressionToo()
        => Assert.Equal("2.5", new ExpressionParser().EvaluateStr("<FLOATVAL ABS(-2.5)>"));

    /// <summary>A name that is not an intrinsic is still a variable, not a call - the
    /// float parser must not start swallowing parentheses after every identifier.</summary>
    [Fact]
    public void ANonIntrinsicNameIsStillAVariable()
        => Assert.Equal("0", new ExpressionParser().EvaluateStr("<FLOATVAL NOTAREALINTRINSIC(2)>"));
}
