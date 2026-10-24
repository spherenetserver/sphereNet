using System.Globalization;
using Microsoft.Extensions.Logging;
using System.Text;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Talk past the bots' own lines. A line a player says to a bot that the intents read
/// as small talk or a question (nothing the bot knows a fact for: the way, the news,
/// what it sells) is handed to the <see cref="Brain"/> with who the bot is, where it is
/// and what it is doing, how well it knows the player and its last words with them.
/// The reply is written while the bot "types" (the usual reply delay, up to
/// <see cref="BrainWaitMs"/>): in time, the bot says it; late, refused (the brain busy),
/// or unfit to say (empty, out of the game world), the bot says one of its own lines as
/// before. Nothing here waits: the brain runs on its own thread.
/// </summary>
public sealed partial class PlayerBotEngine
{
    private volatile IBotBrain? _brain;

    /// <summary>The brain, or null for none: bots answer from their own lines. May be
    /// set from another thread once the model has loaded.</summary>
    public IBotBrain? Brain { get => _brain; set => _brain = value; }
    /// <summary>Longest a bot waits for its reply, from the player's line.</summary>
    public int BrainWaitMs { get; set; } = 4000;
    /// <summary>Exchanges with one player kept for the next reply.</summary>
    public int BrainHistory { get; set; } = 3;
    /// <summary>Speech type of an emote (Source-X TALKMODE_EMOTE): the line shown over
    /// the bot's head in the emote colour, as "*thinks...*".</summary>
    private const byte EmoteMode = 2;
    /// <summary>An exchange older than this is forgotten: a new conversation.</summary>
    private const long ChatMemoryMs = 180_000;

    public long WindowThoughts { get; private set; }
    public long WindowThoughtsSaid { get; private set; }
    public long WindowThoughtsLate { get; private set; }
    public long WindowThoughtsRefused { get; private set; }
    public long WindowThoughtsUnfit { get; private set; }

    private void ResetBrainWindow() =>
        WindowThoughts = WindowThoughtsSaid = WindowThoughtsLate = WindowThoughtsRefused = WindowThoughtsUnfit = 0;

    /// <summary>The replies the brain writes: small talk and questions no fact answers.</summary>
    private static bool BrainAnswers(BotIntentKind kind, string category) => kind switch
    {
        BotIntentKind.Question or BotIntentKind.HowAreYou => true,
        BotIntentKind.None => category is "respond_chat" or "respond_what" or "respond_name",
        _ => false,
    };

    /// <summary>Ask the brain for the reply to a player's line; true when it took it.</summary>
    private bool TryThink(BotMind m, Character player, string text, string lang, long now)
    {
        var brain = Brain;
        if (brain == null || m.Char.IsDead) return false;
        if (m.ChatWith != player.Uid.Value || now - m.ChatAtMs > ChatMemoryMs)
        {
            m.ChatLog.Clear();
            m.ChatWith = player.Uid.Value;
        }
        var thought = new BotThought
        {
            Persona = Persona(m, player, lang),
            History = m.ChatLog.ToArray(),
            Line = text.Trim(),
            Language = lang,
        };
        WindowThoughts++;
        if (!brain.TryThink(thought))
        {
            WindowThoughtsRefused++;
            return false;
        }
        m.PendingThought = thought;
        m.PendingThoughtLine = thought.Line;
        m.PendingThoughtUntilMs = now + BrainWaitMs;
        return true;
    }

    /// <summary>The brain's reply to the queued answer, once the bot may speak: the
    /// line to say, or null to say the bot's own line instead. Waits (returns false)
    /// while the reply may still come.</summary>
    private bool TakeThought(BotMind m, long now, out string? line)
    {
        line = null;
        var thought = m.PendingThought;
        if (thought == null) return true;
        if (!thought.Done && now < m.PendingThoughtUntilMs) return false;
        m.PendingThought = null;
        if (!thought.Done)
        {
            thought.Abandon();
            WindowThoughtsLate++;
            return true;
        }
        line = CleanReply(thought.Reply, BaseName(m.Char));
        if (line == null)
        {
            WindowThoughtsUnfit++;
            return true;
        }
        WindowThoughtsSaid++;
        m.ChatLog.Add((m.PendingThoughtLine ?? "", line));
        while (m.ChatLog.Count > Math.Max(0, BrainHistory)) m.ChatLog.RemoveAt(0);
        m.ChatAtMs = now;
        return true;
    }

    private static readonly string[] OffWorld =
    [
        "language model", "dil modeli", "yapay zeka", "yapay zekâ", "artificial intelligence", "as an ai", "an ai ",
        "assistant", "asistan", "chatgpt", "openai", "http", "www.", "<", ">",
    ];

    /// <summary>A reply fit to say in game: its first line, without quotes, a speaker
    /// prefix, emoji or markup, at most <see cref="MaxReplyChars"/> long; null when
    /// nothing is left or it steps out of the game world.</summary>
    internal static string? CleanReply(string? reply, string botName)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        string line = reply.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        line = line.Trim().Trim('"', '\'', '*', '`', ' ', '“', '”', '«', '»');
        // "Rowena: hello" - the model naming itself.
        int colon = line.IndexOf(':');
        if (colon > 0 && colon <= botName.Length + 2 && line[..colon].Trim().Equals(botName, StringComparison.OrdinalIgnoreCase))
            line = line[(colon + 1)..].Trim();
        var sb = new StringBuilder(line.Length);
        foreach (char c in line.Replace('’', '\'').Replace('‘', '\'').Replace('–', '-').Replace('—', '-').Replace('…', '.'))
        {
            var cat = char.GetUnicodeCategory(c);
            if (char.IsSurrogate(c) || cat is UnicodeCategory.OtherSymbol or UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.PrivateUse)
                continue;
            sb.Append(c);
        }
        line = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // "a little.unpredictable": a small model now and then drops the space.
        line = MissingSpace.Replace(line, ". ");
        if (line.Length > MaxReplyChars)
        {
            int cut = line.LastIndexOf(' ', MaxReplyChars);
            line = line[..(cut > 40 ? cut : MaxReplyChars)].TrimEnd(',', ';', ' ');
        }
        if (line.Length < 2) return null;
        string lower = line.ToLowerInvariant();
        foreach (string word in OffWorld)
            if (lower.Contains(word)) return null;
        return line;
    }

    private const int MaxReplyChars = 140;

    private static readonly System.Text.RegularExpressions.Regex MissingSpace =
        new(@"(?<=\p{Ll}{2})\.(?=\p{Ll}{2})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>What the brain is told: first how to talk (the same for every bot and
    /// every line, so the model's memory of it is kept from one reply to the next), then
    /// who this bot is, where, doing what, and who it talks to.</summary>
    private string Persona(BotMind m, Character? player, string lang)
    {
        var ch = m.Char;
        string language = lang == "tr" ? "Turkish" : "English";
        var sb = new StringBuilder(512);
        sb.Append("You are a player character in the online game Ultima Online (the land of Britannia), ")
          .Append("chatting in game with another player. Answer ONLY in ").Append(language)
          .Append(", in one short casual sentence of at most 15 words, the way a gamer types in chat. ")
          .Append("Stay inside the game world: never mention AI, language models, assistants or the real world, ")
          .Append("and do not claim to be human. If you do not know something, say so briefly. ")
          .Append("Do not promise to do things for the player.\n");

        string who = m.IsCaster ? "mage" : m.IsArcher ? "archer" : m.IsFighter ? "warrior" : "crafter";
        sb.Append("You are ").Append(BaseName(ch)).Append(", a ").Append(who);
        if (m.Home.Length > 0) sb.Append(" from ").Append(m.Home);
        var best = BestSkills(ch, 2);
        if (best.Count > 0)
            sb.Append(", best at ").Append(string.Join(" and ", best.Select(b => SkillWords(b.Skill))));
        sb.Append(". You are ").Append(TraitOf(m)).Append(". ");
        sb.Append("Right now you are ").Append(DoingWords(m)).Append(". ");
        if (_world.FindRegion(ch.Position)?.Name is { Length: > 0 } region)
            sb.Append("You are in ").Append(region).Append(". ");
        if (player == null) return sb.Append("You do not know the player.").ToString();
        string name = FirstName(player);
        sb.Append(BondLevel(m, player) switch
        {
            BotBondLevel.Friend => $"The player is {name}, a friend of yours. ",
            BotBondLevel.Acquaintance => $"The player is {name}; you have met before. ",
            _ => $"The player is {name}, whom you do not know. ",
        });
        if (CompanionOf(player) == m) sb.Append("You travel together with ").Append(name).Append(". ");
        return sb.ToString().TrimEnd();
    }

    // Lines put to a bot from the console, logged with the reply once it is written.
    private readonly List<(BotMind Mind, BotThought Thought, long AskedMs)> _consoleThoughts = [];

    /// <summary>Console PLAYERBOTS THINK: put a line to a bot as a stranger would, with
    /// no player in the game; the reply (or why there is none) goes to the server log.</summary>
    public string ConsoleThink(string botKey, string text)
    {
        string lang = Intents.DetectLanguage(text) ?? Chat.Language;
        if (Brain is not { } brain) return "no brain: playerbots/ai.txt names no model, or it has not loaded (yet)";
        var mind = FindMind(botKey);
        if (mind == null) return $"no bot '{botKey}'";
        var thought = new BotThought { Persona = Persona(mind, null, lang), History = [], Line = text, Language = lang };
        if (!brain.TryThink(thought)) return "the brain is busy; try again";
        _consoleThoughts.Add((mind, thought, NowMs()));
        return $"asked {BaseName(mind.Char)}; the reply goes to the log";
    }

    private void LogConsoleThoughts(long now)
    {
        for (int i = _consoleThoughts.Count - 1; i >= 0; i--)
        {
            var (mind, thought, asked) = _consoleThoughts[i];
            if (!thought.Done && now - asked < 60_000) continue;
            _consoleThoughts.RemoveAt(i);
            _logger.LogInformation("[botbrain] {Name} <- \"{Line}\" -> {Reply} ({Ms} ms)", BaseName(mind.Char), thought.Line,
                thought.Done ? CleanReply(thought.Reply, BaseName(mind.Char)) is { } r ? $"\"{r}\"" : $"unfit: \"{thought.Reply}\"" : "no reply in a minute",
                thought.ThinkMs);
        }
    }

    /// <summary>What the bot is doing, in plain English, for the brain.</summary>
    private string DoingWords(BotMind m)
    {
        if (m.BankStage > 0) return "at the bank, seeing to your gold";
        return m.Mode switch
        {
            BotMode.Travel when m.Goal is { } goal => goal.Type.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase)
                ? $"on your way to hunt in {ShortPlaceName(goal.Name)}"
                : $"walking to the {PlacePhrase(goal, "en")}",
            BotMode.Shop => "shopping",
            BotMode.Loot => "looting what you killed",
            BotMode.Reclaim => "going back to your body for your things",
            BotMode.Fight => "fighting",
            BotMode.Flee => "running from a fight",
            _ when m.At is { } at => $"standing about at the {PlacePhrase(at, "en")}",
            _ => "standing about",
        };
    }
}
