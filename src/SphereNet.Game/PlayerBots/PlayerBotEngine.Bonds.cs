using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>How well a bot knows a player.</summary>
public enum BotBondLevel { Stranger, Acquaintance, Friend }

/// <summary>
/// Friendship. A bot grows fond of a player the way people do: by being talked to, by
/// fighting side by side, by a fair trade or a present, by being raised from the dead;
/// it cools on one who strikes it, and a bond nobody keeps up fades after a few days.
/// The points and thresholds are data (<c>bonds.txt</c>). A bot's own nature, picked
/// once (<c>PLAYERBOT.TRAIT</c>: kind, shy, chatty or stingy), sets how fast it warms
/// and whether it asks for the reagents' worth of a spell. It remembers the last things
/// it shared with players (<c>PLAYERBOT.MEMORY</c>) and brings them up.
/// <list type="bullet">
/// <item><c>PLAYERBOT.BONDS</c>: <c>uid:score:minute</c> per player (the unix minute
/// last met), up to <see cref="BondsMax"/>, the weakest dropped first.</item>
/// <item><c>PLAYERBOT.MEMORY</c>: <c>uid|kind|place|minute</c> joined by <c>;</c>, the
/// last <see cref="MemoryMax"/>; kinds <c>met</c>, <c>hunt</c>, <c>raised</c>,
/// <c>gate</c>, <c>gift</c>, <c>guide</c>.</item>
/// </list>
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string BondsTag = "PLAYERBOT.BONDS";
    public const string MemoryTag = "PLAYERBOT.MEMORY";
    public const string TraitTag = "PLAYERBOT.TRAIT";
    private const int BondsMax = 24;
    private const int MemoryMax = 12;

    private static readonly string[] Traits = ["kind", "shy", "chatty", "stingy"];

    private readonly Dictionary<string, int> _bondRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["acquaintance"] = 5,
        ["friend"] = 25,
        ["max"] = 100,
        ["talk"] = 1,
        ["talk_gap"] = 60,
        ["favor"] = 1,
        ["hunt"] = 2,
        ["hunt_gap"] = 120,
        ["trade"] = 2,
        ["gift_per"] = 100,
        ["gift_max"] = 10,
        ["raised"] = 8,
        ["attacked"] = -15,
        ["decay_days"] = 3,
        ["decay_per_day"] = 1,
        ["trait.kind"] = 35,
        ["trait.shy"] = 20,
        ["trait.chatty"] = 25,
        ["trait.stingy"] = 20,
    };

    /// <summary>Read <c>bonds.txt</c>: <c>key value</c> per line, <c>#</c> comments.
    /// Keys left out keep their defaults.</summary>
    public void LoadBonds(string dataDir)
    {
        string path = Path.Combine(dataDir, "bonds.txt");
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int v)) _bondRules[parts[0]] = v;
        }
    }

    public void SetBondRule(string key, int value) => _bondRules[key] = value;
    private int Rule(string key) => _bondRules.GetValueOrDefault(key);

    private static long UnixMinute() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;

    // ------------------------------------------------------------------
    //  Nature
    // ------------------------------------------------------------------

    /// <summary>The bot's nature, picked once by the weights in bonds.txt and kept.</summary>
    internal string TraitOf(BotMind m)
    {
        if (m.Trait != null) return m.Trait;
        if (m.Char.TryGetTag(TraitTag, out string? t) && t != null && Traits.Contains(t))
            return m.Trait = t;
        int total = Traits.Sum(x => Math.Max(0, Rule("trait." + x)));
        string pick = Traits[0];
        if (total > 0)
        {
            int roll = _rng.Next(total);
            foreach (string x in Traits)
            {
                roll -= Math.Max(0, Rule("trait." + x));
                if (roll < 0) { pick = x; break; }
            }
        }
        m.Char.SetTag(TraitTag, pick);
        return m.Trait = pick;
    }

    // ------------------------------------------------------------------
    //  Bonds
    // ------------------------------------------------------------------

    private readonly record struct Bond(uint Uid, int Score, long Minute);

    private static List<Bond> ReadBonds(Character ch)
    {
        var list = new List<Bond>();
        if (!ch.TryGetTag(BondsTag, out string? tag) || string.IsNullOrEmpty(tag)) return list;
        foreach (string entry in tag.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split(':');
            if (p.Length == 3 && uint.TryParse(p[0], out uint uid) && int.TryParse(p[1], out int score) &&
                long.TryParse(p[2], out long minute))
                list.Add(new Bond(uid, score, minute));
        }
        return list;
    }

    private static void WriteBonds(Character ch, List<Bond> list) =>
        ch.SetTag(BondsTag, string.Join(' ', list.Select(b => $"{b.Uid}:{b.Score}:{b.Minute}")));

    /// <summary>A bond as it stands now: a positive one fades by
    /// <c>decay_per_day</c> a day after <c>decay_days</c> days unseen.</summary>
    private int Faded(Bond b, long minute)
    {
        if (b.Score <= 0) return b.Score;
        long days = (minute - b.Minute) / 1440;
        int grace = Rule("decay_days");
        if (days <= grace) return b.Score;
        return (int)Math.Max(0, b.Score - (days - grace) * Rule("decay_per_day"));
    }

    internal int BondScore(BotMind m, Character player)
    {
        uint uid = player.Uid.Value;
        foreach (var b in ReadBonds(m.Char))
            if (b.Uid == uid) return Faded(b, UnixMinute());
        return 0;
    }

    internal BotBondLevel BondLevel(BotMind m, Character player)
    {
        int s = BondScore(m, player);
        return s >= Rule("friend") ? BotBondLevel.Friend
            : s >= Rule("acquaintance") ? BotBondLevel.Acquaintance
            : BotBondLevel.Stranger;
    }

    /// <summary>A player who struck the bot and has not made up for it.</summary>
    private bool Grudges(BotMind m, Character player) => BondScore(m, player) < 0;

    /// <summary>Points for or against a player. The bot's nature scales a gain (a kind
    /// one warms fast, a shy one slowly); a bond that turns to friendship is said so,
    /// when the player is there to hear it.</summary>
    internal void AddBond(BotMind m, Character player, int points, long now, string? why = null)
    {
        if (points == 0 || player.IsPlayerBot || IsRogueBot(m.Char)) return;
        if (points > 0)
        {
            int pct = TraitOf(m) switch { "kind" => 150, "shy" => 70, "chatty" => 120, _ => 100 };
            points = Math.Max(1, points * pct / 100);
        }
        long minute = UnixMinute();
        var list = ReadBonds(m.Char);
        uint uid = player.Uid.Value;
        int at = list.FindIndex(b => b.Uid == uid);
        int before = at >= 0 ? Faded(list[at], minute) : 0;
        int after = Math.Clamp(before + points, -Rule("max"), Rule("max"));
        if (at >= 0) list.RemoveAt(at);
        list.Add(new Bond(uid, after, minute));
        if (list.Count > BondsMax)
        {
            // The weakest bond the bot has not kept up goes.
            var drop = list.Take(list.Count - 1).OrderBy(b => Faded(b, minute)).ThenBy(b => b.Minute).First();
            list.Remove(drop);
        }
        WriteBonds(m.Char, list);
        if (why != null) m.Note(now, $"{(points > 0 ? "+" : "")}{points} with {player.GetName()} ({why}): {after}");
        WindowBondPoints += points;
        int friend = Rule("friend");
        if (before < friend && after >= friend)
        {
            WindowNewFriends++;
            Memorize(m, player, "met", PlaceName(m.Char.Position));
            if (_rng.Next(100) < 70)
                QueueSoon(m, "bond_friend", now, player);
        }
    }

    /// <summary>A line said to the bot: a point, at most once a <c>talk_gap</c>.</summary>
    private void BondTalk(BotMind m, Character player, long now)
    {
        if (!Due(m, player, "talk", now, Rule("talk_gap") * 1000L)) return;
        AddBond(m, player, Rule("talk"), now);
    }

    /// <summary>True once per <paramref name="gapMs"/> for a bot, a player and a
    /// reason; marks it done.</summary>
    private static bool Due(BotMind m, Character player, string what, long now, long gapMs)
    {
        var key = (player.Uid.Value, what);
        if (m.BondClock.TryGetValue(key, out long last) && now - last < gapMs) return false;
        m.BondClock[key] = now;
        if (m.BondClock.Count > 128)
            foreach (var old in m.BondClock.Where(kv => now - kv.Value > 600_000).Select(kv => kv.Key).ToList())
                m.BondClock.Remove(old);
        return true;
    }

    public long WindowBondPoints { get; private set; }
    public long WindowNewFriends { get; private set; }

    // ------------------------------------------------------------------
    //  Memories
    // ------------------------------------------------------------------

    private readonly record struct Memory(uint Uid, string Kind, string Place, long Minute);

    private static List<Memory> ReadMemories(Character ch)
    {
        var list = new List<Memory>();
        if (!ch.TryGetTag(MemoryTag, out string? tag) || string.IsNullOrEmpty(tag)) return list;
        foreach (string entry in tag.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split('|');
            if (p.Length == 4 && uint.TryParse(p[0], out uint uid) && long.TryParse(p[3], out long minute))
                list.Add(new Memory(uid, p[1], p[2], minute));
        }
        return list;
    }

    /// <summary>Something shared with a player, kept to be brought up later; the same
    /// kind with the same player is kept once, the latest.</summary>
    internal void Memorize(BotMind m, Character player, string kind, string place)
    {
        if (player.IsPlayerBot) return;
        place = place.Replace('|', ' ').Replace(';', ' ');
        var list = ReadMemories(m.Char);
        list.RemoveAll(x => x.Uid == player.Uid.Value && x.Kind == kind);
        list.Add(new Memory(player.Uid.Value, kind, place, UnixMinute()));
        if (list.Count > MemoryMax) list.RemoveRange(0, list.Count - MemoryMax);
        m.Char.SetTag(MemoryTag, string.Join(';', list.Select(x => $"{x.Uid}|{x.Kind}|{x.Place}|{x.Minute}")));
    }

    /// <summary>A memory shared with this player, as a line to say: the category
    /// (<c>memory_&lt;kind&gt;</c>) and its <c>{place}</c> and <c>{when}</c>. Null when
    /// there is none worth bringing up (a place nobody has a name for).</summary>
    private (string Category, Dictionary<string, string> Tokens)? PickMemory(BotMind m, Character player, string lang)
    {
        var mine = ReadMemories(m.Char).Where(x => x.Uid == player.Uid.Value && x.Place.Length > 0).ToList();
        if (mine.Count == 0) return null;
        var pick = mine[_rng.Next(mine.Count)];
        string category = "memory_" + pick.Kind;
        if (!Chat.Has(category, lang)) return null;
        long ago = UnixMinute() - pick.Minute;
        string list = ago < 120 ? "token_ago_today" : ago < 4 * 1440 ? "token_ago_days" : "token_ago_long";
        var tokens = new Dictionary<string, string> { ["place"] = pick.Place };
        if (Chat.Pick(list, _rng, null, lang) is { } when) tokens["when"] = when;
        return (category, tokens);
    }

    /// <summary>A line said to a player soon (after the typing delay), unless the
    /// bot already has something to say.</summary>
    private void QueueSoon(BotMind m, string category, long now, Character to,
        Dictionary<string, string>? tokens = null)
    {
        if (m.PendingCategory != null) return;
        string? lang = _watchers.TryGetValue(to.Uid.Value, out var w) ? w.Language : null;
        QueueReply(m, category, now, to: to, lang: lang, fallback: null, tokens: tokens);
    }

    // ------------------------------------------------------------------
    //  Moments that make or break a bond
    // ------------------------------------------------------------------

    /// <summary>A bot that was raised by someone else (not carried off to a healer):
    /// the one who raised it is thanked and remembered, and its body is walked back to
    /// as after any resurrection.</summary>
    private void OnRaisedByOther(BotMind mind, long now)
    {
        var ch = mind.Char;
        mind.DeadSinceMs = 0;
        mind.GhostUntilMs = 0;
        mind.Note(now, "raised where it fell");
        if (ch.IsInWarMode) mind.Client?.HandleWarMode(false);
        Character? raiser = null;
        int best = int.MaxValue;
        foreach (var c in _world.GetCharsInRange(ch.Position, 12))
        {
            if (c == ch || c.IsDead || !c.IsPlayer || c.IsDeleted) continue;
            int d = c.Position.GetDistanceTo(ch.Position);
            // A player before a bot: a bot raising it is bots' business.
            int rank = d + (c.IsPlayerBot ? 100 : 0);
            if (rank < best) { best = rank; raiser = c; }
        }
        mind.BeginLinger(NearestPlace(ch.Position, 8), now, _rng, short_: true);
        if (raiser is { IsPlayerBot: false })
        {
            AddBond(mind, raiser, Rule("raised"), now, "raised it");
            Memorize(mind, raiser, "raised", PlaceName(ch.Position));
            QueueSoon(mind, "thanks_raised", now, raiser);
        }
        var corpse = mind.CorpseUid.IsValid ? _world.FindItem(mind.CorpseUid) : null;
        if (IsOwnCorpse(corpse, ch) && corpse!.MapIndex == ch.MapIndex &&
            corpse.Position.GetDistanceTo(ch.Position) <= ReclaimRange)
            StartCorpseTrip(mind, now);
        else if (corpse != null)
            HandBack(mind, corpse);
    }

    /// <summary>A real player set on the bot: the bond with them sours, once per fight.</summary>
    private void OnAttackedByPlayer(BotMind mind, Character attacker, long now)
    {
        if (!attacker.HasClient) return;
        if (!Due(mind, attacker, "attacked", now, 120_000)) return;
        AddBond(mind, attacker, Rule("attacked"), now, "attacked it");
        // A companion who turns on it is one no more.
        if (mind.Companion == attacker) EndCompanionship(mind, now, quietly: true);
    }

    /// <summary>A fight won beside a player it goes about with (party leader or
    /// companion): points, and the hunt is remembered.</summary>
    private void OnFightWonBeside(BotMind mind, long now)
    {
        var ch = mind.Char;
        Character? mate = mind.Companion ?? PartyLeader(mind);
        if (mate == null || mate.IsDead || !mate.HasClient || mate.MapIndex != ch.MapIndex ||
            mate.Position.GetDistanceTo(ch.Position) > 15)
            return;
        if (!Due(mind, mate, "hunt", now, Rule("hunt_gap") * 1000L)) return;
        AddBond(mind, mate, Rule("hunt"), now, "a fight won together");
        string place = IsUnderground(ch.Position) && NearestDungeon(ch.Position) is { } dungeon ? dungeon : PlaceName(ch.Position);
        Memorize(mind, mate, "hunt", place);
    }

    /// <summary>The dungeon a point is in, by its nearest dungeon place.</summary>
    private string? NearestDungeon(Point3D p)
    {
        BotDestination? best = null;
        int bestD = 200;
        foreach (var d in Nav.Destinations)
        {
            if (d.Pos.Map != p.Map || !d.Type.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase)) continue;
            int dd = d.Pos.GetDistanceTo(p);
            if (dd < bestD) { bestD = dd; best = d; }
        }
        return best == null ? null : ShortPlaceName(best.Name);
    }
}
