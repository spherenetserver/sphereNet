using System.Text;
using Microsoft.Extensions.Logging;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// The lines players said to a bot that its intents could not read: small talk it
/// could only nod at, or a question it could only shrug at. They are counted by their
/// words (the bot's name left out) and kept with the world, so the keywords of
/// <c>intents.txt</c> grow from what players really say. Console PLAYERBOTS UNHEARD
/// lists the most said.
/// </summary>
public sealed partial class PlayerBotEngine
{
    private sealed class UnheardLine
    {
        public required string Text { get; init; }
        public required string Language { get; set; }
        public required string Kind { get; set; }
        public int Count { get; set; }
        public long LastMs { get; set; }
    }

    private readonly Dictionary<string, UnheardLine> _unheard = new(StringComparer.Ordinal);
    private bool _unheardDirty;

    /// <summary>Most different lines kept; past it the least said (then the oldest) goes.</summary>
    internal const int MaxUnheard = 500;

    private const int MaxUnheardChars = 120;

    /// <summary>Where the unread lines are kept across restarts ("count, language,
    /// kind, line" a line, tab separated, the most said first). Deleting it starts the
    /// count again.</summary>
    public string? UnheardFile { get; set; }

    /// <summary>Lines put to a bot this window that its intents did not read.</summary>
    public long WindowUnheard { get; private set; }

    /// <summary>Different unread lines kept.</summary>
    public int UnheardCount => _unheard.Count;

    /// <summary>A line said to <paramref name="m"/> that it did not understand:
    /// <paramref name="kind"/> is "chat" (read as small talk) or "question" (only known
    /// to be a question).</summary>
    private void NoteUnheard(BotMind m, string text, string lang, string kind, long now)
    {
        string name = FirstName(m.Char).ToLowerInvariant();
        var words = BotIntents.Words(text).Where(w => w != name).ToList();
        if (words.Count == 0) return;
        string key = string.Join(' ', words);
        if (key.Length > MaxUnheardChars) key = key[..MaxUnheardChars].TrimEnd();
        WindowUnheard++;
        _unheardDirty = true;
        if (_unheard.TryGetValue(key, out var seen))
        {
            seen.Count++;
            seen.LastMs = now;
            seen.Language = lang;
            seen.Kind = kind;
            return;
        }
        if (_unheard.Count >= MaxUnheard)
        {
            var least = _unheard.Values.OrderBy(u => u.Count).ThenBy(u => u.LastMs).First();
            _unheard.Remove(least.Text);
        }
        _unheard[key] = new UnheardLine { Text = key, Language = lang, Kind = kind, Count = 1, LastMs = now };
    }

    /// <summary>The kept lines, the most said first.</summary>
    private IEnumerable<UnheardLine> UnheardByCount() =>
        _unheard.Values.OrderByDescending(u => u.Count).ThenByDescending(u => u.LastMs).ThenBy(u => u.Text, StringComparer.Ordinal);

    public int LoadUnheard()
    {
        if (UnheardFile == null || !File.Exists(UnheardFile)) return 0;
        try
        {
            foreach (string line in File.ReadAllLines(UnheardFile, Encoding.UTF8))
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split('\t');
                if (parts.Length != 4 || !int.TryParse(parts[0], out int count) || count <= 0) continue;
                string text = parts[3].Trim();
                if (text.Length == 0 || _unheard.Count >= MaxUnheard) continue;
                _unheard[text] = new UnheardLine { Text = text, Language = parts[1], Kind = parts[2], Count = count };
            }
        }
        catch (IOException ex) { _logger.LogWarning(ex, "[playerbots] could not read {File}", UnheardFile); }
        return _unheard.Count;
    }

    /// <summary>Writes the kept lines when any came since the last write.</summary>
    public void SaveUnheard()
    {
        if (!_unheardDirty || UnheardFile == null) return;
        _unheardDirty = false;
        var sb = new StringBuilder();
        sb.Append("# Lines players said to a bot that intents.txt did not read (PLAYERBOTS UNHEARD).\n")
          .Append("# count<TAB>language<TAB>chat|question<TAB>line - delete the file to start again.\n");
        foreach (var u in UnheardByCount())
            sb.Append(u.Count).Append('\t').Append(u.Language).Append('\t').Append(u.Kind).Append('\t').Append(u.Text).Append('\n');
        try { File.WriteAllText(UnheardFile, sb.ToString(), new UTF8Encoding(false)); }
        catch (IOException ex) { _logger.LogWarning(ex, "[playerbots] could not write {File}", UnheardFile); }
    }

    /// <summary>Console PLAYERBOTS UNHEARD [n]: the <paramref name="n"/> lines said most.</summary>
    public IReadOnlyList<string> UnheardReport(int n)
    {
        if (_unheard.Count == 0) return ["No unread lines yet."];
        var lines = new List<string>
        {
            $"{_unheard.Count} different lines ({_unheard.Values.Sum(u => u.Count)} said); the {Math.Min(n, _unheard.Count)} said most:",
        };
        foreach (var u in UnheardByCount().Take(Math.Max(1, n)))
            lines.Add($"{u.Count,5}  {u.Language,-3} {u.Kind,-8}  {u.Text}");
        return lines;
    }

    /// <summary>Console PLAYERBOTS UNHEARD CLEAR: forgets the kept lines (and the file's).</summary>
    public int ClearUnheard()
    {
        int n = _unheard.Count;
        _unheard.Clear();
        _unheardDirty = true;
        SaveUnheard();
        return n;
    }
}
