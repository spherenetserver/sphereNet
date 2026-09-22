using Microsoft.Extensions.Logging;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every expression function the reference dialogs call is one the engine evaluates.
///
/// The fourth shape a script can take, after a member read, a bare verb and an
/// assignment: &lt;NAME args&gt; inside a value. The dialogs lean on it hardest -
/// &lt;EVAL&gt; for arithmetic, &lt;QVAL&gt; for the inline conditional every gump row
/// uses, &lt;ISEMPTY&gt; to decide whether a row exists at all - so a missing one does
/// not break quietly here, it renders a dialog full of blanks.
///
/// Resolved by ASKING the parser, not by grepping the engine for the name: the
/// names are matched without appearing as quoted literals, so a search for "EVAL"
/// in the C# finds nothing while the engine evaluates it perfectly well. That is the
/// same trap that made an earlier coverage attempt report working code as missing.
/// </summary>
public sealed class DialogExpressionFunctionTests(ITestOutputHelper outp)
{
    private static ExpressionParser NewParser() => new();

    /// <summary>The names, with an argument shape the dialogs actually write and the
    /// answer that shape must produce. A function that parsed but answered nothing
    /// would render an empty gump row just as surely as a missing one.</summary>
    public static TheoryData<string, string, long> DialogFunctions() => new()
    {
        // <EVAL expr> - arithmetic, 282 uses in the reference dialogs alone.
        { "EVAL", "<EVAL 2+3>", 5 },
        { "EVAL", "<EVAL (10/2)-1>", 4 },
        // <QVAL (cond)? a : b> - the inline conditional behind almost every
        // conditional gump row.
        { "QVAL", "<QVAL (1)? 7 : 9>", 7 },
        { "QVAL", "<QVAL (0)? 7 : 9>", 9 },
        // <ISEMPTY x> - 61 uses, gating whether a row is drawn.
        { "ISEMPTY", "<ISEMPTY >", 1 },
        { "ISEMPTY", "<ISEMPTY 5>", 0 },
        // <HVAL n> / <FVAL n> - hex and float renderings of a number.
        { "HVAL", "<HVAL 255>", 255 },
        // <ISNUM x> - is this token a number.
        { "ISNUM", "<ISNUM 12>", 1 },
        { "ISNUM", "<ISNUM abc>", 0 },
    };

    [Theory]
    [MemberData(nameof(DialogFunctions))]
    public void ADialogExpressionFunctionEvaluates(string name, string expr, long expected)
    {
        var parser = NewParser();
        Assert.True(parser.TryEvaluate(expr, out long value),
            $"{name}: the engine did not evaluate {expr}");
        Assert.Equal(expected, value);
    }

    /// <summary>The string-shaped ones answer TEXT, so they are asked the way a gump
    /// row asks them - through the parser's angle-bracket expansion rather than as a
    /// number. The argument shapes are the ones the packs write: STRSUB takes them
    /// comma-separated, STREAT takes a whole string and gives back everything after
    /// the first token.</summary>
    [Theory]
    [InlineData("STRSUB", "<STRSUB 0,3,abcdef>", "abc")]
    // Str_ParseCmds splits on spaces too - the pack's save_finished message is
    // <STRSUB 0 3 <ARGS>> over the elapsed seconds.
    [InlineData("STRSUB", "<STRSUB 0 3 0.1734>", "0.1")]
    [InlineData("STRSUB", "<STRSUB 2 0 abcdef>", "cdef")]
    [InlineData("STRSUB", "<STRSUB -2 2 abcdef>", "ef")]
    [InlineData("STRSUB", "<STRSUB 0 5 \"ab cd\">", "ab cd")]
    [InlineData("STRARG", "<STRARG one two three>", "one")]
    [InlineData("STREAT", "<STREAT one two three>", "two three")]
    // <FVAL n> renders a TENTHS value as "X.Y" (SSC_FVAL, CScriptObj.cpp:729) -
    // which is how every skill and weight readout in the reference dialogs is
    // written: <FVAL <SRC.IMBUING>>. Asked as a number it is the wrong question.
    [InlineData("FVAL", "<FVAL 995>", "99.5")]
    [InlineData("FVAL", "<FVAL -25>", "-2.5")]
    public void AStringDialogFunctionAnswersText(string name, string expr, string expected)
    {
        string got = NewParser().ResolveAngleBrackets(expr);
        outp.WriteLine($"{name}: {expr} -> '{got}'");
        Assert.Equal(expected, got);
    }
}
