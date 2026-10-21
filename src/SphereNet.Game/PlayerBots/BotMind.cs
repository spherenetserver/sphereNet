using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

public enum BotMode { Linger, Travel, Fight, Flee, Reclaim, Loot, Shop }

/// <summary>Why the last step a bot tried was refused: a wall or the lie of the land
/// (walk round it or search), somebody standing there (wait, step aside), out of
/// breath, or held fast (wait).</summary>
public enum BotBlock { None, Static, Mobile, Stamina, Frozen }

/// <summary>What one bot is doing right now. Runtime only; the parts worth
/// resuming after a restart live in the character's PLAYERBOT tags.</summary>
public sealed class BotMind
{
    public Character Char { get; }
    public GameClient? Client { get; init; }
    public BotMode Mode { get; set; } = BotMode.Linger;
    public long NextActMs { get; set; }
    public long NextChatMs { get; set; }
    public long LingerUntilMs { get; set; }
    public long DeadSinceMs { get; set; }
    public long GhostUntilMs { get; set; }
    public BotDestination? Goal { get; set; }
    public BotDestination? At { get; set; }
    public Point3D LingerAnchor { get; set; }

    // Road trip
    public List<int>? Route { get; set; }
    public int Leg { get; set; }
    public Point3D FinalSpot { get; set; }
    /// <summary>Which of the goal's arrival spots FinalSpot is, or -1.</summary>
    public int GoalSpot { get; set; } = -1;
    /// <summary>The trip ends by stepping onto a portal (a teleporter, a gate):
    /// FinalSpot is the portal tile itself and is stepped on, never stopped short of.</summary>
    public bool PortalTrip { get; set; }
    public List<Point3D>? TilePath { get; set; }
    public int TileIndex { get; set; }
    /// <summary>The point <see cref="TilePath"/> leads to.</summary>
    public Point3D TilePathFor { get; set; }
    /// <summary>The road link whose shared tile path <see cref="TilePath"/> is, when it
    /// is one (see the engine's corridors).</summary>
    public (int From, int To)? TilePathCorridor { get; set; }
    // Walking an obstacle's edge: which hand (+1 right, -1 left, 0 not following),
    // the last heading, steps taken along it and the distance where it was met.
    public int FollowSide { get; set; }
    public Direction FollowHeading { get; set; }
    public int FollowSteps { get; set; }
    public int FollowFromDist { get; set; }
    public int BestLegDist { get; set; } = int.MaxValue;
    /// <summary>The distance the leg's progress clock was last restarted at: only a
    /// real gain (three tiles, or any gain while walking straight) restarts it, so a
    /// walk along a wall that inches closer cannot hold off the rescue forever.</summary>
    public int ProgressDist { get; set; } = int.MaxValue;
    public long LegProgressMs { get; set; }
    public long LegStartedMs { get; set; }
    public int LegStartDist { get; set; }
    /// <summary>Other stopping spots tried on this leg after it stalled.</summary>
    public int SpotTries { get; set; }
    public bool Running { get; set; }
    public long CrawlUntilMs { get; set; }
    public Point3D LastPos { get; set; }
    public int GateTries { get; set; }
    /// <summary>The road link the current leg walks (for the shared tile paths), or null.</summary>
    public (int From, int To)? LegLink { get; set; }

    // Carried off by its own step (a teleporter, a gate): where from, handled after the act.
    public bool Carried { get; set; }

    // Teleporter and gate tiles this bot was carried off by lately. A route that
    // only passes over one would bounce the bot back and forth, so it steps round
    // them unless the trip is to that very portal.
    private readonly Dictionary<(int, int, byte), long> _portals = [];

    public void RememberPortal(int x, int y, byte map, long now) => _portals[(x, y, map)] = now + 180_000;

    public bool AvoidsTile(int x, int y, byte map, long now)
    {
        if (_portals.Count == 0 || !_portals.TryGetValue((x, y, map), out long until)) return false;
        if (now > until) { _portals.Remove((x, y, map)); return false; }
        // Going to a portal on purpose: that one is the way.
        if (Goal != null && Goal.IsPortalType && Goal.Pos.Map == map)
        {
            if (Goal.PortalTiles is { Count: > 0 } tiles)
            {
                foreach (var t in tiles)
                    if (t.X == x && t.Y == y) return false;
            }
            if (Math.Max(Math.Abs(Goal.Pos.X - x), Math.Abs(Goal.Pos.Y - y)) <= 3)
                return false;
        }
        return true;
    }
    /// <summary>Start nodes this trip could not walk to; cleared on any progress.</summary>
    public HashSet<int> BadStarts { get; } = [];
    /// <summary>A shut door being walked to, when the way out of a room was not found.</summary>
    public Objects.Items.Item? DoorTarget { get; set; }
    public long DoorSinceMs { get; set; }
    public bool DoorTried { get; set; }
    // The near-door lookup, kept a moment: a stalled bot asks every act.
    public Objects.Items.Item? DoorScan { get; set; }
    public long DoorScanUntilMs { get; set; }

    // Tile path searches (run off the main thread, a tick later; see the engine).
    public bool SearchQueued { get; set; }
    /// <summary>Its request is in the batch being run; it can no longer be changed.</summary>
    public bool SearchInBatch { get; set; }
    /// <summary>The trip goes to a place to wait about it (a player killer's haunt),
    /// not through it: a portal there is not stepped on.</summary>
    public bool Haunting { get; set; }
    public int SearchGeneration { get; set; }
    public Point3D SearchTarget { get; set; }
    public int SearchMaxNodes { get; set; }
    public int SearchRadius { get; set; }
    public bool SearchAvoidChars { get; set; }
    public (int From, int To)? SearchCorridor { get; set; }
    public long NextSearchMs { get; set; }
    public int SearchFailures { get; set; }

    // Somebody in the way: until when the bot waits, how often it has, and since when.
    public long MobileWaitUntilMs { get; set; }
    public int MobileTries { get; set; }
    public long MobileSinceMs { get; set; }
    public long BreathUntilMs { get; set; }

    // Why the last refused step was refused.
    public string? LastRefusal { get; set; }
    public BotBlock LastBlock { get; set; }

    // Progress toward whatever the bot is walking to, in any mode: the point, the
    // best distance and when it was last bettered. A target that moves more than a
    // few tiles is a new one.
    public Point3D WatchAnchor { get; set; }
    public int WatchBest { get; set; } = int.MaxValue;
    public long WatchSinceMs { get; set; }
    public long WatchLastMs { get; set; }

    // Progress clocks for walks that are given up when they make no ground (a horse,
    // a mark, a body, a vendor, a leader), one per purpose so that two walks taking
    // turns do not reset each other.
    private readonly Dictionary<string, (Point3D Anchor, int Best, long Since, long Last)> _progress = [];

    /// <summary>How long the walk for <paramref name="purpose"/> has gone without getting
    /// nearer <paramref name="target"/>. A target moved more than three tiles, or a walk
    /// not asked about for ten seconds, starts over.</summary>
    public long NoGainMs(string purpose, Point3D target, int dist, long now)
    {
        if (!_progress.TryGetValue(purpose, out var p) || p.Anchor.Map != target.Map ||
            p.Anchor.GetDistanceTo(target) > 3 || now - p.Last > 10_000)
        {
            _progress[purpose] = (target, dist, now, now);
            return 0;
        }
        if (dist < p.Best) { p.Best = dist; p.Since = now; }
        p.Last = now;
        _progress[purpose] = p;
        return now - p.Since;
    }

    // A trip that found no road: how often, since when.
    public int NoRouteCount { get; set; }
    public long NoRouteSinceMs { get; set; }

    // The body it left when it died, walked back to and emptied once raised.
    public Serial CorpseUid { get; set; } = Serial.Invalid;
    public long ReclaimUntilMs { get; set; }
    public HashSet<uint> ReclaimSkipped { get; } = [];
    public bool CorpseOpened { get; set; }

    // Party: a player's invitation being answered, and when the leader was last near.
    public Character? InviteFrom { get; set; }
    public long InviteAnswerMs { get; set; }
    public long NextInviteScanMs { get; set; }
    public long LeaderSeenMs { get; set; }
    public long FollowCloseMs { get; set; }
    public long FollowPausedUntilMs { get; set; }

    // A reply waiting out its typing delay: what to say, to whom, in which language,
    // with which tokens, and what to do once it is said (cast a resurrection, say).
    public string? PendingCategory { get; set; }
    public long PendingSayMs { get; set; }
    public string? PendingFallback { get; set; }
    public Character? PendingTo { get; set; }
    public string? PendingLanguage { get; set; }
    public Dictionary<string, string>? PendingTokens { get; set; }
    public Action? PendingAction { get; set; }
    /// <summary>A line said first, joined to the reply: a hello, "like I said".</summary>
    public string? PendingPrefix { get; set; }

    // Talking with players and other bots.
    /// <summary>The scene this bot is acting in, if any; it stays put meanwhile.</summary>
    public BotScene? Scene { get; set; }
    /// <summary>Players this bot has said hello to (or decided not to) on sight, and when.</summary>
    public Dictionary<uint, long> Greeted { get; } = [];
    /// <summary>What a player is haggling with this bot about: the goods and the last price.</summary>
    // The last lines this bot said, so the next pick is another one when it can be.
    private readonly string[] _said = new string[6];
    private int _saidNext;

    public void NoteSaid(string line)
    {
        _said[_saidNext] = line;
        _saidNext = (_saidNext + 1) % _said.Length;
        LastLine = line;
    }

    public bool SaidLately(string line) => Array.IndexOf(_said, line) >= 0;

    /// <summary>The last line this bot said.</summary>
    public string? LastLine { get; private set; }

    public string? TalkTopic { get; set; }
    public string? TalkItem { get; set; }
    public long NextResMs { get; set; }
    /// <summary>When the bot next heals a player who asks.</summary>
    public long NextFavorMs { get; set; }
    public long NextMountCheckMs { get; set; }
    public int MountTries { get; set; }

    // Taking what a kill dropped: whose body, where it fell, until when.
    public Serial LootVictim { get; set; } = Serial.Invalid;
    public Point3D LootFrom { get; set; }
    public long LootUntilMs { get; set; }
    public Objects.Items.Item? LootCorpse { get; set; }
    public bool LootOpened { get; set; }
    public bool LootAll { get; set; }
    public int LootTaken { get; set; }
    public HashSet<uint> LootSkipped { get; } = [];

    // Shopping: what for, at which vendor, and how far the purchase got.
    public BotNeed? ShoppingFor { get; set; }
    public Character? ShopVendor { get; set; }
    public long ShopUntilMs { get; set; }
    public bool ShopListOpen { get; set; }
    public long NextSupplyCheckMs { get; set; }

    // Self care and the trade a rogue plies.
    public long NextMeditateMs { get; set; }
    public long NextCureMs { get; set; }
    public Point3D? FleeTarget { get; set; }
    public long FleeTargetMs { get; set; }
    public long NextRogueMs { get; set; }
    public Character? Mark { get; set; }
    public int RogueStage { get; set; }
    public long NextGearCheckMs { get; set; }
    public int LingerExtensions { get; set; }

    // Fighting
    public Character? Foe { get; set; }
    public BotMode ResumeMode { get; set; } = BotMode.Linger;
    public long FightStartedMs { get; set; }
    /// <summary>When the bot last stood where it can strike its foe (in reach, and in
    /// sight for a bow or a spell). A foe it cannot get at for long is given up.</summary>
    public long FightInReachMs { get; set; }
    public long NextScanMs { get; set; }
    public long NextHealMs { get; set; }
    public long NextCastMs { get; set; }
    public long FleeUntilMs { get; set; }
    public Point3D FleeFrom { get; set; }
    /// <summary>Foes found out of reach (behind a fence, over water), left alone
    /// until the time noted.</summary>
    public Dictionary<uint, long> AvoidFoes { get; } = [];

    public bool Avoids(Character c, long now)
    {
        if (AvoidFoes.Count == 0 || !AvoidFoes.TryGetValue(c.Uid.Value, out long until)) return false;
        if (now < until) return true;
        AvoidFoes.Remove(c.Uid.Value);
        return false;
    }

    // Errands: banking, the rune home, company on the road.
    public int BankStage { get; set; }
    public long NextBankMs { get; set; }
    public long BankUntilMs { get; set; }
    public long BankDeposit { get; set; }
    public long BankWithdraw { get; set; }
    public Character? Banker { get; set; }
    public long NextMarkMs { get; set; }
    /// <summary>When to look whether the last Mark took; 0 when none is pending.</summary>
    public long MarkCheckMs { get; set; }
    public long NextRecallMs { get; set; }
    public long RecallSinceMs { get; set; }
    /// <summary>The bot this one travels with, keeping pace, until it arrives.</summary>
    public BotMind? Leader { get; set; }

    /// <summary>Acts that threw, in a row.</summary>
    public int ActFaults { get; set; }

    /// <summary>How big a monster this bot takes on, as a multiple of its own hit
    /// points; and the hit-point percentage at which it runs. Rolled per bot, so
    /// some gamble and some are careful.</summary>
    public double Nerve { get; }
    public int FleeAt { get; }

    public bool IsFighter { get; }
    public bool IsCaster { get; }
    public bool IsArcher { get; }

    public string Home => Char.TryGetTag(PlayerBotEngine.HomeTag, out string? h) ? h ?? "" : "";

    // The last decisions, for SERV staff looking at why a bot stands where it does.
    private const int TraceSize = 32;
    private readonly (long Ms, string Text)[] _trace = new (long, string)[TraceSize];
    private int _traceNext;
    private int _traceCount;

    public void Note(long now, string text)
    {
        _trace[_traceNext] = (now, text);
        _traceNext = (_traceNext + 1) % TraceSize;
        if (_traceCount < TraceSize) _traceCount++;
    }

    /// <summary>The noted decisions, oldest first.</summary>
    public IEnumerable<(long Ms, string Text)> Trace()
    {
        int start = (_traceNext - _traceCount + TraceSize) % TraceSize;
        for (int i = 0; i < _traceCount; i++)
            yield return _trace[(start + i) % TraceSize];
    }

    public BotMind(Character ch, Random rng)
    {
        Char = ch;
        LingerAnchor = ch.Position;
        LastPos = ch.Position;
        // A resumed bot that was on its way somewhere sets off again at once.
        Mode = ch.TryGetTag(PlayerBotEngine.GoalTag, out _) ? BotMode.Travel : BotMode.Linger;
        string arch = ch.TryGetTag(PlayerBotEngine.ArchetypeTag, out string? a) ? a ?? "" : "";
        IsCaster = arch.Contains("mage", StringComparison.OrdinalIgnoreCase) || ch.GetSkill(SkillType.Magery) >= 600;
        IsArcher = arch.Contains("archer", StringComparison.OrdinalIgnoreCase) || ch.GetSkill(SkillType.Archery) >= 600;
        IsFighter = IsCaster || IsArcher || arch.Contains("warrior", StringComparison.OrdinalIgnoreCase) ||
                    ch.GetSkill(SkillType.Swordsmanship) >= 600 || ch.GetSkill(SkillType.MaceFighting) >= 600;
        Nerve = 0.8 + rng.NextDouble() * 1.2;
        FleeAt = rng.Next(20, 40);
    }

    /// <summary>The point the current leg walks to: the route's next node, then
    /// the destination's stopping spot.</summary>
    public Point3D LegTarget(BotNavGraph nav) =>
        Route != null && Leg < Route.Count ? nav.Waypoints[Route[Leg]].Pos : FinalSpot;

    public void ResetLeg(long now)
    {
        TilePath = null;
        TilePathCorridor = null;
        TileIndex = 0;
        FollowSide = 0;
        BestLegDist = int.MaxValue;
        ProgressDist = int.MaxValue;
        LegProgressMs = now;
        LegStartedMs = now;
        LegStartDist = -1;
        SpotTries = 0;
        MobileTries = 0;
        MobileWaitUntilMs = 0;
    }

    public void EndFightState()
    {
        Foe = null;
        TilePath = null;
        FleeTarget = null;
    }

    public void BeginLinger(BotDestination? at, long now, Random rng, bool short_)
    {
        Mode = BotMode.Linger;
        GateTries = 0;
        PortalTrip = false;
        Haunting = false;
        Leader = null;
        At = at;
        Goal = null;
        Route = null;
        TilePath = null;
        LegLink = null;
        Char.RemoveTag(PlayerBotEngine.GoalTag);
        LingerAnchor = Char.Position;
        LingerExtensions = 0;
        bool bank = at != null && at.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase);
        long span = short_ ? rng.Next(10_000, 60_000)
            : bank ? rng.Next(180_000, 480_000) : rng.Next(60_000, 240_000);
        LingerUntilMs = now + span;
        NextChatMs = now + rng.Next(5_000, 40_000);
        NextActMs = now + rng.Next(1000, 4000);
    }
}
