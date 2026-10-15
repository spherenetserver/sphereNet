using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Diagnostics.Behaviors;
using SphereNet.Network.Manager;

namespace SphereNet.LoadTest;

/// <summary>
/// Hostile-client drill: start the server as a child process, log in fuzz bots
/// (<see cref="FuzzBot"/>) from this process and keep them firing well-framed,
/// malformed packets at every registered handler for the given time. A bot the server
/// drops is logged in again under the same account.
///
/// Watched while it runs: the server process staying up, and the main loop answering
/// a STATUS console command with an advancing tick count (a hang shows as no answer).
/// Afterwards the server saves, shuts down, boots again on that save and audits the
/// loaded world. The report groups every handler fault the server logged by opcode,
/// exception and throwing frame.
///
///   dotnet SphereNet.LoadTest.dll fuzz --root &lt;shard dir with sphere.ini&gt; --duration 600
///   dotnet SphereNet.LoadTest.dll fuzz --synthetic --duration 120
///
/// --root runs against a prepared shard (a COPY of a real one: the drill saves into
/// it). The server is started with --botcity, which lets bot-named accounts log in.
/// </summary>
internal static class FuzzDrill
{
    private sealed class Options
    {
        public string? Root;
        public bool Synthetic;
        public int Bots = 6;
        public int DurationSeconds = 300;
        public int Rate = 25;
        public int Seed = 1;
        public int Port = 27500;
        public string City = "Britain";
        public string ServerDll = Path.Combine(AppContext.BaseDirectory, "SphereNet.Server.dll");
        public string OutDir = Path.GetFullPath("fuzz-results");
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
            var layout = SyntheticShard.Create(root, npcs: 400, items: 2000, bots: 0, spread: "town", seed: o.Seed);
            // No input quota: a fuzz bot sends far more than a player, and being
            // kicked for volume would only cut its sessions short.
            SyntheticShard.WriteIni(layout, port, workerCount: 2, ["SaveOnShutdown=0", "MaxSizeClientIn=0"]);
        }
        else
        {
            root = Path.GetFullPath(o.Root!);
            port = ReadIniPort(Path.Combine(root, "sphere.ini")) ?? port;
        }

        var opcodes = new NetworkManager(1, NullLoggerFactory.Instance).Packets.RegisteredOpcodes().ToArray();
        Console.WriteLine($"[fuzz] {opcodes.Length} registered opcodes: {string.Join(' ', opcodes.Select(b => b.ToString("X2")))}");

        string serverLog = Path.Combine(o.OutDir, "server-fuzz.log");
        string traceDir = Path.Combine(o.OutDir, "traces");
        if (Directory.Exists(traceDir)) Directory.Delete(traceDir, recursive: true);
        Directory.CreateDirectory(traceDir);

        var report = new Dictionary<string, object?>
        {
            ["root"] = root,
            ["bots"] = o.Bots,
            ["durationSeconds"] = o.DurationSeconds,
            ["ratePerBot"] = o.Rate,
            ["seed"] = o.Seed,
        };
        var problems = new List<string>();

        ILogger botLog = NullLogger.Instance;
        var serverArgs = new List<string> { "--headless", "--trustloopback", "--botcity", o.City };
        var server = new ServerHost(o.ServerDll, root, serverArgs, serverLog);
        var slots = new Slot[o.Bots];
        using var cts = new CancellationTokenSource();
        long reconnects = 0;
        var statusLatencies = new List<double>();
        int hangs = 0;
        bool crashed = false;
        try
        {
            await server.WaitForLineAsync(l => l.Contains("Type 'help' for commands", StringComparison.Ordinal),
                TimeSpan.FromMinutes(5), "server start");
            Console.WriteLine("[fuzz] server up; logging bots in");

            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = new Slot(i + 1, new StreamWriter(Path.Combine(traceDir, $"bot{i + 1}.trace"), append: false));
                await StartAsync(slots[i], o, opcodes, port, botLog, cts.Token);
            }
            Console.WriteLine($"[fuzz] {slots.Count(s => s.Bot?.State == BotState.Playing)}/{slots.Length} bots playing");

            // Bring back every bot the server dropped: a malformed body, a logout
            // opcode or the input quota closes the connection by design.
            var supervisor = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    foreach (var s in slots)
                    {
                        if (cts.IsCancellationRequested) break;
                        if (s.Bot?.State == BotState.Playing) continue;
                        s.Drops++;
                        Interlocked.Increment(ref reconnects);
                        try { await StartAsync(s, o, opcodes, port, botLog, cts.Token); }
                        catch (OperationCanceledException) { }
                    }
                    try { await Task.Delay(250, cts.Token); } catch (OperationCanceledException) { }
                }
            });

            var sw = Stopwatch.StartNew();
            long lastTick = -1;
            long nextReport = 30;
            while (sw.Elapsed.TotalSeconds < o.DurationSeconds)
            {
                await Task.Delay(5000);
                if (server.HasExited) { crashed = true; break; }

                // Liveness: the main loop must answer STATUS with a tick count that moved.
                var t0 = Stopwatch.StartNew();
                try
                {
                    var wait = server.WaitForLineAsync(l => l.Contains("Ticks: ", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(20), "STATUS");
                    server.Send("STATUS");
                    string line = await wait;
                    statusLatencies.Add(t0.Elapsed.TotalMilliseconds);
                    var m = Regex.Match(line, @"Ticks: (\d+)");
                    long tick = m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
                    if (tick >= 0 && tick == lastTick)
                    {
                        hangs++;
                        problems.Add($"tick counter did not advance at {sw.Elapsed.TotalSeconds:F0}s ({tick})");
                    }
                    lastTick = tick;
                }
                catch (TimeoutException)
                {
                    hangs++;
                    problems.Add($"main loop did not answer STATUS within 20 s at {sw.Elapsed.TotalSeconds:F0}s");
                }
                catch (InvalidOperationException) { crashed = true; break; }

                if (sw.Elapsed.TotalSeconds >= nextReport)
                {
                    nextReport += 30;
                    Console.WriteLine($"[fuzz] {sw.Elapsed.TotalSeconds:F0}s: sent {slots.Sum(s => s.Sent)} packets, " +
                                      $"{reconnects} reconnects, handler faults so far {CountLines(serverLog, "Packet handler threw")}");
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add("drill aborted: " + ex.Message.Split('\n')[0]);
            Console.WriteLine(ex.Message);
        }
        finally
        {
            cts.Cancel();
            foreach (var s in slots)
            {
                if (s == null) continue;
                try { s.Bot?.Disconnect(); } catch { }
                s.Trace.Dispose();
            }
        }

        if (server.HasExited || crashed)
        {
            crashed = true;
            problems.Add($"SERVER PROCESS EXITED during the fuzz run");
            report["crashTail"] = server.Tail(60);
            report["lastPacketsPerBot"] = slots.Where(s => s != null)
                .ToDictionary(s => $"bot{s.Id}", s => TailLines(Path.Combine(traceDir, $"bot{s.Id}.trace"), 10));
        }

        // Save the fuzzed world, then boot it again and let the loader audit it.
        string? auditLine = null;
        string? loadLine = null;
        if (!crashed)
        {
            try
            {
                var saved = server.WaitForLineAsync(l => l.Contains("World save #", StringComparison.Ordinal) &&
                                                         (l.Contains(" complete", StringComparison.Ordinal) || l.Contains("FAILED", StringComparison.Ordinal)),
                    TimeSpan.FromMinutes(5), "save after fuzz");
                server.Send("SAVE");
                string saveLine = await saved;
                report["saveAfterFuzz"] = saveLine;
                if (saveLine.Contains("FAILED", StringComparison.Ordinal)) problems.Add("save after fuzz FAILED");
            }
            catch (Exception ex) { problems.Add("save after fuzz: " + ex.Message.Split('\n')[0]); }
            await server.StopAsync();
            server.Dispose();

            string rebootLog = Path.Combine(o.OutDir, "server-reboot.log");
            using var reboot = new ServerHost(o.ServerDll, root, ["--headless"], rebootLog);
            try
            {
                var loaded = reboot.WaitForLineAsync(l => l.Contains("World loaded:", StringComparison.Ordinal),
                    TimeSpan.FromMinutes(5), "reload");
                var audit = reboot.WaitForLineAsync(l => l.Contains("World audit:", StringComparison.Ordinal),
                    TimeSpan.FromMinutes(5), "audit");
                loadLine = await loaded;
                auditLine = await audit;
                if (!auditLine.Contains("clean", StringComparison.Ordinal))
                    problems.Add("world audit after reload: " + auditLine);
            }
            catch (Exception ex) { problems.Add("reload after fuzz: " + ex.Message.Split('\n')[0]); }
            await reboot.StopAsync();
            report["reloadAuditAnomalies"] = AuditDetail(rebootLog);
        }
        else
        {
            server.Dispose();
        }
        report["reload"] = loadLine;
        report["audit"] = auditLine;

        // ---- analysis of the server log ----
        var faults = HandlerFaults(serverLog);
        report["packetsSent"] = slots.Where(s => s != null).Sum(s => s.Sent);
        report["reconnects"] = reconnects;
        report["hangs"] = hangs;
        report["crashed"] = crashed;
        report["statusAnswerMsMax"] = statusLatencies.Count > 0 ? Math.Round(statusLatencies.Max(), 1) : null;
        report["handlerFaultsTotal"] = faults.Sum(f => f.Count);
        report["handlerFaults"] = faults;
        report["inputLoopFaults"] = CountLines(serverLog, "Input processing threw");
        report["underrunCloses"] = CountLines(serverLog, "Malformed packet underrun");
        report["slowPackets"] = SlowPackets(serverLog);
        report["otherErrors"] = OtherErrors(serverLog);
        report["problems"] = problems;

        string outPath = Path.Combine(o.OutDir, "fuzz-report.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine($"[fuzz] packets sent: {report["packetsSent"]}, reconnects: {reconnects}, crashed: {crashed}, hangs: {hangs}");
        Console.WriteLine($"[fuzz] handler faults: {report["handlerFaultsTotal"]} in {faults.Count} distinct places");
        foreach (var f in faults.Take(40))
            Console.WriteLine($"   {f.Count,6}x 0x{f.Opcode} {f.Handler}: {f.Exception} at {f.Frame}");
        Console.WriteLine($"[fuzz] input-loop faults: {report["inputLoopFaults"]}, underrun closes: {report["underrunCloses"]}");
        Console.WriteLine($"[fuzz] reload: {loadLine ?? "-"}");
        Console.WriteLine($"[fuzz] audit: {auditLine ?? "-"}");
        foreach (var p in problems) Console.WriteLine($"[fuzz] PROBLEM: {p}");
        Console.WriteLine($"[fuzz] report: {outPath}");
        return crashed || hangs > 0 || problems.Count > 0 ? 1 : 0;
    }

    private sealed class Slot(int id, StreamWriter trace)
    {
        public int Id { get; } = id;
        public StreamWriter Trace { get; } = trace;
        public BotClient? Bot;
        public FuzzBot? Behavior;
        public long SentBefore;
        public int Drops;
        public long Sent => SentBefore + (Behavior?.PacketsSent ?? 0);
    }

    private static async Task StartAsync(Slot s, Options o, byte[] opcodes, int port, ILogger log, CancellationToken ct)
    {
        if (s.Behavior != null) s.SentBefore += s.Behavior.PacketsSent;
        try { s.Bot?.Dispose(); } catch { }
        var bot = new BotClient(s.Id, log);
        s.Bot = bot;
        s.Behavior = null;
        if (!await bot.ConnectAndLoginAsync("127.0.0.1", port, ct))
            return;
        // A new seed per login: the same account replays a different sequence.
        var behavior = new FuzzBot(o.Seed * 1_000_003 + s.Id * 7919 + s.Drops, opcodes, s.Trace, o.Rate);
        s.Behavior = behavior;
        bot.StartBehavior(behavior, ct);
    }

    public sealed record Fault(string Opcode, string Handler, string Exception, string Frame, int Count, string Example);

    /// <summary>Group "Packet handler threw" entries by opcode, exception type and
    /// the first stack frame inside SphereNet code.</summary>
    private static List<Fault> HandlerFaults(string logPath)
    {
        var groups = new Dictionary<(string, string, string, string), (int Count, string Example)>();
        if (!File.Exists(logPath)) return [];
        var lines = ReadShared(logPath);
        var head = new Regex(@"Packet handler threw for #\d+ 0x([0-9A-F]{2}) \((\w+)\)");
        for (int i = 0; i < lines.Length; i++)
        {
            var m = head.Match(lines[i]);
            if (!m.Success) continue;
            string exception = "?", frame = "?";
            for (int j = i + 1; j < Math.Min(lines.Length, i + 40); j++)
            {
                string l = lines[j].Trim();
                if (exception == "?" && Regex.IsMatch(l, @"^[\w.]+(Exception|Error)\b"))
                    exception = l.Length > 160 ? l[..160] : l;
                else if (l.StartsWith("at SphereNet.", StringComparison.Ordinal) && !l.Contains("NetworkManager.ProcessInput", StringComparison.Ordinal))
                {
                    frame = Regex.Replace(l, @" in .*:line", " :line");
                    break;
                }
                if (head.IsMatch(lines[j])) break;
            }
            var key = (m.Groups[1].Value, m.Groups[2].Value, exception.Split(':')[0], frame);
            groups[key] = groups.TryGetValue(key, out var g) ? (g.Count + 1, g.Example) : (1, exception);
        }
        return groups.OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new Fault(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Key.Item4, kv.Value.Count, kv.Value.Example))
            .ToList();
    }

    private static Dictionary<string, int> SlowPackets(string logPath)
    {
        var result = new Dictionary<string, int>();
        if (!File.Exists(logPath)) return result;
        var re = new Regex(@"\[slow_packet\] #\d+ op=0x([0-9A-F]{2}) \((\w+)\)");
        foreach (var l in ReadShared(logPath))
        {
            var m = re.Match(l);
            if (!m.Success) continue;
            string k = $"0x{m.Groups[1].Value} {m.Groups[2].Value}";
            result[k] = result.GetValueOrDefault(k) + 1;
        }
        return result;
    }

    /// <summary>ERR/FTL lines that are not handler faults: tick faults, save errors,
    /// script errors the hostile input provoked. Grouped by message shape.</summary>
    private static Dictionary<string, int> OtherErrors(string logPath)
    {
        var result = new Dictionary<string, int>();
        if (!File.Exists(logPath)) return result;
        foreach (var l in ReadShared(logPath))
        {
            if (!(l.Contains(" ERR] ", StringComparison.Ordinal) || l.Contains(" FTL] ", StringComparison.Ordinal))) continue;
            if (l.Contains("Packet handler threw", StringComparison.Ordinal)) continue;
            int at = l.IndexOf("] ", StringComparison.Ordinal);
            string msg = at >= 0 ? l[(at + 2)..] : l;
            msg = Regex.Replace(msg, @"0x[0-9A-Fa-f]+|\d+", "#");
            if (msg.Length > 140) msg = msg[..140];
            result[msg] = result.GetValueOrDefault(msg) + 1;
        }
        return result.OrderByDescending(kv => kv.Value).Take(40).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static List<string> AuditDetail(string logPath) =>
        File.Exists(logPath)
            ? ReadShared(logPath).SkipWhile(l => !l.Contains("World audit:", StringComparison.Ordinal)).Take(21).ToList()
            : [];

    private static int CountLines(string path, string needle) =>
        File.Exists(path) ? ReadShared(path).Count(l => l.Contains(needle, StringComparison.Ordinal)) : 0;

    private static List<string> TailLines(string path, int n) =>
        File.Exists(path) ? ReadShared(path).TakeLast(n).ToList() : [];

    private static string[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
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
                case "--bots": o.Bots = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--duration": o.DurationSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--rate": o.Rate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--city": o.City = Next(); break;
                case "--server": o.ServerDll = Path.GetFullPath(Next()); break;
                case "--out": o.OutDir = Path.GetFullPath(Next()); break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (o.Root == null && !o.Synthetic)
            throw new ArgumentException("fuzz needs --root <shard copy> or --synthetic");
        if (!File.Exists(o.ServerDll))
            throw new ArgumentException($"Server not found: {o.ServerDll}");
        return o;
    }
}
