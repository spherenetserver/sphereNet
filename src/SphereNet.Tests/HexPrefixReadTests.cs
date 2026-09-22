using SphereNet.Scripting.Expressions;

namespace SphereNet.Tests;

/// <summary>
/// A leading H reads a member as hex, the mirror of the D that reads it as decimal.
///
/// Upstream calls it the shorthand for &lt;HVAL &lt;SOMEVAL&gt;&gt; and leaves a
/// negative value alone (CScriptObj.cpp:553). It tries the whole name first and only
/// then the prefix, so a member that simply begins with H - HITS, HOME - is never
/// mangled; the same guard is here, just reached from the other side.
///
/// The answer carries the leading zero every other hex read in this engine writes: that
/// zero is what marks a number as hex when it is read back. Sixteen written as "10"
/// would come back as ten.
/// </summary>
public sealed class HexPrefixReadTests
{
    private static ExpressionParser Parser() => new()
    {
        VariableResolver = name => name.ToUpperInvariant() switch
        {
            "STR" => "16",
            "HITS" => "45",          // a member that begins with H
            "MORE1" => "08981",      // already hex
            "DEBT" => "-5",
            "NAME" => "Bob",
            _ => null
        },
        FunctionResolver = name => name.Trim().ToUpperInvariant() switch
        {
            "HESAPLA" => "1234",     // a [FUNCTION] that begins with H
            _ => null
        }
    };

    [Fact]
    public void ALeadingHReadsTheMemberAsHex()
    {
        Assert.Equal("010", Parser().EvaluateStr("<hSTR>"));      // 16
        Assert.Equal("02D", Parser().EvaluateStr("<hHITS>"));     // 45
    }

    /// <summary>A value that is already hex is read and written back as hex, so the
    /// prefix is idempotent rather than doubling the conversion.</summary>
    [Fact]
    public void AValueThatIsAlreadyHexStaysItself()
    {
        Assert.Equal("08981", Parser().EvaluateStr("<hMORE1>"));
    }

    /// <summary>A member whose own name begins with H still answers. This is the
    /// trap the D side had: the prefix must not eat a real name.</summary>
    [Fact]
    public void AMemberThatBeginsWithHIsNotEaten()
    {
        Assert.Equal("45", Parser().EvaluateStr("<HITS>"));
        Assert.Equal("1234", Parser().EvaluateStr("<HESAPLA>"));
    }

    /// <summary>A negative value is left as it is, as upstream leaves it.</summary>
    [Fact]
    public void ANegativeValueIsLeftAlone()
    {
        Assert.Equal("-5", Parser().EvaluateStr("<hDEBT>"));
    }

    /// <summary>Text that is not a number becomes zero, because the prefix asks for a
    /// NUMBER and upstream converts unconditionally:
    /// <c>if (*sVal != '-') sVal.FormatLLHex(Str_ToLL(sVal).value_or(0));</c>
    /// (CScriptObj.cpp:554-562) — the negative above is the only value left as
    /// written. This file used to assert the opposite, that the text came back
    /// unchanged, which contradicted the very lines its own header cites. It matters:
    /// the D side is the same code and passing text through there let
    /// <c>ELSEIF (&lt;dLOCAL.ASC&gt; == 0)</c> compare against a string, read its
    /// leading number and never reach the "== 0" — which is how the reference pack's
    /// IsBlank came to answer "blank" for every non-empty string.</summary>
    [Fact]
    public void TextBecomesZero()
    {
        Assert.Equal("00", Parser().EvaluateStr("<hNAME>"));
    }
}
