using System.Text;
using SphereNet.Core.Enums;

namespace SphereNet.Game.PlayerBots;

public enum BotIntentKind
{
    None, Greet, Bye, Thanks, HowAreYou, Where, Party, Res, Wts, Wtb, Question,
    /// <summary>What the bot is doing, where it is going.</summary>
    Doing,
    /// <summary>Where the speaker is: the region or town.</summary>
    Here,
    /// <summary>What the bot is: its class and best skills.</summary>
    WhoAreYou,
    /// <summary>Any news: the last real event the bots know of.</summary>
    News,
    Heal,
    Follow,
    /// <summary>"Are you a bot".</summary>
    Bot,
    /// <summary>A short yes or no, read against what the bot last asked.</summary>
    Yes,
    No,
    /// <summary>How far, which way: the last "where" again.</summary>
    Far,
    /// <summary>To a party bot: wait here.</summary>
    Stay,
    /// <summary>To a party bot: leave the party.</summary>
    LeaveParty,
    /// <summary>To a party bot: how are your hit points and mana.</summary>
    Status,
    /// <summary>What do you have for sale.</summary>
    Selling,
    /// <summary>Open a gate (to a place named, or on a rune handed over).</summary>
    Gate,
    /// <summary>Mark a rune (the player's own blank one) where we stand.</summary>
    Mark,
    /// <summary>A beneficial spell: bless, protection, reflect, night sight...</summary>
    Buff,
    /// <summary>Take me to a place: the bot walks in front.</summary>
    Guide,
    /// <summary>What is this worth: shown in a trade window, priced, not bought.</summary>
    Appraise,
    /// <summary>Watch over me (a companion): fight what sets on the player.</summary>
    Protect,
    /// <summary>Hold my things for me (a companion).</summary>
    Carry,
    /// <summary>Give back what you hold for me.</summary>
    GiveBack,
    /// <summary>What comes next in a trade is a present.</summary>
    Gift,
    /// <summary>What can you do (for me).</summary>
    Can,
    /// <summary>Forget what I asked.</summary>
    Cancel,
    /// <summary>Let us be friends.</summary>
    Friend,
    /// <summary>Travel with me, for good (a companion).</summary>
    Companion,
    /// <summary>A companion let go: go your own way.</summary>
    Dismiss,
    /// <summary>Do you remember me.</summary>
    Remember,
}

/// <summary>What a player's line asks for. <see cref="PlaceType"/> is the destination
/// type a "where" asks about (Bank, Healer...), <see cref="PlaceWord"/> the word the
/// player used for it, echoed back in the answer; <see cref="Rest"/> what follows a
/// WTS / WTB keyword (the goods) and <see cref="At"/> the word index of that keyword
/// (0: the line opens with it, the way a trade shout does). <see cref="AlsoGreets"/>:
/// the line says hello as well ("hi, where is the bank"). <see cref="Spell"/> is the
/// spell a <see cref="BotIntentKind.Buff"/> asks for, when one is named.</summary>
public readonly record struct BotIntent(BotIntentKind Kind, string? PlaceType = null, string? PlaceWord = null,
    string? Rest = null, int At = -1, bool AlsoGreets = false, SpellType? Spell = null);

/// <summary>
/// Reads a player's line the way a bystander would: a few keywords per intent and per
/// place, in any language (data file <c>intents.txt</c>: <c>greet: hi hello selam</c>,
/// <c>place.Bank: bank banka</c>, <c>lang.tr: abi nerde</c>; a phrase joins its words
/// with <c>+</c>). A keyword of four letters or more also matches as a word's start, so
/// a suffixed form ("bankaya") is understood.
/// </summary>
public sealed class BotIntents
{
    private readonly Dictionary<BotIntentKind, string[]> _intents = [];
    private readonly List<(string Type, string[] Words)> _places = [];
    private readonly Dictionary<string, HashSet<string>> _langWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(SpellType Spell, string[] Words)> _buffs = [];

    public static BotIntents Load(string dataDir)
    {
        string path = Path.Combine(dataDir, "intents.txt");
        return File.Exists(path) ? Parse(File.ReadAllLines(path, Encoding.UTF8)) : new BotIntents();
    }

    public static BotIntents Parse(IEnumerable<string> lines)
    {
        var b = new BotIntents();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim();
            string[] words = line[(colon + 1)..]
                .Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant().Replace('+', ' '))
                .ToArray();
            if (words.Length == 0) continue;
            if (key.StartsWith("place.", StringComparison.OrdinalIgnoreCase))
                b._places.Add((key[6..], words));
            else if (key.StartsWith("town.", StringComparison.OrdinalIgnoreCase))
                b._places.Add(("town:" + key[5..], words));
            else if (key.StartsWith("dest.", StringComparison.OrdinalIgnoreCase))
                b._places.Add(("dest:" + key[5..], words));
            else if (key.StartsWith("buff.", StringComparison.OrdinalIgnoreCase) &&
                     Enum.TryParse(key[5..], ignoreCase: true, out SpellType spell))
                b._buffs.Add((spell, words));
            else if (key.StartsWith("lang.", StringComparison.OrdinalIgnoreCase))
                b._langWords[key[5..]] = [.. words];
            else if (key.Equals("not", StringComparison.OrdinalIgnoreCase))
                b._intents[Not] = words;
            else if (key.Equals("more", StringComparison.OrdinalIgnoreCase))
                b._intents[More] = words;
            else if (Enum.TryParse(key, ignoreCase: true, out BotIntentKind kind))
                b._intents[kind] = words;
        }
        return b;
    }

    public static List<string> Words(string text)
    {
        var list = new List<string>();
        foreach (string w in text.ToLowerInvariant().Split(
                     [' ', ',', '.', '!', '?', ':', ';', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
            list.Add(w);
        return list;
    }

    private static bool Matches(List<string> words, string normalized, string key, out int at)
    {
        at = -1;
        if (key.Contains(' '))
        {
            int i = normalized.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return false;
            at = normalized[..i].Count(c => c == ' ');
            return true;
        }
        for (int i = 0; i < words.Count; i++)
            if (words[i] == key || (key.Length >= 4 && words[i].StartsWith(key, StringComparison.Ordinal)))
            {
                at = i;
                return true;
            }
        return false;
    }

    private bool Has(BotIntentKind kind, List<string> words, string normalized, out int at)
    {
        at = -1;
        if (!_intents.TryGetValue(kind, out var keys)) return false;
        foreach (string k in keys)
            if (Matches(words, normalized, k, out at)) return true;
        return false;
    }

    /// <summary>The place a line names, as a destination type and the word used;
    /// <paramref name="skip"/> leaves types out (the "gate" of "open a gate" asks for
    /// no moongate).</summary>
    public (string Type, string Word)? FindPlace(string text, Func<string, bool>? skip = null)
    {
        var words = Words(text);
        string normalized = string.Join(' ', words);
        foreach (var (type, keys) in _places)
        {
            if (skip != null && skip(type)) continue;
            foreach (string k in keys)
                if (Matches(words, normalized, k, out int at))
                    return (type, k.Contains(' ') ? k : words[at]);
        }
        return null;
    }

    /// <summary>The beneficial spell a line names ("bless", "night sight"), if any
    /// (<c>buff.&lt;Spell&gt;: words</c> in the data).</summary>
    public SpellType? FindBuff(string text)
    {
        var words = Words(text);
        string normalized = string.Join(' ', words);
        foreach (var (spell, keys) in _buffs)
            foreach (string k in keys)
                if (Matches(words, normalized, k, out _))
                    return spell;
        return null;
    }

    private static bool IsMoongate(string type) => type.Equals("Moongate", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a line asks for. The more particular asks are tried first ("where
    /// are you going" is a question about the bot, not about a place); a line that also
    /// says hello is marked so (<see cref="BotIntent.AlsoGreets"/>); "not" turns an offer
    /// taken up (party, follow, heal) into a no. A place named alone ("bank?", "what about
    /// the healer") asks the way to it. A yes or a no counts only on a short line.</summary>
    public BotIntent Parse(string text)
    {
        var words = Words(text);
        if (words.Count == 0) return default;
        string normalized = string.Join(' ', words);
        var intent = Read(text, words, normalized);
        if (intent.Kind is BotIntentKind.Party or BotIntentKind.Follow or BotIntentKind.Heal or BotIntentKind.Gate or
                BotIntentKind.Companion or BotIntentKind.Guide or BotIntentKind.Buff or BotIntentKind.Friend &&
            Has(Not, words, normalized, out _))
            return new BotIntent(BotIntentKind.No);
        if (intent.Kind is not (BotIntentKind.Greet or BotIntentKind.None) && Has(BotIntentKind.Greet, words, normalized, out _))
            intent = intent with { AlsoGreets = true };
        return intent;
    }

    // Keys read on their own, not as an intent of their own: "not" words and the words
    // that carry a conversation on to another place ("what about").
    private const BotIntentKind Not = (BotIntentKind)1000;
    private const BotIntentKind More = (BotIntentKind)1001;

    private BotIntent Read(string text, List<string> words, string normalized)
    {
        bool asks = text.TrimEnd().EndsWith('?');
        if (Has(BotIntentKind.Res, words, normalized, out _)) return new BotIntent(BotIntentKind.Res);
        foreach (var kind in Particular)
            if (Has(kind, words, normalized, out _))
                return kind switch
                {
                    // "britain'e gate": the place, not the moongate the word "gate" names.
                    BotIntentKind.Gate when FindPlace(text, IsMoongate) is { } to => new BotIntent(kind, to.Type, to.Word),
                    BotIntentKind.Guide when FindPlace(text) is { } place => new BotIntent(kind, place.Type, place.Word),
                    BotIntentKind.Buff => new BotIntent(kind, Spell: FindBuff(text)),
                    _ => new BotIntent(kind),
                };
        // A spell named on a short line ("bless pls") asks for it.
        if (_buffs.Count > 0 && words.Count <= 5 && FindBuff(text) is { } buff)
            return new BotIntent(BotIntentKind.Buff, Spell: buff);
        if (Has(BotIntentKind.Where, words, normalized, out _))
        {
            var place = FindPlace(text);
            return new BotIntent(BotIntentKind.Where, place?.Type, place?.Word);
        }
        if (Has(BotIntentKind.Far, words, normalized, out _)) return new BotIntent(BotIntentKind.Far);
        if (Has(BotIntentKind.Party, words, normalized, out _)) return new BotIntent(BotIntentKind.Party);
        if (Has(BotIntentKind.Wts, words, normalized, out int wts))
            return new BotIntent(BotIntentKind.Wts, Rest: Remainder(words, wts), At: wts);
        if (Has(BotIntentKind.Wtb, words, normalized, out int wtb))
            return new BotIntent(BotIntentKind.Wtb, Rest: Remainder(words, wtb), At: wtb);
        if (Has(BotIntentKind.Thanks, words, normalized, out _)) return new BotIntent(BotIntentKind.Thanks);
        if (Has(BotIntentKind.Bye, words, normalized, out _)) return new BotIntent(BotIntentKind.Bye);
        if (Has(BotIntentKind.HowAreYou, words, normalized, out _)) return new BotIntent(BotIntentKind.HowAreYou);
        if ((asks || words.Count <= 3 || Has(More, words, normalized, out _)) && FindPlace(text) is { } named)
            return new BotIntent(BotIntentKind.Where, named.Type, named.Word);
        if (Has(BotIntentKind.Greet, words, normalized, out _)) return new BotIntent(BotIntentKind.Greet);
        if (!asks && words.Count <= 3)
        {
            if (Has(BotIntentKind.No, words, normalized, out _)) return new BotIntent(BotIntentKind.No);
            if (Has(BotIntentKind.Yes, words, normalized, out _)) return new BotIntent(BotIntentKind.Yes);
        }
        if (asks || Has(BotIntentKind.Question, words, normalized, out _))
            return new BotIntent(BotIntentKind.Question);
        return default;
    }

    /// <summary>Questions about the bot itself and asks for its help, tried before a
    /// "where" (a "where are you going" asks what the bot is doing).</summary>
    private static readonly BotIntentKind[] Particular =
    [
        BotIntentKind.Bot, BotIntentKind.Cancel, BotIntentKind.Can, BotIntentKind.Selling, BotIntentKind.GiveBack,
        BotIntentKind.Carry, BotIntentKind.Gift, BotIntentKind.Appraise, BotIntentKind.Remember, BotIntentKind.Friend,
        BotIntentKind.Companion, BotIntentKind.Dismiss, BotIntentKind.LeaveParty, BotIntentKind.Status,
        BotIntentKind.Stay, BotIntentKind.Gate, BotIntentKind.Mark, BotIntentKind.Guide, BotIntentKind.Protect,
        BotIntentKind.Heal, BotIntentKind.Buff, BotIntentKind.Follow, BotIntentKind.Doing,
        BotIntentKind.Here, BotIntentKind.WhoAreYou, BotIntentKind.News,
    ];

    /// <summary>The goods after a WTS/WTB keyword: up to three words, no prices.</summary>
    private static string? Remainder(List<string> words, int at)
    {
        var rest = words.Skip(at + 1)
            .TakeWhile(w => !char.IsDigit(w[0]))
            .Take(3)
            .ToList();
        return rest.Count > 0 ? string.Join(' ', rest) : null;
    }

    private static readonly string TurkishLetters = "çğıöşüÇĞİÖŞÜ";

    /// <summary>The language a line is written in, among those the data lists words for:
    /// letters only one of them uses decide first (Turkish ç ğ ı ö ş ü), then the most
    /// listed words. Null when nothing tells.</summary>
    public string? DetectLanguage(string text)
    {
        if (_langWords.ContainsKey("tr") && text.IndexOfAny(TurkishLetters.ToCharArray()) >= 0) return "tr";
        var words = Words(text);
        string? best = null;
        int bestHits = 0;
        foreach (var (lang, set) in _langWords)
        {
            int hits = words.Count(set.Contains);
            if (hits > bestHits) { best = lang; bestHits = hits; }
        }
        return best;
    }
}
