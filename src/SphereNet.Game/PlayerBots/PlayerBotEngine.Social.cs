using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>Being talked to. A bot whose name a player says turns to answer, and keeps
/// answering that player for a minute without being named again (the player's focus).
/// A line nobody is named in can still be taken up by someone in the crowd: a hello,
/// "where is the bank", "lfg", a trade shout, a ghost's plea. Answers are in the
/// language the player wrote in and come a moment later, the way people type.</summary>
public sealed partial class PlayerBotEngine
{
    private const long FocusMs = 60_000;

    /// <summary>A player said <paramref name="text"/> out loud. Only real players are
    /// answered, so two bots never talk themselves into a loop.</summary>
    public void OnHear(Character speaker, string text)
    {
        if (speaker.IsPlayerBot || string.IsNullOrWhiteSpace(text) || _minds.Count == 0) return;
        long now = NowMs();
        var w = WatcherOf(speaker, now);
        w.Language = Intents.DetectLanguage(text) ?? w.Language;
        string lang = w.Language ?? Chat.Language;
        var intent = Intents.Parse(text);

        // A ghost is only understood as a plea to be raised.
        if (speaker.IsDead)
        {
            foreach (var m in FreeBotsNear(speaker.Position, 10))
                if (CanRaise(m, now))
                {
                    Raise(m, speaker, lang, now);
                    return;
                }
            return;
        }

        var words = BotIntents.Words(text);
        BotMind? named = null;
        var near = new List<BotMind>();
        foreach (var ch in _world.GetCharsInRange(speaker.Position, 10))
        {
            if (!ch.IsPlayerBot || ch.IsDead || !_minds.TryGetValue(ch.Uid.Value, out var mind)) continue;
            if (mind.Mode == BotMode.Fight || mind.Mode == BotMode.Flee) continue;
            near.Add(mind);
            if (named == null && words.Contains(FirstName(ch).ToLowerInvariant()))
                named = mind;
        }
        var target = named;
        if (target == null && w.Focus is { } focus && now < w.FocusUntilMs && near.Contains(focus))
            target = focus;
        bool addressed = target != null;
        if (target == null)
        {
            int chance = intent.Kind switch
            {
                BotIntentKind.Greet => 60,
                BotIntentKind.Where => 75,
                BotIntentKind.Party => 55,
                BotIntentKind.HowAreYou => 35,
                BotIntentKind.Question => 25,
                BotIntentKind.Bye => 25,
                BotIntentKind.Wts or BotIntentKind.Wtb => intent.At == 0 && intent.Rest != null ? 30 : 0,
                _ => 0,
            };
            if (near.Count == 0 || chance == 0 || _rng.Next(100) >= chance) return;
            var pool = intent.Kind == BotIntentKind.Party ? near.Where(m => m.IsFighter).ToList() : near;
            if (pool.Count == 0) pool = near;
            target = pool[_rng.Next(pool.Count)];
        }

        if (target.Scene != null) EndScene(target.Scene);
        w.Focus = target;
        w.FocusUntilMs = now + FocusMs;
        if (addressed) Remember(target, speaker);
        Answer(target, speaker, w, intent, text, named != null, now, lang);
    }

    private void Answer(BotMind m, Character player, BotWatcher w, BotIntent intent, string text, bool named,
        long now, string lang)
    {
        Dictionary<string, string>? tokens = null;
        string category;
        string? fallback = "respond_what";
        switch (intent.Kind)
        {
            case BotIntentKind.Greet:
                category = Knows(m, player) ? "friend_greet" : "respond_greet";
                fallback = "respond_greet";
                break;
            case BotIntentKind.Bye:
                category = "respond_bye";
                w.FocusUntilMs = now;
                break;
            case BotIntentKind.Thanks:
                category = "respond_thanks";
                break;
            case BotIntentKind.HowAreYou:
                category = "respond_howru";
                break;
            case BotIntentKind.Where:
                (category, tokens) = AnswerWhere(m, intent, text, lang);
                fallback = "respond_question";
                break;
            case BotIntentKind.Party:
                category = m.IsFighter && Parties?.FindParty(m.Char.Uid) == null ? "respond_party_yes" : "respond_party_no";
                break;
            case BotIntentKind.Res:
                category = "respond_res_no";
                fallback = "respond_question";
                break;
            case BotIntentKind.Wts when intent.Rest != null:
                tokens = new Dictionary<string, string> { ["item"] = intent.Rest };
                m.TalkItem = intent.Rest;
                if (_rng.Next(100) < 50)
                {
                    category = "buy_notice";
                    m.TalkTopic = "wts";
                }
                else
                {
                    category = "trade_not_buying";
                    m.TalkTopic = null;
                }
                break;
            case BotIntentKind.Wtb:
                category = "respond_wtb_none";
                if (intent.Rest != null) tokens = new Dictionary<string, string> { ["item"] = intent.Rest };
                break;
            case BotIntentKind.Question:
                category = "respond_question";
                break;
            default:
                category = Haggle(m, text, ref tokens) ??
                           (named ? "respond_name" : w.Focus == m ? "respond_chat" : "respond_what");
                break;
        }
        WindowReplies++;
        QueueReply(m, category, now, to: player, lang: lang, fallback: fallback, tokens: tokens);
    }

    /// <summary>A player who offered goods to this bot names a price: the bot comes
    /// back lower once, then walks away. A bot carries no gold for a deal it cannot
    /// close, so it never says yes.</summary>
    private string? Haggle(BotMind m, string text, ref Dictionary<string, string>? tokens)
    {
        if (m.TalkTopic == null) return null;
        int price = ParsePrice(text);
        if (price <= 0) return null;
        if (m.TalkTopic == "wts")
        {
            m.TalkTopic = "haggled";
            tokens = new Dictionary<string, string>
            {
                ["price"] = RoundPrice(price * _rng.Next(55, 75) / 100).ToString(),
                ["item"] = m.TalkItem ?? "",
            };
            return "haggle_offer";
        }
        m.TalkTopic = null;
        return "haggle_walkaway";
    }

    /// <summary>A price in a line: "500", "1.5k", "2k".</summary>
    internal static int ParsePrice(string text)
    {
        var m = PriceRegex().Match(text);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) || v <= 0)
            return 0;
        if (m.Groups[2].Success) v *= 1000;
        return (int)Math.Min(int.MaxValue / 2, v);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(k\b)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PriceRegex();

    /// <summary>Directions to what a player asked for, from where the bot stands:
    /// the nearest place of the type named, or the town or dungeon named; the compass
    /// point (<c>token_dir</c>, north first, clockwise) and the distance in steps.</summary>
    private (string Category, Dictionary<string, string>? Tokens) AnswerWhere(BotMind m, BotIntent intent,
        string text, string lang)
    {
        var from = m.Char.Position;
        Point3D? to = null;
        string? word = intent.PlaceWord;
        string? type = intent.PlaceType;
        if (type != null && type.StartsWith("town:", StringComparison.OrdinalIgnoreCase))
        {
            string town = type[5..];
            var inTown = Nav.Destinations.Where(d => d.City.Equals(town, StringComparison.OrdinalIgnoreCase) && d.Pos.Map == from.Map).ToList();
            if (inTown.Count > 0)
                to = new Point3D((short)inTown.Average(d => d.Pos.X), (short)inTown.Average(d => d.Pos.Y), 0, from.Map);
        }
        else if (type != null && type.StartsWith("dest:", StringComparison.OrdinalIgnoreCase))
            to = Nav.FindDestination(type[5..])?.Pos;
        else if (type != null)
            to = Nav.Destinations
                .Where(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && d.Pos.Map == from.Map)
                .OrderBy(d => d.Pos.GetDistanceTo(from))
                .FirstOrDefault()?.Pos;
        if (to is not { } target || word == null)
            return ("respond_where_unknown", null);

        int dx = target.X - from.X, dy = target.Y - from.Y;
        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (dist <= 4) return ("respond_where_here", new Dictionary<string, string> { ["target"] = word });
        // North is up the map (-Y); compass points clockwise from it.
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
        if (angle < 0) angle += 360;
        int index = (int)Math.Round(angle / 45) % 8;
        string? dir = Chat.Line("token_dir", index, lang);
        if (dir == null) return ("respond_where_unknown", null);
        int rounded = dist < 100 ? (dist + 2) / 5 * 5 : (dist + 5) / 10 * 10;
        return (dist > 400 ? "respond_where_far" : "respond_where", new Dictionary<string, string>
        {
            ["target"] = word,
            ["dir"] = dir,
            ["dist"] = rounded.ToString(),
        });
    }

    private void QueueReply(BotMind mind, string category, long now, Character? to = null, string? lang = null,
        string? fallback = "respond_what", Dictionary<string, string>? tokens = null, Action? action = null)
    {
        mind.PendingCategory = category;
        mind.PendingFallback = fallback;
        mind.PendingTo = to;
        mind.PendingLanguage = lang;
        mind.PendingTokens = tokens;
        mind.PendingAction = action;
        mind.PendingSayMs = now + _rng.Next(900, 3200);
    }

    /// <summary>Called each tick: say a queued reply once its typing delay is up, then
    /// do what goes with it.</summary>
    private void SayPending(BotMind mind, long now)
    {
        if (mind.PendingCategory == null || now < mind.PendingSayMs) return;
        string category = mind.PendingCategory;
        var action = mind.PendingAction;
        mind.PendingCategory = null;
        mind.PendingAction = null;
        if (!mind.Char.IsDead)
        {
            Speak(mind, category, fallback: mind.PendingFallback, to: mind.PendingTo, lang: mind.PendingLanguage,
                extra: mind.PendingTokens);
            action?.Invoke();
        }
        mind.PendingTo = null;
        mind.PendingTokens = null;
    }
}
