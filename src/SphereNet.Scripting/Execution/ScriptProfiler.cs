using System.Diagnostics;
using System.Globalization;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Source-X script profiler (CScriptProfiler, EXPERIMENTAL 0x400 EF_Script_Profiler):
/// for every [FUNCTION] and every @trigger name, how many times it ran and how long it
/// took - total, min, max, average. The console's P command prints the entries whose
/// average is above the overall average, as upstream does (CServer::ProfileDump); E
/// clears them.
///
/// Two additions over upstream: the time a call spent in itself, without the functions
/// and triggers it called (Self), and the same per script file - the two numbers that
/// point at what to optimize. Off, every hook is one field read.
/// </summary>
public sealed class ScriptProfiler
{
    public sealed class Entry(string name)
    {
        public string Name { get; } = name;
        public long Called;
        public long TotalTicks;
        public long SelfTicks;
        public long MinTicks = long.MaxValue;
        public long MaxTicks;

        public double TotalMs => Ms(TotalTicks);
        public double SelfMs => Ms(SelfTicks);
        public double AverageMs => Called == 0 ? 0 : TotalMs / Called;
        public double MinMs => Called == 0 ? 0 : Ms(MinTicks);
        public double MaxMs => Ms(MaxTicks);
    }

    /// <summary>One call in progress, so a nested call's time can be taken out of
    /// its caller's self time.</summary>
    private struct Frame
    {
        public Entry Entry;
        public Entry? File;
        public long Start;
        public long ChildTicks;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _triggers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private long _called;
    private long _totalTicks;

    [ThreadStatic] private static Stack<Frame>? t_stack;

    /// <summary>EF_Script_Profiler. Turning it off keeps what was recorded.</summary>
    public volatile bool Enabled;

    /// <summary>The script folder; file names in the report are shown relative to it.</summary>
    public string? BaseDirectory { get; set; }

    /// <summary>Begin timing a function (<paramref name="isTrigger"/> false) or a
    /// trigger. Returns false when the profiler is off; then <see cref="End"/> must
    /// not be called.</summary>
    public bool Begin(string name, bool isTrigger, string? file)
    {
        if (!Enabled)
            return false;
        // Upstream lowercases and keeps only the first word ("f_x args" -> "f_x").
        string key = name;
        int space = key.IndexOf(' ');
        if (space >= 0)
            key = key[..space];
        key = key.ToLowerInvariant();

        Entry entry;
        Entry? fileEntry = null;
        lock (_lock)
        {
            var map = isTrigger ? _triggers : _functions;
            if (!map.TryGetValue(key, out entry!))
                map[key] = entry = new Entry(key);
            if (!string.IsNullOrEmpty(file))
            {
                if (!_files.TryGetValue(file, out fileEntry))
                    _files[file] = fileEntry = new Entry(file);
            }
        }
        (t_stack ??= new Stack<Frame>()).Push(new Frame
        {
            Entry = entry,
            File = fileEntry,
            Start = Stopwatch.GetTimestamp(),
        });
        return true;
    }

    /// <summary>End the call begun by the matching <see cref="Begin"/>.</summary>
    public void End()
    {
        var stack = t_stack;
        if (stack == null || stack.Count == 0)
            return;
        var frame = stack.Pop();
        long elapsed = Stopwatch.GetTimestamp() - frame.Start;
        long self = Math.Max(0, elapsed - frame.ChildTicks);
        if (stack.Count > 0)
        {
            var parent = stack.Pop();
            parent.ChildTicks += elapsed;
            stack.Push(parent);
        }

        lock (_lock)
        {
            Add(frame.Entry, elapsed, self);
            if (frame.File != null)
                Add(frame.File, elapsed, self);
            // Only the outermost call counts toward the overall total, or nested
            // time would be counted once per level.
            _called++;
            if (stack.Count == 0)
                _totalTicks += elapsed;
        }
    }

    private static void Add(Entry e, long elapsed, long self)
    {
        e.Called++;
        e.TotalTicks += elapsed;
        e.SelfTicks += self;
        if (elapsed < e.MinTicks) e.MinTicks = elapsed;
        if (elapsed > e.MaxTicks) e.MaxTicks = elapsed;
    }

    /// <summary>Forget everything recorded (console E).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _functions.Clear();
            _triggers.Clear();
            _files.Clear();
            _called = 0;
            _totalTicks = 0;
        }
    }

    public IReadOnlyList<Entry> Functions { get { lock (_lock) return [.. _functions.Values]; } }
    public IReadOnlyList<Entry> Triggers { get { lock (_lock) return [.. _triggers.Values]; } }
    public IReadOnlyList<Entry> Files { get { lock (_lock) return [.. _files.Values]; } }

    /// <summary>The report the console prints: upstream's lines (entries whose average
    /// is above the overall average) followed by the self-time leaders and the files.</summary>
    public IReadOnlyList<string> Report(int top = 10)
    {
        var lines = new List<string>();
        if (!Enabled && _called == 0)
        {
            lines.Add("Script profiler is off (EXPERIMENTAL flag 0x400 EF_Script_Profiler)");
            return lines;
        }

        Entry[] functions, triggers, files;
        long called, totalTicks;
        lock (_lock)
        {
            functions = [.. _functions.Values];
            triggers = [.. _triggers.Values];
            files = [.. _files.Values];
            called = _called;
            totalTicks = _totalTicks;
        }
        if (called == 0)
        {
            lines.Add("Script profiler is not yet informational");
            return lines;
        }

        var inv = CultureInfo.InvariantCulture;
        double totalMs = Ms(totalTicks);
        double average = totalMs / called;
        lines.Add(string.Format(inv,
            "Scripts: called {0} times and took a total of {1:F4} seconds ({2:F4}s average). Reporting with highest average.",
            called, totalMs / 1000.0, average / 1000.0));

        foreach (var f in functions.Where(f => f.AverageMs > average).OrderByDescending(f => f.AverageMs))
            lines.Add(Line("FUNCTION", f, inv));
        foreach (var t in triggers.Where(t => t.AverageMs > average).OrderByDescending(t => t.AverageMs))
            lines.Add(Line("TRIGGER", t, inv));

        var bySelf = functions.Select(f => ("FUNCTION", f)).Concat(triggers.Select(t => ("TRIGGER", t)))
            .OrderByDescending(x => x.Item2.SelfTicks).Take(top).ToList();
        if (bySelf.Count > 0)
        {
            lines.Add($"Most time in themselves (top {bySelf.Count}):");
            foreach (var (kind, e) in bySelf)
                lines.Add(string.Format(inv, "  {0} '{1}' self {2:F4} s of {3:F4} s, {4} calls",
                    kind, e.Name, e.SelfMs / 1000.0, e.TotalMs / 1000.0, e.Called));
        }

        var topFiles = files.OrderByDescending(f => f.SelfTicks).Take(top).ToList();
        if (topFiles.Count > 0)
        {
            lines.Add($"Script files by time spent in them (top {topFiles.Count}):");
            foreach (var f in topFiles)
                lines.Add(string.Format(inv, "  {0}: self {1:F4} s, {2} calls", ShortPath(f.Name), f.SelfMs / 1000.0, f.Called));
        }
        lines.Add("Report complete!");
        return lines;
    }

    private static string Line(string kind, Entry e, IFormatProvider inv) => string.Format(inv,
        "{0} '{1}' called {2} times, took {3:F4} seconds average ({4:F4} min, {5:F4} max), total: {6:F4} s, self: {7:F4} s.",
        kind, e.Name, e.Called, e.AverageMs / 1000.0, e.MinMs / 1000.0, e.MaxMs / 1000.0, e.TotalMs / 1000.0, e.SelfMs / 1000.0);

    private string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(BaseDirectory))
            return path;
        try { return Path.GetRelativePath(BaseDirectory, path); }
        catch { return path; }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
