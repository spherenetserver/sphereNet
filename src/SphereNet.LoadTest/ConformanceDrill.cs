using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.LoadTest;

/// <summary>
/// Client-version conformance drill. For each client version given, bots that log in
/// exactly as a 2D client of that version does (a bare 4-byte seed before 6.0.4.0,
/// otherwise 0xEF with the version; the relay's auth id as a raw seed on the game
/// connection; an 0xBD answer when asked) play the ordinary bot roles against the
/// server, and every packet the server sends them is checked against how that client
/// frames and reads it and whether that client knows it at all
/// (<see cref="ReferenceClientProtocol"/>). Each server packet arrives as one Huffman
/// unit, so its true extent is known and a disagreement names the packet itself.
///
///   dotnet SphereNet.LoadTest.dll conformance --root &lt;shard copy&gt; --clients 2.0.3.0,5.0.1.0,7.0.20.0 --duration 180
/// </summary>
internal static class ConformanceDrill
{
    private sealed class Options
    {
        public string? Root;
        public bool Synthetic;
        public List<uint> Clients = [ReferenceClientProtocol.Pack(7, 0, 20, 0)];
        public int BotsPerRole = 1;
        public int DurationSeconds = 180;
        public int Port = 27800;
        public string City = "Britain";
        public string ServerDll = Path.Combine(AppContext.BaseDirectory, "SphereNet.Server.dll");
        public string OutDir = Path.GetFullPath("conformance-results");
    }

    private static readonly (string Name, Func<int, IBotBehavior> Make)[] Roles =
    [
        ("walker", s => new WalkerBot(s)),
        ("combat", s => new CombatBot(s)),
        ("mage", s => new MageBot(s)),
        ("loot", s => new LootBot(s)),
        ("vendor", s => new VendorBot(s)),
        ("trade", s => new TradeBot(s)),
        ("social", s => new SocialBot(s)),
        ("skill", s => new SkillBot(s)),
    ];

    private sealed class VersionRun
    {
        public required uint Client;
        public string Version => ReferenceClientProtocol.Describe(Client);
        public long Packets;
        public int LoggedIn;
        public int BotFramingErrors;
        public readonly ConcurrentDictionary<byte, long> Seen = new();
        public readonly ConcurrentDictionary<string, (long Count, string Example)> Violations = new();
        public readonly List<string> Problems = [];

        public void Observe(byte[] p)
        {
            Interlocked.Increment(ref Packets);
            Seen.AddOrUpdate(p[0], 1, (_, n) => n + 1);
            string? problem = ReferenceClientProtocol.Check(p, Client);
            if (problem == null) return;
            string hex = Convert.ToHexString(p.AsSpan(0, Math.Min(p.Length, 48)));
            Violations.AddOrUpdate(problem, _ => (1, hex), (_, v) => (v.Count + 1, v.Example));
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        Directory.CreateDirectory(o.OutDir);
        string root;
        int port = o.Port;
        if (o.Synthetic)
        {
            root = Path.Combine(o.OutDir, "shard");
            var layout = SyntheticShard.Create(root, npcs: 400, items: 2000, bots: 0, spread: "town", seed: 1);
            SyntheticShard.WriteIni(layout, port, workerCount: 2, ["SaveOnShutdown=0"]);
        }
        else
        {
            root = Path.GetFullPath(o.Root!);
            port = ReadIniPort(Path.Combine(root, "sphere.ini")) ?? port;
        }

        var runs = new List<VersionRun>();
        for (int vi = 0; vi < o.Clients.Count; vi++)
        {
            var run = new VersionRun { Client = o.Clients[vi] };
            runs.Add(run);
            Console.WriteLine($"[conformance] === client {run.Version}: {o.BotsPerRole} bot(s) per role, {o.DurationSeconds}s");
            // A server of its own per version: nothing one version's bots leave
            // behind can decide whether the next version's bots get in.
            var server = new ServerHost(o.ServerDll, root, ["--headless", "--trustloopback", "--botcity", o.City],
                Path.Combine(o.OutDir, $"server-{run.Version}.log"));
            try
            {
                await server.WaitForLineAsync(l => l.Contains("Type 'help' for commands", StringComparison.Ordinal),
                    TimeSpan.FromMinutes(5), "server start");
                await RunVersionAsync(o, run, server, port, firstBotId: 100 * (vi + 1));
                if (server.HasExited)
                    run.Problems.Add("server exited during the run");
                else
                    await CheckConnectionsReapedAsync(server, run, port);
            }
            catch (Exception ex)
            {
                run.Problems.Add("aborted: " + ex.Message.Split('\n')[0]);
            }
            finally
            {
                await server.StopAsync();
                server.Dispose();
            }
            PrintRun(run);
        }

        var report = runs.Select(r => new
        {
            client = r.Version,
            botsLoggedIn = r.LoggedIn,
            serverPackets = r.Packets,
            botFramingErrors = r.BotFramingErrors,
            opcodesSeen = r.Seen.OrderBy(kv => kv.Key).ToDictionary(kv => $"0x{kv.Key:X2}", kv => kv.Value),
            violations = r.Violations.OrderByDescending(kv => kv.Value.Count)
                .Select(kv => new { problem = kv.Key, count = kv.Value.Count, example = kv.Value.Example }).ToList(),
            problems = r.Problems,
        }).ToList();
        string outPath = Path.Combine(o.OutDir, "conformance-report.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(new { root, durationSecondsPerClient = o.DurationSeconds, clients = report },
            new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine("[conformance] summary:");
        foreach (var r in runs)
            Console.WriteLine($"   {r.Version,-12} bots {r.LoggedIn,2}  packets {r.Packets,7}  opcodes {r.Seen.Count,3}  " +
                              $"violations {r.Violations.Values.Sum(v => v.Count),6} in {r.Violations.Count,2} kinds  " +
                              $"bot framing errors {r.BotFramingErrors}{(r.Problems.Count > 0 ? "  PROBLEMS: " + string.Join("; ", r.Problems) : "")}");
        Console.WriteLine($"[conformance] report: {outPath}");
        bool ok = runs.Count == o.Clients.Count &&
                  runs.All(r => r.Violations.IsEmpty && r.Problems.Count == 0 && r.BotFramingErrors == 0);
        return ok ? 0 : 1;
    }

    private static async Task RunVersionAsync(Options o, VersionRun run, ServerHost server, int port, int firstBotId)
    {
        var bots = new List<BotClient>();
        using var cts = new CancellationTokenSource();
        try
        {
            int id = firstBotId;
            foreach (var (name, make) in Roles)
            {
                for (int i = 0; i < o.BotsPerRole; i++)
                {
                    id++;
                    var bot = new BotClient(id, NullLogger.Instance)
                    {
                        ClientVersion = run.Client,
                        RawGameSeed = true,
                        ServerPacketObserver = run.Observe,
                    };
                    bots.Add(bot);
                    if (await bot.ConnectAndLoginAsync("127.0.0.1", port, cts.Token))
                    {
                        run.LoggedIn++;
                        bot.StartBehavior(make(id * 7919), cts.Token);
                    }
                    else
                    {
                        run.Problems.Add($"{name} bot {id} could not log in");
                    }
                }
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < o.DurationSeconds && !server.HasExited)
            {
                await Task.Delay(30_000);
                Console.WriteLine($"[conformance] {run.Version} {sw.Elapsed.TotalSeconds:F0}s: {Interlocked.Read(ref run.Packets)} packets, " +
                                  $"{run.Seen.Count} opcodes, {run.Violations.Values.Sum(v => v.Count)} violations");
            }
        }
        finally
        {
            cts.Cancel();
            foreach (var b in bots)
            {
                run.BotFramingErrors += b.World.FramingErrors;
                try { b.Disconnect(); } catch { }
            }
        }
    }

    /// <summary>Every bot has closed its socket: the server must have dropped every
    /// connection, or the per-address limit fills with dead ones.</summary>
    private static async Task CheckConnectionsReapedAsync(ServerHost server, VersionRun run, int port)
    {
        int open = -1;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var wait = server.WaitForLineAsync(l => l.Contains("Connections: ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20), "STATUS");
            server.Send("STATUS");
            string line = await wait;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"Connections: (\d+)");
            open = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
            if (open == 0) return;
            await Task.Delay(1000);
        }
        run.Problems.Add($"{open} connection(s) still open 10 s after every bot closed its socket; " +
                         $"TCP states on the server port: {TcpStates(port)}");
    }

    /// <summary>Who holds the leaked connection: CLOSE_WAIT on the server's side means
    /// the client closed and the server never noticed; ESTABLISHED on both sides means
    /// the client never closed.</summary>
    private static string TcpStates(int port)
    {
        try
        {
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var states = props.GetActiveTcpConnections()
                .Where(c => c.LocalEndPoint.Port == port || c.RemoteEndPoint.Port == port)
                .GroupBy(c => (Side: c.LocalEndPoint.Port == port ? "server" : "client", c.State))
                .Select(g => $"{g.Key.Side} {g.Key.State} x{g.Count()}");
            return string.Join(", ", states);
        }
        catch (Exception ex) { return "unavailable: " + ex.Message; }
    }

    private static void PrintRun(VersionRun r)
    {
        Console.WriteLine($"[conformance] {r.Version}: {r.Packets} packets in {r.Seen.Count} opcodes " +
                          $"({string.Join(' ', r.Seen.Keys.Order().Select(k => k.ToString("X2")))}); bot framing errors {r.BotFramingErrors}");
        foreach (var (problem, v) in r.Violations.OrderByDescending(kv => kv.Value.Count).Take(25))
            Console.WriteLine($"   {v.Count,7}x {problem}\n            {v.Example}");
        foreach (var p in r.Problems) Console.WriteLine($"[conformance] PROBLEM: {p}");
    }

    private static int? ReadIniPort(string ini)
    {
        if (!File.Exists(ini)) return null;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var l = raw.Trim();
            if (l.StartsWith("ServPort", StringComparison.OrdinalIgnoreCase) && l.Contains('='))
                if (int.TryParse(l[(l.IndexOf('=') + 1)..].Trim(), out int p)) return p;
        }
        return null;
    }

    private static uint ParseVersion(string text)
    {
        var parts = text.Split('.').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        if (parts.Length != 4) throw new ArgumentException($"client version '{text}' must be major.minor.build.extra, e.g. 7.0.20.0");
        return ReferenceClientProtocol.Pack(parts[0], parts[1], parts[2], parts[3]);
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--root": o.Root = Next(); break;
                case "--synthetic": o.Synthetic = true; break;
                case "--client":
                case "--clients":
                    o.Clients = Next().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ParseVersion).ToList();
                    break;
                case "--bots-per-role": o.BotsPerRole = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--duration": o.DurationSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--city": o.City = Next(); break;
                case "--server": o.ServerDll = Path.GetFullPath(Next()); break;
                case "--out": o.OutDir = Path.GetFullPath(Next()); break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (o.Root == null && !o.Synthetic)
            throw new ArgumentException("conformance needs --root <shard copy> or --synthetic");
        if (!File.Exists(o.ServerDll))
            throw new ArgumentException($"Server not found: {o.ServerDll}");
        return o;
    }
}
