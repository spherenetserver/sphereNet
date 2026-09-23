using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Host;
using SphereNet.Network.Packets;
using SphereNet.Panel;
using SphereNet.Panel.Logging;
using SphereNet.Server.Admin;
using SphereNet.Server.Ipc;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The admin panel's character actions on a real character: SAY/EMOTE reach the
/// nearby broadcast as speech, a verb line runs the r_Verb chain (verb table,
/// [FUNCTION], property) with the panel as its source, and the quick actions go
/// through the same engine paths the GM commands use.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PanelCharacterActionsTests
{
    private sealed class FakeClient : ITextConsole
    {
        public readonly List<(string Text, ushort? Hue)> Messages = [];
        public PrivLevel GetPrivLevel() => PrivLevel.Player;
        public string GetName() => "client";
        public void SysMessage(string text) => Messages.Add((text, null));
        public void SysMessage(string text, ushort hue) => Messages.Add((text, hue));
    }

    private static (GameWorld World, Character Ch) Setup()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.BodyId = 0x0190;
        ch.Name = "Tester";
        ch.IsPlayer = true;
        ch.Str = 60; ch.Dex = 50; ch.Int = 40;
        ch.MaxHits = 60; ch.MaxMana = 40; ch.MaxStam = 50;
        ch.Hits = 60; ch.Mana = 40; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        return (world, ch);
    }

    private static PanelCharacterActions Actions(GameWorld world, ITextConsole? client = null,
        CommandHandler? commands = null, Action<Character>? redraw = null) =>
        new(world, _ => client, redraw, commands);

    private static List<PacketWriter> CaptureBroadcasts()
    {
        var sent = new List<PacketWriter>();
        Character.BroadcastNearby = (_, _, packet, _) => sent.Add(packet);
        return sent;
    }

    /// <summary>Field report: ADMIN from the panel said "applied" and opened
    /// nothing - the pack's ADMIN [FUNCTION] feeds SRC.CTAG and opens its dialog on
    /// SRC, and the panel was SRC. "command" hands the line to the player's own
    /// client as a typed .command instead.</summary>
    [Fact]
    public void CommandRunsTheLineAsThePlayersOwnCommand()
    {
        var (world, ch) = Setup();
        var ran = new List<(Character, string)>();
        var actions = new PanelCharacterActions(world, _ => null, null, null,
            (who, line) => { ran.Add((who, line)); return true; });

        var result = actions.Execute(ch.Uid.Value, new PlayerActionRequest("command", ".admin"));

        Assert.True(result.Ok);
        var (who, line) = Assert.Single(ran);
        Assert.Same(ch, who);
        Assert.Equal("admin", line);
    }

    [Fact]
    public void CommandNeedsThePlayerOnline()
    {
        var (world, ch) = Setup();
        var actions = new PanelCharacterActions(world, _ => null, null, null, (_, _) => null);

        var result = actions.Execute(ch.Uid.Value, new PlayerActionRequest("command", "admin"));

        Assert.False(result.Ok);
        Assert.Contains("not online", Assert.Single(result.Lines));
    }

    [Fact]
    public void SayBroadcastsTheLineAsTheCharactersSpeech()
    {
        var (world, ch) = Setup();
        var sent = CaptureBroadcasts();

        var result = Actions(world).Execute(ch.Uid.Value, new PlayerActionRequest("say", "Hail traveller"));

        Assert.True(result.Ok);
        var packet = Assert.Single(sent).Build().Span;
        Assert.Equal(0x1C, packet[0]);           // ASCII speech
        Assert.Equal(0, packet[9]);              // regular talk
        Assert.Contains("Hail traveller", Encoding.ASCII.GetString(packet));
    }

    [Fact]
    public void SayWithTextTheAsciiPacketCannotCarryGoesOutAsUnicode()
    {
        var (world, ch) = Setup();
        var sent = CaptureBroadcasts();

        Assert.True(Actions(world).Execute(ch.Uid.Value, new PlayerActionRequest("say", "Günaydın")).Ok);

        Assert.Equal(0xAE, Assert.Single(sent).Build().Span[0]);
    }

    [Fact]
    public void EmoteBroadcastsAnEmoteLine()
    {
        var (world, ch) = Setup();
        var sent = CaptureBroadcasts();

        var result = Actions(world).Execute(ch.Uid.Value, new PlayerActionRequest("emote", "waves"));

        Assert.True(result.Ok);
        var packet = Assert.Single(sent).Build().Span;
        Assert.Equal(0x1C, packet[0]);
        Assert.Equal(2, packet[9]);              // emote
        Assert.Contains("waves", Encoding.ASCII.GetString(packet));
    }

    [Fact]
    public void VerbLineSetsAPropertyOnTheCharacter()
    {
        var (world, ch) = Setup();

        var result = Actions(world).Execute(ch.Uid.Value, new PlayerActionRequest("verb", "HITS=12"));

        Assert.True(result.Ok);
        Assert.Equal(12, ch.Hits);
    }

    [Fact]
    public void VerbLineRunsAScriptFunctionWithThePanelAsSource()
    {
        var (world, ch) = Setup();
        (ObjBase Target, string Name, string Args, ITextConsole? Source)? call = null;
        ObjBase.RunScriptFunction = (obj, name, args, source) =>
        {
            if (!name.Equals("f_panel_test", StringComparison.OrdinalIgnoreCase)) return false;
            call = (obj, name, args, source);
            source?.SysMessage("function said hello");
            return true;
        };
        try
        {
            var result = Actions(world).Execute(ch.Uid.Value, new PlayerActionRequest("verb", "f_panel_test 5,6"));

            Assert.True(result.Ok);
            Assert.NotNull(call);
            Assert.Same(ch, call!.Value.Target);
            Assert.Equal("5,6", call.Value.Args);
            Assert.Equal(PrivLevel.Owner, call.Value.Source!.GetPrivLevel());
            Assert.Null(call.Value.Source.GetSourceChar());
            Assert.Contains("function said hello", result.Lines);
        }
        finally { ObjBase.RunScriptFunction = null; }
    }

    [Fact]
    public void AVerbNothingAcceptsIsReportedAsNotOk()
    {
        var (world, ch) = Setup();
        var result = Actions(world).Execute(ch.Uid.Value, new PlayerActionRequest("verb", "NO_SUCH_VERB_XYZ"));
        Assert.False(result.Ok);
    }

    [Fact]
    public void VerbChangingTheBodyRedrawsTheCharacterForItsOwnClient()
    {
        var (world, ch) = Setup();
        Character? redrawn = null;
        var result = Actions(world, redraw: c => redrawn = c)
            .Execute(ch.Uid.Value, new PlayerActionRequest("verb", "BODY=0x191"));
        Assert.True(result.Ok);
        Assert.Equal(0x191, ch.BodyId);
        Assert.Same(ch, redrawn);
    }

    [Fact]
    public void HealFillsEveryPoolAndCuresPoison()
    {
        var (world, ch) = Setup();
        ch.Hits = 3; ch.Mana = 1; ch.Stam = 2;
        ch.SetStatFlag(StatFlag.Poisoned);

        var result = Actions(world).Execute(ch.Uid.Value, new PlayerActionRequest("heal"));

        Assert.True(result.Ok);
        Assert.Equal(ch.MaxHits, ch.Hits);
        Assert.Equal(ch.MaxMana, ch.Mana);
        Assert.Equal(ch.MaxStam, ch.Stam);
        Assert.False(ch.IsPoisoned);
    }

    [Fact]
    public void KillThenResurrectGoThroughTheCharacterVerbs()
    {
        var (world, ch) = Setup();
        var actions = Actions(world);

        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("kill")).Ok);
        Assert.True(ch.IsDead);
        Assert.False(actions.Execute(ch.Uid.Value, new PlayerActionRequest("kill")).Ok);

        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("resurrect")).Ok);
        Assert.False(ch.IsDead);
        Assert.False(actions.Execute(ch.Uid.Value, new PlayerActionRequest("resurrect")).Ok);
    }

    [Fact]
    public void FreezeAndHideToggleTheStatFlags()
    {
        var (world, ch) = Setup();
        var actions = Actions(world);

        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("freeze")).Ok);
        Assert.True(ch.IsStatFlag(StatFlag.Freeze));
        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("unfreeze")).Ok);
        Assert.False(ch.IsStatFlag(StatFlag.Freeze));

        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("hide")).Ok);
        Assert.True(ch.IsStatFlag(StatFlag.Hidden));
        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("unhide")).Ok);
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void TeleportMovesWithinTheMapAndRefusesOutsideIt()
    {
        var (world, ch) = Setup();
        var actions = Actions(world);

        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("teleport", X: 1500, Y: 1600, Z: 5)).Ok);
        Assert.Equal(new Point3D(1500, 1600, 5, 0), ch.Position);

        Assert.False(actions.Execute(ch.Uid.Value, new PlayerActionRequest("teleport", X: 7000, Y: 100)).Ok);
        Assert.False(actions.Execute(ch.Uid.Value, new PlayerActionRequest("teleport", X: 10, Y: 10, Map: 9)).Ok);
        Assert.Equal(new Point3D(1500, 1600, 5, 0), ch.Position);
    }

    [Fact]
    public void MessageNeedsAnOnlineClientAndCarriesTheHue()
    {
        var (world, ch) = Setup();
        Assert.False(Actions(world).Execute(ch.Uid.Value, new PlayerActionRequest("message", "hi")).Ok);

        var client = new FakeClient();
        Assert.True(Actions(world, client).Execute(ch.Uid.Value, new PlayerActionRequest("message", "hi", Hue: 0x22)).Ok);
        Assert.Equal(("hi", (ushort?)0x22), Assert.Single(client.Messages));
    }

    [Fact]
    public void JailFreezesAndTagsAndUnjailReleases()
    {
        var (world, ch) = Setup();
        var commands = new CommandHandler();
        var actions = Actions(world, commands: commands);

        Assert.False(actions.Execute(ch.Uid.Value, new PlayerActionRequest("unjail")).Ok);
        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("jail", Minutes: 5)).Ok);
        Assert.True(ch.IsStatFlag(StatFlag.Freeze));
        Assert.True(ch.TryGetTag("JAIL_RELEASE", out string? release) && long.Parse(release!) > DateTime.UtcNow.Ticks);

        Assert.True(actions.Execute(ch.Uid.Value, new PlayerActionRequest("unjail")).Ok);
        Assert.False(ch.IsStatFlag(StatFlag.Freeze));
        Assert.False(ch.TryGetTag("JAIL_RELEASE", out _));
    }

    [Fact]
    public void UnknownSerialIsNotFound()
    {
        var (world, _) = Setup();
        var result = Actions(world).Execute(0x00ABCDEF, new PlayerActionRequest("heal"));
        Assert.True(result.NotFound);
        Assert.Null(Actions(world).Detail(0x00ABCDEF));
        Assert.True(Actions(world).Execute(0x40000001, new PlayerActionRequest("heal")).NotFound);
    }

    [Fact]
    public void DetailReportsStatsNonZeroSkillsAndBoundedTags()
    {
        var (world, ch) = Setup();
        ch.Fame = 1200; ch.Karma = -300;
        ch.SetSkill(SkillType.Magery, 1000);
        ch.SetSkill(SkillType.Tactics, 455);
        ch.SetTag("QUEST", "3");
        ch.SetTag("LONG", new string('x', 1000));
        for (int i = 0; i < PlayerDetail.MaxTags + 20; i++)
            ch.SetTag($"FILL{i:D3}", i.ToString());
        ch.SetStatFlag(StatFlag.Freeze);

        var d = Actions(world).Detail(ch.Uid.Value)!;

        Assert.Equal("Tester", d.Name);
        Assert.Equal((60, 50, 40), (d.Str, d.Dex, d.Int));
        Assert.Equal((60, 60), (d.Hits, d.MaxHits));
        Assert.Equal((1200, -300), (d.Fame, d.Karma));
        Assert.Equal((0, 1000, 1000, 0), (d.MapId, d.X, d.Y, d.Z));
        Assert.True(d.Frozen);
        Assert.False(d.Online);
        Assert.False(string.IsNullOrEmpty(d.NotorietyName));
        Assert.Equal(2, d.Skills.Count);
        Assert.Contains(d.Skills, s => s.Name == "Magery" && s.Value == 1000);
        Assert.Contains(d.Skills, s => s.Name == "Tactics" && s.Value == 455);
        Assert.Equal(PlayerDetail.MaxTags, d.Tags.Count);
        Assert.True(d.TagsTruncated);
        Assert.All(d.Tags, t => Assert.True(t.Value.Length <= PlayerDetail.MaxTagValueLength + 3));
    }
}

/// <summary>
/// The panel routes for character and server actions: they sit behind the bearer
/// token, refuse malformed input before the game is touched, and write an audit
/// line naming the action.
/// </summary>
public sealed class PanelPlayerActionEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "actions test";

    private PanelHost? _host;
    private HttpClient? _http;
    private string _tmpDir = "";
    private string _token = "";
    private readonly List<(uint Serial, PlayerActionRequest Request)> _actions = [];
    private readonly List<string> _staff = [];
    private readonly List<(string Name, string Args)> _functions = [];
    private readonly List<string> _audit = [];

    public async Task InitializeAsync()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sphnet_actions_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        string ini = Path.Combine(_tmpDir, "sphere.ini");
        File.WriteAllText(ini, "[SPHERE]\r\nServName=Test\r\nServPort=2593\r\n", Encoding.UTF8);

        var ctx = new PanelContext
        {
            ServerName = "Test",
            IniPath = ini,
            AdminPassword = Core.Configuration.PasswordHelper.Hash(AdminPassword),
            IsServerRunning = () => true,
            PlayerAction = (serial, req) =>
            {
                lock (_actions) _actions.Add((serial, req));
                return serial == 0x99
                    ? PlayerActionResult.Missing(serial)
                    : PlayerActionResult.Done("did " + req.Action);
            },
            GetPlayerDetail = serial => serial == 5
                ? new PlayerDetail(5, "Five", "", "acct", 1, true, true, 0x190, 0, 1, 2, 3,
                    10, 20, 30, 1, 2, 3, 4, 5, 6, 100, -100, 0, 1, "Innocent",
                    false, false, false, false, false,
                    [new SkillValueInfo(25, "Magery", 1000)], [new TagInfo("A", "b")])
                : null,
            StaffMessage = text => { lock (_staff) _staff.Add(text); return 2; },
            ExecuteServerFunction = (name, args) =>
            {
                lock (_functions) _functions.Add((name, args));
                return ["out:" + name];
            },
            AuditLog = msg => { lock (_audit) _audit.Add(msg); },
        };

        int port = FreePort();
        _host = new PanelHost(ctx, port, new PanelLogSink(),
            LoggerFactory.Create(_ => { }).CreateLogger("actions-test"));
        _host.Start();
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        for (int i = 0; i < 100; i++)
        {
            try { if ((await _http.GetAsync("/health")).IsSuccessStatusCode) break; }
            catch (HttpRequestException) { }
            await Task.Delay(50);
        }
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { password = AdminPassword });
        login.EnsureSuccessStatusCode();
        _token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    public Task DisposeAsync()
    {
        _http?.Dispose();
        _host?.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object payload, bool auth = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        if (auth) req.Headers.Add("Authorization", $"Bearer {_token}");
        return await _http!.SendAsync(req);
    }

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Authorization", $"Bearer {_token}");
        return await _http!.SendAsync(req);
    }

    [Theory]
    [InlineData("/api/players/5/action")]
    [InlineData("/api/server/staffmessage")]
    [InlineData("/api/server/function")]
    public async Task NewRoutesNeedTheToken(string path)
    {
        var res = await PostAsync(path, new { action = "heal", message = "x", name = "f_x" }, auth: false);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Empty(_actions);
        Assert.Empty(_staff);
        Assert.Empty(_functions);
    }

    [Fact]
    public async Task DetailNeedsTheTokenToo()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http!.GetAsync("/api/players/5/detail")).StatusCode);
    }

    public static IEnumerable<object[]> BadActions() =>
    [
        [new { action = "explode" }],
        [new { action = "" }],
        [new { action = "say" }],
        [new { action = "say", text = "   " }],
        [new { action = "say", text = new string('a', 257) }],
        [new { action = "say", text = "line\nbreak" }],
        [new { action = "verb", text = new string('a', 513) }],
        [new { action = "message", text = "hi", hue = 70000 }],
        [new { action = "teleport", x = 10 }],
        [new { action = "teleport", x = -1, y = 5 }],
        [new { action = "teleport", x = 10, y = 5, z = 200 }],
        [new { action = "teleport", x = 10, y = 5, map = 300 }],
        [new { action = "jail", minutes = -5 }],
    ];

    [Theory]
    [MemberData(nameof(BadActions))]
    public async Task MalformedActionsAreRefusedBeforeTheGame(object body)
    {
        var res = await PostAsync("/api/players/5/action", body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(_actions);
    }

    [Theory]
    [InlineData("/api/players/0/action")]
    [InlineData("/api/players/1073741825/action")] // 0x40000001 - an item serial
    public async Task NonCharacterSerialsAreRefused(string path)
    {
        var res = await PostAsync(path, new { action = "heal" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(_actions);
    }

    [Fact]
    public async Task AValidActionReachesTheGameNormalisedAndIsAudited()
    {
        var res = await PostAsync("/api/players/5/action",
            new { action = "SAY", text = "  hello there  ", hue = 5, x = 1, y = 2 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("did say", body.GetProperty("lines")[0].GetString());

        var (serial, req) = Assert.Single(_actions);
        Assert.Equal(5u, serial);
        // Only what "say" reads survives normalisation.
        Assert.Equal(new PlayerActionRequest("say", "hello there"), req);
        lock (_audit)
            Assert.Contains(_audit, a => a.Contains("serial=0x00000005") && a.Contains("action=say") && a.Contains("hello there"));
    }

    [Fact]
    public async Task TeleportKeepsItsCoordinates()
    {
        var res = await PostAsync("/api/players/5/action", new { action = "teleport", x = 1500, y = 1600, z = -5, map = 1 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(new PlayerActionRequest("teleport", X: 1500, Y: 1600, Z: -5, Map: 1), Assert.Single(_actions).Request);
    }

    [Fact]
    public async Task AnUnknownCharacterIs404()
    {
        var res = await PostAsync("/api/players/153/action", new { action = "heal" }); // 0x99
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task DetailIsServedAndMissingIs404()
    {
        var ok = await GetAsync("/api/players/5/detail");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Five", body.GetProperty("name").GetString());
        Assert.Equal("Magery", body.GetProperty("skills")[0].GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/api/players/6/detail")).StatusCode);
    }

    [Fact]
    public async Task StaffMessageIsValidatedSentAndAudited()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/server/staffmessage", new { message = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PostAsync("/api/server/staffmessage", new { message = new string('m', 300) })).StatusCode);
        Assert.Empty(_staff);

        var res = await PostAsync("/api/server/staffmessage", new { message = " meeting in 5 " });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recipients").GetInt32());
        Assert.Equal("meeting in 5", Assert.Single(_staff));
        lock (_audit) Assert.Contains(_audit, a => a.Contains("staff message") && a.Contains("meeting in 5"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("f_x y")]
    [InlineData("f_x;rm")]
    [InlineData(".serv")]
    public async Task FunctionNamesAreValidated(string name)
    {
        var res = await PostAsync("/api/server/function", new { name, args = "" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(_functions);
    }

    [Fact]
    public async Task FunctionRunsAndReturnsItsLines()
    {
        var res = await PostAsync("/api/server/function", new { name = "SERV.f_hello", args = "1, 2" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("out:SERV.f_hello", body.GetProperty("lines")[0].GetString());
        Assert.Equal(("SERV.f_hello", "1, 2"), Assert.Single(_functions));
        lock (_audit) Assert.Contains(_audit, a => a.Contains("server function SERV.f_hello"));
    }
}

/// <summary>
/// Host mode: the new operations cross the named-pipe bridge with every argument
/// intact (none of them named like an envelope key) and their answers back.
/// </summary>
public sealed class IpcPlayerActionTests
{
    [Fact]
    public async Task ActionsDetailStaffAndFunctionCrossTheHostBridge()
    {
        string pipeName = "sn-actions-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new IpcServer(pipeName);
        var seen = new List<(uint, PlayerActionRequest)>();
        string? staffText = null;
        (string, string)? function = null;
        server.SetContext(new PanelContext
        {
            PlayerAction = (serial, req) =>
            {
                seen.Add((serial, req));
                return serial == 0x1234
                    ? new PlayerActionResult(true, ["line one", "line two"])
                    : PlayerActionResult.Missing(serial);
            },
            GetPlayerDetail = serial => serial == 0x1234
                ? new PlayerDetail(serial, "Remote", "the Tested", "acct", 4, true, true, 0x191,
                    1, 1500, 1600, -5, 70, 80, 90, 50, 70, 60, 90, 40, 80, 5000, -2000, 3, 6, "Murderer",
                    true, true, false, true, true,
                    [new SkillValueInfo(25, "Magery", 1000)],
                    [new TagInfo("QUEST", "3")], TagsTruncated: true)
                : null,
            StaffMessage = text => { staffText = text; return 3; },
            ExecuteServerFunction = (name, args) => { function = (name, args); return ["ran " + name]; },
        });
        Task serverTask = server.RunAsync(cts.Token);

        using var bridge = new IpcBridge();
        await bridge.ConnectAsync(pipeName);
        var ctx = HostPanelContext.Build(
            new ServerProcess("x.exe", bridge, new PanelLogSink()), bridge, null, null);

        var request = new PlayerActionRequest("teleport", "t", 0x22, 1500, 1600, -5, 1, 7);
        var result = await Task.Run(() => ctx.PlayerAction!(0x1234, request));
        Assert.True(result.Ok);
        Assert.Equal(["line one", "line two"], result.Lines);
        Assert.Equal((0x1234u, request), Assert.Single(seen));

        // Nulls stay null across the wire.
        var missing = await Task.Run(() => ctx.PlayerAction!(0x9999, new PlayerActionRequest("heal")));
        Assert.True(missing.NotFound);
        Assert.Equal(new PlayerActionRequest("heal"), seen[1].Item2);

        var detail = await Task.Run(() => ctx.GetPlayerDetail!(0x1234));
        Assert.NotNull(detail);
        Assert.Equal(("Remote", 1500, 1600, -5, "Murderer"), (detail!.Name, detail.X, detail.Y, detail.Z, detail.NotorietyName));
        Assert.Equal((true, true, true), (detail.Dead, detail.Frozen, detail.Jailed));
        Assert.Equal("Magery", Assert.Single(detail.Skills).Name);
        Assert.Equal(new TagInfo("QUEST", "3"), Assert.Single(detail.Tags));
        Assert.True(detail.TagsTruncated);
        Assert.Null(await Task.Run(() => ctx.GetPlayerDetail!(0x9999)));

        Assert.Equal(3, await Task.Run(() => ctx.StaffMessage!("staff only")));
        Assert.Equal("staff only", staffText);

        Assert.Equal(["ran f_x"], await Task.Run(() => ctx.ExecuteServerFunction!("f_x", "1,2")));
        Assert.Equal(("f_x", "1,2"), function);

        cts.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
    }
}
