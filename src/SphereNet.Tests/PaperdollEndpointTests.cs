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
        [new PaperdollItemInfo(5, 0x40000001, 0x1517, 0x21, "shirt")],
        NotoTitle: "Glorious", FameTitle: "Lord", FullName: $"The Glorious Lord Char{serial}",
        GuildAbbrev: "ABC", GuildTitle: "Knight", TradeTitle: "Knight");

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

        // A free port is only free until someone takes it: FreePort releases the one
        // it found, and under a full parallel run another test can bind it before
        // this host does, or the host is slow to come up. Either left every test of
        // the class failing in its setup, whatever it tested - so a host that does not
        // answer is replaced on a fresh port rather than given up on.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int port = FreePort();
            var host = new PanelHost(Context(ini), port, new PanelLogSink(),
                LoggerFactory.Create(_ => { }).CreateLogger("paperdoll-test"));
            host.Start();
            var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    if ((await http.GetAsync("/health")).IsSuccessStatusCode)
                    {
                        _hosts.Add(host);
                        _clients.Add(http);
                        return http;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            http.Dispose();
            host.Dispose();
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
        // The name line's parts, for a page that styles them separately.
        Assert.Equal("Glorious", doc.RootElement.GetProperty("notoTitle").GetString());
        Assert.Equal("Lord", doc.RootElement.GetProperty("fameTitle").GetString());
        Assert.Equal("The Glorious Lord Char1", doc.RootElement.GetProperty("fullName").GetString());
        Assert.Equal("ABC", doc.RootElement.GetProperty("guildAbbrev").GetString());
        Assert.Equal("Knight", doc.RootElement.GetProperty("guildTitle").GetString());
        Assert.Equal("Knight", doc.RootElement.GetProperty("tradeTitle").GetString());
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
