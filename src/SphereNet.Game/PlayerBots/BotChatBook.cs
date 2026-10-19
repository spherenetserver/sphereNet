using System.Text;
using System.Text.RegularExpressions;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Lines a server-driven player says, one category per text file
/// (<c>chat/&lt;language&gt;/&lt;category&gt;.txt</c>: one line each, <c>#</c> starts a
/// comment). Every language directory is loaded, so a bot can answer a player in the
/// language the player speaks; <see cref="Language"/> is the one used when nobody
/// listening has shown theirs. A category a language lacks falls back to <c>en</c>.
/// Lines carry <c>{token}</c> placeholders; a line whose tokens the caller cannot fill
/// is skipped rather than said half-written. Categories named <c>token_*</c> are word
/// lists the tokens are filled from (<c>token_item</c>, <c>token_when</c>, ...).
/// </summary>
public sealed partial class BotChatBook
{
    private const string Fallback = "en";
    private readonly Dictionary<string, Dictionary<string, string[]>> _books = new(StringComparer.OrdinalIgnoreCase);

    public string Language { get; }
    public IReadOnlyCollection<string> Languages => _books.Keys;

    /// <summary>Categories the default language can say (its own or the fallback's).</summary>
    public int CategoryCount =>
        Book(Language).Keys.Union(Book(Fallback).Keys, StringComparer.OrdinalIgnoreCase).Count();

    private BotChatBook(string language) => Language = language;

    public static BotChatBook Load(string dataDir, string language)
    {
        var book = new BotChatBook(string.IsNullOrWhiteSpace(language) ? Fallback : language);
        string root = Path.Combine(dataDir, "chat");
        if (Directory.Exists(root))
            foreach (string dir in Directory.GetDirectories(root))
                book.LoadDir(Path.GetFileName(dir), dir);
        return book;
    }

    /// <summary>Test seam: a book built from in-memory categories of one language.</summary>
    public static BotChatBook FromLines(string language, IReadOnlyDictionary<string, string[]> lines) =>
        FromLanguages(language, new Dictionary<string, IReadOnlyDictionary<string, string[]>> { [language] = lines });

    /// <summary>Test seam: a book of several languages.</summary>
    public static BotChatBook FromLanguages(string language,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> languages)
    {
        var book = new BotChatBook(language);
        foreach (var (lang, lines) in languages)
        {
            var target = book.BookFor(lang);
            foreach (var (k, v) in lines) target[k] = v;
        }
        return book;
    }

    private Dictionary<string, string[]> BookFor(string lang)
    {
        if (!_books.TryGetValue(lang, out var b))
            _books[lang] = b = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        return b;
    }

    private static readonly Dictionary<string, string[]> Empty = [];

    private Dictionary<string, string[]> Book(string lang) => _books.GetValueOrDefault(lang) ?? Empty;

    private void LoadDir(string lang, string dir)
    {
        var target = BookFor(lang);
        foreach (string file in Directory.GetFiles(dir, "*.txt"))
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToArray();
            if (lines.Length > 0)
                target[Path.GetFileNameWithoutExtension(file)] = lines;
        }
    }

    public bool HasLanguage(string? lang) => lang != null && _books.ContainsKey(lang);

    private string[]? Lines(string category, string? lang)
    {
        lang = HasLanguage(lang) ? lang! : Language;
        if (Book(lang).TryGetValue(category, out var lines) && lines.Length > 0) return lines;
        if (Book(Fallback).TryGetValue(category, out lines) && lines.Length > 0) return lines;
        return null;
    }

    public bool Has(string category, string? lang = null) => Lines(category, lang) != null;

    /// <summary>Line <paramref name="index"/> of a category as written (an ordered word
    /// list such as the compass points of <c>token_dir</c>), or null.</summary>
    public string? Line(string category, int index, string? lang = null)
    {
        var lines = Lines(category, lang);
        return lines != null && index >= 0 && index < lines.Length ? lines[index] : null;
    }

    /// <summary>A random line of <paramref name="category"/> in <paramref name="lang"/>
    /// (default: <see cref="Language"/>) with its tokens filled from
    /// <paramref name="tokens"/>; null when the category is empty or no line could be
    /// filled within a few draws.</summary>
    public string? Pick(string category, Random rng, IReadOnlyDictionary<string, string>? tokens = null,
        string? lang = null)
    {
        var lines = Lines(category, lang);
        if (lines == null) return null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            string line = lines[rng.Next(lines.Length)];
            string? filled = Fill(line, tokens);
            if (filled != null) return filled;
        }
        return null;
    }

    internal static string? Fill(string line, IReadOnlyDictionary<string, string>? tokens)
    {
        bool missing = false;
        string result = TokenRegex().Replace(line, m =>
        {
            if (tokens != null && tokens.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrEmpty(v))
                return v;
            missing = true;
            return m.Value;
        });
        return missing ? null : result;
    }

    [GeneratedRegex(@"\{([a-zA-Z_]+)\}")]
    private static partial Regex TokenRegex();
}
