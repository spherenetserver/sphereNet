using SphereNet.Core.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// SERV.URL is the shard's own address, read from the ini.
///
/// Upstream stores the ini's URL= on the server and answers SC_URL with it verbatim
/// (CServerDef.cpp:403 and :492). SphereNet answered the literal string "localhost"
/// and never read the key at all, which is not a cosmetic stub: the reference pack
/// builds its help-page links straight out of it —
///
///     DHTMLGUMP ... a href="&lt;SERV.URL&gt;" ... a href="&lt;SERV.URL&gt;/wiki" ...
///     SRC.OpenWebUrl &lt;SERV.URL&gt;/donates
///
/// — so every one of them pointed at whatever machine the server was running on,
/// with no scheme in front of it. A client asked to open "localhost" hands it to the
/// shell, which cannot tell what it is and asks; on a fullscreen client that
/// question opens BEHIND the game and the session looks frozen from the inside.
/// </summary>
public sealed class ServerUrlConfigTests(ITestOutputHelper output)
{
    private static SphereConfig Load(string body)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"url_cfg_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, body);
        try
        {
            var parser = new IniParser();
            parser.Load(tmp);
            var cfg = new SphereConfig();
            cfg.LoadFromIni(parser);
            return cfg;
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData("URL=www.uo.com", "www.uo.com")]     // as the stock ini writes it
    [InlineData("URL=shard.example.com/uo", "shard.example.com/uo")]
    [InlineData("URL=shard.example.com   // the website", "shard.example.com")]
    [InlineData("URL=  shard.example.com  ", "shard.example.com")]
    public void TheIniValueIsAnsweredVerbatim(string line, string expected)
    {
        var cfg = Load("[SPHERE]\n" + line + "\n");
        output.WriteLine($"{line} -> '{cfg.Url}'");
        Assert.Equal(expected, cfg.Url);
    }

    /// <summary>A scheme cannot be written here, and that is not our bug to fix:
    /// "//" opens a comment anywhere in a line and upstream's reader breaks on it
    /// too (CScript.cpp:406-410), so "https://x" is read as "https:" on both engines.
    /// It is pinned because it looks exactly like a parser fault and is not — the
    /// key holds a bare host, which is why the stock ini ships URL=www.uo.com and
    /// why the reference pack puts the scheme on itself (FUNCTION OpenWebUrl builds
    /// "https:/" + "/" + args, and SERV.URLLINK wraps the value in https:// upstream).
    /// A pack that drops the bare value into an href produces a scheme-less link,
    /// which a browser launcher cannot resolve.</summary>
    [Fact]
    public void ASchemeInTheIniIsCutAtTheCommentMarker()
    {
        var cfg = Load("[SPHERE]\nURL=https://example.com\n");
        output.WriteLine($"URL=https://example.com -> '{cfg.Url}'");
        Assert.Equal("https:", cfg.Url);
    }

    [Fact]
    /// <summary>With no key the project's own address stands in, rather than a name
    /// that resolves to whatever machine the server happens to be running on. That
    /// was the whole defect: "localhost" is a real host and a browser will try it.</summary>
    public void NoKeyFallsBackToTheProjectAddress()
    {
        var cfg = Load("[SPHERE]\nServName=Test\n");
        output.WriteLine($"no URL key -> '{cfg.Url}'");
        Assert.Equal("www.spherenetserver.com", cfg.Url);
    }
}
