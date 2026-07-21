using System.Linq;
using SphereNet.Scripting.Expressions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 04 — expression engine process-fatal input hardening.
///   C4: unbounded native recursion (parens / operator chains / unary chains)
///       → StackOverflow, which is uncatchable and kills the process.
///   H1: {lo hi} brace random overflowed hi+1 at long.MaxValue → throw on the tick.
///   H2: ASCPAD with an attacker-sized count built a multi-GB string → OOM.
///   H3: CHR of a UTF-16 surrogate code point threw from ConvertFromUtf32.
///
/// H4 (the shared, mutable ExpressionParser used across parallel workers) is a
/// threading concern tied to İş 02 keeping trigger evaluation on the serial
/// apply phase, and is addressed there rather than here.
/// </summary>
public sealed class ExpressionHardeningTests
{
    // ---- C4: recursion depth guard (must fail controlled, never StackOverflow) ----

    [Fact]
    public void DeeplyNestedParens_FailsControlled_NoStackOverflow()
    {
        var p = new ExpressionParser();
        string expr = new string('(', 10000) + "1" + new string(')', 10000);
        var ex = Record.Exception(() => p.Evaluate(expr));
        Assert.Null(ex);
    }

    [Fact]
    public void LongOperatorChain_FailsControlled_NoStackOverflow()
    {
        var p = new ExpressionParser();
        string expr = string.Concat(Enumerable.Repeat("1+", 10000)) + "1";
        var ex = Record.Exception(() => p.Evaluate(expr));
        Assert.Null(ex);
    }

    [Fact]
    public void LongUnaryChain_FailsControlled_NoStackOverflow()
    {
        var p = new ExpressionParser();
        string expr = new string('-', 10000) + "1";
        var ex = Record.Exception(() => p.Evaluate(expr));
        Assert.Null(ex);
    }

    [Fact]
    public void FloatDeeplyNested_FailsControlled_NoStackOverflow()
    {
        var p = new ExpressionParser();
        string expr = new string('(', 10000) + "1.0" + new string(')', 10000);
        var ex = Record.Exception(() => p.EvaluateFloat(expr));
        Assert.Null(ex);
    }

    [Fact]
    public void ModestNesting_StillEvaluatesCorrectly()
    {
        // The guard must not regress ordinary nesting depth.
        var p = new ExpressionParser();
        Assert.Equal(8, p.Evaluate("2*3+1"));          // right-fold: 2*(3+1)
        Assert.Equal(6, p.Evaluate("((((((6))))))"));  // 6 nested parens
        Assert.Equal(5, p.Evaluate("--5"));            // double negate
    }

    // ---- H1: {lo hi} brace random must never overflow / throw ----

    [Fact]
    public void BraceRange_AtLongMaxValue_DoesNotThrow_AndStaysInRange()
    {
        var p = new ExpressionParser();

        long v1 = 0, v2 = 0, v3 = 0;
        var ex = Record.Exception(() =>
        {
            v1 = p.Evaluate("{1 0x7fffffffffffffff}");                    // [1, MaxValue]
            v2 = p.Evaluate("{0x7fffffffffffffff 0x7fffffffffffffff}");   // {max max}
            v3 = p.Evaluate("{100 1}");                                   // inverted written form
        });

        Assert.Null(ex);
        Assert.True(v1 >= 1);                       // in range, no crash
        Assert.Equal(long.MaxValue, v2);            // lo == hi collapses to the value
        Assert.InRange(v3, 1, 100);                 // Min/Max normalizes the order
    }

    // ---- H2: ASCPAD count is clamped, no OOM ----

    [Fact]
    public void AscPad_HugeCount_IsClamped_NotOutOfMemory()
    {
        var p = new ExpressionParser();

        string r = "";
        var ex = Record.Exception(() => r = p.EvaluateStr("<ASCPAD 2147483647,x>"));
        Assert.Null(ex);

        // Clamped to 4096 groups: first is 'x' (0x78), the rest padding "00".
        var groups = r.Split(' ');
        Assert.Equal(4096, groups.Length);
        Assert.Equal("78", groups[0]);
        Assert.Equal("00", groups[1]);
    }

    [Fact]
    public void AscPad_NegativeCount_YieldsEmpty()
    {
        var p = new ExpressionParser();
        Assert.Equal("", p.EvaluateStr("<ASCPAD -5,x>"));
    }

    // ---- H3: CHR rejects surrogates, keeps valid code points ----

    [Fact]
    public void Chr_SurrogateCodePoints_ReturnEmpty_NoThrow()
    {
        var p = new ExpressionParser();
        Assert.Equal("", p.EvaluateStr("<CHR 0xd800>"));
        Assert.Equal("", p.EvaluateStr("<CHR 0xdfff>"));
    }

    [Fact]
    public void Chr_ValidCodePoints_StillProduceCharacters()
    {
        var p = new ExpressionParser();
        Assert.Equal("A", p.EvaluateStr("<CHR 65>"));                       // BMP
        Assert.Equal(char.ConvertFromUtf32(0x1F600), p.EvaluateStr("<CHR 0x1F600>")); // supplementary
    }
}
