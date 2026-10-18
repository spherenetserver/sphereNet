using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>Being talked to. A bot whose name a player says turns to answer; a
/// greeting gets a greeting back from someone in the crowd, or from nobody, which
/// was also how it went. Replies come a moment later, the way people type.</summary>
public sealed partial class PlayerBotEngine
{
    private static readonly string[] Greetings =
        ["hi", "hello", "hey", "hail", "greetings", "yo", "sup", "sa", "selam", "slm", "merhaba", "selamlar", "mrb", "hey"];

    /// <summary>A player said <paramref name="text"/> out loud. Only real players are
    /// answered, so two bots never talk themselves into a loop.</summary>
    public void OnHear(Character speaker, string text)
    {
        if (speaker.IsPlayerBot || speaker.IsDead || string.IsNullOrWhiteSpace(text) || _minds.Count == 0) return;
        long now = NowMs();
        var words = Words(text);
        bool question = text.TrimEnd().EndsWith('?');
        bool greeting = words.Any(w => Greetings.Contains(w));

        BotMind? named = null;
        var near = new List<BotMind>();
        foreach (var ch in _world.GetCharsInRange(speaker.Position, 10))
        {
            if (!ch.IsPlayerBot || ch.IsDead || !_minds.TryGetValue(ch.Uid.Value, out var mind)) continue;
            if (mind.Mode == BotMode.Fight || mind.Mode == BotMode.Flee) continue;
            near.Add(mind);
            if (named == null && words.Contains(ch.Name.ToLowerInvariant().Split(' ')[0]))
                named = mind;
        }
        if (named != null)
        {
            Queue(named, question ? "respond_question" : greeting ? "respond_greet" : "respond_name", now);
            return;
        }
        if (greeting && near.Count > 0 && _rng.Next(100) < 60)
            Queue(near[_rng.Next(near.Count)], "respond_greet", now);
    }

    private void Queue(BotMind mind, string category, long now)
    {
        mind.PendingCategory = category;
        mind.PendingSayMs = now + _rng.Next(900, 3200);
    }

    /// <summary>Called each act: say a queued reply once its typing delay is up.</summary>
    private void SayPending(BotMind mind, long now)
    {
        if (mind.PendingCategory == null || now < mind.PendingSayMs) return;
        string category = mind.PendingCategory;
        mind.PendingCategory = null;
        Speak(mind, category, fallback: "respond_what");
    }

    private static HashSet<string> Words(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string w in text.ToLowerInvariant().Split([' ', ',', '.', '!', '?', ':', ';', '"', '\''],
                     StringSplitOptions.RemoveEmptyEntries))
            set.Add(w);
        return set;
    }
}
