using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>Being talked to. A bot whose name a player says turns to answer, and keeps
/// answering that player for a minute without being named again (the player's focus).
/// A line nobody is named in can still be taken up by someone in the crowd: a hello,
/// "where is the bank", "lfg", a trade shout, a ghost's plea. Answers are in the
/// language the player wrote in and come a moment later, the way people type.
/// A conversation is followed: the place last asked about, and the question a bot last
/// asked the player, give a short follow-up ("and the healer?", "how far?", "yes") its
/// meaning. What a bot knows (where it is going, where this is, what it is, the news)
/// it tells.</summary>
public sealed partial class PlayerBotEngine
{
    private const long FocusMs = 60_000;
    /// <summary>How long a place asked about stays the topic of the conversation.</summary>
    private const long TopicMs = 120_000;
    /// <summary>How long a bot's question waits for its yes or no.</summary>
    private const long AskedMs = 60_000;
    /// <summary>The same question again within this is answered "like I said".</summary>
    private const long RepeatMs = 30_000;

    /// <summary>A player said <paramref name="text"/> out loud. Only real players are
    /// answered, so two bots never talk themselves into a loop.</summary>
    public void OnHear(Character speaker, string text)
    {
        if (speaker.IsPlayerBot || string.IsNullOrWhiteSpace(text) || _minds.Count == 0) return;
        long now = NowMs();
        var w = WatcherOf(speaker, now);
        w.LastSpokeMs = now;
        w.Language = Intents.DetectLanguage(text) ?? w.Language;
        string lang = w.Language ?? Chat.Language;
        var intent = InContext(w, Intents.Parse(text), text, now);
        if (TryPartyCommand(speaker, intent, text, viaParty: false, now, lang)) return;
        // A favour in hand: the line may be its answer ("britain", "never mind").
        if (!speaker.IsDead && TryFavorFollowUp(speaker, w, intent, text, now, lang)) return;

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
        // A yes or a no answers whoever asked the question.
        if (target == null && intent.Kind is BotIntentKind.Yes or BotIntentKind.No && w.AskedBy is { } asker &&
            near.Contains(asker))
            target = asker;
        // A companion is who a player turns to: an ask or a question named to nobody is its.
        if (target == null && intent.Kind != BotIntentKind.None && CompanionOf(speaker) is { } mate && near.Contains(mate))
            target = mate;
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
                BotIntentKind.Here => 50,
                BotIntentKind.Heal => 50,
                BotIntentKind.Bot => 40,
                BotIntentKind.News => 30,
                BotIntentKind.Follow => 30,
                BotIntentKind.Doing => 20,
                BotIntentKind.Gate => 60,
                BotIntentKind.Buff => 45,
                BotIntentKind.Mark => 40,
                BotIntentKind.Guide => 45,
                BotIntentKind.Can => 30,
                BotIntentKind.Appraise => 25,
                _ => 0,
            };
            if (near.Count == 0 || chance == 0 || _rng.Next(100) >= chance) return;
            var pool = intent.Kind switch
            {
                BotIntentKind.Party or BotIntentKind.Follow => near.Where(m => m.IsFighter).ToList(),
                BotIntentKind.Heal or BotIntentKind.Buff or BotIntentKind.Mark => near.Where(m => m.IsCaster).ToList(),
                BotIntentKind.Gate => near.Where(CanGate).ToList(),
                _ => near,
            };
            if (pool.Count == 0) pool = near;
            target = pool[_rng.Next(pool.Count)];
        }

        if (target.Scene != null) EndScene(target.Scene);
        w.Focus = target;
        w.FocusUntilMs = now + FocusMs;
        if (addressed)
        {
            Remember(target, speaker);
            BondTalk(target, speaker, now);
        }
        Answer(target, speaker, w, intent, text, named != null, now, lang);
    }

    /// <summary>A line read against the conversation: a place named after a "where"
    /// asks the way there too; "where?" or "how far?" with no place asks about the place
    /// last talked of; a yes or a no means something only after a bot's question.</summary>
    private BotIntent InContext(BotWatcher w, BotIntent intent, string text, long now)
    {
        bool topical = w.TopicPlaceType != null && now - w.TopicMs < TopicMs;
        switch (intent.Kind)
        {
            case BotIntentKind.None when topical && w.TopicKind == BotIntentKind.Where && Intents.FindPlace(text) is { } place:
                return intent with { Kind = BotIntentKind.Where, PlaceType = place.Type, PlaceWord = place.Word };
            case BotIntentKind.Where when intent.PlaceType == null && topical:
            case BotIntentKind.Far when topical:
                return intent with { Kind = BotIntentKind.Where, PlaceType = w.TopicPlaceType, PlaceWord = w.TopicPlaceWord };
            case BotIntentKind.Far:
                return intent with { Kind = BotIntentKind.Question };
            case BotIntentKind.Yes or BotIntentKind.No when w.Asked == null || now - w.AskedMs >= AskedMs:
                return default;
        }
        return intent;
    }

    private void Answer(BotMind m, Character player, BotWatcher w, BotIntent intent, string text, bool named,
        long now, string lang, bool party = false)
    {
        Dictionary<string, string>? tokens = null;
        string category;
        string? fallback = "respond_what";
        string? prefix = null;
        Action? action = null;
        string? topicType = intent.PlaceType, topicWord = intent.PlaceWord;
        // A question asked again at once: "like I said".
        if (intent.Kind is BotIntentKind.Where or BotIntentKind.Far or BotIntentKind.Doing or BotIntentKind.Here or
                BotIntentKind.WhoAreYou or BotIntentKind.News or BotIntentKind.Question or BotIntentKind.HowAreYou or
                BotIntentKind.Party or BotIntentKind.Res or BotIntentKind.Heal or BotIntentKind.Follow or
                BotIntentKind.Bot or BotIntentKind.Selling &&
            intent.Kind == w.TopicKind && (intent.PlaceType == null || intent.PlaceType == w.TopicPlaceType) && now - w.TopicMs < RepeatMs)
            prefix = "respond_repeat";
        else if (intent.AlsoGreets)
            prefix = Knows(m, player) && Chat.Has("friend_greet", lang) ? "friend_greet" : "respond_greet";
        string? asked = w.AskedBy == m && w.Asked != null && now - w.AskedMs < AskedMs ? w.Asked : null;
        var onYes = asked != null ? w.AskedYes : null;
        w.Asked = null;
        w.AskedYes = null;
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
                (category, tokens) = AnswerWhere(player.Position, intent, lang);
                fallback = "respond_question";
                break;
            case BotIntentKind.Doing:
                (category, tokens, topicType, topicWord) = AnswerDoing(m, player, lang);
                fallback = "respond_doing_idle";
                break;
            case BotIntentKind.Here:
                string here = PlaceName(player.Position);
                category = here.Length > 0 ? "respond_here" : "respond_here_unknown";
                tokens = new Dictionary<string, string> { ["here"] = here };
                fallback = "respond_question";
                break;
            case BotIntentKind.WhoAreYou:
                category = "respond_whoami";
                tokens = SkillTokens(m.Char);
                fallback = "respond_question";
                break;
            case BotIntentKind.News:
                (category, tokens) = AnswerNews(m, lang, now);
                fallback = "respond_news_none";
                break;
            case BotIntentKind.Heal:
                (category, action) = AnswerHeal(m, player, now);
                break;
            case BotIntentKind.Follow when m.Companion == player:
                m.CompanionHoldUntilMs = 0;
                category = "party_follow";
                break;
            case BotIntentKind.Follow when Parties?.FindParty(m.Char.Uid) == null && m.Companion == null &&
                                          BondLevel(m, player) == BotBondLevel.Friend && !IsRogueBot(m.Char):
                // A friend asked along goes along for good.
                (category, tokens) = AnswerCompanion(m, player, now);
                break;
            case BotIntentKind.Gate:
                (category, tokens) = AskGate(m, player, w, intent, lang, now);
                fallback = null;
                break;
            case BotIntentKind.Mark:
                (category, tokens) = AskMark(m, player, w, lang, now);
                fallback = null;
                break;
            case BotIntentKind.Buff:
                (category, tokens) = AskBuff(m, player, w, intent, lang, now);
                fallback = null;
                break;
            case BotIntentKind.Guide:
                (category, tokens) = AskGuide(m, player, w, intent, lang, now);
                fallback = null;
                break;
            case BotIntentKind.Appraise:
                (category, tokens) = AskAppraise(m, player, w, lang, now);
                break;
            case BotIntentKind.Carry:
                (category, tokens) = AskCarry(m, player, w, lang, now);
                fallback = null;
                break;
            case BotIntentKind.GiveBack:
                (category, tokens) = AskGiveBack(m, player, now);
                fallback = null;
                break;
            case BotIntentKind.Gift:
                (category, tokens) = AskGift(m, player, w, lang, now);
                break;
            case BotIntentKind.Can:
                (category, tokens) = AnswerCan(m, player, lang);
                fallback = null;
                break;
            case BotIntentKind.Cancel:
                category = "ack_yes";
                break;
            case BotIntentKind.Friend:
                (category, tokens) = AnswerFriend(m, player);
                fallback = "respond_chat";
                break;
            case BotIntentKind.Companion:
                (category, tokens) = AnswerCompanion(m, player, now);
                fallback = null;
                break;
            case BotIntentKind.Dismiss:
                (category, tokens) = AnswerDismiss(m, player, now);
                fallback = "ack_yes";
                break;
            case BotIntentKind.Remember:
                (category, tokens, string? first) = AnswerRemember(m, player, lang);
                if (first != null) prefix = first;
                fallback = "remember_yes";
                break;
            case BotIntentKind.Protect:
                if (m.Companion == player)
                {
                    m.Protecting = true;
                    category = "protect_yes";
                }
                else category = "protect_no";
                fallback = null;
                break;
            case BotIntentKind.Stay when m.Companion == player:
                m.CompanionHoldUntilMs = now + 600_000;
                m.HoldAt = m.Char.Position;
                category = "party_stay";
                fallback = null;
                break;
            case BotIntentKind.Status when m.Companion == player:
                tokens = new Dictionary<string, string>
                {
                    ["hp"] = m.Char.Hits.ToString(), ["maxhp"] = m.Char.MaxHits.ToString(),
                    ["mana"] = m.Char.Mana.ToString(), ["maxmana"] = m.Char.MaxMana.ToString(),
                };
                category = "party_status";
                fallback = null;
                break;
            case BotIntentKind.Follow:
                var botParty = Parties?.FindParty(m.Char.Uid);
                category = botParty != null && botParty == Parties?.FindParty(player.Uid) ? "respond_follow_already"
                    : m.IsFighter && botParty == null ? "respond_follow_yes" : "respond_follow_no";
                break;
            case BotIntentKind.Bot:
                category = "respond_bot";
                break;
            case BotIntentKind.Yes or BotIntentKind.No:
                string answer = intent.Kind == BotIntentKind.Yes ? "_yes" : "_no";
                category = asked != null && Chat.Has(asked + answer, lang) ? asked + answer : "ack" + answer;
                fallback = "respond_chat";
                // What the question offered is done on a yes.
                if (intent.Kind == BotIntentKind.Yes && onYes != null) action = onYes;
                break;
            case BotIntentKind.None when asked != null && Chat.Has(asked + "_reply", lang):
                category = asked + "_reply";
                fallback = "respond_chat";
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
                if (Trades != null && m.Client != null && !IsPk(m) && PackGold(m.Char) / 2 >= 100)
                {
                    // It has the gold for a real deal: show it in a trade window.
                    category = "trade_show";
                    m.TalkTopic = null;
                }
                else if (_rng.Next(100) < 50)
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
                category = AnswerWtb(m, player, intent.Rest, now, ref tokens) ?? "respond_wtb_none";
                if (tokens == null && intent.Rest != null) tokens = new Dictionary<string, string> { ["item"] = intent.Rest };
                break;
            case BotIntentKind.Selling:
                (category, tokens) = AnswerStock(m);
                break;
            case BotIntentKind.Question:
                category = "respond_question";
                break;
            default:
                category = Haggle(m, text, ref tokens) ??
                           (named ? "respond_name" : w.Focus == m ? "respond_chat" : "respond_what");
                break;
        }
        if (intent.Kind is not (BotIntentKind.None or BotIntentKind.Yes or BotIntentKind.No))
        {
            w.TopicKind = intent.Kind;
            w.TopicMs = now;
            // A place stays the topic until another one is named.
            if (topicType != null)
            {
                w.TopicPlaceType = topicType;
                w.TopicPlaceWord = topicWord;
            }
        }
        WindowReplies++;
        QueueReply(m, category, now, to: player, lang: lang, fallback: fallback, tokens: tokens, action: action,
            prefix: prefix, party: party);
        // Small talk and questions no fact answers: the brain writes the reply while the
        // bot types; its own line stands in when the reply is late.
        if (action == null && !party && BrainAnswers(intent.Kind, category))
            TryThink(m, player, text, lang, now);
    }

    /// <summary>What the bot is doing, from what it is doing: walking somewhere (that
    /// place becomes the topic, so "where is that?" is answered), hunting, shopping,
    /// banking, on its way back to its corpse, keeping someone company, or standing
    /// about somewhere.</summary>
    private (string, Dictionary<string, string>?, string?, string?) AnswerDoing(BotMind m, Character player, string lang)
    {
        if (PartyLeader(m) == player) return ("respond_doing_party", null, null, null);
        switch (m.Mode)
        {
            case BotMode.Shop: return ("respond_doing_shop", null, null, null);
            case BotMode.Loot or BotMode.Reclaim: return ("respond_doing_reclaim", null, null, null);
        }
        if (m.BankStage > 0) return ("respond_doing_bank", null, null, null);
        if (m.Mode == BotMode.Travel && m.Goal is { } goal)
        {
            string phrase = PlacePhrase(goal, lang);
            var tokens = new Dictionary<string, string> { ["goal"] = phrase };
            string category = goal.Type.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase) ? "respond_doing_hunt"
                : m.Leader != null ? "respond_doing_company" : "respond_doing_travel";
            if (m.Leader != null) tokens["other"] = BaseName(m.Leader.Char);
            return (category, tokens, "dest:" + goal.Name, phrase);
        }
        if (m.At is { } at)
            return ("respond_doing_linger", new Dictionary<string, string> { ["at"] = PlacePhrase(at, lang) }, null, null);
        return ("respond_doing_idle", null, null, null);
    }

    /// <summary>A destination the way a player says it: a dungeon by its name, a town's
    /// place by the town and the word for its kind (<c>token_placetype</c>:
    /// <c>Bank=bank</c>), else its name.</summary>
    private string PlacePhrase(BotDestination d, string lang)
    {
        if (d.Type.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase)) return ShortPlaceName(d.Name);
        string? word = null;
        for (int i = 0; Chat.Line("token_placetype", i, lang) is { } line; i++)
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(d.Type, StringComparison.OrdinalIgnoreCase))
            {
                word = line[(eq + 1)..].Trim();
                break;
            }
        }
        if (word == null) return d.Name;
        return d.City.Length > 0 ? d.City + " " + word : word;
    }

    /// <summary>The bot's two best skills, for "what are you": <c>{skill1}</c>,
    /// <c>{val1}</c> (whole points), <c>{skill2}</c>.</summary>
    private static Dictionary<string, string> SkillTokens(Character ch)
    {
        var tokens = new Dictionary<string, string>();
        var best = BestSkills(ch, 2);
        if (best.Count > 0)
        {
            tokens["skill1"] = SkillWords(best[0].Skill);
            tokens["val1"] = (best[0].Value / 10).ToString();
        }
        if (best.Count > 1) tokens["skill2"] = SkillWords(best[1].Skill);
        return tokens;
    }

    /// <summary>"EvaluatingIntel" as "evaluating intel".</summary>
    internal static string SkillWords(SkillType skill) => CamelWords(skill.ToString());

    /// <summary>A CamelCase name in lower-case words.</summary>
    private static string CamelWords(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>The news, asked for: a story from the bots' news told the way a scene
    /// tells it (the <c>_self</c> version by the bot it happened to); "nothing much"
    /// when there is none.</summary>
    private (string, Dictionary<string, string>?) AnswerNews(BotMind m, string lang, long now)
    {
        var news = Gossip.Pick(_rng, now, []);
        if (news == null) return ("respond_news_none", null);
        string category = "gossip_" + news.Kind;
        if (news.ActorUid == m.Char.Uid.Value && Chat.Has(category + "_self", lang)) category += "_self";
        var tokens = new Dictionary<string, string> { ["actor"] = news.Actor };
        if (news.Other.Length > 0) tokens["other"] = news.Other;
        if (news.Place.Length > 0) tokens["place"] = news.Place;
        tokens["count"] = Math.Max(2, Gossip.PkCount(news.OtherUid)).ToString();
        if (Chat.Pick(now - news.AtMs < 600_000 ? "token_when_now" : "token_when_earlier", _rng, null, lang) is { } when)
            tokens["when"] = when;
        return (category, tokens);
    }

    /// <summary>"Heal me": a caster with the spell in its book and the mana casts it on
    /// the player through its client (Cure when they are poisoned), once every 20
    /// seconds; a player not hurt is told so.</summary>
    private (string, Action?) AnswerHeal(BotMind m, Character player, long now)
    {
        bool poisoned = player.IsPoisoned;
        if (!poisoned && player.Hits >= player.MaxHits) return ("respond_heal_fine", null);
        var ch = m.Char;
        SpellType? spell = poisoned ? (HasSpell(ch, SpellType.Cure) ? SpellType.Cure : null)
            : HasSpell(ch, SpellType.GreaterHeal) ? SpellType.GreaterHeal
            : HasSpell(ch, SpellType.Heal) ? SpellType.Heal : null;
        if (!m.IsCaster || spell == null || m.Client == null || ch.Mana < 11 || now < m.NextFavorMs ||
            ch.TryGetCastingSpell(out _))
            return ("respond_heal_no", null);
        m.NextFavorMs = now + 20_000;
        var client = m.Client;
        var cast = spell.Value;
        return ("respond_heal_yes", () =>
        {
            if (!player.IsDead && !player.IsDeleted && Within(player.Position, ch.Position, 10))
                client.HandleCastSpell(cast, player.Uid.Value);
        });
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
    private (string Category, Dictionary<string, string>? Tokens) AnswerWhere(Point3D from, BotIntent intent,
        string lang)
    {
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
        string? fallback = "respond_what", Dictionary<string, string>? tokens = null, Action? action = null,
        string? prefix = null, bool party = false)
    {
        mind.PendingThought?.Abandon();
        mind.PendingThought = null;
        mind.ThinkingShown = false;
        mind.PendingPrefix = prefix;
        mind.PendingParty = party;
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
        // The brain's reply, when one is being written: wait for it while it may come.
        if (!TakeThought(mind, now, out string? thought))
        {
            // The reply is not written yet when the bot would have answered: it shows
            // it is thinking (an emote, once), so the player waits for it.
            if (!mind.ThinkingShown)
            {
                mind.ThinkingShown = true;
                Speak(mind, "brain_thinking", fallback: null, to: mind.PendingTo, lang: mind.PendingLanguage, mode: EmoteMode);
            }
            return;
        }
        string category = mind.PendingCategory;
        var action = mind.PendingAction;
        mind.PendingCategory = null;
        mind.PendingAction = null;
        if (!mind.Char.IsDead && thought != null)
        {
            Utter(mind, "brain", thought, party: false);
            action?.Invoke();
        }
        else if (!mind.Char.IsDead)
        {
            if (Speak(mind, category, fallback: mind.PendingFallback, to: mind.PendingTo, lang: mind.PendingLanguage,
                    extra: mind.PendingTokens, prefix: mind.PendingPrefix, party: mind.PendingParty) &&
                mind.PendingTo is { } player && _watchers.TryGetValue(player.Uid.Value, out var w) &&
                mind.LastLine is { } said && said.TrimEnd().EndsWith('?'))
            {
                // The bot asked the player something: their yes or no is read against it.
                w.Asked = category;
                w.AskedBy = mind;
                w.AskedMs = now;
                w.AskedYes = mind.PendingOnYes;
            }
            action?.Invoke();
        }
        mind.PendingOnYes = null;
        mind.PendingPrefix = null;
        mind.PendingParty = false;
        mind.PendingTo = null;
        mind.PendingTokens = null;
    }
}
