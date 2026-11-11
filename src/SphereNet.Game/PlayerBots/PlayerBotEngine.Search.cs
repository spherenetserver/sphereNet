using System.Diagnostics;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Tile path searches, off the main thread. A bot that meets a wall asks for a search
/// (<see cref="RequestSearch"/>) and walks the wall's edge meanwhile. At the end of a
/// tick up to <see cref="SearchesPerTick"/> waiting requests are gathered into a
/// batch; the host runs the batch in the next tick's parallel phase, beside the
/// creature path prestage and under the same read-only rules
/// (<see cref="RunSearches"/>), and the paths are handed to their bots before they
/// act. A batch the host did not run is run by the engine itself, on its own thread,
/// before it acts. Each search is the walk rule itself (the bot is the walker), with
/// a node budget and a deadline.
///
/// A search that failed waits 3, 10, then 20 seconds before the same bot may ask
/// again; one that only ran out of time asks again a second later, twice. Searches
/// are weighted (<see cref="PathOptions.Hasty"/>): a way soon, not the best way. A path found for a road link is kept (a corridor): the next bot stopped on
/// that link follows it without a search of its own.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>Searches run per tick across every bot.</summary>
    public int SearchesPerTick { get; set; } = 16;
    /// <summary>Most nodes one search may open.</summary>
    public int SearchMaxNodes { get; set; } = 2000;
    /// <summary>Time one search may take.</summary>
    public double SearchMs { get; set; } = 5.0;
    /// <summary>Time the whole batch may hold the phase it runs in: a search that has
    /// not started by then is not run (its bot asks again when next stopped), and none
    /// runs past it.</summary>
    public double SearchBatchMs { get; set; } = 6.0;
    private long _batchDeadline;

    private readonly List<BotMind> _wantSearch = [];
    private readonly List<SearchRequest> _batch = [];
    private SearchResult[] _batchResults = new SearchResult[16];
    private bool _batchRan;

    private readonly record struct SearchRequest(
        BotMind Mind, int Generation, Point3D From, Point3D To, int MaxNodes, int Radius,
        PathOptions Options, (int From, int To)? Corridor);

    private struct SearchResult
    {
        public List<Point3D>? Path;
        public long Us;
        public bool Ran;
        public bool TimedOut;
    }

    public long WindowSearchQueued { get; private set; }
    public long WindowSearchDenied { get; private set; }
    public long WindowSearchDropped { get; private set; }
    public long WindowSearchesNotRun { get; private set; }
    public int WindowSearchBacklog { get; private set; }
    public long WindowCorridorUses { get; private set; }
    public long WindowStallSearches { get; private set; }
    /// <summary>Searches that ran out of time and were asked again soon.</summary>
    public long WindowSearchTimeouts { get; private set; }
    /// <summary>Diagonal path steps taken as two straight ones (a corner the search
    /// cut and the step would not).</summary>
    public long WindowCornerSplits { get; private set; }

    /// <summary>Ask for a tile path to <paramref name="target"/>. False when this bot
    /// must wait before asking again.</summary>
    private bool RequestSearch(BotMind mind, Point3D target, long now, bool avoidChars = false)
    {
        var ch = mind.Char;
        int dist = ch.Position.GetDistanceTo(target);
        if (mind.SearchQueued)
        {
            // Still waiting its turn: a moved target replaces the old one.
            if (!mind.SearchInBatch)
                FillRequest(mind, target, dist, avoidChars);
            return true;
        }
        if (now < mind.NextSearchMs)
        {
            WindowSearchDenied++;
            return false;
        }
        FillRequest(mind, target, dist, avoidChars);
        mind.SearchQueued = true;
        _wantSearch.Add(mind);
        WindowSearchQueued++;
        if (mind.Mode == BotMode.Travel) WindowStallSearches++;
        mind.Note(now, $"search asked {ch.Position} -> {target} ({mind.SearchMaxNodes} nodes{(avoidChars ? ", round people" : "")})");
        return true;
    }

    private void FillRequest(BotMind mind, Point3D target, int dist, bool avoidChars)
    {
        mind.SearchTarget = target;
        mind.SearchMaxNodes = Math.Clamp(300 + dist * 40, 300, Math.Max(300, SearchMaxNodes));
        mind.SearchRadius = dist + 10;
        mind.SearchAvoidChars = avoidChars;
        mind.SearchCorridor = _stepCorridor;
    }

    /// <summary>End of tick: the next batch, first come first served.</summary>
    private void CollectSearches()
    {
        if (_wantSearch.Count > WindowSearchBacklog) WindowSearchBacklog = _wantSearch.Count;
        if (_batch.Count > 0) return; // the last one was never applied (host skipped a tick)
        int take = Math.Min(Math.Max(0, SearchesPerTick), _wantSearch.Count);
        for (int i = 0; i < take; i++)
        {
            var m = _wantSearch[i];
            if (m.Char.IsDeleted || !_minds.ContainsKey(m.Char.Uid.Value))
            {
                m.SearchQueued = false;
                continue;
            }
            m.SearchGeneration++;
            m.SearchInBatch = true;
            var options = PathOptions.OpenDoors | PathOptions.Hasty |
                          (m.SearchAvoidChars ? PathOptions.AvoidChars : PathOptions.None);
            _batch.Add(new SearchRequest(m, m.SearchGeneration, m.Char.Position, m.SearchTarget,
                m.SearchMaxNodes, m.SearchRadius, options, m.SearchCorridor));
        }
        _wantSearch.RemoveRange(0, take);
        _batchRan = false;
    }

    /// <summary>Run the gathered batch. Called by the host in its parallel phase (the
    /// world is only read there); <paramref name="po"/> null runs it on this thread.
    /// Running it twice does nothing.</summary>
    public void RunSearches(ParallelOptions? po = null)
    {
        if (_batchRan || _batch.Count == 0) return;
        _batchRan = true;
        if (_batchResults.Length < _batch.Count) _batchResults = new SearchResult[_batch.Count * 2];
        Array.Clear(_batchResults, 0, _batch.Count);
        _batchDeadline = Stopwatch.GetTimestamp() + (long)(SearchBatchMs * Stopwatch.Frequency / 1000.0);
        if (po != null && _batch.Count > 1)
            Parallel.For(0, _batch.Count, po, RunOneSearch);
        else
            for (int i = 0; i < _batch.Count; i++) RunOneSearch(i);
    }

    /// <summary>Searches waiting to run (the host asks before its parallel phase).</summary>
    public int PendingSearches => _batchRan ? 0 : _batch.Count;

    private long _searchFaults;

    /// <summary>Tile searches that threw, since start.</summary>
    public long SearchFaults => Interlocked.Read(ref _searchFaults);

    private void RunOneSearch(int i)
    {
        var r = _batch[i];
        long s0 = Stopwatch.GetTimestamp();
        // The batch's time is up: this one waits for its bot to ask again.
        if (s0 >= _batchDeadline) return;
        List<Point3D>? path = null;
        bool timedOut = false;
        try
        {
            long deadline = Math.Min(s0 + (long)(SearchMs * Stopwatch.Frequency / 1000.0), _batchDeadline);
            path = _pathfinder.FindPath(r.From, r.To, r.From.Map, CanFlags.None, r.Mind.Char,
                maxNodes: r.MaxNodes, maxRadius: r.Radius, deadline: deadline, options: r.Options);
            timedOut = path == null && Pathfinder.LastSearchTimedOut;
        }
        catch (Exception ex)
        {
            path = null; // a search that throws is a search that found nothing
            // ...but it is counted, and the first few say what threw: swallowed, a
            // pathfinder fault looked exactly like "no way there". Runs on a worker.
            if (Interlocked.Increment(ref _searchFaults) <= 5)
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_logger, ex,
                    "[playerbots] path search {From} -> {To} threw", r.From, r.To);
        }
        _batchResults[i] = new SearchResult
        {
            Path = path,
            Ran = true,
            TimedOut = timedOut,
            Us = (Stopwatch.GetTimestamp() - s0) * 1_000_000 / Stopwatch.Frequency,
        };
    }

    /// <summary>Start of tick: hand the paths to their bots.</summary>
    private void ApplySearches(long now)
    {
        if (_batch.Count == 0) return;
        if (!_batchRan) RunSearches(null);
        for (int i = 0; i < _batch.Count; i++)
        {
            var r = _batch[i];
            var res = _batchResults[i];
            var m = r.Mind;
            if (m.SearchGeneration != r.Generation) { WindowSearchDropped++; continue; }
            m.SearchQueued = false;
            m.SearchInBatch = false;
            if (!res.Ran)
            {
                // Not run (the batch's time ran out first): it may ask again soon.
                WindowSearchesNotRun++;
                m.NextSearchMs = now + 300;
                continue;
            }
            WindowSearches++;
            if (res.Us > WindowMaxSearchUs) WindowMaxSearchUs = res.Us;
            if (res.Path is { Count: > 0 } found)
            {
                // The path starts where the search did, so a bot that stands next to
                // that tile (another bot on the same link, later) can join it there.
                var path = new List<Point3D>(found.Count + 1) { r.From };
                path.AddRange(found);
                // It may have stepped along the wall meanwhile: join where it stands.
                int join = JoinIndex(path, m.Char.Position);
                if (join < 0)
                {
                    WindowSearchDropped++;
                    m.NextSearchMs = now;
                    continue;
                }
                m.TilePath = path;
                m.TileIndex = join;
                m.TilePathFor = r.To;
                m.TilePathCorridor = null;
                m.FollowSide = 0;
                m.SearchFailures = 0;
                m.SearchTimeouts = 0;
                m.NextSearchMs = now + 1000;
                // A road link's way is kept from any search along it; the way into a
                // stopping spot only while none is kept (one found at a rescue starts at
                // the road node, where every bot joins it).
                if (r.Corridor is { } key && Chebyshev(path[^1], r.To) <= 1 && (key.To >= 0 || !_corridors.ContainsKey(key)))
                {
                    StoreCorridor(key, path);
                    m.TilePathCorridor = key;
                }
                m.Note(now, $"path found, {path.Count} tiles in {res.Us}us");
            }
            else if (res.TimedOut && ++m.SearchTimeouts <= 2)
            {
                // Out of time, not out of ways: the walk along the wall goes on and
                // the next search starts nearer. Only a third in a row counts as failed.
                WindowSearchTimeouts++;
                m.NextSearchMs = now + 1000;
                m.Note(now, $"search for {r.To} ran out of time ({r.MaxNodes} nodes, {res.Us}us); again soon");
            }
            else
            {
                WindowFailedSearches++;
                m.SearchTimeouts = 0;
                m.SearchFailures++;
                m.NextSearchMs = now + m.SearchFailures switch { 1 => 3000, 2 => 10_000, _ => 20_000 };
                m.Note(now, $"no path to {r.To} ({r.MaxNodes} nodes, {res.Us}us)");
            }
        }
        _batch.Clear();
    }

    // ------------------------------------------------------------------
    //  Corridors: the way along a road link, found once, walked by all
    // ------------------------------------------------------------------

    private readonly Dictionary<(int From, int To), List<Point3D>> _corridors = [];
    private const int MaxCorridors = 20_000;

    /// <summary>Stopping spots per place a kept way into one can be told apart for.</summary>
    private const int SpotsPerPlace = 64;

    /// <summary>The key of the kept way along the last leg, from road node
    /// <paramref name="node"/> into stopping spot <paramref name="spot"/> of a place (-1:
    /// the place itself, for one the data gives no spot): the node and a negative number
    /// naming the place and the spot, so it never meets a road link's key. Null for a
    /// place made up at runtime.</summary>
    private static (int From, int To)? ArrivalKey(BotDestination goal, int spot, int node) =>
        goal.Index < 0 || node < 0 || spot < -1 || spot >= SpotsPerPlace - 1
            ? null
            : (node, -1 - (goal.Index * SpotsPerPlace + spot + 1));

    /// <summary>The place and the spot an arrival key names.</summary>
    private BotDestination? ArrivalOf(int to, out int spot)
    {
        int code = -1 - to;
        spot = code % SpotsPerPlace - 1;
        int index = code / SpotsPerPlace;
        return index >= 0 && index < Nav.Destinations.Count ? Nav.Destinations[index] : null;
    }

    private string CorridorEndName(int to) =>
        to >= 0 ? Nav.Waypoints[to].Name : ArrivalOf(to, out int spot) is { } d ? $"{d.Name} spot {spot}" : "?";

    public int CorridorCount => _corridors.Count;

    private void StoreCorridor((int From, int To) key, List<Point3D> path)
    {
        if (_corridors.Count >= MaxCorridors) _corridors.Clear();
        _corridors[key] = path;
        _corridorsDirty = true;
    }

    private void ForgetCorridor((int From, int To)? key)
    {
        if (key is { } k && _corridors.Remove(k)) _corridorsDirty = true;
    }

    /// <summary>Where the kept ways are written (beside the world save), so a restart
    /// does not have to find them again. Null keeps them in memory only.</summary>
    public string? CorridorFile { get; set; }
    private bool _corridorsDirty;
    private long _nextCorridorSaveMs;
    private const long CorridorSaveEveryMs = 300_000;

    /// <summary>Read the kept ways: one per line, "from|to|x,y,z;x,y,z;..." by node name,
    /// and "spot|from|place|spot|x,y,z;..." for the way from a road node into a stopping
    /// spot (spot -1: the place itself). A line whose nodes or place are gone or whose
    /// path does not end where it should is skipped.</summary>
    public int LoadCorridors()
    {
        if (CorridorFile == null || !File.Exists(CorridorFile)) return 0;
        int n = 0;
        foreach (string line in File.ReadLines(CorridorFile))
        {
            var parts = line.Split('|');
            int from, to;
            Point3D end;
            string points;
            if (parts.Length == 5 && parts[0] == "spot")
            {
                from = Nav.FindWaypoint(parts[1]);
                if (from < 0 || Nav.FindDestination(parts[2]) is not { } place || !int.TryParse(parts[3], out int spot) ||
                    spot < -1 || spot >= place.Arrivals.Count || ArrivalKey(place, spot, from) is not { } key)
                    continue;
                to = key.To;
                end = spot >= 0 ? place.Arrivals[spot] : place.Pos;
                points = parts[4];
            }
            else if (parts.Length == 3)
            {
                from = Nav.FindWaypoint(parts[0]);
                to = Nav.FindWaypoint(parts[1]);
                if (from < 0 || to < 0) continue;
                end = Nav.Waypoints[to].Pos;
                points = parts[2];
            }
            else continue;
            byte map = end.Map;
            var path = new List<Point3D>();
            foreach (string t in points.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var c = t.Split(',');
                if (c.Length != 3 || !short.TryParse(c[0], out short x) || !short.TryParse(c[1], out short y) ||
                    !sbyte.TryParse(c[2], out sbyte z))
                {
                    path = null;
                    break;
                }
                path.Add(new Point3D(x, y, z, map));
            }
            if (path is not { Count: > 0 } || Chebyshev(path[^1], end) > 1) continue;
            _corridors[(from, to)] = path;
            n++;
        }
        return n;
    }

    /// <summary>Write the kept ways when they changed, every five minutes (or now, with
    /// <paramref name="force"/>). The text is built here; the file is written off the
    /// main thread.</summary>
    public void SaveCorridorsIfDue(long now, bool force = false)
    {
        if (CorridorFile == null || (!force && (!_corridorsDirty || now < _nextCorridorSaveMs))) return;
        _nextCorridorSaveMs = now + CorridorSaveEveryMs;
        _corridorsDirty = false;
        var sb = new System.Text.StringBuilder();
        foreach (var ((from, to), path) in _corridors)
        {
            if (to >= 0)
                sb.Append(Nav.Waypoints[from].Name).Append('|').Append(Nav.Waypoints[to].Name).Append('|');
            else if (ArrivalOf(to, out int spot) is { } place)
                sb.Append("spot|").Append(Nav.Waypoints[from].Name).Append('|').Append(place.Name).Append('|')
                  .Append(spot).Append('|');
            else
                continue;
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(path[i].X).Append(',').Append(path[i].Y).Append(',').Append(path[i].Z);
            }
            sb.AppendLine();
        }
        string text = sb.ToString(), file = CorridorFile;
        void Write()
        {
            try
            {
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, file, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (force) Write();
        else Task.Run(Write);
    }

    /// <summary>The kept way along the road link being walked, joined where the bot
    /// stands. Paths are shared between bots and never changed once kept.</summary>
    private bool TryCorridor(BotMind mind, Point3D target, long now)
    {
        if (_stepCorridor is not { } key || !_corridors.TryGetValue(key, out var path)) return false;
        if (Chebyshev(path[^1], target) > 1) return false;
        int join = JoinIndex(path, mind.Char.Position);
        if (join < 0) return false;
        mind.TilePath = path;
        mind.TileIndex = join;
        mind.TilePathFor = target;
        mind.TilePathCorridor = key;
        mind.FollowSide = 0;
        WindowCorridorUses++;
        mind.Note(now, $"follows the kept way {Nav.Waypoints[key.From].Name} -> {CorridorEndName(key.To)}");
        return true;
    }
}
