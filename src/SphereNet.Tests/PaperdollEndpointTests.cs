using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SphereNet.Panel;
using SphereNet.Panel.Logging;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The paperdoll endpoints against a real Kestrel host: the authenticated /api
/// pair, and the anonymous /public pair with its rules - off by default, players
/// only, one 404 for "not allowed" and "not found", CORS only for listed origins,
/// and nothing staff-only in the JSON.
/// </summary>
public sealed class PaperdollEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "paperdoll test";
    private const string AllowedOrigin = "https://shard.example";
    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private readonly List<string> _tmpDirs = [];
    private readonly List<PanelHost> _hosts = [];
    private readonly List<HttpClient> _clients = [];
    private HttpClient _enabled = null!;
    private HttpClient _disabled = null!;
    private int _pngCalls;

    private static PaperdollInfo Info(uint serial, bool player, int plevel) => new(
        serial, $"Char{serial}", "the Tester", $"Char{serial}, the Tester", 0x190, false,
        player, plevel, "secretaccount", true, true,
        [new PaperdollItemInfo(5, 0x40000001, 0x1517, 0x21, "shirt")]);

    private PanelContext Context(string iniPath) => new()
    {
        ServerName = "Test",
        IniPath = iniPath,
        AdminPassword = Core.Configuration.PasswordHelper.Hash(AdminPassword),
        IsServerRunning = () => true,
        GetPaperdoll = serial => serial switch
        {
            1 => Info(1, player: true, plevel: 1),
            2 => Info(2, player: true, plevel: 4),   // GM
            3 => Info(3, player: false, plevel: 1),  // NPC
            _ => null,
        },
        GetPaperdollPng = (serial, _) =>
        {
            Interlocked.Increment(ref _pngCalls);
            return serial is 1 or 2 or 3 ? FakePng : null;
        },
    };

    public async Task InitializeAsync()
    {
        // Host name, not the full origin: sphere.ini cuts a value at "//".
        _enabled = await StartAsync("PublicPaperdoll=1\r\nPublicPaperdollOrigins=shard.example\r\n");
        _disabled = await StartAsync("");
    }

    private async Task<HttpClient> StartAsync(string extraIni)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_pd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tmpDirs.Add(dir);
        string ini = Path.Combine(dir, "sphere.ini");
        File.WriteAllText(ini, "[SPHERE]\r\nServName=Test\r\nServPort=2593\r\n" + extraIni, Encoding.UTF8);

        int port = FreePort();
        var host = new PanelHost(Context(ini), port, new PanelLogSink(),
            LoggerFactory.Create(_ => { }).CreateLogger("paperdoll-test"));
        host.Start();
        _hosts.Add(host);
        var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        _clients.Add(http);
        for (int i = 0; i < 100; i++)
        {
            try
            {
                if ((await http.GetAsync("/health")).IsSuccessStatusCode) return http;
            }
            catch (HttpRequestException) { }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("panel host did not start listening");
    }

    public Task DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        foreach (var h in _hosts) h.Dispose();
        foreach (var d in _tmpDirs)
            try { Directory.Delete(d, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // --- public ------------------------------------------------------------

    [Theory]
    [InlineData("/public/paperdoll/1.png")]
    [InlineData("/public/paperdoll/1.json")]
    public async Task PublicEndpointsAre404WhenDisabled(string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _disabled.GetAsync(path)).StatusCode);
        Assert.Equal(0, Volatile.Read(ref _pngCalls));
    }

    [Fact]
    public async Task PublicPngOfAPlayerIsServedAndCacheable()
    {
        var res = await _enabled.GetAsync("/public/paperdoll/1.png");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakePng, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal(TimeSpan.FromSeconds(60), res.Headers.CacheControl?.MaxAge);
    }

    [Theory]
    [InlineData("/public/paperdoll/2.png")]      // staff
    [InlineData("/public/paperdoll/3.png")]      // NPC
    [InlineData("/public/paperdoll/99.png")]     // unknown
    [InlineData("/public/paperdoll/2.json")]
    [InlineData("/public/paperdoll/3.json")]
    [InlineData("/public/paperdoll/0x40000001.png")] // item serial range
    [InlineData("/public/paperdoll/nonsense.png")]
    public async Task PublicAnswersTheSame404ForHiddenAndMissing(string path)
    {
        var res = await _enabled.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Empty(await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PublicJsonOmitsStaffOnlyFields()
    {
        var res = await _enabled.GetAsync("/public/paperdoll/0x1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Char1, the Tester", doc.RootElement.GetProperty("paperdollText").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("equipment").GetArrayLength());
        Assert.DoesNotContain("privLevel", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretaccount", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("online", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorsHeaderOnlyForListedOrigins()
    {
        using var ok = new HttpRequestMessage(HttpMethod.Get, "/public/paperdoll/1.json");
        ok.Headers.Add("Origin", AllowedOrigin);
        var okRes = await _enabled.SendAsync(ok);
        Assert.Equal(AllowedOrigin,
            okRes.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null);

        using var bad = new HttpRequestMessage(HttpMethod.Get, "/public/paperdoll/1.json");
        bad.Headers.Add("Origin", "https://evil.example");
        var badRes = await _enabled.SendAsync(bad);
        Assert.False(badRes.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // --- authenticated -----------------------------------------------------

    [Theory]
    [InlineData("/api/paperdoll/1")]
    [InlineData("/api/paperdoll/1.png")]
    [InlineData("/API/Paperdoll/1.png")]
    public async Task ApiPaperdollRequiresAToken(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _enabled.GetAsync(path)).StatusCode);
        Assert.Equal(0, Volatile.Read(ref _pngCalls));
    }

    [Fact]
    public async Task ApiPaperdollServesAnyCharacterWithAToken()
    {
        string token = await LoginAsync(_enabled);

        var info = await GetAsync(_enabled, token, "/api/paperdoll/02");   // staff: allowed here
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        string body = await info.Content.ReadAsStringAsync();
        Assert.Contains("\"privLevel\":4", body);

        var png = await GetAsync(_enabled, token, "/api/paperdoll/3.png?frame=1");
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(_enabled, token, "/api/paperdoll/99")).StatusCode);
    }

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

    // --- parsing / limiter -------------------------------------------------

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

    // --- helpers -----------------------------------------------------------

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<string> LoginAsync(HttpClient http)
    {
        var res = await http.PostAsJsonAsync("/api/auth/login", new { password = AdminPassword });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.Token;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient http, string token, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Authorization", $"Bearer {token}");
        return http.SendAsync(req);
    }

    private sealed record LoginResponse(string Token, string ServerName);
}
