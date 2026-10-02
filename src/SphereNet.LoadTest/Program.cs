using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SphereNet.LoadTest;

/// <summary>
/// Real-socket load scenario. For every (workers, bots) pair: build a fresh synthetic
/// shard, start SphereNet.Server on it, log the bots in over TCP, let them walk, talk
/// and double-click for a warm-up, then open the server's PERF window, ask for one
/// world save half way through, and read back the server's report next to the bots'
/// own move-answer latencies. Results go to a JSON artifact; with --check the run
/// fails when a metric breaks loadtest-baseline.json.
///
///   dotnet bin/Release/SphereNet.LoadTest.dll --workers 1,2,4,8 --bots 50,200
///       --duration 45 --warmup 10 --out loadtest-results.json --check
/// </summary>
internal static class Program
{
    private sealed class Options
    {
        public int[] Workers = [1, 2, 4, 8];
        public int[] Bots = [50, 200];
        public int DurationSeconds = 45;
        public int WarmupSeconds = 10;
        public int Npcs = 2000;
        public int Items = 4000;
        public int Seed = 1;
        public string Spread = "cluster";
        public int BasePort = 27000;
        public int LoginConcurrency = 20;
        public string ServerDll = Path.Combine(AppContext.BaseDirectory, "SphereNet.Server.dll");
        public string WorkDir = Path.Combine(Path.GetTempPath(), "spherenet-loadtest");
        public string OutPath = Path.GetFullPath("loadtest-results.json");
        public string? BaselinePath = Path.Combine(AppContext.BaseDirectory, "loadtest-baseline.json");
        public bool Check;
        public int Cpus;
        public string? Commit = Environment.GetEnvironmentVariable("GITHUB_SHA");
    }

    public static async Task<int> Main(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (!File.Exists(o.ServerDll))
        {
            Console.Error.WriteLine($"Server not found: {o.ServerDll} (build SphereNet.Server or pass --server).");
            return 2;
        }

        // --cpus N: pin the runner - and through inheritance the server it starts - to
        // the first N logical processors, to rehearse a smaller host (a CI runner) on
        // a bigger one. The server sizes its worker pool from what it is allowed.
        if (o.Cpus > 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            int n = Math.Min(o.Cpus, Math.Min(Environment.ProcessorCount, 63));
            Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)((1L << n) - 1);
        }

        var machine = DescribeMachine();
        if (o.Cpus > 0) machine["affinityCpus"] = o.Cpus;
        Console.WriteLine($"[loadtest] machine: {machine["cpu"]} ({machine["logicalCores"]} logical cores), {machine["os"]}");
        var runs = new List<RunResult>();
        int runIndex = 0;
        foreach (int bots in o.Bots)
        {
            foreach (int workers in o.Workers)
            {
                Console.WriteLine($"[loadtest] run {runIndex + 1}/{o.Bots.Length * o.Workers.Length}: workers={workers} bots={bots}");
                var result = await RunOneAsync(o, workers, bots, o.BasePort + runIndex * 10);
                runs.Add(result);
                PrintRun(result);
                runIndex++;
            }
        }

        // Every run of one bot count must have started from the same world, whatever
        // its worker count: the comparison is meaningless otherwise.
        foreach (var group in runs.GroupBy(r => r.Bots))
        {
            if (group.Select(r => r.ScenarioHash).Distinct().Count() > 1)
                foreach (var r in group)
                {
                    r.Problems.Add("scenario hash differs between runs of the same bot count");
                    r.Valid = false;
                }
        }

        List<CheckResult>? checks = null;
        if (o.BaselinePath != null && File.Exists(o.BaselinePath))
        {
            var baseline = Baseline.Load(o.BaselinePath);
            checks = baseline.Check(runs);
            // Latency tails on shared hardware have outliers no code change caused. A
            // run that breaks a limit is measured once more, and only a breach that
            // repeats fails the job; both attempts stay in the artifact.
            for (int i = 0; i < runs.Count; i++)
            {
                var first = runs[i];
                if (checks.Where(c => c.Workers == first.Workers && c.Bots == first.Bots).All(c => c.Pass))
                    continue;
                Console.WriteLine($"[loadtest] retry: workers={first.Workers} bots={first.Bots} broke a limit; measuring it once more");
                var again = await RunOneAsync(o, first.Workers, first.Bots, o.BasePort + (runIndex++) * 10);
                again.FirstAttempt = first;
                PrintRun(again);
                runs[i] = again;
            }
            checks = baseline.Check(runs);
        }
        else if (o.Check)
        {
            Console.Error.WriteLine($"--check needs a baseline file; not found: {o.BaselinePath}");
            return 2;
        }

        bool allValid = runs.All(r => r.Valid);
        bool thresholdsPass = checks == null || checks.All(c => c.Pass);
        var output = new JsonObject
        {
            ["measured"] = true,
            ["commit"] = o.Commit,
            ["timestampUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["machine"] = machine,
            ["scenario"] = new JsonObject
            {
                ["name"] = "synthetic-town-real-socket",
                ["spread"] = o.Spread,
                ["npcs"] = o.Npcs,
                ["items"] = o.Items,
                ["seed"] = o.Seed,
                ["warmupSeconds"] = o.WarmupSeconds,
                ["durationSeconds"] = o.DurationSeconds,
                ["saveAt"] = "half of the measured window",
                ["botScript"] = "step every 420-480 ms (80% straight), speech ~6 s, double-click ~4 s",
            },
            ["baseline"] = o.BaselinePath,
            ["runs"] = JsonSerializer.SerializeToNode(runs.Select(r => new
            {
                r.Workers,
                r.Bots,
                r.Valid,
                r.ScenarioHash,
                Retried = r.FirstAttempt != null,
                FirstAttempt = r.FirstAttempt == null ? null : new
                {
                    r.FirstAttempt.Valid,
                    r.FirstAttempt.Problems,
                    r.FirstAttempt.Metrics,
                },
                r.Problems,
                r.Metrics,
                Server = r.Server,
                r.ServerErrorLines,
            }), Stats.Json),
            ["checks"] = checks == null ? null : JsonSerializer.SerializeToNode(checks, Stats.Json),
            ["pass"] = allValid && thresholdsPass,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(o.OutPath)!);
        File.WriteAllText(o.OutPath, output.ToJsonString(Stats.Json));
        Console.WriteLine($"[loadtest] results: {o.OutPath}");

        if (checks != null)
        {
            foreach (var c in checks.Where(c => !c.Pass))
                Console.WriteLine($"[loadtest] FAIL workers={c.Workers} bots={c.Bots} {c.Metric}={(c.Value?.ToString(CultureInfo.InvariantCulture) ?? "missing")} limit max={c.Max?.ToString(CultureInfo.InvariantCulture) ?? "-"} min={c.Min?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            Console.WriteLine($"[loadtest] baseline checks: {checks.Count(c => c.Pass)}/{checks.Count} passed");
        }
        if (!allValid)
            Console.WriteLine("[loadtest] at least one run produced incomplete measurements (see problems).");
        return o.Check && !(allValid && thresholdsPass) ? 1 : 0;
    }

    private static async Task<RunResult> RunOneAsync(Options o, int workers, int botCount, int port)
    {
        var result = new RunResult { Workers = workers, Bots = botCount };
        string root = Path.Combine(o.WorkDir, $"w{workers}-b{botCount}");
        var layout = SyntheticShard.Create(root, o.Npcs, o.Items, botCount, o.Spread, o.Seed);
        SyntheticShard.WriteIni(layout, port, workers);
        result.ScenarioHash = layout.ScenarioHash;

        // --trustloopback: the whole fleet connects from 127.0.0.1, which the
        // connection flood filter would otherwise throttle.
        var serverArgs = new List<string> { "--headless", "--trustloopback" };
        using var server = new ServerHost(o.ServerDll, root, serverArgs, Path.Combine(root, "server-console.log"));
        var bots = new List<LoadBot>();
        using var cts = new CancellationTokenSource();
        var behaviourTasks = new List<Task>();
        try
        {
            await server.WaitForLineAsync(l => l.Contains("Type 'help' for commands", StringComparison.Ordinal),
                TimeSpan.FromMinutes(3), "server start");

            // Logins, a bounded number at a time (a real login wave, not a SYN flood).
            var loginSw = Stopwatch.StartNew();
            using (var gate = new SemaphoreSlim(o.LoginConcurrency))
            {
                var logins = new List<Task>();
                for (int i = 0; i < botCount; i++)
                {
                    var bot = new LoadBot(i + 1, o.Seed);
                    bots.Add(bot);
                    await gate.WaitAsync();
                    logins.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (await bot.LoginAsync("127.0.0.1", port, cts.Token))
                            {
                                var run = Task.Run(() => bot.RunAsync(cts.Token));
                                lock (behaviourTasks) behaviourTasks.Add(run);
                            }
                        }
                        finally { gate.Release(); }
                    }));
                }
                await Task.WhenAll(logins);
            }
            double loginWaveMs = loginSw.Elapsed.TotalMilliseconds;
            int loggedIn = bots.Count(b => b.LoggedIn);
            foreach (var reason in bots.Where(b => !b.LoggedIn).Select(b => b.FailReason ?? "unknown").Distinct().Take(5))
                result.Problems.Add("login failure: " + reason);

            await Task.Delay(TimeSpan.FromSeconds(o.WarmupSeconds));

            // Measured window.
            var markWait = server.WaitForLineAsync(l => l.Contains("[perf_window] mark", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30), "PERF MARK");
            server.Send("PERF MARK");
            await markWait;
            LoadBot.Measuring = true;
            var traffic0 = bots.Select(b => (b.BytesIn, b.BytesOut, b.PacketsIn)).ToArray();
            var windowSw = Stopwatch.StartNew();

            await Task.Delay(TimeSpan.FromSeconds(o.DurationSeconds / 2.0));
            var saveWait = server.WaitForLineAsync(l => l.Contains("World snapshot captured", StringComparison.Ordinal)
                                                        || l.Contains("Save phases:", StringComparison.Ordinal),
                TimeSpan.FromSeconds(60), "world save");
            server.Send("save");
            await saveWait;
            var remaining = TimeSpan.FromSeconds(o.DurationSeconds) - windowSw.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining);

            LoadBot.Measuring = false;
            double windowS = windowSw.Elapsed.TotalSeconds;
            var reportWait = server.WaitForLineAsync(l => l.Contains("[perf_window] {", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30), "PERF REPORT");
            server.Send("PERF REPORT");
            string reportLine = await reportWait;
            string json = reportLine[(reportLine.IndexOf("[perf_window] ", StringComparison.Ordinal) + "[perf_window] ".Length)..];
            result.Server = JsonNode.Parse(json);

            CollectMetrics(result, bots, traffic0, windowS, loginWaveMs, loggedIn);
        }
        catch (Exception ex)
        {
            result.Problems.Add("run aborted: " + ex.Message.Split('\n')[0]);
            Console.WriteLine(ex.Message);
        }
        finally
        {
            LoadBot.Measuring = false;
            cts.Cancel();
            try { await Task.WhenAll(behaviourTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            foreach (var b in bots) b.Close();
            await server.StopAsync();
        }

        result.Metrics["server.errors"] = server.Errors;
        result.ServerErrorLines = server.ErrorLines.ToList();
        Validate(result, o, botCount);
        return result;
    }

    private static void CollectMetrics(RunResult r, List<LoadBot> bots,
        (long In, long Out, long Packets)[] traffic0, double windowS, double loginWaveMs, int loggedIn)
    {
        var m = r.Metrics;
        var ok = bots.Where(b => b.LoggedIn).ToList();
        m["clients.loggedIn"] = loggedIn;
        m["clients.loginFailures"] = bots.Count - loggedIn;
        m["clients.disconnects"] = bots.Count(b => b.DisconnectedWhileMeasuring);
        m["clients.loginWaveMs"] = Math.Round(loginWaveMs, 1);
        var loginMs = ok.Select(b => b.LoginMs).OrderBy(x => x).ToArray();
        m["clients.loginP50Ms"] = Stats.Percentile(loginMs, 0.50);
        m["clients.loginMaxMs"] = loginMs.Length > 0 ? Math.Round(loginMs[^1], 1) : 0;

        var lat = ok.SelectMany(b => b.AckLatencyUs).ToArray();
        Array.Sort(lat);
        long sentMeasured = ok.Sum(b => (long)b.MovesSentMeasured);
        m["ack.samples"] = lat.Length;
        m["ack.p50Ms"] = Stats.PercentileMs(lat, 0.50);
        m["ack.p95Ms"] = Stats.PercentileMs(lat, 0.95);
        m["ack.p99Ms"] = Stats.PercentileMs(lat, 0.99);
        m["ack.maxMs"] = lat.Length > 0 ? Math.Round(lat[^1] / 1000.0, 3) : 0;
        long answered = ok.Sum(b => (long)b.Acks + b.Rejects);
        m["ack.rejectRate"] = answered > 0 ? Math.Round(ok.Sum(b => (double)b.Rejects) / answered, 4) : 0;
        long sentAll = ok.Sum(b => (long)b.MovesSent);
        m["ack.lostRate"] = sentAll > 0 ? Math.Round(ok.Sum(b => (double)b.StepsLost) / sentAll, 4) : 0;
        m["ack.droppedByRejectRate"] = sentAll > 0 ? Math.Round(ok.Sum(b => (double)b.StepsDroppedByReject) / sentAll, 4) : 0;
        m["ack.movesSentInWindow"] = sentMeasured;

        int n = Math.Max(1, ok.Count);
        double inBytes = 0, outBytes = 0, inPackets = 0;
        for (int i = 0; i < bots.Count; i++)
        {
            if (!bots[i].LoggedIn) continue;
            inBytes += bots[i].BytesIn - traffic0[i].In;
            outBytes += bots[i].BytesOut - traffic0[i].Out;
            inPackets += bots[i].PacketsIn - traffic0[i].Packets;
        }
        // Client view: "in" is what the server sent (compressed wire bytes).
        m["net.serverToClientBytesPerBotPerSec"] = Math.Round(inBytes / n / windowS, 1);
        m["net.clientToServerBytesPerBotPerSec"] = Math.Round(outBytes / n / windowS, 1);
        m["net.serverToClientPacketsPerBotPerSec"] = Math.Round(inPackets / n / windowS, 1);

        var s = r.Server;
        if (s == null || s["Tick"] == null) return;
        double D(JsonNode? node) => node?.GetValue<double>() ?? 0;
        m["tick.samples"] = D(s["Tick"]!["Samples"]);
        m["tick.avgMs"] = D(s["Tick"]!["AvgMs"]);
        m["tick.p50Ms"] = D(s["Tick"]!["P50Ms"]);
        m["tick.p95Ms"] = D(s["Tick"]!["P95Ms"]);
        m["tick.p99Ms"] = D(s["Tick"]!["P99Ms"]);
        m["tick.maxMs"] = D(s["Tick"]!["MaxMs"]);
        m["loop.maxMs"] = D(s["Loop"]!["MaxMs"]);
        m["loop.over50Ms"] = D(s["Loop"]!["Over50Ms"]);
        m["loop.over100Ms"] = D(s["Loop"]!["Over100Ms"]);
        m["gc.gen0"] = D(s["Gc"]!["Gen0"]);
        m["gc.gen1"] = D(s["Gc"]!["Gen1"]);
        m["gc.gen2"] = D(s["Gc"]!["Gen2"]);
        m["gc.pausePercent"] = D(s["Gc"]!["PausePercent"]);
        m["gc.pauseMs"] = D(s["Gc"]!["PauseMs"]);
        m["gc.allocMBPerSec"] = D(s["Gc"]!["AllocMBPerSec"]);
        m["gc.heapMB"] = D(s["Gc"]!["HeapMB"]);
        m["npc.due"] = D(s["NpcBudget"]!["Due"]);
        m["npc.deferred"] = D(s["NpcBudget"]!["Deferred"]);
        m["npc.maxDuePerTick"] = D(s["NpcBudget"]!["MaxDuePerTick"]);
        m["npc.oldestWaitMs"] = D(s["NpcBudget"]!["OldestWaitMs"]);
        m["npc.lateP95Ms"] = D(s["NpcBudget"]!["LatenessP95Ms"]);
        m["npc.lateP99Ms"] = D(s["NpcBudget"]!["LatenessP99Ms"]);
        m["save.count"] = D(s["Save"]!["Count"]);
        m["save.captureMs"] = D(s["Save"]!["MaxCaptureMs"]);
        m["save.mainThreadMs"] = D(s["Save"]!["MaxMainThreadMs"]);
        m["multicore.fallbacks"] = D(s["MulticoreFallbacks"]);
        m["server.playingClients"] = D(s["PlayingClients"]);
        m["server.workerCount"] = D(s["WorkerCount"]);
    }

    /// <summary>A run counts only when it measured what it set out to: every bot in,
    /// the server's report read, a tick and a move sample stream of plausible size and
    /// exactly the one save asked for. Anything else is reported as an incomplete
    /// measurement and fails the job - never read as a fast result.</summary>
    private static void Validate(RunResult r, Options o, int botCount)
    {
        var m = r.Metrics;
        double Get(string k) => m.TryGetValue(k, out var v) ? v : -1;
        if (r.Server == null) r.Problems.Add("no server perf report");
        if (Get("clients.loggedIn") != botCount) r.Problems.Add($"only {Get("clients.loggedIn")} of {botCount} bots logged in");
        if (Get("server.playingClients") >= 0 && Get("server.playingClients") < botCount)
            r.Problems.Add($"server saw {Get("server.playingClients")} playing clients, expected {botCount}");
        // ~2 steps/s per bot are sent; ask for at least half of that answered.
        double minAck = botCount * o.DurationSeconds * 1.0;
        if (Get("ack.samples") < minAck) r.Problems.Add($"ack samples {Get("ack.samples")} < {minAck}");
        double minTicks = o.DurationSeconds * 10 * 0.8;
        if (Get("tick.samples") < minTicks) r.Problems.Add($"tick samples {Get("tick.samples")} < {minTicks}");
        if (Get("save.count") != 1) r.Problems.Add($"save count {Get("save.count")} != 1");
        r.Valid = r.Problems.Count == 0;
    }

    private static void PrintRun(RunResult r)
    {
        double G(string k) => r.Metrics.TryGetValue(k, out var v) ? v : double.NaN;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[loadtest]   valid={r.Valid} tick p50/p95/p99/max={G("tick.p50Ms"):F2}/{G("tick.p95Ms"):F2}/{G("tick.p99Ms"):F2}/{G("tick.maxMs"):F2}ms " +
            $"ack p50/p95/p99/max={G("ack.p50Ms"):F2}/{G("ack.p95Ms"):F2}/{G("ack.p99Ms"):F2}/{G("ack.maxMs"):F2}ms (n={G("ack.samples")}) " +
            $"loopMax={G("loop.maxMs"):F1}ms save capture/main={G("save.captureMs"):F1}/{G("save.mainThreadMs"):F1}ms " +
            $"gc g0/g1/g2={G("gc.gen0")}/{G("gc.gen1")}/{G("gc.gen2")} pause={G("gc.pausePercent"):F2}% alloc={G("gc.allocMBPerSec"):F1}MB/s " +
            $"npc due/deferred={G("npc.due")}/{G("npc.deferred")} lateP99={G("npc.lateP99Ms")}ms " +
            $"out/bot={G("net.serverToClientBytesPerBotPerSec"):F0}B/s errors={G("server.errors")}"));
        foreach (var p in r.Problems)
            Console.WriteLine("[loadtest]   problem: " + p);
    }

    private static JsonObject DescribeMachine()
    {
        string cpu = RuntimeInformation.ProcessArchitecture.ToString();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name) cpu = name.Trim();
            }
            else if (File.Exists("/proc/cpuinfo"))
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                if (line != null) cpu = line[(line.IndexOf(':') + 1)..].Trim();
            }
        }
        catch { }
        return new JsonObject
        {
            ["cpu"] = cpu,
            ["logicalCores"] = Environment.ProcessorCount,
            ["os"] = RuntimeInformation.OSDescription,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["ci"] = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
        };
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        static int[] Ints(string v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--workers": o.Workers = Ints(Next()); break;
                case "--bots": o.Bots = Ints(Next()); break;
                case "--duration": o.DurationSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--warmup": o.WarmupSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--npcs": o.Npcs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--items": o.Items = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--spread": o.Spread = Next().ToLowerInvariant(); break;
                case "--port": o.BasePort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--server": o.ServerDll = Path.GetFullPath(Next()); break;
                case "--work": o.WorkDir = Path.GetFullPath(Next()); break;
                case "--out": o.OutPath = Path.GetFullPath(Next()); break;
                case "--baseline": o.BaselinePath = Path.GetFullPath(Next()); break;
                case "--no-baseline": o.BaselinePath = null; break;
                case "--check": o.Check = true; break;
                case "--cpus": o.Cpus = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--commit": o.Commit = Next(); break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (o.Spread is not ("cluster" or "town"))
            throw new ArgumentException("--spread must be cluster or town");
        if (o.DurationSeconds < 10 || o.DurationSeconds > 3000)
            throw new ArgumentException("--duration must be 10..3000 seconds");
        return o;
    }
}
