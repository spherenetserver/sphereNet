using System.Diagnostics;
using System.Text;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// What staff need to see why a bot stands where it does: the stalled bots by mode
/// (sampled once a second for the 30-second report), the reasons steps were refused,
/// the bots stalled longest right now (<see cref="ListStalls"/>), one bot's last
/// decisions (<see cref="TraceOf"/>), and a check of the road data against the running
/// world (<see cref="NavCheck"/>).
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>A bot that made no ground toward what it walks to for this long counts
    /// as stalled in the report.</summary>
    private const long StallReportMs = 10_000;

    private readonly Dictionary<string, long> _refusals = new(StringComparer.Ordinal);
    private readonly int[] _stalledMax = new int[Enum.GetValues<BotMode>().Length];
    private readonly int[] _stalledLast = new int[Enum.GetValues<BotMode>().Length];
    private long _nextStallSampleMs;

    public long WindowMobileWaits { get; private set; }
    public long WindowSideSteps { get; private set; }
    public long WindowDoorwaySteps { get; private set; }
    public long WindowUnreachableFoes { get; private set; }
    /// <summary>Walks given up because they made no ground (a horse, a mark, a body,
    /// a vendor, a leader out of reach).</summary>
    public long WindowGiveUps { get; private set; }

    private void ResetDiagnosticsWindow()
    {
        _refusals.Clear();
        Array.Clear(_stalledMax);
    }

    /// <summary>How long this bot has made no ground toward what it walks to; 0 when
    /// it is not walking anywhere. A trip counts by its leg's progress clock.</summary>
    public long StallOf(BotMind mind, long now)
    {
        if (mind.Char.IsDead) return 0;
        if (mind.Mode == BotMode.Travel && mind.Route != null)
            return now - mind.LegProgressMs;
        return StalledMs(mind, now);
    }

    private void SampleStalls(long now)
    {
        if (now < _nextStallSampleMs) return;
        _nextStallSampleMs = now + 1000;
        Array.Clear(_stalledLast);
        foreach (var m in _order)
            if (StallOf(m, now) > StallReportMs)
                _stalledLast[(int)m.Mode]++;
        for (int i = 0; i < _stalledLast.Length; i++)
            if (_stalledLast[i] > _stalledMax[i]) _stalledMax[i] = _stalledLast[i];
    }

    /// <summary>The most bots stalled at once in the window, by mode, e.g.
    /// "Travel:3 Fight:1"; "-" when none.</summary>
    public string StallReport()
    {
        var sb = new StringBuilder();
        var modes = Enum.GetValues<BotMode>();
        for (int i = 0; i < modes.Length; i++)
            if (_stalledMax[i] > 0)
                sb.Append(sb.Length > 0 ? " " : "").Append(modes[i]).Append(':').Append(_stalledMax[i]);
        return sb.Length > 0 ? sb.ToString() : "-";
    }

    /// <summary>The reasons steps were refused in the window, most first.</summary>
    public string RefusalReport() =>
        _refusals.Count == 0 ? "-"
            : string.Join(" ", _refusals.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}:{kv.Value}"));

    /// <summary>The <paramref name="count"/> bots stalled longest right now, one line each.</summary>
    public IReadOnlyList<string> ListStalls(int count)
    {
        long now = NowMs();
        var lines = new List<string>();
        foreach (var (m, stall) in _order
                     .Select(m => (Mind: m, Stall: StallOf(m, now)))
                     .Where(x => x.Stall > 2000)
                     .OrderByDescending(x => x.Stall)
                     .Take(Math.Max(1, count)))
        {
            var ch = m.Char;
            string where = m.Mode switch
            {
                BotMode.Travel => $"{m.Goal?.Name ?? "?"} leg {m.Leg}/{m.Route?.Count ?? 0} -> {(m.Route != null ? m.LegTarget(Nav) : default)}",
                BotMode.Fight or BotMode.Flee => m.Foe != null ? $"{m.Foe.GetName()} at {m.Foe.Position}" : "-",
                _ => $"{m.WatchAnchor}",
            };
            lines.Add($"0{ch.Uid.Value:X8} {BaseName(ch)} at {ch.Position} {m.Mode} {stall / 1000}s {where} " +
                      $"refusal={m.LastRefusal ?? "-"} edge={m.FollowSide} path={(m.TilePath != null ? $"{m.TileIndex}/{m.TilePath.Count}" : "-")} " +
                      $"search={(m.SearchQueued ? "queued" : m.SearchFailures > 0 ? $"failed x{m.SearchFailures}" : "-")}");
        }
        if (lines.Count == 0) lines.Add("no bot is stalled");
        return lines;
    }

    /// <summary>A one-line account of what a bot is doing, for the panel: its mode, where
    /// it is going, and how long it has been stalled when it has.</summary>
    public string ActivityOf(Objects.Characters.Character ch)
    {
        if (!_minds.TryGetValue(ch.Uid.Value, out var m)) return "";
        long stall = StallOf(m, NowMs());
        string what = ch.IsDead ? "ghost" : m.Mode switch
        {
            BotMode.Travel => $"Travel -> {m.Goal?.Name ?? "?"}",
            BotMode.Linger => m.At != null ? $"Linger at {m.At.Name}" : "Linger",
            BotMode.Fight or BotMode.Flee => $"{m.Mode} {m.Foe?.GetName() ?? ""}".TrimEnd(),
            _ => m.Mode.ToString(),
        };
        return stall > StallReportMs ? $"{what} (stalled {stall / 1000}s)" : what;
    }

    /// <summary>The <paramref name="count"/> places with the most bots at them or on
    /// their way, against the room their type has.</summary>
    public IReadOnlyList<string> ListPlaces(int count)
    {
        long now = NowMs();
        var lines = Nav.Destinations
            .Select(d => (Place: d, Crowd: CrowdAt(d, now)))
            .Where(x => x.Crowd > 0)
            .OrderByDescending(x => x.Crowd)
            .Take(Math.Max(1, count))
            .Select(x => $"{x.Crowd,4}/{PlaceRoom(x.Place),-3} {x.Place.Name} ({x.Place.Type}) at {x.Place.Pos}")
            .ToList();
        if (lines.Count == 0) lines.Add("no bot is at or on its way to any place");
        return lines;
    }

    /// <summary>The bots within <paramref name="range"/> of a point on map 0..n, one line
    /// each with what it is doing and its last decision: why a crowd stands where it does.</summary>
    public IReadOnlyList<string> ListNear(int x, int y, int range, int map)
    {
        long now = NowMs();
        var lines = new List<string>();
        var near = _order.Where(m => m.Char.MapIndex == map &&
                                     Math.Max(Math.Abs(m.Char.X - x), Math.Abs(m.Char.Y - y)) <= range).ToList();
        lines.Add($"{near.Count} bots within {range} of {x},{y} (map {map})");
        foreach (var g in near.GroupBy(m => ActivityOf(m.Char)).OrderByDescending(g => g.Count()))
            lines.Add($"  {g.Count(),4}  {g.Key}");
        foreach (var m in near.Take(40))
        {
            var last = m.Trace().LastOrDefault();
            lines.Add($"0{m.Char.Uid.Value:X8} {BaseName(m.Char)} at {m.Char.Position} {ActivityOf(m.Char)} " +
                      $"| {(last.Text != null ? $"-{(now - last.Ms) / 1000}s {last.Text}" : "-")}");
        }
        return lines;
    }

    /// <summary>One bot's last decisions, oldest first, found by uid (hex or decimal)
    /// or by a part of its name.</summary>
    public IReadOnlyList<string> TraceOf(string key)
    {
        var mind = FindMind(key);
        if (mind == null) return [$"no bot '{key}'"];
        long now = NowMs();
        var ch = mind.Char;
        var lines = new List<string>
        {
            $"0{ch.Uid.Value:X8} {BaseName(ch)} at {ch.Position} mode {mind.Mode} goal {mind.Goal?.Name ?? "-"} " +
            $"stalled {StallOf(mind, now) / 1000}s refusal {mind.LastRefusal ?? "-"}",
        };
        foreach (var (ms, text) in mind.Trace())
            lines.Add($"  -{(now - ms) / 1000.0:0.0}s {text}");
        return lines;
    }

    private BotMind? FindMind(string key)
    {
        key = key.Trim();
        if (key.Length == 0) return null;
        string hex = key.TrimStart('0', 'x', 'X');
        if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint huid) &&
            _minds.TryGetValue(huid, out var byHex))
            return byHex;
        if (uint.TryParse(key, out uint duid) && _minds.TryGetValue(duid, out var byDec))
            return byDec;
        return _order.FirstOrDefault(m => BaseName(m.Char).Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check the road data against the running world. Always: every portal-type place
    /// and whether the world has a portal there. With <paramref name="links"/>: a tile
    /// search along every road link and from every stopping spot to its node, with an
    /// online bot as the walker (the walk rule is a character's); failures are listed
    /// and, with <paramref name="learn"/>, left out of routes and written to the bad-spot
    /// file as if bots had learned them. A link search runs on this thread: on a full
    /// road network it takes a while.
    /// </summary>
    public IReadOnlyList<string> NavCheck(bool links, bool learn, int maxNodes = 4000)
    {
        if (!_portalsResolved) ResolvePortals();
        var lines = new List<string>();
        var portals = Nav.Destinations.Where(d => d.IsPortalType).ToList();
        lines.Add($"portal places: {portals.Count}, with a portal in this world: {portals.Count(d => !d.Portalless)}, walked through: {portals.Count(d => d.WalkedThrough)}, without: {portals.Count(d => d.Portalless && !d.WalkedThrough)}");
        foreach (var d in portals.Where(d => d.Portalless && !d.WalkedThrough))
            lines.Add($"  no portal: {d.Name} ({d.Type}) at {d.Pos}{(IsDeadPortal(d) ? " - never chosen" : " - visited as a place")}");
        if (!links) return lines;

        var walker = _order.FirstOrDefault(m => !m.Char.IsDead)?.Char;
        if (walker == null)
        {
            lines.Add("link check needs an online bot as the walker; none is online");
            return lines;
        }
        var sw = Stopwatch.StartNew();
        int checkedLinks = 0, badLinks = 0, checkedSpots = 0, badSpots = 0;
        for (int i = 0; i < Nav.Waypoints.Count; i++)
        {
            var a = Nav.Waypoints[i];
            foreach (int j in a.Links)
            {
                if (j < i) continue;
                checkedLinks++;
                var b = Nav.Waypoints[j];
                if (Reachable(a.Pos, b.Pos, walker, maxNodes)) continue;
                badLinks++;
                lines.Add($"  link {a.Name} {a.Pos} -> {b.Name} {b.Pos}: no way");
                if (learn && Nav.MarkBadLink(i, j))
                {
                    BadLinks++;
                    AppendBadSpotLine($"link|{a.Name}|{b.Name}");
                }
            }
        }
        foreach (var d in Nav.Destinations)
            for (int s = 0; s < d.Arrivals.Count; s++)
            {
                if (d.ArrivalLinks[s].Count == 0) continue;
                checkedSpots++;
                var node = Nav.Waypoints[d.ArrivalLinks[s][0]];
                var spot = d.Arrivals[s];
                if (d.IsPortalType && d.PortalTiles is { Count: > 0 } && d.PortalTiles.Exists(t => SameTile(t, spot)))
                    spot = BesideTile(spot); // a search onto a teleporter tile proves nothing
                if (Reachable(node.Pos, spot, walker, maxNodes)) continue;
                badSpots++;
                lines.Add($"  spot {d.Name}#{s} {d.Arrivals[s]} from {node.Name}: no way");
                if (learn && d.BadArrivals.Add(s))
                {
                    BadSpots++;
                    AppendBadSpotLine($"{d.Name}|{s}");
                }
            }
        lines.Insert(1, $"links: {checkedLinks} checked, {badLinks} with no way; stopping spots: {checkedSpots} checked, {badSpots} with no way ({sw.ElapsedMilliseconds} ms{(learn ? ", learned" : "")})");
        return lines;
    }

    private bool Reachable(Point3D from, Point3D to, Objects.Characters.Character walker, int maxNodes)
    {
        if (from.Map != to.Map) return false;
        int dist = from.GetDistanceTo(to);
        if (dist <= 1) return true;
        var path = _pathfinder.FindPath(from, to, from.Map, CanFlags.None, walker,
            maxNodes: maxNodes, maxRadius: dist + 20, options: PathOptions.OpenDoors);
        return path != null;
    }

    private void AppendBadSpotLine(string line)
    {
        if (BadSpotFile == null) return;
        try { File.AppendAllText(BadSpotFile, line + Environment.NewLine); }
        catch (IOException) { }
    }
}
