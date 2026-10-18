using System.Text;
using System.Text.RegularExpressions;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Lines a server-driven player says, one category per text file
/// (<c>chat/&lt;language&gt;/&lt;category&gt;.txt</c>: one line each, <c>#</c> starts a
/// comment). A category the chosen language lacks falls back to <c>en</c>. Lines
/// carry <c>{token}</c> placeholders; a line whose tokens the caller cannot fill is
/// skipped rather than said half-written.
/// </summary>
public sealed partial class BotChatBook
{
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.OrdinalIgnoreCase);

    public string Language { get; }
    public int CategoryCount => _lines.Count;

    private BotChatBook(string language) => Language = language;

    public static BotChatBook Load(string dataDir, string language)
    {
        var book = new BotChatBook(string.IsNullOrWhiteSpace(language) ? "en" : language);
        // The fallback first, the chosen language over it.
        book.LoadDir(Path.Combine(dataDir, "chat", "en"));
        if (!book.Language.Equals("en", StringComparison.OrdinalIgnoreCase))
            book.LoadDir(Path.Combine(dataDir, "chat", book.Language));
        return book;
    }

    /// <summary>Test seam: a book built from in-memory categories.</summary>
    public static BotChatBook FromLines(string language, IReadOnlyDictionary<string, string[]> lines)
    {
        var book = new BotChatBook(language);
        foreach (var (k, v) in lines) book._lines[k] = v;
        return book;
    }

    private void LoadDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string file in Directory.GetFiles(dir, "*.txt"))
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToArray();
            if (lines.Length > 0)
                _lines[Path.GetFileNameWithoutExtension(file)] = lines;
        }
    }

    public bool Has(string category) => _lines.ContainsKey(category);

    /// <summary>A random line of <paramref name="category"/> with its tokens filled
    /// from <paramref name="tokens"/>; null when the category is empty or no line
    /// could be filled within a few draws.</summary>
    public string? Pick(string category, Random rng, IReadOnlyDictionary<string, string>? tokens = null)
    {
        if (!_lines.TryGetValue(category, out var lines) || lines.Length == 0) return null;
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
