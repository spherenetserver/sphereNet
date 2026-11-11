using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Manners: the small things that make a bot read as somebody when a player looks at
/// it. It turns to whoever it talks to (the engine's own facing update, so the viewers
/// see the turn), bows or salutes on a greeting or a thanks (the client's animate
/// request), says small gestures as emotes, eats and drinks what its pack holds (a
/// double click, as a player's), keeps the pace of the road (a stroll in town, a run on
/// the open road, a breath at a junction), does not set off in the same tick as the
/// others at its place, and does not say what a player in earshot has just heard from
/// another bot. Things that would appear out of nothing - a mount, a whole kit - appear
/// where no player looks.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>Percent chance a greeting or a thanks comes with a bow or a salute.</summary>
    public int GesturePercent { get; set; } = 45;
    /// <summary>Percent chance a walker a player sees stops a breath at a junction.</summary>
    public int JunctionPausePercent { get; set; } = 18;
    /// <summary>Percent chance an idle bot looks up at a player passing close by.</summary>
    public int GlancePercent { get; set; } = 35;

    /// <summary>How long a line one bot said is left alone by the others, for the
    /// players who heard it.</summary>
    private const long SharedLineMs = 180_000;
    private const long PaceCheckMs = 2000;

    public long WindowTurns { get; private set; }
    public long WindowGestures { get; private set; }
    public long WindowEmotes { get; private set; }
    public long WindowMeals { get; private set; }
    /// <summary>Mounts and kits held back because a player was looking.</summary>
    public long WindowConjureWaits { get; private set; }
    public long WindowThanks { get; private set; }
    public long WindowTheftReacts { get; private set; }
    public long WindowFightReacts { get; private set; }
    public long WindowGhostWalks { get; private set; }
    public long WindowGhostRaises { get; private set; }
    public long WindowSeats { get; private set; }
    public long WindowPractice { get; private set; }
    public long WindowPoiScans { get; private set; }

    private void ResetMannersWindow()
    {
        WindowTurns = WindowGestures = WindowEmotes = WindowMeals = WindowConjureWaits = 0;
        WindowThanks = WindowTheftReacts = WindowFightReacts = WindowGhostWalks = WindowGhostRaises = 0;
        WindowSeats = WindowPractice = WindowPoiScans = WindowCorpseRuns = WindowDeathsResumed = 0;
    }

    // ------------------------------------------------------------------
    //  Facing
    // ------------------------------------------------------------------

    /// <summary>Turn the bot to face <paramref name="target"/> through the engine's
    /// facing update (UpdateDir: the direction changes and the viewers are told through
    /// the facing hook, as for any character a script turns). False when it already
    /// faces that way.</summary>
    internal bool Face(BotMind mind, Point3D target)
    {
        var ch = mind.Char;
        if (ch.IsDeleted || target.Map != ch.MapIndex || (target.X == ch.X && target.Y == ch.Y)) return false;
        if (ch.Position.GetDirectionTo(target) == ch.Direction) return false;
        ch.FaceToward(target);
        WindowTurns++;
        return true;
    }

    /// <summary>Whether the bot is standing: a turn then is seen, and is not undone by
    /// its next step.</summary>
    private static bool IsStanding(BotMind mind) =>
        mind.Mode is BotMode.Linger or BotMode.Shop && mind.SettleTo == null;

    /// <summary>A player's line reached this bot: it turns to them at once, before the
    /// reply is typed, and keeps looking at them a while. On the road it stops a moment
    /// for them (not while it follows or leads somebody).</summary>
    private void Attend(BotMind mind, Character speaker, long now)
    {
        var ch = mind.Char;
        if (ch.IsDead || speaker == ch) return;
        var m = mind.Manners;
        if (mind.Mode == BotMode.Travel && mind.Guiding == null && mind.Leader == null && mind.Companion == null &&
            now >= m.NextStopMs && Within(speaker.Position, ch.Position, 8))
        {
            m.NextStopMs = now + 20_000;
            mind.NextActMs = Math.Max(mind.NextActMs, now + _rng.Next(1500, 3000));
            Face(mind, speaker.Position);
        }
        else if (IsStanding(mind))
            Face(mind, speaker.Position);
        m.LookAt = speaker;
        m.LookUntilMs = now + 8000;
    }

    /// <summary>The bot said a line to somebody: it faces them, and a greeting or a
    /// thanks may come with a bow.</summary>
    private void OnSpoke(BotMind mind, string category, Character to, long now)
    {
        var ch = mind.Char;
        if (to == ch || ch.IsDead || to.IsDeleted) return;
        if (IsStanding(mind) && Within(to.Position, ch.Position, HearRange))
            Face(mind, to.Position);
        if (GestureFor(mind, category) is { } gesture && _rng.Next(100) < GesturePercent)
            Gesture(mind, gesture, now);
    }

    /// <summary>The body language that goes with a line: a bow with a greeting or a
    /// thanks, a salute from a warrior.</summary>
    private static string? GestureFor(BotMind mind, string category)
    {
        bool greets = category is "respond_greet" or "friend_greet" or "bond_friend";
        bool thanks = category.StartsWith("thanks_", StringComparison.Ordinal);
        if (!greets && !thanks) return null;
        return mind.IsFighter && !mind.IsCaster && greets ? "salute" : "bow";
    }

    /// <summary>A bow or a salute, asked for the way a client asks: the animate text
    /// command (0x12 type 0xC7, "bow" / "salute").</summary>
    internal bool Gesture(BotMind mind, string action, long now)
    {
        var m = mind.Manners;
        if (mind.Client == null || mind.Char.IsDead || now < m.NextGestureMs ||
            mind.Mode is BotMode.Fight or BotMode.Flee)
            return false;
        m.NextGestureMs = now + 8000;
        mind.Client.HandleTextCommand(0xC7, action);
        WindowGestures++;
        return true;
    }

    /// <summary>A gesture written out (*waves*): said as an emote.</summary>
    private static bool IsEmoteLine(string line) =>
        line.Length > 2 && line[0] == '*' && line[^1] == '*';

    /// <summary>A small human gesture from <paramref name="category"/>, said overhead as
    /// an emote; spaced out per bot.</summary>
    private bool Emote(BotMind mind, string category, long now)
    {
        var m = mind.Manners;
        if (now < m.NextEmoteMs || mind.Char.IsDead) return false;
        if (!Speak(mind, category, fallback: null, mode: EmoteMode)) return false;
        m.NextEmoteMs = now + _rng.Next(45_000, 150_000);
        WindowEmotes++;
        return true;
    }

    // ------------------------------------------------------------------
    //  Eating and drinking
    // ------------------------------------------------------------------

    /// <summary>Eat (or drink) something out of the pack with a double click, as a
    /// player does; never a thing held for a player, and no liquor (a drunk bot staggers
    /// off its road). True when something was used up.</summary>
    private bool TryEat(BotMind mind, long now, bool drink)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        m.NextEatMs = now + _rng.Next(300_000, 720_000);
        if (mind.Client == null || ch.IsDead || ch.Backpack is not { } pack) return false;
        Item? pick = null;
        foreach (var item in _world.GetContainerContents(pack.Uid))
        {
            if (item.IsDeleted || HeldFor(ch, item) != 0) continue;
            bool isDrink = item.ItemType == ItemType.Drink;
            bool isFood = item.ItemType is ItemType.Food or ItemType.Fruit;
            if (!isDrink && !isFood) continue;
            if (pick == null || isDrink == drink) pick = item;
            if (isDrink == drink) break;
        }
        if (pick == null)
        {
            m.WantsFood = true;
            return false;
        }
        m.WantsFood = false;
        bool wasDrink = pick.ItemType == ItemType.Drink;
        ushort amount = pick.Amount, graphic = pick.BaseId;
        mind.Client.HandleDoubleClick(pick.Uid.Value);
        if (!pick.IsDeleted && pick.Amount >= amount && pick.BaseId == graphic) return false; // too full for it
        WindowMeals++;
        mind.Note(now, wasDrink ? "has a drink" : "has a bite");
        if (_rng.Next(100) < 60 && Speak(mind, wasDrink ? "emote_drink" : "emote_eat", fallback: null, mode: EmoteMode))
            WindowEmotes++;
        return true;
    }

    // ------------------------------------------------------------------
    //  What the audience has heard
    // ------------------------------------------------------------------

    private readonly Dictionary<uint, Dictionary<string, long>> _heardLines = [];

    /// <summary>Whether a player in earshot of <paramref name="at"/> heard this very
    /// line from a bot within the last few minutes: two bots by one player do not say
    /// the same thing one after the other.</summary>
    private bool HeardLately(Point3D at, string line, long now)
    {
        if (_heardLines.Count == 0) return false;
        foreach (var w in _watching)
            if (Within(w.Char.Position, at, HearRange) && _heardLines.TryGetValue(w.Char.Uid.Value, out var heard) &&
                heard.TryGetValue(line, out long when) && now - when < SharedLineMs)
                return true;
        return false;
    }

    private void NoteHeard(Point3D at, string line, long now)
    {
        foreach (var w in _watching)
        {
            if (!Within(w.Char.Position, at, HearRange)) continue;
            uint uid = w.Char.Uid.Value;
            if (!_heardLines.TryGetValue(uid, out var heard))
                _heardLines[uid] = heard = new Dictionary<string, long>(StringComparer.Ordinal);
            heard[line] = now;
            if (heard.Count > 96)
                foreach (string old in heard.Where(kv => now - kv.Value >= SharedLineMs).Select(kv => kv.Key).ToList())
                    heard.Remove(old);
        }
        // Players long gone take what they heard with them.
        if (_heardLines.Count > _watching.Count + 32)
            foreach (uint gone in _heardLines.Keys.Where(k => !_watchers.ContainsKey(k)).ToList())
                _heardLines.Remove(gone);
    }

    // ------------------------------------------------------------------
    //  Pace
    // ------------------------------------------------------------------

    /// <summary>Whether the next step of a trip is run. Nobody looking, it is the
    /// trip's own pace, as before. Where a player sees it the pace follows the road: a
    /// walk inside a guarded town, on the last steps into a place and when out of
    /// breath; a run on the open road, on a trip made in a hurry and on a leg that has
    /// taken far too long. Looked at again every two seconds, so a walker does not
    /// flicker between the two.</summary>
    private bool PaceRun(BotMind mind, bool finalLeg, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        // A ghost on its way to a healer hurries; company and a guide keep the pace set.
        if (ch.IsDead) return m.Ghost == BotGhostStage.Walking && ch.Stam > ch.MaxStam / 3;
        // The last open ground stood on: where a death underground belongs to.
        if (!IsUnderground(ch.Position)) m.LastSurface = ch.Position;
        // Back for its body before it rots: at a run, seen or not.
        if (OnCorpseRun(mind)) return ch.Stam > ch.MaxStam / 5;
        if (mind.Leader != null || mind.Guiding != null) return mind.Running && ch.Stam > ch.MaxStam / 3;
        if (now >= m.PaceUntilMs)
        {
            m.PaceUntilMs = now + PaceCheckMs;
            m.PaceSeen = IsWatched(ch.Position);
            if (m.PaceSeen) m.PaceRun = DecidePace(mind, finalLeg, now);
        }
        if (!m.PaceSeen) return mind.Running && ch.Stam > ch.MaxStam / 3;
        return m.PaceRun && !finalLeg && ch.Stam > ch.MaxStam / 5;
    }

    private bool DecidePace(BotMind mind, bool finalLeg, long now)
    {
        var ch = mind.Char;
        // Out of breath: it walks until half its wind is back.
        bool winded = ch.MaxStam > 0 &&
                      (mind.Manners.PaceRun ? ch.Stam * 3 <= ch.MaxStam : ch.Stam * 2 <= ch.MaxStam);
        if (winded || finalLeg) return false;
        bool late = mind.LegStartDist > 0 && now - mind.LegStartedMs > 15_000 + mind.LegStartDist * 600L;
        if (late || mind.Running) return true;
        return !IsGuardedAt(ch.Position);
    }

    /// <summary>After a step of a trip a player can see: a breath at a junction (a new
    /// leg of the road), looking down the way it takes next; else, now and then, a stop
    /// to look about.</summary>
    private void PauseOnTheWay(BotMind mind, bool stepped, bool run, bool finalLeg, long now)
    {
        var m = mind.Manners;
        bool junction = ReferenceEquals(m.PaceRoute, mind.Route) && m.PaceLeg != mind.Leg && mind.Leg > 0;
        m.PaceRoute = mind.Route;
        m.PaceLeg = mind.Leg;
        if (!stepped || mind.Leader != null || mind.Guiding != null || mind.Char.IsDead || OnCorpseRun(mind)) return;
        if (junction && JunctionPausePercent > 0 && IsWatched(mind.Char.Position))
        {
            if (_rng.Next(100) >= JunctionPausePercent) return;
            mind.NextActMs += _rng.Next(600, 1800);
            Face(mind, mind.LegTarget(Nav));
            WindowPauses++;
            return;
        }
        if (!run && !finalLeg && _rng.Next(1000) < PausePerMille && IsWatched(mind.Char.Position))
        {
            mind.NextActMs += _rng.Next(1000, 3000);
            WindowPauses++;
        }
    }

    private readonly Dictionary<BotDestination, long> _departSlots = [];

    /// <summary>How long a bot whose stay has ended waits before its first step: a
    /// moment, and behind whoever else left the same place a moment ago, so a group does
    /// not start walking on the same tick.</summary>
    private long DepartDelay(BotMind mind, long now)
    {
        const long first = 200;
        if (mind.At is not { } at) return first;
        long last = _departSlots.GetValueOrDefault(at);
        long slot = last + 1500 <= now ? now + first : Math.Min(now + 6000, Math.Max(now + first, last + _rng.Next(600, 1500)));
        _departSlots[at] = slot;
        return slot - now;
    }

    // ------------------------------------------------------------------
    //  Nothing out of nothing where a player looks
    // ------------------------------------------------------------------

    private readonly List<Character> _strayMounts = [];
    private long _nextStraySweepMs;

    /// <summary>Whether a mount may be made for the bot here and now: only where no
    /// player sees the spot. Seen, the rider goes on foot and is asked again later.</summary>
    private bool MayConjureMount(BotMind mind)
    {
        if (!IsWatched(mind.Char.Position, UnseenRange)) return true;
        WindowConjureWaits++;
        return false;
    }

    /// <summary>A creature its rider gives up: removed at once where nobody sees it,
    /// else left standing and removed once nobody does.</summary>
    private void LeaveMount(Character horse)
    {
        if (!IsWatched(horse.Position, UnseenRange))
        {
            _world.DeleteObject(horse);
            return;
        }
        _strayMounts.Add(horse);
    }

    private void SweepStrayMounts(long now)
    {
        _nextStraySweepMs = now + 5000;
        for (int i = _strayMounts.Count - 1; i >= 0; i--)
        {
            var horse = _strayMounts[i];
            bool gone = horse.IsDeleted || horse.IsDead || horse.IsStatFlag(StatFlag.Ridden);
            if (!gone && IsWatched(horse.Position, UnseenRange)) continue;
            if (!gone) _world.DeleteObject(horse);
            _strayMounts.RemoveAt(i);
        }
    }

    /// <summary>Creatures left standing until nobody looks.</summary>
    public int StrayMounts => _strayMounts.Count;

    /// <summary>Whether a bare bot may be given its kit here and now. Where no player
    /// sees it, yes. Seen, it does what a player with nothing does: at a bank it opens
    /// its box (says so to the banker) and dresses from it; anywhere else it sets off
    /// for the nearest bank of its road first.</summary>
    private bool MayConjureKit(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (!IsWatched(ch.Position, UnseenRange)) return true;
        var m = mind.Manners;
        if (mind.At is { } at && at.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase))
        {
            // A bank with no banker in reach: it waits here until nobody looks.
            if (FindBanker(ch) is not { } banker)
            {
                WindowConjureWaits++;
                mind.NextGearCheckMs = now + 15_000;
                return false;
            }
            if (m.BankSaidMs == 0 || now - m.BankSaidMs > 120_000)
            {
                Face(mind, banker.Position);
                SayBank(mind, now);
                mind.NextGearCheckMs = now + _rng.Next(2500, 4000);
                return false;
            }
            if (now - m.BankSaidMs >= 2000) return true;
            mind.NextGearCheckMs = now + 1500;
            return false;
        }
        WindowConjureWaits++;
        mind.NextGearCheckMs = now + 15_000;
        if (NearestBank(mind) is { } bank)
        {
            mind.Goal = bank;
            ch.SetTag(GoalTag, bank.Name);
            mind.Mode = BotMode.Travel;
            mind.Route = null;
            mind.NextActMs = now + DepartDelay(mind, now);
            mind.Note(now, $"bare where a player looks; to {bank.Name} to dress");
        }
        return false;
    }

    /// <summary>The nearest bank on the piece of road the bot stands on.</summary>
    private BotDestination? NearestBank(BotMind mind)
    {
        var here = mind.Char.Position;
        int node = Nav.FindNearest(here, 64);
        if (node < 0) return null;
        int component = Nav.Waypoints[node].Component;
        BotDestination? best = null;
        int bestDist = 400;
        foreach (var d in Nav.Destinations)
        {
            if (d == mind.At || d.City.Length == 0 || d.Pos.Map != here.Map ||
                !d.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase) || Nav.ComponentOf(d) != component || !IsUsable(d))
                continue;
            int dist = d.Pos.GetDistanceTo(here);
            if (dist < bestDist) { bestDist = dist; best = d; }
        }
        return best;
    }

    /// <summary>Open the bank box the way players do: by saying so where the banker
    /// hears it (<c>bank_open</c>, the pack's own keyword).</summary>
    private void SayBank(BotMind mind, long now)
    {
        mind.Manners.BankSaidMs = now;
        if (!Speak(mind, "bank_open", fallback: null, force: true))
            Utter(mind, "bank_open", "bank", party: false);
    }
}
