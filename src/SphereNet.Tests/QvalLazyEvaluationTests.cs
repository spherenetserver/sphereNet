using SphereNet.Scripting.Expressions;

namespace SphereNet.Tests;

/// <summary>
/// QVAL evaluates only the branch it returns.
///
/// Upstream treats QVAL as a special case in the text walker precisely so it can do
/// this: it finds where the statement ends without parsing what is inside, picks the
/// branch, and only then evaluates that one (CExpression.cpp:2446, "lazy evaluation,
/// instead of fully evaluating the whole string on the first pass").
///
/// It matters because a branch is not always safe to read. The packs write
/// &lt;QVAL (&lt;REF1.ISVALID&gt;)?&lt;REF1.NAME&gt;:&gt; - the guard exists exactly
/// because reading the name when the reference is not valid is the thing being avoided.
/// Evaluating both sides does the read the guard was written to prevent.
/// </summary>
public sealed class QvalLazyEvaluationTests
{
    /// <summary>A parser that records every name it is asked for.</summary>
    private static (ExpressionParser P, List<string> Asked) Recording()
    {
        var asked = new List<string>();
        var p = new ExpressionParser
        {
            VariableResolver = name =>
            {
                asked.Add(name.ToUpperInvariant());
                return name.ToUpperInvariant() switch
                {
                    "YES" => "1",
                    "NO" => "0",
                    "TAKEN" => "taken",
                    "SKIPPED" => "skipped",
                    _ => null
                };
            }
        };
        return (p, asked);
    }

    [Fact]
    public void TheBranchNotTakenIsNeverRead()
    {
        var (p, asked) = Recording();
        Assert.Equal("taken", p.EvaluateStr("<QVAL (<YES>)?<TAKEN>:<SKIPPED>>"));
        Assert.DoesNotContain("SKIPPED", asked);
    }

    [Fact]
    public void AndTheOtherWayAround()
    {
        var (p, asked) = Recording();
        Assert.Equal("skipped", p.EvaluateStr("<QVAL (<NO>)?<TAKEN>:<SKIPPED>>"));
        Assert.DoesNotContain("TAKEN", asked);
    }

    /// <summary>The guard form the packs write, with an empty else.</summary>
    [Fact]
    public void AnEmptyElseBranchReadsNothing()
    {
        var (p, asked) = Recording();
        Assert.Equal("", p.EvaluateStr("<QVAL (<NO>)?<TAKEN>:>"));
        Assert.DoesNotContain("TAKEN", asked);
    }
}
