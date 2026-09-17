using System.Reflection;

namespace SphereNet.Tests;

/// <summary>
/// SERV.&lt;function&gt; is the same call as the bare function.
///
/// Upstream the SERV chain ends at CScriptObj::r_WriteVal, and that is where the
/// script function table lives (the SSC_ cases, CScriptObj.cpp:410) - so
/// &lt;SERV.CHR 65&gt; and &lt;CHR 65&gt; reach the same code. Here SERV had a switch of
/// its own with nothing behind it, so a SERV-prefixed function read fell through to
/// the defname lookup, which rejects anything containing a space, and returned
/// nothing.
///
/// What showed it: the live pack's packet scripts rebuild received bytes into text
/// with &lt;SERV.CHR &lt;byte&gt;&gt; in 350 places across three files, and every one of
/// them produced an empty string.
/// </summary>
public sealed class ServFunctionPrefixTests
{
    private static string Resolve(string property)
    {
        var method = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(null, [property]) ?? "";
    }

    /// <summary>The read the packet scripts make.</summary>
    [Fact]
    public void ChrThroughTheServPrefixReturnsTheCharacter()
    {
        Assert.Equal("A", Resolve("CHR 65"));
        Assert.Equal(" ", Resolve("CHR 32"));
    }

    /// <summary>And the rest of the table it shares a home with.</summary>
    [Theory]
    [InlineData("STRLEN abc", "3")]
    [InlineData("STRUPPER abc", "ABC")]
    [InlineData("STRLOWER ABC", "abc")]
    [InlineData("STRREVERSE abc", "cba")]
    [InlineData("ASC A", "41")]
    [InlineData("EVAL 2+3", "5")]
    [InlineData("MAX 3,7", "7")]
    public void TheFunctionTableIsReachableThroughServ(string property, string expected)
    {
        Assert.Equal(expected, Resolve(property));
    }

    /// <summary>A server property keeps its own answer - the fallback runs only after
    /// every case and the defname lookup have declined.</summary>
    [Fact]
    public void ARealServerPropertyStillWins()
    {
        Assert.Equal("SphereNet 1.0", Resolve("VERSION"));
    }

    /// <summary>A name nothing answers still reads back as nothing, and a second SERV
    /// hop does not re-enter the resolver.</summary>
    [Fact]
    public void AnUnknownNameIsStillUnknown()
    {
        Assert.Equal("", Resolve("NOT_A_SERVER_PROPERTY"));
        Assert.Equal("", Resolve("NOTAFUNCTION abc"));
        Assert.Equal("", Resolve("SERV.CHR 65"));
    }
}
