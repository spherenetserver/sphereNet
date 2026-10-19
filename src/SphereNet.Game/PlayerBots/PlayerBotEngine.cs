using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Server-driven player characters. Each one is a real player character (ISPLAYER,
/// saved with the world like any other) that the server plays: it travels the road
/// network between towns, banks, shops and inns, lingers and talks, fights what
/// attacks it and hunts out of town, heals itself and walks to a healer as a ghost.
///
/// A bot is played through its own <see cref="GameClient"/> (see
/// <see cref="CreateClient"/>) whose NetState has no socket: attack, cast, speech and
/// item use go through exactly the handlers a connected player's packets reach. It
/// is online (it ticks, keeps its sectors awake, is seen by witnesses and guards)
/// but it is not a client: FORCLIENTS and player counts leave it out.
///
/// Persistence: an ordinary player record. The marker and the little state needed
/// to resume - home town, archetype, the destination it was heading for - are tags
/// (PLAYERBOT, PLAYERBOT.HOME, PLAYERBOT.ARCH, PLAYERBOT.GOAL); <see cref="AdoptSaved"/>
/// takes them back after a load and each continues from where it stood.
///
/// Archetypes are CHARDEFs listed in archetypes.txt; their ON=@Create dresses and
/// skills the character the way any pack NPC is set up.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string MarkerTag = "PLAYERBOT";
    public const string HomeTag = "PLAYERBOT.HOME";
    public const string ArchetypeTag = "PLAYERBOT.ARCH";
    public const string GoalTag = "PLAYERBOT.GOAL";
    public const string CorpseTag = "PLAYERBOT.CORPSE";
    /// <summary>The bot's own name, without <see cref="NamePrefix"/>.</summary>
    public const string NameTag = "PLAYERBOT.NAME";

    /// <summary>Put before every bot's name as players see it ("[Bot] "), so nobody
    /// takes a bot for a person. Empty for none. Applied when a bot comes online; the
    /// name without it is kept in <see cref="NameTag"/>, so a changed prefix replaces
    /// the old one.</summary>
    public string NamePrefix { get; set; } = "";

    private readonly GameWorld _world;
    private readonly MovementEngine _movement;
    private readonly Pathfinder _pathfinder;
    private readonly ResourceHolder? _resources;
    private readonly ILogger _logger;
    private readonly Random _rng;
    private readonly Dictionary<uint, BotMind> _minds = [];
    private readonly List<BotMind> _order = [];
    private int _cursor;
    private readonly List<(string DefName, int Weight)> _archetypes = [];
    private long _nextPopulationCheckMs;

    public BotNavGraph Nav { get; }
    public BotChatBook Chat { get; }
    /// <summary>What a player's line asks for (intents.txt).</summary>
    public BotIntents Intents { get; set; } = new();
    /// <summary>The little exchanges bots act out for players (scenes.txt).</summary>
    public BotSceneBook Scenes { get; set; } = new();
    /// <summary>Real happenings bots talk about.</summary>
    public BotGossip Gossip { get; } = new();
    /// <summary>Mean seconds between two scenes played for one player.</summary>
    public int ChatPaceSeconds { get; set; } = 30;
    public int TargetCount { get; set; }

    /// <summary>Tile path searches allowed per tick across every bot, and the time
    /// they may take together. A bot walks straight at its next waypoint and only
    /// searches when that is blocked, so most ticks search nothing.</summary>
    // One search per tick on the terrain-only rule (see StepToward).
    public int PathSearchesPerTick { get; set; } = 1;
    public double PathSearchBudgetMs { get; set; } = 3.0;
    public int PathSearchMaxNodes { get; set; } = 300;
    /// <summary>Stalled legs sent to open a door near by, and last legs moved to
    /// another stopping spot.</summary>
    public long WindowStallDoors { get; private set; }
    /// <summary>A stalled leg this near its end gets a tile search of at most this
    /// many nodes and milliseconds.</summary>
    public int StallSearchRange { get; set; } = 40;
    public int StallSearchMaxNodes { get; set; } = 800;
    public double StallSearchMs { get; set; } = 4.0;
    public long WindowStallSearches { get; private set; }
    /// <summary>Tile searches given up at their deadline, since start.</summary>
    public long SearchTimeouts => _pathfinder.TimedOut;
    public long WindowSpotSwitches { get; private set; }
    /// <summary>Time all bot decisions of one tick may take together.</summary>
    public double ActBudgetMs { get; set; } = 12.0;

    /// <summary>Runs the chardef's own @Create (and @NPCRestock) on a new bot.</summary>
    public Action<Character>? RunCreateScript { get; set; }
    /// <summary>A wired GameClient playing the bot, registered so the world finds it
    /// as the character's client. Null leaves the bot walking and talking only.</summary>
    public Func<Character, GameClient?>? CreateClient { get; set; }
    /// <summary>Undo <see cref="CreateClient"/> when a bot goes offline.</summary>
    public Action<Character>? DropClient { get; set; }
    /// <summary>Says a line overhead when the bot has no client to speak through.</summary>
    public Action<Character, string>? Say { get; set; }
    /// <summary>A bot appeared or jumped: tell the clients around it.</summary>
    public Action<Character>? OnAppeared { get; set; }

    public int Count => _minds.Count;
    public long StuckRescues { get; private set; }
    /// <summary>Arrival spots learned to be unreachable.</summary>
    public int BadSpots { get; private set; }

    /// <summary>Where learned bad spots are kept across restarts (one
    /// "destination|spot" per line; belongs to the world, so it sits with the save).
    /// Deleting it lets bots try every spot again.</summary>
    public string? BadSpotFile { get; set; }

    public int LoadBadSpots()
    {
        if (BadSpotFile == null || !File.Exists(BadSpotFile)) return 0;
        int n = 0;
        foreach (string line in File.ReadAllLines(BadSpotFile))
        {
            if (line.StartsWith("link|", StringComparison.Ordinal))
            {
                var parts = line.Split('|');
                if (parts.Length == 3 && Nav.MarkBadLink(Nav.FindWaypoint(parts[1]), Nav.FindWaypoint(parts[2])))
                    BadLinks++;
                continue;
            }
            int bar = line.LastIndexOf('|');
            if (bar <= 0 || !int.TryParse(line.AsSpan(bar + 1), out int spot)) continue;
            if (Nav.FindDestination(line[..bar]) is { } dest && spot >= 0 && spot < dest.Arrivals.Count &&
                dest.BadArrivals.Add(spot))
                n++;
        }
        BadSpots += n;
        return n;
    }

    private const int RescuesToMarkSpot = 2;

    /// <summary>A road link a bot had to be lifted off twice is left out of routes
    /// (kept in the bad-spot file as "link|from|to" by node name).</summary>
    private void LearnBadLink(int from, int to)
    {
        var key = (Math.Min(from, to), Math.Max(from, to));
        int seen = _linkFailures.GetValueOrDefault(key) + 1;
        _linkFailures[key] = seen;
        if (seen < RescuesToMarkSpot || !Nav.MarkBadLink(from, to)) return;
        BadLinks++;
        if (BadSpotFile == null) return;
        try
        {
            File.AppendAllText(BadSpotFile,
                $"link|{Nav.Waypoints[from].Name}|{Nav.Waypoints[to].Name}{Environment.NewLine}");
        }
        catch (IOException ex) { _logger.LogWarning(ex, "[playerbots] could not record a bad link"); }
    }

    private readonly Dictionary<(int, int), int> _linkFailures = [];
    /// <summary>Road links learned to be impassable.</summary>
    public int BadLinks { get; private set; }

    private void LearnBadSpot(BotDestination dest, int spot)
    {
        if (dest.BadArrivals.Contains(spot)) return;
        int seen = dest.SpotFailures.GetValueOrDefault(spot) + 1;
        dest.SpotFailures[spot] = seen;
        if (seen < RescuesToMarkSpot || !dest.BadArrivals.Add(spot)) return;
        BadSpots++;
        if (BadSpotFile == null) return;
        try { File.AppendAllText(BadSpotFile, $"{dest.Name}|{spot}{Environment.NewLine}"); }
        catch (IOException ex) { _logger.LogWarning(ex, "[playerbots] could not record a bad spot"); }
    }

    // Telemetry window, read and reset by the host's periodic report.
    public long WindowTicks { get; private set; }
    public long WindowTotalUs { get; private set; }
    public long WindowMaxUs { get; private set; }
    public long WindowSearches { get; private set; }
    public long WindowSteps { get; private set; }
    public long WindowFights { get; private set; }
    public long WindowMaxSearchUs { get; private set; }
    public long WindowFailedSearches { get; private set; }
    public long WindowDoors { get; private set; }
    public long WindowRescues { get; private set; }
    public long WindowSlowestActUs { get; private set; }
    public string WindowSlowestAct { get; private set; } = "";
    public long WindowSpawnUs { get; private set; }
    public long WindowMaxRouteUs { get; private set; }
    public long WindowMaxStepUs { get; private set; }
    public long WindowDeferred { get; private set; }
    public long WindowDeaths { get; private set; }
    public long WindowReclaims { get; private set; }
    public long WindowHandBacks { get; private set; }
    public long WindowMoves { get; private set; }
    public long WindowMoveUs { get; private set; }
    public long WindowMaxMoveUs { get; private set; }
    public string WindowMaxMoveWhere { get; private set; } = "";
    /// <summary>Lines said, answers to players, scenes started.</summary>
    public long WindowLines { get; private set; }
    public long WindowReplies { get; private set; }
    public long WindowScenes { get; private set; }
    public void ResetWindow()
    {
        WindowLines = WindowReplies = WindowScenes = WindowSeenSum = WindowSeenSamples = WindowMovedHouse = WindowMounts = WindowRegears = WindowRobesShed = WindowStallDoors = WindowSpotSwitches = WindowStallSearches = 0;
        WindowPurchases = WindowMeditations = WindowPkAttacks = WindowThefts = 0;
        WindowSeenMax = 0;
        WindowTicks = WindowTotalUs = WindowMaxUs = WindowSearches = WindowSteps = WindowFights = 0;
        WindowMaxSearchUs = WindowFailedSearches = WindowDoors = WindowRescues = 0;
        WindowSlowestActUs = WindowSpawnUs = WindowMaxRouteUs = WindowMaxStepUs = 0;
        WindowMoves = WindowMoveUs = WindowMaxMoveUs = WindowDeaths = WindowReclaims = WindowHandBacks = WindowDeferred = 0;
        WindowMaxMoveWhere = "";
        WindowSlowestAct = "";
    }

    private int _searchesLeft;
    private long _searchDeadline;

    public PlayerBotEngine(GameWorld world, MovementEngine movement, ResourceHolder? resources,
        BotNavGraph nav, BotChatBook chat, ILogger logger, int seed = 0)
    {
        _world = world;
        _movement = movement;
        _pathfinder = new Pathfinder(world);
        _resources = resources;
        Nav = nav;
        Chat = chat;
        _logger = logger;
        _rng = seed != 0 ? new Random(seed) : new Random();
    }

    public void LoadArchetypes(string dataDir)
    {
        string path = Path.Combine(dataDir, "archetypes.txt");
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            int weight = parts.Length > 1 && int.TryParse(parts[1], out int w) ? Math.Max(1, w) : 1;
            AddArchetype(parts[0], weight);
        }
    }

    public void AddArchetype(string defName, int weight = 1) => _archetypes.Add((defName, weight));

    public bool IsBot(Character ch) => _minds.ContainsKey(ch.Uid.Value);
    public BotMind? MindOf(Character ch) => _minds.GetValueOrDefault(ch.Uid.Value);

    // ------------------------------------------------------------------
    //  Population
    // ------------------------------------------------------------------

    /// <summary>Take back every PLAYERBOT character the world save holds. Call once,
    /// after the world is loaded and before the first tick.</summary>
    public int AdoptSaved()
    {
        int adopted = 0;
        foreach (var ch in _world.GetAllCharactersSnapshot())
        {
            if (ch.IsDeleted || !ch.TryGetTag(MarkerTag, out _)) continue;
            if (ch.IsOnline) continue;
            BringOnline(ch);
            if (ch.TryGetTag(GoalTag, out string? goal) && Nav.FindDestination(goal ?? "") is { } dest)
                _minds[ch.Uid.Value].Goal = dest;
            adopted++;
        }
        return adopted;
    }

    /// <summary>Spawn one bot at a random town destination. Returns null when no
    /// archetype resolves to a CHARDEF or there is nowhere to stand.</summary>
    public Character? SpawnOne()
    {
        var start = PickDestination(null, spawnable: true);
        if (start == null) return null;
        string? defName = PickArchetype();
        if (defName == null) return null;
        int defIndex = ResolveCharDef(defName);
        if (defIndex < 0) return null;

        var ch = CreateFromDef(defIndex);
        ch.SetTag(MarkerTag, "1");
        ch.SetTag(HomeTag, start.City);
        ch.SetTag(ArchetypeTag, defName);
        int startSpot = start.PickArrival(_rng);
        var pos = startSpot >= 0 ? start.Arrivals[startSpot] : start.Pos;
        if (!_world.PlaceCharacter(ch, pos))
        {
            _world.DeleteObject(ch);
            return null;
        }
        BringOnline(ch);
        _minds[ch.Uid.Value].BeginLinger(start, NowMs(), _rng, short_: true);
        return ch;
    }

    private Character CreateFromDef(int defIndex)
    {
        var def = DefinitionLoader.GetCharDef(defIndex);
        var ch = _world.CreateCharacter();
        ch.CharDefIndex = defIndex;
        ushort body = def is { DispIndex: > 0 } ? def.DispIndex : (ushort)0x0190;
        ch.BaseId = body;
        ch.BodyId = body;
        ch.OBody = body;
        ch.IsPlayer = false; // @Create runs the way it does for any pack NPC
        ch.CreatedUtcSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (def != null)
        {
            ch.Name = string.IsNullOrWhiteSpace(def.Name) ? "Adventurer" : DefinitionLoader.ResolveNamePool(def.Name);
            ch.Str = (short)Math.Clamp(Roll(def.StrMin, def.StrMax, 50), 1, short.MaxValue);
            ch.Dex = (short)Math.Clamp(Roll(def.DexMin, def.DexMax, 50), 1, short.MaxValue);
            ch.Int = (short)Math.Clamp(Roll(def.IntMin, def.IntMax, 50), 1, short.MaxValue);
            CharDefHelper.ApplyCombatProperties(ch, def);
            CharDefHelper.ApplyNpcDefinitionSkills(ch, def);
            CharDefHelper.ApplyNpcDefinitionTags(ch, def);
            RunCreateScript?.Invoke(ch);
            ch.ForgetLastCreatedItem();
        }
        else
        {
            ch.Name = "Adventurer";
            ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        }
        // From here on it is a player character.
        ch.IsPlayer = true;
        ch.NpcBrain = NpcBrainType.None;
        ch.ClearStatFlag(StatFlag.Spawned);
        ch.SpeechColor = SpeechHues[_rng.Next(SpeechHues.Length)];
        EnsureBackpack(ch);
        ch.MaxHits = ch.Str; ch.Hits = ch.MaxHits;
        ch.MaxStam = ch.Dex; ch.Stam = ch.MaxStam;
        ch.MaxMana = ch.Int; ch.Mana = ch.MaxMana;
        return ch;
    }

    // Plain readable speech hues, as players picked them.
    private static readonly ushort[] SpeechHues = [0x0034, 0x0035, 0x0040, 0x0054, 0x0059, 0x005A, 0x0062, 0x0084, 0x0090, 0x03B2];

    private void EnsureBackpack(Character ch)
    {
        if (ch.Backpack != null) return;
        var pack = _world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        pack.Name = "Backpack";
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
    }

    private void BringOnline(Character ch)
    {
        string own = BaseName(ch);
        ch.SetTag(NameTag, own);
        ch.Name = NamePrefix + own;
        ch.IsPlayer = true;
        ch.IsPlayerBot = true;
        ch.IsOnline = true;
        _world.AddOnlinePlayer(ch);
        _world.PlaceCharacter(ch, ch.Position);
        var mind = new BotMind(ch, _rng) { Client = CreateClient?.Invoke(ch) };
        if (_minds.Remove(ch.Uid.Value, out var previous)) _order.Remove(previous);
        _minds[ch.Uid.Value] = mind;
        _order.Add(mind);
        OnAppeared?.Invoke(ch);
    }

    /// <summary>Take a bot offline: it stays in the world save as a logged-out
    /// player and comes back on the next adopt.</summary>
    public void Release(Character ch)
    {
        if (!_minds.Remove(ch.Uid.Value, out var mind)) return;
        _order.Remove(mind);
        if (mind.Scene != null) EndScene(mind.Scene);
        if (mind.Client != null) DropClient?.Invoke(ch);
        ch.IsOnline = false;
        ch.IsPlayerBot = false;
        _world.RemoveOnlinePlayer(ch);
        OnAppeared?.Invoke(ch);
    }

    private void KeepPopulation(long now)
    {
        if (now < _nextPopulationCheckMs) return;
        // One new bot at a time: its @Create runs the pack's script, which is the
        // costliest thing a bot ever does. Arrivals are spread out like logins are.
        _nextPopulationCheckMs = now + 400;
        int missing = TargetCount - _minds.Count;
        if (missing > 0)
        {
            long s0 = Stopwatch.GetTimestamp();
            SpawnOne();
            long us = (Stopwatch.GetTimestamp() - s0) * 1_000_000 / Stopwatch.Frequency;
            if (us > WindowSpawnUs) WindowSpawnUs = us;
        }
        if (missing < 0)
        {
            foreach (var mind in _minds.Values.Take(Math.Min(-missing, 5)).ToList())
                Release(mind.Char);
        }
    }

    // ------------------------------------------------------------------
    //  Tick
    // ------------------------------------------------------------------

    public void Tick(long now)
    {
        long t0 = Stopwatch.GetTimestamp();
        _nowMs = now;
        KeepPopulation(now);
        RefreshAudience(now);
        TickScenes(now);
        _searchesLeft = PathSearchesPerTick;
        _searchDeadline = t0 + (long)(PathSearchBudgetMs * Stopwatch.Frequency / 1000.0);
        List<uint>? gone = null;
        // Decisions share a time budget per tick; bots due once it is spent wait for
        // the next tick (100 ms later, which nobody sees), and the next tick starts
        // where this one stopped so no bot is always last.
        long actDeadline = t0 + (long)(ActBudgetMs * Stopwatch.Frequency / 1000.0);
        bool budgetSpent = false;
        int count = _order.Count;
        int start = count > 0 ? _cursor % count : 0;
        long last = Stopwatch.GetTimestamp();
        for (int k = 0; k < count; k++)
        {
            int idx = (start + k) % count;
            var mind = _order[idx];
            var ch = mind.Char;
            if (ch.IsDeleted)
            {
                (gone ??= []).Add(ch.Uid.Value);
                continue;
            }
            try
            {
                // The client's own timers - swings, casts, skill and bandage delays -
                // run every tick, as they do for a connected player.
                mind.Client?.TickClientState();
                if (mind.PendingCategory != null)
                    SayPending(mind, now);
                if (now < mind.NextActMs) continue;
                if (budgetSpent)
                {
                    WindowDeferred++;
                    continue;
                }
                // Moved far without walking there: a gate, a teleporter or a
                // script carried it. Plan again from wherever it is now.
                if (mind.LastPos.Map != ch.Position.Map || mind.LastPos.GetDistanceTo(ch.Position) > 12)
                {
                    if (mind.Mode == BotMode.Travel && !ch.IsDead)
                        mind.BeginLinger(null, now, _rng, short_: true);
                    mind.ResetLeg(now);
                }
                long a0 = Stopwatch.GetTimestamp();
                BotMode before = mind.Mode;
                Act(mind, now);
                mind.LastPos = ch.Position;
                last = Stopwatch.GetTimestamp();
                long actUs = (last - a0) * 1_000_000 / Stopwatch.Frequency;
                if (actUs > WindowSlowestActUs)
                {
                    WindowSlowestActUs = actUs;
                    WindowSlowestAct = $"{before}->{mind.Mode}";
                }
                if (last > actDeadline)
                {
                    budgetSpent = true;
                    _cursor = idx + 1;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[playerbots] {Name} act failed", ch.Name);
                mind.NextActMs = now + 5000;
            }
        }
        if (gone != null)
            foreach (uint uid in gone)
                if (_minds.Remove(uid, out var dead)) _order.Remove(dead);

        long us = (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency;
        WindowTicks++;
        WindowTotalUs += us;
        if (us > WindowMaxUs) WindowMaxUs = us;
    }

    private void Act(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (ch.IsDead)
        {
            ActGhost(mind, now);
            return;
        }
        if (now >= mind.NextInviteScanMs)
        {
            mind.NextInviteScanMs = now + 1500;
            CheckInvites(mind, now);
        }
        if (mind.Mode != BotMode.Fight && mind.Mode != BotMode.Flee && TryStartFight(mind, now))
            return;
        if (CheckGear(mind, now) || CheckMount(mind, now))
            return;
        if (mind.Mode is BotMode.Linger or BotMode.Travel)
        {
            if (ch.IsPoisoned && now >= mind.NextCureMs && TryCure(mind, now))
            {
                mind.NextActMs = now + 600;
                return;
            }
            if (mind.Scene == null && TryMeditate(mind, now))
                return;
        }
        if (mind.Mode == BotMode.Linger && mind.Scene == null && ActThief(mind, now))
            return;
        if (mind.Mode is BotMode.Linger or BotMode.Travel && ActFollow(mind, now))
            return;
        switch (mind.Mode)
        {
            case BotMode.Linger:
                ActLinger(mind, now);
                break;
            case BotMode.Travel:
                ActTravel(mind, now);
                break;
            case BotMode.Fight:
                ActFight(mind, now);
                break;
            case BotMode.Flee:
                ActFlee(mind, now);
                break;
            case BotMode.Reclaim:
                ActReclaim(mind, now);
                break;
            case BotMode.Loot:
                ActLoot(mind, now);
                break;
            case BotMode.Shop:
                ActShop(mind, now);
                break;
        }
    }

    // ------------------------------------------------------------------
    //  Ghost
    // ------------------------------------------------------------------

    /// <summary>Region (AREADEF NAME or defname) a dead murderer is raised at.</summary>
    public string RedRespawnRegion { get; set; } = "Delucia";
    /// <summary>A raised bot walks back to a corpse within this many tiles; a
    /// farther one has what is left on it handed back.</summary>
    public int ReclaimRange { get; set; } = 40;

    private void ActGhost(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (mind.DeadSinceMs == 0)
        {
            mind.DeadSinceMs = now;
            WindowDeaths++;
            mind.EndFightState();
            mind.Goal = null;
            mind.Route = null;
            ch.RemoveTag(GoalTag);
            // The body is near where it fell; a ghost taken back from the save has
            // moved on, and finds it by the uid it noted then.
            mind.CorpseUid = FindOwnCorpse(ch)?.Uid ?? SavedCorpse(ch);
            if (mind.CorpseUid.IsValid) ch.SetTag(CorpseTag, $"0{mind.CorpseUid.Value:x8}");
            _logger.LogInformation("[playerbots] {Name} died at {Pos}; corpse {Corpse}", ch.Name, ch.Position,
                mind.CorpseUid.IsValid ? mind.CorpseUid.ToString() : "not found");
            if (_rng.Next(100) < 50) Speak(mind, "ghost");
            // Half a minute to a minute as a ghost about the body, in war mode so the
            // living see it (a manifested ghost), then off to be raised.
            mind.GhostUntilMs = now + _rng.Next(30_000, 60_000);
            mind.LingerAnchor = ch.Position;
            mind.NextActMs = now + _rng.Next(1_000, 2_000);
            return;
        }

        if (now < mind.GhostUntilMs)
        {
            if (!ch.IsInWarMode) mind.Client?.HandleWarMode(true);
            WanderNear(mind, mind.LingerAnchor, 5, run: false);
            if (_rng.Next(100) < 8) Speak(mind, "ghost_plea", fallback: "ghost");
            mind.NextActMs = now + _rng.Next(1_200, 3_000);
            return;
        }
        if (ch.IsInWarMode) mind.Client?.HandleWarMode(false);

        // Carried off like a stuck bot: a murderer to its own town, anyone else to
        // the nearest town's healer, and raised there.
        var (spot, at) = RespawnSpot(ch);
        if (spot is { } where)
            Teleport(ch, where);
        if (mind.Client != null) mind.Client.OnResurrect();
        else ch.Resurrect();
        _logger.LogInformation("[playerbots] {Name} raised at {Pos} ({Where}): {Result}", ch.Name, ch.Position,
            ch.IsMurderer ? RedRespawnRegion : at?.City ?? "where it stood", ch.IsDead ? "still dead" : "alive");
        if (ch.IsDead)
        {
            mind.NextActMs = now + 10_000;
            return;
        }
        mind.DeadSinceMs = 0;
        if (_rng.Next(100) < 40) Speak(mind, "death_res");
        mind.BeginLinger(at, now, _rng, short_: true);

        // The body: walked back to when it is near, else what is left on it is
        // handed back. Until then anyone may loot it.
        var corpse = mind.CorpseUid.IsValid ? _world.FindItem(mind.CorpseUid) : null;
        if (IsOwnCorpse(corpse, ch) && corpse!.MapIndex == ch.MapIndex &&
            corpse.Position.GetDistanceTo(ch.Position) <= ReclaimRange)
            StartCorpseTrip(mind, now);
        else
            HandBack(mind, corpse);
    }

    /// <summary>Where a dead bot is raised: a murderer at the anchor of
    /// <see cref="RedRespawnRegion"/>, anyone else at a stopping spot of the nearest
    /// healer (by distance; a ghost is carried there). Null leaves it where it is.</summary>
    private (Point3D? Spot, BotDestination? At) RespawnSpot(Character ch)
    {
        if (ch.IsMurderer && _world.TryGetRegionPoint(RedRespawnRegion, out var red))
            return (BotNavGraph.TrySeatNear(_world.MapData, red, out var seated) ? seated : red, null);
        BotDestination? best = null;
        int bestDist = int.MaxValue;
        foreach (var d in Nav.Destinations)
        {
            if (!d.Type.Equals("Healer", StringComparison.OrdinalIgnoreCase) || d.City.Length == 0) continue;
            int dist = d.Pos.Map == ch.MapIndex ? d.Pos.GetDistanceTo(ch.Position) : int.MaxValue - 1;
            if (dist < bestDist) { bestDist = dist; best = d; }
        }
        if (best == null) return (null, null);
        int spot = best.PickArrival(_rng);
        return (spot >= 0 ? best.Arrivals[spot] : best.Pos, best);
    }

    // ------------------------------------------------------------------
    //  Speech
    // ------------------------------------------------------------------

    /// <summary>Say a line of <paramref name="category"/> the way a player types it:
    /// through the client's speech handler (NPCs hear it, @Speech runs), or as a plain
    /// overhead line when the bot has no client. Nobody within earshot, nothing is
    /// said. The language is <paramref name="lang"/>, else the nearest listener's,
    /// else the default. <c>{name}</c> is whoever the bot speaks to
    /// (<paramref name="to"/>), <c>{me}</c> the bot. Returns whether a line was said.</summary>
    private bool Speak(BotMind mind, string category, string? fallback = "small_talk", Character? to = null,
        string? lang = null, IReadOnlyDictionary<string, string>? extra = null, bool force = false)
    {
        var ch = mind.Char;
        // Talk nobody hears is skipped; a line that does something in the world (a
        // call for the guards) is said anyway.
        var listener = WatcherNear(ch.Position, HearRange);
        if (listener == null && !force) return false;
        lang ??= listener?.Language ?? Chat.Language;
        var tokens = Tokens(mind, lang);
        if (to != null) tokens["name"] = FirstName(to);
        if (extra != null)
            foreach (var (k, v) in extra) tokens[k] = v;
        string? line = Chat.Pick(category, _rng, tokens, lang) ??
                       (fallback != null ? Chat.Pick(fallback, _rng, tokens, lang) : null);
        if (line == null) return false;
        WindowLines++;
        if (mind.Client != null)
            mind.Client.HandleSpeech(0, ch.SpeechColor != 0 ? ch.SpeechColor : (ushort)0x03B2, 3, line);
        else
            Say?.Invoke(ch, line);
        return true;
    }

    /// <summary>The tokens any line may use: where the bot is and is going, a price, a
    /// count, its own name, who it fights or who stands by, and a word from each
    /// <c>token_*</c> list (goods, materials, pets, "last night") in the language.</summary>
    private Dictionary<string, string> Tokens(BotMind mind, string lang)
    {
        var ch = mind.Char;
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["place"] = mind.At?.City is { Length: > 0 } c ? c : mind.Home,
            ["dest"] = mind.Goal?.City is { Length: > 0 } g ? g : mind.Home,
            ["price"] = (_rng.Next(1, 40) * 50).ToString(),
            ["count"] = _rng.Next(2, 30).ToString(),
            ["me"] = FirstName(ch),
        };
        if (mind.Foe != null) tokens["other"] = mind.Foe.GetName();
        else
            foreach (var other in _world.GetCharsInRange(ch.Position, 8))
                if (other != ch && other.IsPlayer && !other.IsDead)
                {
                    tokens["other"] = BaseName(other);
                    break;
                }
        foreach (string list in TokenLists)
            if (Chat.Pick("token_" + list, _rng, null, lang) is { } word)
                tokens[list] = word;
        if (DungeonName() is { } dungeon) tokens["dungeon"] = dungeon;
        return tokens;
    }

    private static readonly string[] TokenLists = ["item", "when", "mat", "pet"];

    /// <summary>A dungeon's name the way players say it: "destard", "orc cave".</summary>
    private string? DungeonName()
    {
        _dungeonNames ??= Nav.Destinations
            .Where(d => d.Type.Equals("DungeonEntrance", StringComparison.OrdinalIgnoreCase))
            .Select(d => ShortPlaceName(d.Name))
            .Distinct()
            .ToArray();
        return _dungeonNames.Length > 0 ? _dungeonNames[_rng.Next(_dungeonNames.Length)] : null;
    }

    private string[]? _dungeonNames;

    internal static string ShortPlaceName(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(w => !(w.Length >= 2 && w[0] == 'L' && char.IsDigit(w[1])) &&
                            !w.Equals("Entrance", StringComparison.OrdinalIgnoreCase) &&
                            !w.Equals("Deep", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return string.Join(' ', words.Count > 0 ? words : [name]).ToLowerInvariant();
    }

    /// <summary>A character's name as people say it: a bot's without its prefix.</summary>
    internal static string BaseName(Character ch) =>
        ch.TryGetTag(NameTag, out string? own) && !string.IsNullOrEmpty(own) ? own : ch.Name ?? "";

    internal static string FirstName(Character ch)
    {
        string name = BaseName(ch);
        int sp = name.IndexOf(' ');
        return sp > 0 ? name[..sp] : name;
    }

    /// <summary>The engine's clock: the time of the last tick, so what players say
    /// between ticks is timed on the same clock the bots act on.</summary>
    private long NowMs() => _nowMs != 0 ? _nowMs : Environment.TickCount64;

    private long _nowMs;
}
