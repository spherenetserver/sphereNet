using SphereNet.Panel;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The public paperdoll rules that need no web host: which origins get CORS, which
/// serials a request may name, and the per-client quota. They lived in
/// PaperdollEndpointTests, whose setup starts two Kestrel hosts per test - so a host
/// that was slow to start under a full parallel run failed these too, although none of
/// them touches a host.
/// </summary>
public sealed class PaperdollPublicRulesTests
{
    [Theory]
    [InlineData("shard.example", "https://shard.example", "https://shard.example")]
    [InlineData("shard.example", "http://SHARD.example", "http://SHARD.example")]
    [InlineData("shard.example", "https://evil.example", null)]
    [InlineData("shard.example", "https://shard.example:8443", null)]
    [InlineData("shard.example:8443", "https://shard.example:8443", "https://shard.example:8443")]
    [InlineData("https://shard.example/", "https://shard.example", "https://shard.example")]
    [InlineData("*", "https://anything.example", "*")]
    [InlineData("", "https://shard.example", null)]
    [InlineData("shard.example", "null", null)]
    public void CorsOriginMatchesListedHosts(string ini, string origin, string? expected) =>
        Assert.Equal(expected, PanelHost.PublicCorsOrigin(origin, PanelHost.ParsePublicOrigins(ini)));

    [Theory]
    [InlineData("123", 123u, false)]
    [InlineData("123.png", 123u, true)]
    [InlineData("123.JSON", 123u, false)]
    [InlineData("0x1A2B.png", 0x1A2Bu, true)]
    [InlineData("01a2b", 0x1A2Bu, false)]
    [InlineData("0X3FFFFFFF", 0x3FFFFFFFu, false)]
    public void SerialParsesHexAndDecimal(string raw, uint serial, bool png)
    {
        Assert.True(PanelHost.TryParsePaperdollRequest(raw, out uint s, out bool p));
        Assert.Equal(serial, s);
        Assert.Equal(png, p);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0x40000000")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("abc")]
    [InlineData("")]
    public void SerialRejectsNonCharacterValues(string raw) =>
        Assert.False(PanelHost.TryParsePaperdollRequest(raw, out _, out _));

    [Fact]
    public void LimiterAllowsTheQuotaPerWindow()
    {
        long now = 0;
        var limiter = new PublicRequestLimiter(3, TimeSpan.FromMinutes(1), () => now);
        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));
        Assert.False(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("b"));
        now += 60_000;
        Assert.True(limiter.TryAcquire("a"));
    }
}
