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

    // The conversation so far: what was last asked and about which place, and the
    // question a bot last put to this player, so "and the healer?", "how far?" or a
    // plain "yes" are read against it.
    public BotIntentKind TopicKind { get; set; }
    public string? TopicPlaceType { get; set; }
    public string? TopicPlaceWord { get; set; }
    public long TopicMs { get; set; }
    public string? Asked { get; set; }
    public BotMind? AskedBy { get; set; }
    public long AskedMs { get; set; }
    /// <summary>What a bot's question offers: done on a yes ("shall i heal you?").</summary>
    public Action? AskedYes { get; set; }

    /// <summary>The favour this player has asked of a bot, while it is in hand.</summary>
    public BotFavor? Favor { get; set; }
    /// <summary>When a bot last offered this player help unasked, and where and since
    /// when the player has stood about (someone looking for something).</summary>
    public long LastOfferMs { get; set; }
    public Point3D IdleAnchor { get; set; }
    public long IdleSinceMs { get; set; }
    public long LastSpokeMs { get; set; }
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
    private readonly Dictionary<string, int> _placeRooms = new(StringComparer.OrdinalIgnoreCase);
    private int _defaultPlaceRoom = 6;
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
    /// (destination type weights and, optionally, room; <c>*</c> sets the default).
    /// Lines are <c>name weight [room]</c>; a name may contain spaces.</summary>
    public void LoadDistribution(string dataDir)
    {
        ReadWeights(Path.Combine(dataDir, "towns.txt"), _townWeights);
        ReadPlaces(Path.Combine(dataDir, "places.txt"));
        if (_placeWeights.Remove("*", out int def)) _defaultPlaceWeight = def;
        if (_placeRooms.Remove("*", out int room)) _defaultPlaceRoom = room;
    }

    public void SetTownWeight(string town, int weight) => _townWeights[town] = weight;
    public void SetPlaceWeight(string type, int weight) => _placeWeights[type] = weight;
    public void SetPlaceRoom(string type, int room) => _placeRooms[type] = room;

    private void ReadPlaces(string path)
    {
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[^1], out int last)) continue;
            if (parts.Length >= 3 && int.TryParse(parts[^2], out int weight))
            {
                string name = string.Join(' ', parts[..^2]);
                _placeWeights[name] = Math.Max(0, weight);
                _placeRooms[name] = Math.Max(1, last);
            }
            else
                _placeWeights[string.Join(' ', parts[..^1])] = Math.Max(0, last);
        }
    }

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

    /// <summary>How many bots a place of this type holds before it is full: past it a
    /// bank would be a scrum of bots shoving each other about the doorway.</summary>
    private int PlaceRoom(BotDestination d) => _placeRooms.TryGetValue(d.Type, out int r) ? r : _defaultPlaceRoom;

    /// <summary>Places of one type closer than this share a crowd (a bank listed twice
    /// in the data): who stands at one stands at the other.</summary>
    private const int CrowdShareRange = 10;

    private readonly Dictionary<BotDestination, int> _placeCrowd = [];
    private readonly Dictionary<BotDestination, List<BotDestination>> _crowdNeighbours = [];
    private long _nextCrowdCountMs;

    /// <summary>Bots at a place or on their way to it, counted once every two seconds,
    /// with the places that share its crowd.</summary>
    private int CrowdAt(BotDestination d, long now)
    {
        if (now >= _nextCrowdCountMs)
        {
            _nextCrowdCountMs = now + 2000;
            _placeCrowd.Clear();
            foreach (var m in _order)
            {
                if (m.Char.IsDead) continue;
                var place = m.Mode == BotMode.Travel ? m.Goal : m.Mode == BotMode.Linger ? m.At : null;
                if (place != null) _placeCrowd[place] = _placeCrowd.GetValueOrDefault(place) + 1;
            }
        }
        if (!_crowdNeighbours.TryGetValue(d, out var near))
        {
            near = Nav.Destinations.Where(o => o.Pos.Map == d.Pos.Map && o.Pos.GetDistanceTo(d.Pos) <= CrowdShareRange &&
                                               o.Type.Equals(d.Type, StringComparison.OrdinalIgnoreCase)).ToList();
            _crowdNeighbours[d] = near;
        }
        int crowd = 0;
        foreach (var o in near) crowd += _placeCrowd.GetValueOrDefault(o);
        return crowd;
    }

    /// <summary>A place's weight with its crowd taken off: the fuller it is the less it
    /// draws, and a full one draws nobody.</summary>
    private int RoomyWeight(BotDestination d, long now)
    {
        int room = PlaceRoom(d);
        int free = room - CrowdAt(d, now);
        return free <= 0 ? 0 : PlaceWeight(d) * free;
    }

    /// <summary>A place by weight and room. When every one is full (a small town with
    /// more bots than places), the one least over its room, so the overflow spreads
    /// over the town instead of piling onto the place with the biggest weight.</summary>
    private BotDestination? PickPlace(IReadOnlyList<BotDestination> list)
    {
        long now = NowMs();
        if (list.Any(d => RoomyWeight(d, now) > 0)) return WeightedPick(list, d => RoomyWeight(d, now));
        var drawing = list.Where(d => PlaceWeight(d) > 0).ToList();
        if (drawing.Count == 0) return WeightedPick(list, PlaceWeight);
        double least = drawing.Min(d => (double)CrowdAt(d, now) / PlaceRoom(d));
        var emptiest = drawing.Where(d => (double)CrowdAt(d, now) / PlaceRoom(d) <= least + 0.25).ToList();
        return WeightedPick(emptiest, PlaceWeight);
    }

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
        return pulled.Count > 0 ? PickPlace(pulled) : null;
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
