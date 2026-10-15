using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SphereNet.LoadTest;

/// <summary>
/// Kill-during-save drill: the real server process is killed (TerminateProcess /
/// SIGKILL, no shutdown path, no exception handler) at a random moment inside a world
/// save, then booted again on whatever the save directory holds. Repeated for every
/// configuration given.
///
/// The world is a synthetic shard whose population does not change on its own (NPCs
/// wander, nothing spawns, dies or decays in the time a run takes), so every whole
/// generation holds the same number of items and characters. A boot that loads any
/// other count - or refuses to boot - has loaded part of a generation, or none. Which
/// generation it picked (the current one or a .bakN) is recorded as well.
///
/// What this cannot show: a power cut. A killed process's written bytes still reach
/// the disk through the OS cache; losing the cache too is what the saver's explicit
/// flushes are for, and no user-mode drill can pull that plug.
///
///   dotnet SphereNet.LoadTest.dll savekill --iterations 25 --items 300000 --npcs 20000
/// </summary>
internal static class SaveKillDrill
{
    private sealed class Options
    {
        public int Iterations = 20;
        public int Items = 200_000;
        public int Npcs = 20_000;
        public int Seed = 1;
        public int Port = 27600;
        public string[] Configs = ["Text:0:bg0:bak2", "BinaryGz:4:bg1:bak2", "Binary:2:bg1:bak0", "TextGz:0:bg0:bak0"];
        public string ServerDll = Path.Combine(AppContext.BaseDirectory, "SphereNet.Server.dll");
        public string WorkDir = Path.Combine(Path.GetTempPath(), "spherenet-savekill");
        public string OutPath = Path.GetFullPath("savekill-report.json");
    }

    private sealed record Config(string Format, int Shards, bool Background, int Backups)
    {
        public string Name => $"{Format}:{Shards}:bg{(Background ? 1 : 0)}:bak{Backups}";

        public static Config Parse(string s)
        {
            var p = s.Split(':');
            if (p.Length != 4 || !p[2].StartsWith("bg") || !p[3].StartsWith("bak"))
                throw new ArgumentException($"config '{s}' must look like Format:shards:bg0|bg1:bakN");
            return new Config(p[0], int.Parse(p[1], CultureInfo.InvariantCulture), p[2] == "bg1",
                int.Parse(p[3][3..], CultureInfo.InvariantCulture));
        }
    }

    private sealed class Iteration
    {
        public int Index { get; set; }
        public int KillAfterMs { get; set; }
        public string? SaveStateAtKill { get; set; }
        public string? Loaded { get; set; }
        public string? Generation { get; set; }
        public bool Ok { get; set; }
        public string? Problem { get; set; }
        public List<string> LoaderErrors { get; set; } = [];
    }

    public static async Task<int> RunAsync(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        var all = new List<object>();
        bool pass = true;
        int portOffset = 0;
        foreach (var cfg in o.Configs.Select(Config.Parse))
        {
            var (ok, result) = await RunConfigAsync(o, cfg, o.Port + 10 * portOffset++);
            pass &= ok;
            all.Add(result);
        }
        File.WriteAllText(o.OutPath, JsonSerializer.Serialize(new { pass, configs = all },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[savekill] {(pass ? "PASS" : "FAIL")} - report: {o.OutPath}");
        return pass ? 0 : 1;
    }

    private static async Task<(bool Ok, object Result)> RunConfigAsync(Options o, Config cfg, int port)
    {
        string root = Path.Combine(o.WorkDir, cfg.Name.Replace(':', '_'));
        Console.WriteLine($"[savekill] === {cfg.Name}: building a {o.Items}-item / {o.Npcs}-NPC shard in {root}");
        var layout = SyntheticShard.Create(root, o.Npcs, o.Items, bots: 0, spread: "town", seed: o.Seed);
        SyntheticShard.WriteIni(layout, port, workerCount: 2,
        [
            $"SaveFormat={cfg.Format}",
            $"SaveShards={cfg.Shards}",
            $"SaveBackground={(cfg.Background ? 1 : 0)}",
            $"BackupLevels={cfg.Backups}",
            "SaveOnShutdown=0",
            "LogFileLevel=Information",
        ]);

        var iterations = new List<Iteration>();
        string? expected = null;
        double saveMs = 0;
        string logDir = Path.Combine(root, "drill-logs");
        Directory.CreateDirectory(logDir);

        // Calibration: one boot, one whole save in the configured format, one boot
        // on it. That save's counts are what every later boot must load, and its
        // duration bounds the kill window.
        {
            using var s = Start(o, root, Path.Combine(logDir, "calibrate-1.log"));
            string loaded = await BootAsync(s);
            var sw = Stopwatch.StartNew();
            string done = await SaveAsync(s, TimeSpan.FromMinutes(10));
            saveMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"[savekill] first boot: {loaded}; whole save: {saveMs:F0} ms ({done})");
            Kill(s);
        }
        {
            using var s = Start(o, root, Path.Combine(logDir, "calibrate-2.log"));
            expected = Counts(await BootAsync(s));
            var sw = Stopwatch.StartNew();
            await SaveAsync(s, TimeSpan.FromMinutes(10));
            saveMs = Math.Max(saveMs, sw.Elapsed.TotalMilliseconds);
            Kill(s);
            Console.WriteLine($"[savekill] every boot must load: {expected}; kill window 0..{saveMs * 1.15:F0} ms after SAVE");
        }

        var rng = new Random(o.Seed * 31 + cfg.Name.GetHashCode(StringComparison.Ordinal));
        for (int i = 1; i <= o.Iterations; i++)
        {
            var it = new Iteration { Index = i, KillAfterMs = rng.Next(0, (int)(saveMs * 1.15) + 1) };
            iterations.Add(it);
            string log = Path.Combine(logDir, $"iter-{i:D3}.log");
            using (var s = Start(o, root, log))
            {
                try
                {
                    string loaded = await BootAsync(s);
                    it.Loaded = Counts(loaded);
                    it.Generation = GenerationUsed(log);
                    it.LoaderErrors = LoaderErrors(log);
                    it.Ok = it.Loaded == expected;
                    if (!it.Ok) it.Problem = $"loaded {it.Loaded}, expected {expected}";
                }
                catch (Exception ex)
                {
                    it.Ok = false;
                    it.Problem = "boot failed: " + ex.Message.Split('\n')[0];
                    it.LoaderErrors = LoaderErrors(log);
                    Console.WriteLine($"[savekill] iter {i}: {it.Problem}");
                    break; // the save directory is what the next boot would face too
                }

                // Every fifth save is allowed to finish, so the chain of whole saves
                // that later kills interrupt keeps moving.
                if (i % 5 == 0)
                {
                    await SaveAsync(s, TimeSpan.FromMinutes(10));
                    it.SaveStateAtKill = "completed (not killed)";
                    Kill(s);
                }
                else
                {
                    s.Send("SAVE");
                    await Task.Delay(it.KillAfterMs);
                    Kill(s);
                    it.SaveStateAtKill = SaveState(log);
                }
            }
            Console.WriteLine($"[savekill] iter {i,3}: booted {it.Loaded} from {it.Generation,-8} " +
                              $"{(it.Ok ? "ok" : "BAD")}; then kill +{it.KillAfterMs} ms -> save {it.SaveStateAtKill}");
        }

        // One last boot on what the final kill left.
        var final = new Iteration { Index = o.Iterations + 1 };
        {
            string log = Path.Combine(logDir, "final.log");
            using var s = Start(o, root, log);
            try
            {
                final.Loaded = Counts(await BootAsync(s));
                final.Generation = GenerationUsed(log);
                final.LoaderErrors = LoaderErrors(log);
                final.Ok = final.Loaded == expected;
                if (!final.Ok) final.Problem = $"loaded {final.Loaded}, expected {expected}";
            }
            catch (Exception ex) { final.Problem = "boot failed: " + ex.Message.Split('\n')[0]; }
            Kill(s);
        }
        iterations.Add(final);
        Console.WriteLine($"[savekill] final boot: {final.Loaded} from {final.Generation} {(final.Ok ? "ok" : "BAD: " + final.Problem)}");

        bool ok = iterations.All(x => x.Ok);
        var byGen = iterations.GroupBy(x => x.Generation ?? "?").ToDictionary(g => g.Key, g => g.Count());
        var byState = iterations.Where(x => x.SaveStateAtKill != null)
            .GroupBy(x => x.SaveStateAtKill!).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"[savekill] {cfg.Name}: {(ok ? "PASS" : "FAIL")} - boots by generation: " +
                          string.Join(", ", byGen.Select(kv => $"{kv.Key}={kv.Value}")) +
                          "; kills by save phase: " + string.Join(", ", byState.Select(kv => $"{kv.Key}={kv.Value}")));
        return (ok, new
        {
            config = cfg.Name,
            pass = ok,
            expected,
            wholeSaveMs = Math.Round(saveMs),
            bootsByGeneration = byGen,
            killsBySavePhase = byState,
            iterations,
        });
    }

    private static ServerHost Start(Options o, string root, string log) =>
        new(o.ServerDll, root, ["--headless"], log);

    private static async Task<string> BootAsync(ServerHost s)
    {
        string loaded = await s.WaitForLineAsync(l => l.Contains("World loaded:", StringComparison.Ordinal) &&
                                                      l.Contains(" chars", StringComparison.Ordinal),
            TimeSpan.FromMinutes(10), "world load");
        await s.WaitForLineAsync(l => l.Contains("Type 'help' for commands", StringComparison.Ordinal),
            TimeSpan.FromMinutes(5), "server ready");
        return loaded;
    }

    private static async Task<string> SaveAsync(ServerHost s, TimeSpan timeout)
    {
        var done = s.WaitForLineAsync(l => l.Contains("World save #", StringComparison.Ordinal) &&
                                           (l.Contains(" complete", StringComparison.Ordinal) || l.Contains("FAILED", StringComparison.Ordinal)),
            timeout, "save");
        s.Send("SAVE");
        string line = await done;
        if (line.Contains("FAILED", StringComparison.Ordinal))
            throw new InvalidOperationException("save failed: " + line);
        return line;
    }

    private static void Kill(ServerHost s) => s.Dispose(); // Process.Kill(entireProcessTree)

    private static string Counts(string loadedLine)
    {
        var m = Regex.Match(loadedLine, @"World loaded: (\d+) items, (\d+) chars");
        return m.Success ? $"{m.Groups[1].Value} items/{m.Groups[2].Value} chars" : loadedLine;
    }

    private static string GenerationUsed(string log)
    {
        foreach (var l in ReadShared(log))
        {
            var m = Regex.Match(l, @"Loading from BACKUP generation \.bak(\d+)");
            if (m.Success) return ".bak" + m.Groups[1].Value;
        }
        return "current";
    }

    /// <summary>The loader's fallback errors: which generation it rejected and why.</summary>
    private static List<string> LoaderErrors(string log) =>
        ReadShared(log)
            .Where(l => l.Contains("Save generation", StringComparison.Ordinal) ||
                        l.Contains("integrity validation", StringComparison.Ordinal) ||
                        l.Contains("FTL]", StringComparison.Ordinal))
            .Select(l => l.Length > 300 ? l[..300] : l)
            .Take(10)
            .ToList();

    /// <summary>How far the killed save had got, from its last log line.</summary>
    private static string SaveState(string log)
    {
        string state = "not started";
        foreach (var l in ReadShared(log))
        {
            if (l.Contains("Save requested", StringComparison.Ordinal) || l.Contains("World save requested", StringComparison.Ordinal)) state = "requested";
            if (l.Contains("World snapshot captured", StringComparison.Ordinal)) state = "captured";
            if (Regex.IsMatch(l, @"World save #\d+ starting")) state = "writing";
            if (Regex.IsMatch(l, @"World save #\d+ complete")) state = "completed";
            if (l.Contains("Save phases:", StringComparison.Ordinal)) state = "completed";
        }
        return state;
    }

    private static string[] ReadShared(string path)
    {
        if (!File.Exists(path)) return [];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--iterations": o.Iterations = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--items": o.Items = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--npcs": o.Npcs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--configs": o.Configs = Next().Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--server": o.ServerDll = Path.GetFullPath(Next()); break;
                case "--work": o.WorkDir = Path.GetFullPath(Next()); break;
                case "--out": o.OutPath = Path.GetFullPath(Next()); break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (!File.Exists(o.ServerDll))
            throw new ArgumentException($"Server not found: {o.ServerDll}");
        return o;
    }
}
