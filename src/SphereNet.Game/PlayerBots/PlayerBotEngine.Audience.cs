using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>A connected player as the bots see one: the language they were last heard
/// in, when the next little scene near them is due, the bot they are talking with,
/// and how many bots stand within earshot.</summary>
public sealed class BotWatcher
{
    public required Character Char { get; init; }
    public string? Language { get; set; }
    public long NextSceneMs { get; set; }
    public long LastGreetMs { get; set; }
    public long LastRedAlertMs { get; set; }
    public long LastSeenMs { get; set; }
    public int Crowd { get; set; }
    public BotMind? Focus { get; set; }
    public long FocusUntilMs { get; set; }
}

/// <summary>
/// Who is watching, and where bots spend their time. Bots are for the players who
/// see them: a bot nobody can see or hear says nothing, and bots lean toward the
/// places players are (<see cref="Gravity"/>), up to a crowd of <see cref="CrowdCap"/>
/// around any one player. Home towns follow <c>towns.txt</c> weights and the places a
/// bot goes follow <c>places.txt</c> weights (a bank is a crowd, a tailor is not).
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>How far a player hears and sees a bot.</summary>
    public const int HearRange = 18;

    private readonly Dictionary<uint, BotWatcher> _watchers = [];
    private readonly List<BotWatcher> _watching = [];
    private long _nextAudienceMs;
    private readonly Dictionary<string, int> _townWeights = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _placeWeights = new(StringComparer.OrdinalIgnoreCase);
    private int _defaultPlaceWeight = 1;
    private Dictionary<string, int> _homeCounts = new(StringComparer.OrdinalIgnoreCase);
    private long _nextHomeCountMs;

    /// <summary>Percent of a bot's town trips that go to a place near a player.</summary>
    public int Gravity { get; set; } = 40;
    /// <summary>Bots around one player beyond which no more are drawn to them.</summary>
    public int CrowdCap { get; set; } = 14;

    public IReadOnlyCollection<BotWatcher> Watching => _watching;

    // Telemetry: bots within sight of each player, sampled once a second.
    public long WindowSeenSum { get; private set; }
    public long WindowSeenSamples { get; private set; }
    public int WindowSeenMax { get; private set; }

    /// <summary>Read <c>towns.txt</c> (home town weights) and <c>places.txt</c>
    /// (destination type weights; <c>*</c> sets the default). Lines are
    /// <c>name weight</c>; a name may contain spaces.</summary>
    public void LoadDistribution(string dataDir)
    {
        ReadWeights(Path.Combine(dataDir, "towns.txt"), _townWeights);
        ReadWeights(Path.Combine(dataDir, "places.txt"), _placeWeights);
        if (_placeWeights.Remove("*", out int def)) _defaultPlaceWeight = def;
    }

    public void SetTownWeight(string town, int weight) => _townWeights[town] = weight;
    public void SetPlaceWeight(string type, int weight) => _placeWeights[type] = weight;

    private static void ReadWeights(string path, Dictionary<string, int> into)
    {
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.LastIndexOfAny([' ', '\t']);
            if (sp <= 0 || !int.TryParse(line[(sp + 1)..], out int w)) continue;
            into[line[..sp].Trim()] = Math.Max(0, w);
        }
    }

    private int PlaceWeight(BotDestination d) => _placeWeights.TryGetValue(d.Type, out int w) ? w : _defaultPlaceWeight;

    private T? WeightedPick<T>(IReadOnlyList<T> items, Func<T, int> weight)
    {
        long total = 0;
        foreach (var i in items) total += Math.Max(0, weight(i));
        if (total <= 0) return items.Count > 0 ? items[_rng.Next(items.Count)] : default;
        long roll = (long)(_rng.NextDouble() * total);
        foreach (var i in items)
        {
            roll -= Math.Max(0, weight(i));
            if (roll < 0) return i;
        }
        return items[^1];
    }

    // ------------------------------------------------------------------
    //  Audience
    // ------------------------------------------------------------------

    private BotWatcher WatcherOf(Character player, long now)
    {
        if (!_watchers.TryGetValue(player.Uid.Value, out var w))
            _watchers[player.Uid.Value] = w = new BotWatcher
            {
                Char = player,
                NextSceneMs = now + _rng.Next(3000, 9000),
                LastSeenMs = now,
            };
        return w;
    }

    /// <summary>Once a second: the connected players, how many bots each can see, and
    /// what each of them should hear next.</summary>
    private void RefreshAudience(long now)
    {
        if (now < _nextAudienceMs) return;
        _nextAudienceMs = now + 1000;
        _watching.Clear();
        foreach (var ch in _world.OnlinePlayers)
        {
            if (!ch.HasClient || ch.IsDeleted) continue;
            var w = WatcherOf(ch, now);
            w.LastSeenMs = now;
            int crowd = 0;
            foreach (var c in _world.GetCharsInRange(ch.Position, HearRange))
                if (c.IsPlayerBot && !c.IsDead && c.MapIndex == ch.MapIndex) crowd++;
            w.Crowd = crowd;
            _watching.Add(w);
            WindowSeenSum += crowd;
            WindowSeenSamples++;
            if (crowd > WindowSeenMax) WindowSeenMax = crowd;
        }
        if (_watchers.Count > _watching.Count + 32)
            foreach (uint uid in _watchers.Where(kv => now - kv.Value.LastSeenMs > 600_000).Select(kv => kv.Key).ToList())
                _watchers.Remove(uid);
        foreach (var w in _watching)
            Direct(w, now);
    }

    private static bool Within(Point3D a, Point3D b, int range) =>
        a.Map == b.Map && Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) <= range;

    /// <summary>The nearest connected player within <paramref name="range"/> of a point.</summary>
    private BotWatcher? WatcherNear(Point3D p, int range)
    {
        BotWatcher? best = null;
        int bestD = int.MaxValue;
        foreach (var w in _watching)
        {
            var wp = w.Char.Position;
            if (wp.Map != p.Map) continue;
            int d = Math.Max(Math.Abs(wp.X - p.X), Math.Abs(wp.Y - p.Y));
            if (d <= range && d < bestD) { best = w; bestD = d; }
        }
        return best;
    }

    private bool IsWatched(Point3D p, int range = HearRange) => WatcherNear(p, range) != null;

    // ------------------------------------------------------------------
    //  Where bots spend their time
    // ------------------------------------------------------------------

    /// <summary>Town trips drawn toward players: a place within 30 tiles of a player
    /// who is not already surrounded, no farther than a walk across town.</summary>
    private BotDestination? PickNearPlayers(BotMind mind, IEnumerable<BotDestination> pool)
    {
        if (Gravity <= 0 || _watching.Count == 0 || _rng.Next(100) >= Gravity) return null;
        var here = mind.Char.Position;
        var pulled = pool.Where(d => d != mind.At && Within(d.Pos, here, 220) &&
                                     WatcherNear(d.Pos, 30) is { } w && w.Crowd < CrowdCap).ToList();
        return pulled.Count > 0 ? WeightedPick(pulled, PlaceWeight) : null;
    }

    /// <summary>The home town a new bot gets: the town furthest below its share of
    /// the population by <c>towns.txt</c>; null when no weights are set (then the
    /// spawn place decides, as before).</summary>
    private string? PickSpawnTown(IReadOnlyCollection<string> towns)
    {
        if (_townWeights.Count == 0) return null;
        var counts = HomeCounts(0, force: true);
        double total = towns.Sum(t => (double)_townWeights.GetValueOrDefault(t));
        if (total <= 0) return null;
        int population = _minds.Count + 1;
        var deficits = towns
            .Select(t => (Town: t, Gap: _townWeights.GetValueOrDefault(t) / total * population - counts.GetValueOrDefault(t)))
            .Where(x => _townWeights.GetValueOrDefault(x.Town) > 0)
            .ToList();
        if (deficits.Count == 0) return null;
        var under = deficits.Where(x => x.Gap > 0).ToList();
        if (under.Count == 0) under = deficits;
        double sum = under.Sum(x => Math.Max(0.1, x.Gap));
        double roll = _rng.NextDouble() * sum;
        foreach (var (town, gap) in under)
        {
            roll -= Math.Max(0.1, gap);
            if (roll < 0) return town;
        }
        return under[^1].Town;
    }

    private Dictionary<string, int> HomeCounts(long now, bool force = false)
    {
        if (!force && now < _nextHomeCountMs) return _homeCounts;
        _nextHomeCountMs = now + 30_000;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _order)
        {
            string home = m.Home;
            if (home.Length > 0) counts[home] = counts.GetValueOrDefault(home) + 1;
        }
        return _homeCounts = counts;
    }

    /// <summary>A bot of a town that has more than its share, out of sight of every
    /// player, now and then moves house to a town that has too few. Its next trips
    /// take it there (by road, or by moongate when no road leads home).</summary>
    private bool MaybeMoveHouse(BotMind mind, long now)
    {
        if (_townWeights.Count == 0 || _rng.Next(100) >= 4 || IsWatched(mind.Char.Position, 40)) return false;
        string home = mind.Home;
        var counts = HomeCounts(now);
        double total = _townWeights.Values.Sum();
        if (total <= 0) return false;
        int population = _minds.Count;
        double Share(string t) => _townWeights.GetValueOrDefault(t) / total * population;
        if (counts.GetValueOrDefault(home) <= Share(home) * 1.15) return false;
        var under = _townWeights.Keys
            .Where(t => t != home && counts.GetValueOrDefault(t) < Share(t) * 0.85 &&
                        Nav.Destinations.Any(d => d.City.Equals(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (under.Count == 0) return false;
        string to = WeightedPick(under, t => (int)Math.Max(1, Share(t) - counts.GetValueOrDefault(t)))!;
        mind.Char.SetTag(HomeTag, to);
        counts[home] = counts.GetValueOrDefault(home) - 1;
        counts[to] = counts.GetValueOrDefault(to) + 1;
        WindowMovedHouse++;
        return true;
    }

    public long WindowMovedHouse { get; private set; }
}
