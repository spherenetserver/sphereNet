using SphereNet.Scripting.Expressions;

namespace SphereNet.Tests;

/// <summary>
/// The operators a real pack writes inside an expression.
///
/// Never measured before: the sweeps cover the NAMES a script uses - members,
/// verbs, assigned keys, functions - and say nothing about the arithmetic between
/// them. A missing operator does not announce itself either; the expression parses
/// to something, and the something is wrong.
///
/// These are the operators the reference distribution actually uses, with the shapes
/// it writes them in. f_getDefnameFromCorpse alone needs the shift and the bitwise
/// or in one line: &lt;HVAL (&lt;DEF.RES_CHARDEF&gt; &lt;&lt;
/// &lt;DEF.RESTYPE_BIT_SHIFT&gt;)|&lt;MOREX&gt;&gt;.
/// </summary>
public sealed class ExpressionOperatorTests
{
    private static long Eval(string expr)
    {
        Assert.True(new ExpressionParser().TryEvaluate(expr, out long value),
            $"the engine did not evaluate: {expr}");
        return value;
    }

    [Theory]
    // arithmetic
    [InlineData("2+3", 5)]
    [InlineData("10-4", 6)]
    [InlineData("6*7", 42)]
    [InlineData("20/4", 5)]
    [InlineData("17%5", 2)]
    // bitwise - the corpse-defname helper needs the shift and the or together
    [InlineData("1<<4", 16)]
    [InlineData("256>>4", 16)]
    [InlineData("(6<<2)|3", 27)]
    [InlineData("12&10", 8)]
    [InlineData("12|3", 15)]
    [InlineData("12^10", 6)]
    // logical
    [InlineData("1&&1", 1)]
    [InlineData("1&&0", 0)]
    [InlineData("0||1", 1)]
    [InlineData("0||0", 0)]
    [InlineData("!0", 1)]
    [InlineData("!5", 0)]
    // comparison
    [InlineData("5>3", 1)]
    [InlineData("3>=3", 1)]
    [InlineData("2<1", 0)]
    [InlineData("2<=2", 1)]
    [InlineData("4==4", 1)]
    [InlineData("4!=4", 0)]
    // precedence: the reason a pack can write these without bracketing everything
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("1+2>2", 1)]
    public void AnOperatorARealPackWritesEvaluates(string expr, long expected)
    {
        Assert.Equal(expected, Eval(expr));
    }

    /// <summary>The flag test every script writes: a status flag masked out of a
    /// bitfield, which is the single most common use of &amp; in the packs.</summary>
    [Fact]
    public void AFlagTestMasksTheBitItNames()
    {
        // 016 is 0x16 = 10110b, so the 0x04 bit IS set and the 0x20 bit is not.
        Assert.Equal(4, Eval("016 & 04"));     // both operands leading-zero hex
        Assert.Equal(0, Eval("016 & 020"));
    }

    /// <summary>A leading zero is hex inside an expression too, not only in a key's
    /// argument - 044 is 68. Reading it as decimal would give a wrong answer that
    /// still looks like an answer.</summary>
    [Fact]
    public void ALeadingZeroIsHexInsideAnExpression()
    {
        Assert.Equal(68, Eval("044"));
        Assert.Equal(0x1BF2, Eval("01bf2"));
        Assert.Equal(44, Eval("44"));
    }
}
