using System.Net;
using Microsoft.AspNetCore.Http;
using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Panel.Auth;
using SphereNet.Panel.Updates;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Panel pieces that need no running host: the script checker the editor saves
/// through, the proxy-aware client address, session revocation, the Host's crash
/// backoff and the update script's wait.
/// </summary>
public sealed class PanelHardeningUnitTests
{
    // --- Script checker ---------------------------------------------------

    [Theory]
    [InlineData("FORCHARS 3")]
    [InlineData("FORITEMS 5")]
    [InlineData("FORPLAYERS 18")]
    [InlineData("FORCLIENTS 18")]
    [InlineData("FORCONTTYPE t_gold")]
    [InlineData("FORCHARLAYER 21")]
    [InlineData("FORTIMERF f_x")]
    public void AnObjectLoopClosedByEndforIsValid(string loop)
    {
        var result = PanelHost.ValidateScriptContent($"[FUNCTION f_test]\n{loop}\n  SAY hi\nENDFOR\n");
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void AnIfWrittenWithoutASpaceStillOpensABlock()
    {
        var result = PanelHost.ValidateScriptContent("[FUNCTION f_test]\nIF(<ARGN1>)\n  SAY hi\nENDIF\n");
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ProseInATextSectionOpensNothing()
    {
        const string script = "[BOOK b_test 1]\nIf you read this, for sure it works.\n" +
                              "[DIALOG d_test TEXT]\nIf you want\nFor you\n";
        var result = PanelHost.ValidateScriptContent(script);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void AnUnclosedBlockIsReportedInItsOwnSection()
    {
        var result = PanelHost.ValidateScriptContent("[FUNCTION f_a]\nIF 1\n[FUNCTION f_b]\nENDIF\n");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.StartsWith("Line 2: IF block is not closed"));
        Assert.Contains(result.Errors, e => e.StartsWith("Line 4: END block without IF"));
    }

    // --- Proxy awareness ---------------------------------------------------

    private static HttpContext Request(string remote, params (string Key, string Value)[] headers)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        foreach (var (k, v) in headers) http.Request.Headers[k] = v;
        return http;
    }

    [Fact]
    public void ALocalProxysForwardedClientIsTheRateLimitKey()
    {
        var http = Request("127.0.0.1", ("X-Forwarded-For", "10.0.0.9, 203.0.113.7"));
        Assert.Equal("203.0.113.7", PanelHost.ClientAddress(http));
        Assert.True(PanelHost.IsProxied(http.Request));
    }

    [Fact]
    public void AForwardedHeaderFromARemotePeerIsNotBelieved()
    {
        var http = Request("198.51.100.4", ("X-Forwarded-For", "127.0.0.1"));
        Assert.Equal("198.51.100.4", PanelHost.ClientAddress(http));
    }

    [Fact]
    public void AnUnlistedHostIsRefusedAndStarAllowsAll()
    {
        var allowed = new HashSet<string>(["localhost", "panel.example.org"], StringComparer.OrdinalIgnoreCase);
        Assert.True(PanelHost.IsAllowedHost("Panel.Example.org", allowed));
        Assert.False(PanelHost.IsAllowedHost("rebind.example", allowed));
        Assert.True(PanelHost.IsAllowedHost("anything", null));
    }

    // --- Sessions -----------------------------------------------------------

    [Fact]
    public void RevokeAllEndsEverySessionAndAnnouncesEach()
    {
        var store = new TokenStore();
        string a = store.Create(), b = store.Create();
        var announced = new List<string>();
        store.TokenInvalidated += announced.Add;

        Assert.Equal(2, store.RevokeAll());
        Assert.False(store.Validate(a));
        Assert.False(store.Validate(b));
        Assert.Equal(2, announced.Count);
    }

    // --- Host crash restart / updater wait -------------------------------------

    [Fact]
    public void CrashRestartsBackOffAndCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ServerProcess.CrashRestartDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), ServerProcess.CrashRestartDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(20), ServerProcess.CrashRestartDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(120), ServerProcess.CrashRestartDelay(10));
    }

    [Fact]
    public void TheUpdateScriptWaitsAsLongAsItIsTold()
    {
        // The Host may spend HostShutdownTimeoutMs on the server's shutdown save;
        // a fixed 120 s kill in the script cut that save short.
        Assert.Contains("[int]$HostWaitSeconds", UpdaterScript.PowerShell);
        Assert.Contains("WaitForExit($HostWaitSeconds * 1000)", UpdaterScript.PowerShell);
        Assert.DoesNotContain("WaitForExit(120000)", UpdaterScript.PowerShell);
    }

    // --- Script pack layout ---------------------------------------------------

    [Theory]
    // Scripts-X keeps its tree at the repository root.
    [InlineData("Scripts-X-main/core/sphere_defs.scp", false, "core/sphere_defs.scp")]
    [InlineData("Scripts-X-main/spheretables.scp", false, "spheretables.scp")]
    [InlineData("Scripts-X-main/_incomplete/x.scp", false, null)]
    [InlineData("Scripts-X-main/_syntax highlighting/sphere.xml", false, null)]
    [InlineData("Scripts-X-main/", false, null)]
    // A pack with a scripts/ folder installs only that folder.
    [InlineData("Scripts-T-main/scripts/items/a.scp", true, "items/a.scp")]
    [InlineData("Scripts-T-main/README.md", true, null)]
    public void PackEntriesMapUnderTheScriptsFolder(string entry, bool nested, string? expected)
    {
        Assert.Equal(expected, PanelHost.PackRelativePath(entry, nested));
    }

    [Fact]
    public void TheDefaultPackIsTheSourceXScripts()
    {
        Assert.Equal("Sphereserver/Scripts-X", PanelHost.DefaultScriptPackRepo);
    }
}
