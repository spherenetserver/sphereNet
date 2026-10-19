using System.Text;

namespace SphereNet.Game.PlayerBots;

/// <summary>One line of a scene: who says it (A or B), which chat category (one of
/// several, chosen at random) and, for haggling, the price it names as a percentage
/// of the scene's asking price (0: unchanged).</summary>
public sealed record BotSceneStep(char Speaker, string[] Categories, int PricePct);

/// <summary>A little exchange bots act out where players can hear it.</summary>
public sealed record BotSceneDef(string Name, int Weight, string[] At, bool Night, bool Day, bool Fighter,
    bool Caster, string[] Events, BotSceneStep[] Steps)
{
    public bool NeedsTwo => Steps.Any(s => s.Speaker == 'B');
}

/// <summary>
/// The scene list (data file <c>scenes.txt</c>). One scene per line:
/// <code>name weight [condition ...] : A:category &gt; B:category/category*70 &gt; ...</code>
/// Conditions: <c>at=Bank|Inn</c> (where A stands), <c>night</c> / <c>day</c> (server
/// clock, night 21:00-05:00), <c>fighter</c> / <c>caster</c> (what A is),
/// <c>event=pk|death</c> (needs real news of that kind; its actor, other and place fill
/// the tokens). <c>$place</c> as a category is what is said where A stands.
/// <c>*N</c> names N percent of the scene's asking price.
/// </summary>
public sealed class BotSceneBook
{
    public List<BotSceneDef> Scenes { get; } = [];

    public static BotSceneBook Load(string dataDir)
    {
        string path = Path.Combine(dataDir, "scenes.txt");
        return File.Exists(path) ? Parse(File.ReadAllLines(path, Encoding.UTF8)) : new BotSceneBook();
    }

    public static BotSceneBook Parse(IEnumerable<string> lines)
    {
        var book = new BotSceneBook();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon <= 0) continue;
            var head = line[..colon].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (head.Length < 2 || !int.TryParse(head[1], out int weight) || weight <= 0) continue;
            string[] at = [], events = [];
            bool night = false, day = false, fighter = false, caster = false;
            foreach (string cond in head.Skip(2))
            {
                if (cond.StartsWith("at=", StringComparison.OrdinalIgnoreCase)) at = cond[3..].Split('|');
                else if (cond.StartsWith("event=", StringComparison.OrdinalIgnoreCase)) events = cond[6..].Split('|');
                else if (cond.Equals("night", StringComparison.OrdinalIgnoreCase)) night = true;
                else if (cond.Equals("day", StringComparison.OrdinalIgnoreCase)) day = true;
                else if (cond.Equals("fighter", StringComparison.OrdinalIgnoreCase)) fighter = true;
                else if (cond.Equals("caster", StringComparison.OrdinalIgnoreCase)) caster = true;
            }
            var steps = new List<BotSceneStep>();
            foreach (string part in line[(colon + 3)..].Split('>', StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (s.Length < 3 || s[1] != ':' || (s[0] != 'A' && s[0] != 'B')) { steps.Clear(); break; }
                string body = s[2..];
                int pct = 0;
                int star = body.IndexOf('*');
                if (star >= 0)
                {
                    int.TryParse(body[(star + 1)..], out pct);
                    body = body[..star];
                }
                var cats = body.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (cats.Length == 0) { steps.Clear(); break; }
                steps.Add(new BotSceneStep(s[0], cats, pct));
            }
            if (steps.Count == 0 || steps[0].Speaker != 'A') continue;
            book.Scenes.Add(new BotSceneDef(head[0], weight, at, night, day, fighter, caster, events, [.. steps]));
        }
        return book;
    }
}

/// <summary>A scene being acted out: who plays A and B, which line is next and the
/// tokens every line shares (the goods, the price, the news).</summary>
public sealed class BotScene
{
    public required BotSceneDef Def { get; init; }
    public required BotMind A { get; init; }
    public BotMind? B { get; init; }
    public required string Language { get; init; }
    public Dictionary<string, string> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int BasePrice { get; init; }
    public BotGossipEvent? Event { get; init; }
    public int Step { get; set; }
    public long NextMs { get; set; }
}
