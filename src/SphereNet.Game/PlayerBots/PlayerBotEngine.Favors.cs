using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Favours a player asks of a bot in so many words, followed through over several
/// lines: "open a gate" (where to? a rune of the bot's, or one the player hands over;
/// the reagents' worth asked of someone it hardly knows; mana rested for; the cast;
/// the gate held open and, for a companion, walked through after the player), "mark
/// my rune", "bless me", "take me to the bank", "what is this worth", "hold this",
/// "give me my things", a present. Every spell is the bot's own cast through its client
/// (mana, reagents, skill, fizzles, the region's anti-magic) - the bot checks what it
/// can before it promises, and says why when it cannot. What a player hands over to be
/// held is kept on the bot (<c>PLAYERBOT.HELD</c>, <c>item:player</c> pairs) until it
/// is traded back; it is never sold or used.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string HeldTag = "PLAYERBOT.HELD";

    /// <summary>The spell engine, to read a spell's mana and reagents and to test
    /// whether the bot can cast it now. Null: the bot just tries.</summary>
    public SphereNet.Game.Magic.SpellEngine? Spells { get; set; }

    /// <summary>How long a favour waits for the player's answer, rune or money.</summary>
    private const long FavorWaitMs = 90_000;
    /// <summary>How long a favour may take once under way (mana included).</summary>
    private const long FavorWorkMs = 180_000;
    /// <summary>Magery from which a bot offers to open gates.</summary>
    private const int GateMagery = 600;

    public long WindowFavors { get; private set; }
    public long WindowFavorFails { get; private set; }
    public long WindowGates { get; private set; }
    public long WindowGifts { get; private set; }

    // ------------------------------------------------------------------
    //  Held for players
    // ------------------------------------------------------------------

    private static List<(uint Item, uint Player)> ReadHeld(Character ch)
    {
        var list = new List<(uint, uint)>();
        if (!ch.TryGetTag(HeldTag, out string? tag) || string.IsNullOrEmpty(tag)) return list;
        foreach (string entry in tag.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.IndexOf(':');
            if (colon > 0 && uint.TryParse(entry.AsSpan(0, colon), out uint item) &&
                uint.TryParse(entry.AsSpan(colon + 1), out uint player))
                list.Add((item, player));
        }
        return list;
    }

    private static void WriteHeld(Character ch, List<(uint Item, uint Player)> list)
    {
        if (list.Count == 0) ch.RemoveTag(HeldTag);
        else ch.SetTag(HeldTag, string.Join(' ', list.Select(h => $"{h.Item}:{h.Player}")));
    }

    /// <summary>The player an item in the bot's pack is held for, or 0.</summary>
    private static uint HeldFor(Character ch, Item item)
    {
        foreach (var (i, p) in ReadHeld(ch))
            if (i == item.Uid.Value) return p;
        return 0;
    }

    private static void Hold(Character ch, Item item, Character player)
    {
        var list = ReadHeld(ch);
        list.RemoveAll(h => h.Item == item.Uid.Value);
        list.Add((item.Uid.Value, player.Uid.Value));
        WriteHeld(ch, list);
    }

    /// <summary>What the bot holds for a player and still has (in its pack, at any
    /// depth); entries for things it no longer has are dropped.</summary>
    private List<Item> HeldItems(Character ch, uint player)
    {
        var list = ReadHeld(ch);
        var items = new List<Item>();
        bool changed = false;
        foreach (var (uid, p) in list.ToList())
        {
            var item = _world.FindItem(new Serial(uid));
            if (item == null || item.IsDeleted || item.GetTopLevelObj() != ch)
            {
                list.Remove((uid, p));
                changed = true;
                continue;
            }
            if (p == player) items.Add(item);
        }
        if (changed) WriteHeld(ch, list);
        return items;
    }

    // ------------------------------------------------------------------
    //  Asking
    // ------------------------------------------------------------------

    private BotFavor NewFavor(BotMind m, BotWatcher w, Character player, BotFavorKind kind, string lang, long now)
    {
        // One favour per player: asking another bot drops the one before.
        if (w.Favor is { } old && old.Bot != m) EndFavor(old, now, done: false);
        if (m.Favor is { } mine && mine.Player != player) EndFavor(mine, now, done: false);
        var f = new BotFavor
        {
            Kind = kind, Player = player, Bot = m, Language = lang, StartedMs = now, UntilMs = now + FavorWaitMs,
        };
        w.Favor = f;
        m.Favor = f;
        m.Note(now, $"asked by {player.GetName()}: {kind}");
        // Off on a trip of its own: it stops for the player.
        if (m.Mode == BotMode.Travel && m.Guiding == null)
            m.BeginLinger(NearestPlace(m.Char.Position, 6), now, _rng, short_: true);
        m.LingerUntilMs = Math.Max(m.LingerUntilMs, now + 60_000);
        return f;
    }

    /// <summary>Whether this bot will do anything for the player at all; the line that
    /// says no otherwise. A spell on a criminal or a murderer would make the bot one.</summary>
    private string? Refusal(BotMind m, Character player, bool spell)
    {
        if (IsRogueBot(m.Char)) return "favor_refuse";
        if (Grudges(m, player)) return "favor_refuse_grudge";
        if (spell && (player.IsMurderer || player.IsFlaggedAsCriminal)) return "favor_refuse_red";
        if (m.Favor is { } other && other.Player != player && !IsWaiting(other)) return "favor_busy";
        if (m.Companion != null && m.Companion != player && m.Companion.HasClient &&
            m.Companion.Position.GetDistanceTo(m.Char.Position) <= 20 && !spell)
            return "favor_busy";
        return null;
    }

    private static bool IsWaiting(BotFavor f) =>
        f.Stage is BotFavorStage.Asking or BotFavorStage.AwaitRune or BotFavorStage.AwaitPay or BotFavorStage.AwaitGoods;

    /// <summary>The reagents' worth of a spell, asked of a player the bot hardly knows;
    /// nothing from a friend, a companion, or an acquaintance of a kind bot. A stingy
    /// one asks double.</summary>
    internal long FeeFor(BotMind m, Character player, SpellType spell)
    {
        var level = BondLevel(m, player);
        string trait = TraitOf(m);
        if (m.Companion == player || level == BotBondLevel.Friend ||
            (level == BotBondLevel.Acquaintance && trait == "kind"))
            return 0;
        long worth = ReagentWorth(spell);
        return trait == "stingy" ? worth * 2 : worth;
    }

    /// <summary>What a spell's reagents are worth, when set; else read from the spell's
    /// definition (<see cref="ReagentWorth"/>).</summary>
    public Func<SpellType, long>? ReagentWorthOf { get; set; }

    /// <summary>What a spell's reagents are worth: each one's make value
    /// (VALUE at quality 0) times the amount. 0 when it is not known.</summary>
    private long ReagentWorth(SpellType spell)
    {
        if (ReagentWorthOf != null) return ReagentWorthOf(spell);
        var def = Spells?.GetSpellDef(spell);
        if (def == null) return 0;
        long sum = 0;
        foreach (var (rid, amount) in def.Reagents)
            if (DefinitionLoader.GetItemDef(rid.Index) is { } idef)
                sum += (long)VendorEngine.GetMakeValue(idef, 0, 0) * Math.Max(1, amount);
        return sum;
    }

    /// <summary>A spell's mana: its definition's, else the classic cost of its circle.</summary>
    private int ManaFor(SpellType spell)
    {
        if (Spells?.GetSpellDef(spell) is { ManaCost: > 0 } def) return def.ManaCost;
        int circle = Math.Clamp(((int)spell - 1) / 8, 0, 7);
        return CircleMana[circle];
    }

    private static readonly int[] CircleMana = [4, 6, 9, 11, 14, 20, 40, 50];

    // ------------------------------------------------------------------
    //  Gate
    // ------------------------------------------------------------------

    private bool CanGate(BotMind m) =>
        m.IsCaster && m.Client != null && HasSpell(m.Char, SpellType.GateTravel) &&
        m.Char.GetSkill(SkillType.Magery) >= GateMagery;

    private (string, Dictionary<string, string>?) AskGate(BotMind m, Character player, BotWatcher w, BotIntent intent,
        string lang, long now)
    {
        if (Refusal(m, player, spell: true) is { } no) return (no, null);
        if (!CanGate(m)) return ("gate_cant", null);
        var f = NewFavor(m, w, player, BotFavorKind.Gate, lang, now);
        if (intent.PlaceType != null) return GateTo(m, f, intent.PlaceType, intent.PlaceWord ?? "", now);
        var words = RuneWords(m.Char);
        if (words.Count == 0)
        {
            f.Stage = BotFavorStage.AwaitRune;
            return ("gate_no_runes", null);
        }
        f.Stage = BotFavorStage.Asking;
        return ("gate_where", new Dictionary<string, string> { ["runes"] = string.Join(", ", words) });
    }

    /// <summary>The place asked for: one of the bot's runes, else a rune of the
    /// player's is asked for.</summary>
    private (string, Dictionary<string, string>?) GateTo(BotMind m, BotFavor f, string type, string word, long now)
    {
        f.PlaceType = type;
        f.PlaceWord = word;
        var tokens = new Dictionary<string, string> { ["target"] = word };
        var rune = RuneFor(m.Char, type);
        if (rune == null)
        {
            f.Stage = BotFavorStage.AwaitRune;
            f.UntilMs = now + FavorWaitMs;
            return ("gate_no_rune", tokens);
        }
        f.Rune = rune;
        f.RuneIsPlayers = false;
        return Proceed(m, f, now);
    }

    /// <summary>Everything is known: the money first when some is asked, else the work.</summary>
    private (string, Dictionary<string, string>?) Proceed(BotMind m, BotFavor f, long now)
    {
        var tokens = new Dictionary<string, string> { ["target"] = f.PlaceWord ?? "" };
        if (f.Kind == BotFavorKind.Gate && f.Rune != null && f.Rune.TryGetRuneMark(out var mark) &&
            GateBlocked(m.Char.Position, mark) is { } blocked)
        {
            EndFavor(f, now, done: false);
            return (blocked, tokens);
        }
        if (!f.Paid)
        {
            var spell = f.Kind == BotFavorKind.Gate ? SpellType.GateTravel : SpellType.Mark;
            f.Fee = FeeFor(m, f.Player, spell);
            if (f.Fee > 0)
            {
                f.Stage = BotFavorStage.AwaitPay;
                f.UntilMs = now + FavorWaitMs;
                tokens["price"] = f.Fee.ToString();
                return ("favor_fee", tokens);
            }
        }
        f.Stage = BotFavorStage.Working;
        f.UntilMs = now + FavorWorkMs;
        return (f.Kind == BotFavorKind.Gate ? "gate_ok" : "mark_ok", tokens);
    }

    /// <summary>Why a gate from <paramref name="from"/> to <paramref name="to"/> will not
    /// open, as the spell would refuse it (the regions' anti-magic, a gate-in or gate-out
    /// flag, a ship); null when it may.</summary>
    private string? GateBlocked(Point3D from, Point3D to)
    {
        var src = _world.FindRegion(from);
        var dst = _world.FindRegion(to);
        if (src != null && (src.NoMagic || src.IsFlag(RegionFlag.Gate) ||
                            (src.TryGetProperty("GATEOUT", out string? gOut) && gOut == "0")))
            return "gate_blocked_here";
        if (dst != null && (dst.NoMagic || dst.IsFlag(RegionFlag.Gate) || dst.IsFlag(RegionFlag.Recall) ||
                            dst.IsFlag(RegionFlag.Ship) ||
                            (dst.TryGetProperty("GATEIN", out string? gIn) && gIn == "0")))
            return "gate_blocked_there";
        return null;
    }

    /// <summary>The gate the bot's own cast opened where it stands.</summary>
    private Item? OwnGate(Character ch)
    {
        foreach (var item in _world.GetItemsInRange(ch.Position, 1))
            if (!item.IsDeleted && item.ItemType == ItemType.Telepad && item.More1 == ch.Uid.Value)
                return item;
        return null;
    }

    private void ActGateFavor(BotMind m, BotFavor f, long now)
    {
        var ch = m.Char;
        var player = f.Player;
        switch (f.Stage)
        {
            case BotFavorStage.Working:
            {
                if (!CloseTo(m, f, player.Position, 2, now)) return;
                if (f.Rune is not { } rune || rune.IsDeleted || rune.GetTopLevelObj() != ch)
                {
                    FavorSay(m, f, "favor_failed");
                    EndFavor(f, now, done: false);
                    return;
                }
                if (!ReadyToCast(m, f, SpellType.GateTravel, now)) return;
                m.Client!.HandleCastSpell(SpellType.GateTravel, rune.Uid.Value);
                f.Tries++;
                f.Stage = BotFavorStage.Casting;
                f.CheckMs = now + 1500;
                m.Note(now, $"opens a gate to {f.PlaceWord} for {player.GetName()}");
                m.NextActMs = now + 700;
                return;
            }
            case BotFavorStage.Casting:
            {
                if (OwnGate(ch) is { } gate)
                {
                    f.Gate = gate;
                    f.Stage = BotFavorStage.Open;
                    f.UntilMs = now + 60_000;
                    if (m.Companion == player) f.Along = true;
                    // Its own gate is not walked over by mistake (on the way to hand a rune back).
                    m.RememberPortal(gate.X, gate.Y, gate.MapIndex, now);
                    WindowGates++;
                    FavorSay(m, f, "gate_open");
                    FavorDone(m, f, now, "gate");
                    m.NextActMs = now + 800;
                    return;
                }
                if (ch.TryGetCastingSpell(out _) || now < f.CheckMs + 4000)
                {
                    m.NextActMs = now + 500;
                    return;
                }
                // Fizzled, or refused at the end: once or twice more, then no.
                if (f.Tries < 3)
                {
                    FavorSay(m, f, "favor_retry");
                    f.Stage = BotFavorStage.Working;
                    m.NextActMs = now + 1500;
                    return;
                }
                FavorSay(m, f, "favor_failed");
                EndFavor(f, now, done: false);
                return;
            }
            case BotFavorStage.Open:
            {
                var gate = f.Gate;
                if (gate == null || gate.IsDeleted)
                {
                    EndFavor(f, now, done: true);
                    return;
                }
                // Off the gate, so the player can step on it.
                if (!f.SteppedAside)
                {
                    f.SteppedAside = true;
                    if (ch.X == gate.X && ch.Y == gate.Y) StepAside(m);
                    m.NextActMs = now + 600;
                    return;
                }
                var landing = gate.MoreP;
                bool crossed = player.MapIndex == landing.Map && player.Position.GetDistanceTo(landing) <= 6;
                if (crossed && !f.Along)
                {
                    EndFavor(f, now, done: true);
                    return;
                }
                if (crossed && f.Along)
                {
                    // After the player, through the gate.
                    if (ch.Position.GetDistanceTo(landing) <= 6)
                    {
                        EndFavor(f, now, done: true);
                        return;
                    }
                    m.ForgetPortal(gate.X, gate.Y, gate.MapIndex);
                    bool stepped = StepToward(m, gate.Position, run: false, searchWithin: 8);
                    if (m.Carried || ch.Position.GetDistanceTo(landing) <= 6)
                    {
                        EndFavor(f, now, done: true);
                        return;
                    }
                    m.NextActMs = now + StepDelay(m, stepped, false, now);
                    return;
                }
                m.NextActMs = now + 700;
                return;
            }
        }
        m.NextActMs = now + 1000;
    }

    /// <summary>One step off the tile it stands on, any way that is free.</summary>
    private void StepAside(BotMind m)
    {
        int first = _rng.Next(8);
        for (int i = 0; i < 8; i++)
            if (Move(m, (Direction)((first + i) % 8), run: false))
                return;
    }

    // ------------------------------------------------------------------
    //  Mark a player's rune
    // ------------------------------------------------------------------

    private (string, Dictionary<string, string>?) AskMark(BotMind m, Character player, BotWatcher w, string lang, long now)
    {
        if (Refusal(m, player, spell: true) is { } no) return (no, null);
        if (!m.IsCaster || m.Client == null || !HasSpell(m.Char, SpellType.Mark) ||
            m.Char.GetSkill(SkillType.Magery) < 500)
            return ("mark_cant", null);
        var f = NewFavor(m, w, player, BotFavorKind.Mark, lang, now);
        f.PlaceWord = PlaceName(player.Position).ToLowerInvariant();
        f.Stage = BotFavorStage.AwaitRune;
        return ("mark_give", null);
    }

    private void ActMarkFavor(BotMind m, BotFavor f, long now)
    {
        var ch = m.Char;
        switch (f.Stage)
        {
            case BotFavorStage.Working:
                // The mark is where the bot stands: right by the player.
                if (!CloseTo(m, f, f.Player.Position, 1, now)) return;
                if (f.Rune is not { } rune || rune.IsDeleted || rune.GetTopLevelObj() != ch)
                {
                    EndFavor(f, now, done: false);
                    return;
                }
                if (!ReadyToCast(m, f, SpellType.Mark, now)) return;
                m.Client!.HandleCastSpell(SpellType.Mark, rune.Uid.Value);
                f.Tries++;
                f.Stage = BotFavorStage.Casting;
                f.CheckMs = now + 1500;
                m.NextActMs = now + 700;
                return;
            case BotFavorStage.Casting:
                if (f.Rune is { } marked && !marked.IsDeleted && marked.TryGetRuneMark(out var at) &&
                    at.GetDistanceTo(ch.Position) <= 1 && (int)marked.More1 > 0)
                {
                    FavorSay(m, f, "mark_done");
                    FavorDone(m, f, now, null);
                    return;
                }
                if (ch.TryGetCastingSpell(out _) || now < f.CheckMs + 4000)
                {
                    m.NextActMs = now + 500;
                    return;
                }
                if (f.Tries < 3)
                {
                    FavorSay(m, f, "favor_retry");
                    f.Stage = BotFavorStage.Working;
                    m.NextActMs = now + 1500;
                    return;
                }
                FavorSay(m, f, "favor_failed");
                EndFavor(f, now, done: false);
                return;
        }
        m.NextActMs = now + 1000;
    }

    // ------------------------------------------------------------------
    //  Beneficial spells
    // ------------------------------------------------------------------

    /// <summary>The spells a bot casts on a player who asks, in the order one is picked
    /// when none is named.</summary>
    private static readonly SpellType[] Buffs =
    [
        SpellType.Bless, SpellType.Protection, SpellType.MagicReflect, SpellType.NightSight,
        SpellType.Strength, SpellType.Agility, SpellType.Cunning,
    ];

    private (string, Dictionary<string, string>?) AskBuff(BotMind m, Character player, BotWatcher w, BotIntent intent,
        string lang, long now)
    {
        if (Refusal(m, player, spell: true) is { } no) return (no, null);
        var spell = intent.Spell ?? Buffs.FirstOrDefault(s => HasSpell(m.Char, s));
        var tokens = new Dictionary<string, string> { ["spell"] = SpellWords(intent.Spell ?? SpellType.Bless) };
        if (!m.IsCaster || m.Client == null || spell == 0 || !HasSpell(m.Char, spell))
            return ("buff_cant", tokens);
        if (now < m.NextFavorMs) return ("buff_wait", tokens);
        var f = NewFavor(m, w, player, BotFavorKind.Buff, lang, now);
        f.Spell = spell;
        f.Stage = BotFavorStage.Working;
        f.UntilMs = now + 60_000;
        tokens["spell"] = SpellWords(spell);
        return ("buff_ok", tokens);
    }

    /// <summary>"MagicReflect" as "magic reflect".</summary>
    private static string SpellWords(SpellType spell) => CamelWords(spell.ToString());

    private void ActBuffFavor(BotMind m, BotFavor f, long now)
    {
        var ch = m.Char;
        if (f.Stage == BotFavorStage.Working)
        {
            if (!CloseTo(m, f, f.Player.Position, 6, now)) return;
            if (!_world.CanSeeLOS(ch.Position, f.Player.Position))
            {
                bool stepped = StepToward(m, f.Player.Position, run: false, searchWithin: 12);
                m.NextActMs = now + StepDelay(m, stepped, false, now);
                return;
            }
            if (!ReadyToCast(m, f, f.Spell, now)) return;
            m.Client!.HandleCastSpell(f.Spell, f.Player.Uid.Value);
            m.NextFavorMs = now + 10_000;
            f.Stage = BotFavorStage.Casting;
            f.CheckMs = now + 1500;
            m.NextActMs = now + 700;
            return;
        }
        if (f.Stage == BotFavorStage.Casting)
        {
            if (ch.TryGetCastingSpell(out _) || now < f.CheckMs)
            {
                m.NextActMs = now + 500;
                return;
            }
            FavorDone(m, f, now, null);
            return;
        }
        m.NextActMs = now + 1000;
    }

    // ------------------------------------------------------------------
    //  Showing the way
    // ------------------------------------------------------------------

    private (string, Dictionary<string, string>?) AskGuide(BotMind m, Character player, BotWatcher w, BotIntent intent,
        string lang, long now)
    {
        if (Refusal(m, player, spell: false) is { } no) return (no, null);
        if (m.Client == null || IsPk(m)) return ("favor_refuse", null);
        var f = NewFavor(m, w, player, BotFavorKind.Guide, lang, now);
        if (intent.PlaceType == null)
        {
            f.Stage = BotFavorStage.Asking;
            return ("guide_where", null);
        }
        return GuideTo(m, f, intent.PlaceType, intent.PlaceWord ?? "", now);
    }

    /// <summary>The place to lead the player to: the nearest of its kind to them, a
    /// town's bank (or any place of the town), or the place named - on the bot's road.
    /// Too far to walk, a caster offers a gate instead.</summary>
    private (string, Dictionary<string, string>?) GuideTo(BotMind m, BotFavor f, string type, string word, long now)
    {
        var ch = m.Char;
        var tokens = new Dictionary<string, string> { ["target"] = word };
        int here = Nav.FindNearest(ch.Position, 64);
        int component = here >= 0 ? Nav.Waypoints[here].Component : -1;
        var from = f.Player.Position;
        IEnumerable<BotDestination> pool = Nav.Destinations.Where(d => d.Pos.Map == from.Map && Nav.ComponentOf(d) == component);
        BotDestination? to;
        if (type.StartsWith("town:", StringComparison.OrdinalIgnoreCase))
        {
            string town = type[5..];
            var inTown = pool.Where(d => d.City.Equals(town, StringComparison.OrdinalIgnoreCase)).ToList();
            to = inTown.FirstOrDefault(d => d.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase)) ?? inTown.FirstOrDefault();
        }
        else if (type.StartsWith("dest:", StringComparison.OrdinalIgnoreCase))
            to = Nav.FindDestination(type[5..]) is { } d && pool.Contains(d) ? d : null;
        else
            to = pool.Where(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Pos.GetDistanceTo(from)).FirstOrDefault();
        if (to == null)
        {
            EndFavor(f, now, done: false);
            return ("guide_unknown", tokens);
        }
        if (to.Pos.GetDistanceTo(from) <= 6)
        {
            EndFavor(f, now, done: false);
            return ("respond_where_here", tokens);
        }
        if (to.Pos.GetDistanceTo(ch.Position) > 400)
        {
            EndFavor(f, now, done: false);
            if (CanGate(m) && RuneFor(ch, type) != null && _watchers.TryGetValue(f.Player.Uid.Value, out var w))
            {
                // Offered: a yes starts the gate.
                var player = f.Player;
                string lang = f.Language ?? Chat.Language;
                m.PendingOnYes = () =>
                {
                    var (category, t) = AskGate(m, player, w,
                        new BotIntent(BotIntentKind.Gate, type, word), lang, NowMs());
                    QueueReply(m, category, NowMs(), to: player, lang: lang, fallback: null, tokens: t);
                };
                return ("guide_far_gate", tokens);
            }
            return ("guide_far", tokens);
        }
        f.To = to;
        f.PlaceType = type;
        f.PlaceWord = word;
        f.Stage = BotFavorStage.Working;
        f.UntilMs = now + 600_000;
        m.Guiding = f.Player;
        m.GuideFarSinceMs = 0;
        m.Goal = to;
        ch.SetTag(GoalTag, to.Name);
        m.Mode = BotMode.Travel;
        m.Route = null;
        m.Leader = null;
        m.NextActMs = now + 2500;
        m.Note(now, $"leads {f.Player.GetName()} to {to.Name}");
        return ("guide_ok", tokens);
    }

    /// <summary>Leading a player: wait while they lag behind, give up when they are
    /// gone. True when the bot waits this act.</summary>
    private bool PaceGuided(BotMind m, long now)
    {
        var p = m.Guiding;
        if (p == null) return false;
        var f = m.Favor;
        if (f == null || f.Kind != BotFavorKind.Guide || p.IsDeleted || !p.IsOnline || p.IsDead)
        {
            m.Guiding = null;
            return false;
        }
        int apart = p.MapIndex == m.Char.MapIndex ? p.Position.GetDistanceTo(m.Char.Position) : int.MaxValue;
        if (apart <= 5)
        {
            m.GuideFarSinceMs = 0;
            m.Running = p.IsMounted || m.Running && apart <= 3;
            return false;
        }
        if (m.GuideFarSinceMs == 0) m.GuideFarSinceMs = now;
        if (apart > 30 && now - m.GuideFarSinceMs > 45_000)
        {
            // Gone another way: the walk is off.
            m.Note(now, $"lost {p.GetName()} on the way");
            EndFavor(f, now, done: false);
            m.Guiding = null;
            m.BeginLinger(null, now, _rng, short_: true);
            return true;
        }
        if (apart > 10 && !f.SaidWait)
        {
            f.SaidWait = true;
            FavorSay(m, f, "guide_wait");
        }
        m.NextActMs = now + 700;
        return true;
    }

    /// <summary>Arrived with a player in tow.</summary>
    private void GuideArrived(BotMind m, long now)
    {
        var p = m.Guiding;
        m.Guiding = null;
        if (p == null || m.Favor is not { Kind: BotFavorKind.Guide } f) return;
        FavorSay(m, f, "guide_here");
        FavorDone(m, f, now, "guide");
    }

    // ------------------------------------------------------------------
    //  Things handed over: carry, a present, a price
    // ------------------------------------------------------------------

    private (string, Dictionary<string, string>?) AskCarry(BotMind m, Character player, BotWatcher w, string lang, long now)
    {
        if (Refusal(m, player, spell: false) is { } no) return (no, null);
        if (m.Companion != player || Trades == null || m.Client == null) return ("carry_no", null);
        var f = NewFavor(m, w, player, BotFavorKind.Carry, lang, now);
        f.Stage = BotFavorStage.AwaitGoods;
        return ("carry_give", null);
    }

    private (string, Dictionary<string, string>?) AskGift(BotMind m, Character player, BotWatcher w, string lang, long now)
    {
        if (IsRogueBot(m.Char) || Trades == null || m.Client == null) return ("respond_chat", null);
        var f = NewFavor(m, w, player, BotFavorKind.Gift, lang, now);
        f.Stage = BotFavorStage.AwaitGoods;
        return ("gift_ok", null);
    }

    private (string, Dictionary<string, string>?) AskAppraise(BotMind m, Character player, BotWatcher w, string lang,
        long now)
    {
        if (Refusal(m, player, spell: false) is { } no) return (no, null);
        if (Trades == null || m.Client == null) return ("respond_question", null);
        var f = NewFavor(m, w, player, BotFavorKind.Appraise, lang, now);
        f.Stage = BotFavorStage.AwaitGoods;
        return ("appraise_show", null);
    }

    private (string, Dictionary<string, string>?) AskGiveBack(BotMind m, Character player, long now)
    {
        if (HeldItems(m.Char, player.Uid.Value).Count == 0) return ("giveback_none", null);
        m.GiveBackTo = player;
        m.GiveBackUntilMs = now + 90_000;
        // Asked for: now, whatever was turned down before.
        m.NextGiveBackMs = now + 2000;
        m.NextActMs = Math.Min(m.NextActMs, now + 3000);
        return ("giveback_yes", null);
    }

    /// <summary>Take what it holds for a player over and trade it back. False when
    /// there is nothing to hand back now.</summary>
    private bool ActGiveBack(BotMind m, long now)
    {
        var p = m.GiveBackTo;
        if (p == null || Trades == null || m.Client is not { } client) return false;
        var ch = m.Char;
        var items = HeldItems(ch, p.Uid.Value);
        int dist = p.MapIndex == ch.MapIndex ? p.Position.GetDistanceTo(ch.Position) : int.MaxValue;
        if (items.Count == 0 || now > m.GiveBackUntilMs || p.IsDeleted || !p.IsOnline || p.IsDead || dist > 24 ||
            m.Mode is BotMode.Fight or BotMode.Flee)
        {
            m.GiveBackTo = null;
            return false;
        }
        if (Trades.FindTradeFor(ch) != null || Trades.FindTradeFor(p) != null) return false;
        if (dist > 2)
        {
            bool stepped = StepToward(m, p.Position, run: dist > 6, searchWithin: 24);
            if (m.NoGainMs("giveback", p.Position, dist, now) > 20_000)
            {
                m.GiveBackTo = null;
                return false;
            }
            m.NextActMs = now + StepDelay(m, stepped, dist > 6, now);
            return true;
        }
        if (now < m.NextGiveBackMs) return false;
        m.NextGiveBackMs = now + 15_000;
        if (!client.InitiateTrade(p) || Trades.FindTradeFor(ch) is not { } trade) return false;
        var mine = trade.GetOwnContainer(ch);
        foreach (var item in items)
        {
            client.HandleItemPickup(item.Uid.Value, item.Amount);
            client.HandleItemDrop(item.Uid.Value, 30, 30, 0, mine.Uid.Value);
            if (item.ContainedIn != mine.Uid && ch.Backpack != null && item.ContainedIn != ch.Backpack.Uid)
                client.ReleaseDraggedItem(toGround: false);
        }
        m.Note(now, $"hands {items.Count} things back to {p.GetName()}");
        m.NextActMs = now + _rng.Next(600, 1200);
        return true;
    }

    // ------------------------------------------------------------------
    //  Following a favour through
    // ------------------------------------------------------------------

    /// <summary>A line read against a favour in hand: the answer to the bot's
    /// "where to?", a no or a "never mind", "you too" while its gate stands. True when
    /// the line was about the favour.</summary>
    private bool TryFavorFollowUp(Character speaker, BotWatcher w, BotIntent intent, string text, long now, string lang)
    {
        var f = w.Favor;
        if (f == null) return false;
        var m = f.Bot;
        if (m.Favor != f || m.Char.IsDead || m.Char.IsDeleted || !Within(m.Char.Position, speaker.Position, HearRange))
        {
            if (m.Favor != f) w.Favor = null;
            return false;
        }
        // Another bot named: the line is for it.
        var words = BotIntents.Words(text);
        if (!words.Contains(FirstName(m.Char).ToLowerInvariant()))
            foreach (var c in _world.GetCharsInRange(speaker.Position, 10))
                if (c != m.Char && c.IsPlayerBot && words.Contains(FirstName(c).ToLowerInvariant()))
                    return false;

        string? category = null;
        Dictionary<string, string>? tokens = null;
        // A bare "no" counts while the bot waits on the player, question or not.
        bool no = intent.Kind == BotIntentKind.No || Intents.Parse(text).Kind == BotIntentKind.No;
        if (intent.Kind == BotIntentKind.Cancel || (no && IsWaiting(f)))
        {
            category = "favor_cancel";
            EndFavor(f, now, done: false);
        }
        else if (f.Stage == BotFavorStage.Asking && f.Kind == BotFavorKind.Gate &&
                 Intents.FindPlace(text, t => t.Equals("Moongate", StringComparison.OrdinalIgnoreCase)) is { } to)
            (category, tokens) = GateTo(m, f, to.Type, to.Word, now);
        else if (f.Stage == BotFavorStage.Asking && f.Kind == BotFavorKind.Guide && Intents.FindPlace(text) is { } place)
            (category, tokens) = GuideTo(m, f, place.Type, place.Word, now);
        else if (f.Kind == BotFavorKind.Gate && f.Stage is BotFavorStage.Open or BotFavorStage.Casting or BotFavorStage.Working &&
                 intent.Kind is BotIntentKind.Follow or BotIntentKind.Companion or BotIntentKind.Yes)
        {
            f.Along = true;
            category = "gate_along";
        }
        if (category == null) return false;
        w.Focus = m;
        w.FocusUntilMs = now + FocusMs;
        WindowReplies++;
        QueueReply(m, category, now, to: speaker, lang: lang, fallback: null, tokens: tokens);
        return true;
    }

    /// <summary>One act of a favour in hand. False when there is none, so the bot's
    /// own plans go on.</summary>
    private bool ActFavor(BotMind m, long now)
    {
        var f = m.Favor;
        if (f == null) return false;
        var p = f.Player;
        if (p.IsDeleted || !p.IsOnline || (p.IsDead && f.Kind != BotFavorKind.Gate) ||
            p.MapIndex != m.Char.MapIndex && f.Stage != BotFavorStage.Open)
        {
            EndFavor(f, now, done: false);
            return false;
        }
        if (now > f.UntilMs)
        {
            if (!IsWaiting(f) && f.Stage != BotFavorStage.Open) FavorSay(m, f, "favor_failed");
            EndFavor(f, now, done: f.Stage == BotFavorStage.Open);
            return false;
        }
        // A guided walk is the trip itself; a trip that ended short of the place ends it.
        if (f.Kind == BotFavorKind.Guide && f.Stage == BotFavorStage.Working)
        {
            if (m.Mode != BotMode.Travel || m.Guiding == null) EndFavor(f, now, done: false);
            return false;
        }
        if (IsWaiting(f))
        {
            // Waiting for an answer or a trade: it stays by the player.
            int d = p.Position.GetDistanceTo(m.Char.Position);
            if (d > 3 && d <= 20)
            {
                bool stepped = StepToward(m, p.Position, run: d > 8, searchWithin: 20);
                m.NextActMs = now + StepDelay(m, stepped, d > 8, now);
            }
            else
                m.NextActMs = now + 1000;
            return true;
        }
        switch (f.Kind)
        {
            case BotFavorKind.Gate: ActGateFavor(m, f, now); break;
            case BotFavorKind.Mark: ActMarkFavor(m, f, now); break;
            case BotFavorKind.Buff: ActBuffFavor(m, f, now); break;
            default: EndFavor(f, now, done: false); return false;
        }
        return true;
    }

    /// <summary>Walk to within <paramref name="range"/> of a point for a favour. True when there.</summary>
    private bool CloseTo(BotMind m, BotFavor f, Point3D at, int range, long now)
    {
        int d = m.Char.Position.GetDistanceTo(at);
        if (d <= range) return true;
        bool stepped = StepToward(m, at, run: d > 6, searchWithin: 24);
        if (m.NoGainMs("favor", at, d, now) > 30_000)
        {
            FavorSay(m, f, "favor_cant_reach");
            EndFavor(f, now, done: false);
            return false;
        }
        m.NextActMs = now + StepDelay(m, stepped, d > 6, now);
        return false;
    }

    /// <summary>Ready for the spell: casting nothing else, the mana (it rests for it,
    /// saying so once), and the spell castable as the engine would judge it (reagents,
    /// the region). False when it waits or has given up.</summary>
    private bool ReadyToCast(BotMind m, BotFavor f, SpellType spell, long now)
    {
        var ch = m.Char;
        if (ch.TryGetCastingSpell(out _))
        {
            m.NextActMs = now + 500;
            return false;
        }
        int mana = ManaFor(spell);
        if (ch.Mana < mana)
        {
            if (ch.MaxMana < mana)
            {
                FavorSay(m, f, "favor_failed");
                EndFavor(f, now, done: false);
                return false;
            }
            if (!f.SaidMana)
            {
                f.SaidMana = true;
                FavorSay(m, f, "favor_mana");
            }
            if (now >= m.NextMeditateMs && m.Client != null)
            {
                m.NextMeditateMs = now + 12_000;
                m.Client.HandleUseSkill((int)SkillType.Meditation);
                WindowMeditations++;
            }
            f.UntilMs = Math.Max(f.UntilMs, now + 30_000);
            m.NextActMs = now + 2000;
            return false;
        }
        if (Spells != null && !Spells.CanCastTest(ch, spell, checkAntiMagic: true))
        {
            FavorSay(m, f, "favor_no_regs");
            EndFavor(f, now, done: false);
            return false;
        }
        return true;
    }

    /// <summary>A line about the favour, said now to the player it is for.</summary>
    private void FavorSay(BotMind m, BotFavor f, string category)
    {
        var tokens = new Dictionary<string, string> { ["target"] = f.PlaceWord ?? "", ["spell"] = SpellWords(f.Spell) };
        Speak(m, category, fallback: null, to: f.Player, lang: f.Language, extra: tokens);
    }

    /// <summary>A favour done: a point with the player and, for a gate or a walk, a
    /// memory of it; a rune of the player's goes back to them.</summary>
    private void FavorDone(BotMind m, BotFavor f, long now, string? memory)
    {
        WindowFavors++;
        AddBond(m, f.Player, Rule("favor"), now, $"did a {f.Kind} favour");
        if (memory != null && (f.PlaceWord ?? "").Length > 0) Memorize(m, f.Player, memory, f.PlaceWord!);
        if (f.Stage != BotFavorStage.Open) EndFavor(f, now, done: true);
        else if (f.RuneIsPlayers) StartGiveBack(m, f.Player, now);
    }

    private void StartGiveBack(BotMind m, Character player, long now)
    {
        m.GiveBackTo = player;
        m.GiveBackUntilMs = now + 90_000;
        m.NextGiveBackMs = now + 2000;
    }

    private void EndFavor(BotFavor f, long now, bool done)
    {
        var m = f.Bot;
        if (m.Favor == f) m.Favor = null;
        if (_watchers.TryGetValue(f.Player.Uid.Value, out var w) && w.Favor == f) w.Favor = null;
        if (m.Guiding == f.Player && f.Kind == BotFavorKind.Guide) m.Guiding = null;
        if (!done) WindowFavorFails++;
        m.Note(now, $"{f.Kind} favour for {f.Player.GetName()} {(done ? "done" : "dropped")}");
        // A rune, a blank rune or goods of the player's it still holds go back.
        if (HeldItems(m.Char, f.Player.Uid.Value).Count > 0 && f.Kind != BotFavorKind.Carry)
            StartGiveBack(m, f.Player, now);
    }

    // ------------------------------------------------------------------
    //  What it can do
    // ------------------------------------------------------------------

    /// <summary>"What can you do": the favours this bot can do now, from its skills,
    /// book, runes and how well it knows the player (<c>token_ability</c> words).</summary>
    private (string, Dictionary<string, string>?) AnswerCan(BotMind m, Character player, string lang)
    {
        if (IsRogueBot(m.Char)) return ("favor_refuse", null);
        var ch = m.Char;
        var keys = new List<string>();
        if (CanGate(m))
        {
            var runes = RuneWords(ch);
            keys.Add(runes.Count > 0 ? "gate:" + string.Join(", ", runes) : "gate");
        }
        if (m.IsCaster && (HasSpell(ch, SpellType.GreaterHeal) || HasSpell(ch, SpellType.Heal)) ||
            ch.GetSkill(SkillType.Healing) >= 500)
            keys.Add("heal");
        if (m.IsCaster && HasSpell(ch, SpellType.Cure)) keys.Add("cure");
        if (m.IsCaster && HasSpell(ch, SpellType.Resurrection) && ch.GetSkill(SkillType.Magery) >= 700) keys.Add("res");
        if (m.IsCaster && Buffs.Any(s => HasSpell(ch, s))) keys.Add("buff");
        if (m.IsCaster && HasSpell(ch, SpellType.Mark) && ch.GetSkill(SkillType.Magery) >= 500) keys.Add("mark");
        keys.Add("guide");
        if (Trades != null && m.Client != null) keys.Add("appraise");
        if (m.Companion == player) { keys.Add("carry"); keys.Add("protect"); }
        else if (BondLevel(m, player) == BotBondLevel.Friend) keys.Add("companion");
        var phrases = new List<string>();
        foreach (string key in keys)
        {
            int colon = key.IndexOf(':');
            string id = colon > 0 ? key[..colon] : key;
            string? phrase = KeyedWord("token_ability", id, lang);
            if (phrase == null) continue;
            phrases.Add(colon > 0 ? $"{phrase} ({key[(colon + 1)..]})" : phrase);
        }
        if (phrases.Count == 0) return ("respond_can_none", null);
        return ("respond_can", new Dictionary<string, string> { ["abilities"] = string.Join(", ", phrases) });
    }

    /// <summary>The word for <paramref name="key"/> in a <c>key=word</c> list.</summary>
    private string? KeyedWord(string category, string key, string lang)
    {
        for (int i = 0; Chat.Line(category, i, lang) is { } line; i++)
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }
}
