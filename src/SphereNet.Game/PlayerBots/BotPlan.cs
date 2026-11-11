using System.Globalization;

namespace SphereNet.Game.PlayerBots;

/// <summary>What a bot can choose to do next when a stay ends.</summary>
public enum BotActivity { None, Hunt, Work, Sell, Shop, Bank, Rest, Social, Wander, Home }

/// <summary>What a bot wants: each is an urgency from 0 (met) to 1 (pressing), read
/// from its real state when a stay ends.</summary>
public enum BotDrive { Money, Supplies, Safety, Load, Purse, Growth, Social, Adventure }

/// <summary>One step of a day plan: the activity and, when it has one, the place it is
/// done at (a destination name).</summary>
public readonly record struct BotPlanStep(BotActivity Activity, string? Target = null);

/// <summary>
/// A bot's short day plan and the little it remembers for its drives. The steps, the
/// step it is on, why it made the plan and when are kept as the <c>PLAYERBOT.PLAN</c>
/// tag (see <see cref="Write"/>); the rest is runtime only and starts over after a
/// restart.
/// </summary>
public sealed class BotPlan
{
    public List<BotPlanStep> Steps { get; } = [];
    /// <summary>The step the bot is on (the one it last set off to do).</summary>
    public int Step { get; set; }
    /// <summary>The unix minute the plan was made.</summary>
    public long MadeMinute { get; set; }
    /// <summary>The drive that made the plan (its first step's main reason).</summary>
    public BotDrive Why { get; set; }
    /// <summary>The need behind a <see cref="BotDrive.Supplies"/> plan (shopping.txt name).</summary>
    public string? WhyNeed { get; set; }
    /// <summary>The unix minute of its last death (0: none known).</summary>
    public long DeathMinute { get; set; }

    /// <summary>Something took the bot away from its plan (a death, a favour, being
    /// carried off): the next choice starts a new one.</summary>
    public bool Interrupted { get; set; }
    public bool Loaded { get; set; }

    // Drive memory (runtime).
    public long LastSocialMs { get; set; }
    public string? Town { get; set; }
    public long TownSinceMs { get; set; }
    public BotActivity LastActivity { get; set; }
    public int Repeats { get; set; }
    /// <summary>The hunting ground of the hunt in hand, while it lasts.</summary>
    public BotDestination? HuntAt { get; set; }
    /// <summary>The last choice, for the console: drives and the options weighed.</summary>
    public string? LastChoice { get; set; }
    /// <summary>Per activity, until when it is not offered: it would not start lately.</summary>
    public long[] NoStartUntil { get; } = new long[BotPlanBook.ActivityCount];

    public BotPlanStep? Current => Step >= 0 && Step < Steps.Count ? Steps[Step] : null;
    public BotPlanStep? Next => Step + 1 >= 0 && Step + 1 < Steps.Count ? Steps[Step + 1] : null;

    public void Clear()
    {
        Steps.Clear();
        Step = 0;
        WhyNeed = null;
    }

    /// <summary>The tag value: <c>minute|step|why[:need]|act[:target];act...|death minute</c>.</summary>
    public string Write()
    {
        string steps = string.Join(';', Steps.Select(s => s.Target is { Length: > 0 } t ? $"{s.Activity}:{t}" : s.Activity.ToString()));
        string why = WhyNeed is { Length: > 0 } n ? $"{Why}:{n}" : Why.ToString();
        return string.Create(CultureInfo.InvariantCulture, $"{MadeMinute}|{Step}|{why}|{steps}|{DeathMinute}");
    }

    /// <summary>Read a tag written by <see cref="Write"/>; false (nothing changed) when it does not parse.</summary>
    public bool Read(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        var p = raw.Split('|');
        if (p.Length < 4 || !long.TryParse(p[0], out long minute) || !int.TryParse(p[1], out int step)) return false;
        var steps = new List<BotPlanStep>();
        foreach (string s in p[3].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = s.IndexOf(':');
            string act = colon > 0 ? s[..colon] : s;
            if (!Enum.TryParse(act, true, out BotActivity a) || a == BotActivity.None) return false;
            steps.Add(new BotPlanStep(a, colon > 0 ? s[(colon + 1)..] : null));
        }
        string why = p[2];
        int wc = why.IndexOf(':');
        if (!Enum.TryParse(wc > 0 ? why[..wc] : why, true, out BotDrive drive)) drive = BotDrive.Adventure;
        Steps.Clear();
        Steps.AddRange(steps);
        Step = Math.Clamp(step, 0, Math.Max(0, steps.Count - 1));
        MadeMinute = minute;
        Why = drive;
        WhyNeed = wc > 0 ? why[(wc + 1)..] : null;
        DeathMinute = p.Length > 4 && long.TryParse(p[4], out long d) ? d : 0;
        return true;
    }
}

/// <summary>
/// The planner's tunables (<c>playerbots/plans.txt</c>): how much each kind of bot
/// cares about each drive, what each activity does for each drive, the activities'
/// base worth, the gold each class likes to have, rough prices of supplies, and the
/// knobs of the choice itself. Lines:
/// <code>
/// weight &lt;who&gt; &lt;drive&gt; &lt;factor&gt;     who: * mage archer warrior crafter tradesman bold timid trait.&lt;t&gt; role.&lt;r&gt;
/// does &lt;activity&gt; &lt;drive&gt; &lt;how well&gt; (negative: it makes the drive worse, or wants it met first)
/// base &lt;activity&gt; &lt;utility&gt;
/// keep &lt;class&gt; &lt;gold&gt;
/// price &lt;need&gt; &lt;gold each&gt;
/// need &lt;need&gt; &lt;weight&gt; [&lt;weight with another answer&gt;]
/// forbid &lt;who&gt; &lt;activity&gt;
/// set &lt;name&gt; &lt;value&gt;
/// </code>
/// A file missing a line keeps the built-in value (<see cref="Defaults"/>).
/// </summary>
public sealed class BotPlanBook
{
    public static readonly int ActivityCount = Enum.GetValues<BotActivity>().Length;
    public static readonly int DriveCount = Enum.GetValues<BotDrive>().Length;

    private readonly double[,] _does = new double[ActivityCount, DriveCount];
    private readonly double[] _base = new double[ActivityCount];
    private readonly List<(string Who, BotDrive Drive, double Factor)> _weights = [];
    private readonly Dictionary<string, int> _keep = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _price = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Alone, double Covered)> _need = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Who, BotActivity Activity)> _forbid = [];

    /// <summary>Utility lost per 100 tiles of road to an activity's place.</summary>
    public double Travel { get; private set; } = 0.06;
    /// <summary>How much a hunting ground's deaths weigh against it.</summary>
    public double Risk { get; private set; } = 0.6;
    /// <summary>How much a hunting ground's fit to the bot's strength counts.</summary>
    public double Fit { get; private set; } = 0.4;
    /// <summary>Softmax temperature of the pick among the best options.</summary>
    public double Temperature { get; private set; } = 0.08;
    /// <summary>How many of the best options the pick is among.</summary>
    public int Top { get; private set; } = 3;
    /// <summary>Options this far below the best are not picked.</summary>
    public double Margin { get; private set; } = 0.2;
    /// <summary>Hysteresis: bonus to the plan's next step.</summary>
    public double Stick { get; private set; } = 0.15;
    /// <summary>A drive this pressing that the planned step does nothing for re-plans.</summary>
    public double Urgent { get; private set; } = 0.75;
    public int MinSteps { get; private set; } = 2;
    public int MaxSteps { get; private set; } = 4;
    /// <summary>Minutes a plan is kept before a new one is made.</summary>
    public int StaleMinutes { get; private set; } = 90;
    /// <summary>Seconds without company before the social drive is full.</summary>
    public int SocialSeconds { get; private set; } = 1200;
    /// <summary>Seconds in one town before the adventure drive is full.</summary>
    public int BoredomSeconds { get; private set; } = 2400;
    /// <summary>Utility of going home for a bot away from it.</summary>
    public double Homesick { get; private set; } = 0.35;
    /// <summary>How much worse a drive an activity makes worse gets, when a plan is strung.</summary>
    public double Wear { get; private set; } = 0.35;
    /// <summary>Gold carried at which the purse drive is full (it is felt from the bank
    /// errand's deposit mark, PlayerBotEngine.BankDepositAbove).</summary>
    public int PurseFull { get; private set; } = 1500;
    /// <summary>Seconds an activity that would not start is not offered again.</summary>
    public int FailRestSeconds { get; private set; } = 600;

    /// <summary>The built-in values; plans.txt as shipped says the same.</summary>
    public static readonly string[] Defaults =
    [
        "weight mage supplies 1.2",
        "weight warrior adventure 1.2",
        "weight archer adventure 1.1",
        "weight crafter adventure 0.5",
        "weight tradesman growth 1.1",
        "weight bold safety 0.6",
        "weight bold adventure 1.3",
        "weight timid safety 1.5",
        "weight timid adventure 0.7",
        "weight trait.chatty social 1.8",
        "weight trait.kind social 1.2",
        "weight trait.shy social 0.4",
        "weight trait.stingy money 1.4",
        "weight trait.stingy purse 1.3",
        "weight role.thief social 1.6",
        "does hunt adventure 1.0", "does hunt growth 0.7", "does hunt money 0.5", "does hunt supplies -1.2",
        "does hunt safety -1.0", "does hunt load -0.9", "does hunt purse -0.3",
        "does work money 0.7", "does work growth 0.8", "does work adventure 0.15", "does work load -0.7",
        "does work safety -0.2",
        "does sell load 1.0", "does sell money 0.5",
        "does shop supplies 1.0",
        "does bank purse 1.0", "does bank load 0.1",
        "does rest safety 1.0", "does rest social 0.3",
        "does social social 1.0", "does social adventure 0.2",
        "does wander adventure 0.35", "does wander social 0.2",
        "does home safety 0.3", "does home social 0.3",
        "base hunt 0.05", "base work 0.15", "base social 0.1", "base wander 0.12",
        "keep mage 500", "keep warrior 400", "keep archer 400", "keep crafter 300",
        "price reagent 5", "price bandage 2", "price arrow 2", "price bolt 2", "price rune 15", "price food 6",
        "price heal 30", "price cure 30", "price refresh 20",
        "need reagent 1.0", "need bandage 0.9 0.6", "need arrow 1.0 0.4", "need bolt 1.0 0.4",
        "need heal 0.7 0.3", "need cure 0.5 0.25", "need refresh 0.2", "need rune 0.3", "need food 0.4",
        "need ingot 0.6", "need cloth 0.6", "need board 0.6",
        "forbid role.thief hunt", "forbid role.thief work",
    ];

    public static BotPlanBook Default()
    {
        var book = new BotPlanBook();
        book.Apply(Defaults);
        return book;
    }

    /// <summary>The built-in values with <c>plans.txt</c> read over them.</summary>
    public static BotPlanBook Load(string dataDir)
    {
        var book = Default();
        string path = Path.Combine(dataDir, "plans.txt");
        if (File.Exists(path)) book.Apply(File.ReadAllLines(path));
        return book;
    }

    /// <summary>Lines that were not understood (for the load log and the tests).</summary>
    public List<string> Rejected { get; } = [];

    public void Apply(IEnumerable<string> lines)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            // A comment may close a line too.
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            var p = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!ApplyLine(p)) Rejected.Add(line);
        }
    }

    private static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private bool ApplyLine(string[] p)
    {
        switch (p[0].ToLowerInvariant())
        {
            case "weight" when p.Length == 4 && Enum.TryParse(p[2], true, out BotDrive d) && Num(p[3], out double f):
                _weights.RemoveAll(w => w.Who.Equals(p[1], StringComparison.OrdinalIgnoreCase) && w.Drive == d);
                _weights.Add((p[1], d, f));
                return true;
            case "does" when p.Length == 4 && Enum.TryParse(p[1], true, out BotActivity a) && Enum.TryParse(p[2], true, out BotDrive dd) &&
                             Num(p[3], out double how):
                _does[(int)a, (int)dd] = how;
                return true;
            case "base" when p.Length == 3 && Enum.TryParse(p[1], true, out BotActivity ba) && Num(p[2], out double b):
                _base[(int)ba] = b;
                return true;
            case "keep" when p.Length == 3 && int.TryParse(p[2], out int gold):
                _keep[p[1]] = Math.Max(1, gold);
                return true;
            case "price" when p.Length == 3 && int.TryParse(p[2], out int price):
                _price[p[1]] = Math.Max(0, price);
                return true;
            case "need" when p.Length is 3 or 4 && Num(p[2], out double alone) &&
                             (p.Length == 3 || Num(p[3], out _)):
                double covered = p.Length == 4 && Num(p[3], out double c) ? c : alone;
                _need[p[1]] = (Math.Clamp(alone, 0, 1), Math.Clamp(covered, 0, 1));
                return true;
            case "forbid" when p.Length == 3 && Enum.TryParse(p[2], true, out BotActivity fa):
                if (!_forbid.Contains((p[1], fa))) _forbid.Add((p[1], fa));
                return true;
            case "set" when p.Length == 3 && Num(p[2], out double v):
                return Set(p[1], v);
        }
        return false;
    }

    private bool Set(string name, double v)
    {
        switch (name.ToLowerInvariant())
        {
            case "travel": Travel = v; break;
            case "risk": Risk = v; break;
            case "fit": Fit = v; break;
            case "temperature": Temperature = Math.Max(0.001, v); break;
            case "top": Top = Math.Max(1, (int)v); break;
            case "margin": Margin = Math.Max(0, v); break;
            case "stick": Stick = v; break;
            case "urgent": Urgent = v; break;
            case "minsteps": MinSteps = Math.Max(1, (int)v); break;
            case "maxsteps": MaxSteps = Math.Max(1, (int)v); break;
            case "stale": StaleMinutes = Math.Max(1, (int)v); break;
            case "social": SocialSeconds = Math.Max(1, (int)v); break;
            case "boredom": BoredomSeconds = Math.Max(1, (int)v); break;
            case "homesick": Homesick = v; break;
            case "wear": Wear = v; break;
            case "pursefull": PurseFull = Math.Max(1, (int)v); break;
            case "failrest": FailRestSeconds = Math.Max(0, (int)v); break;
            default: return false;
        }
        if (MaxSteps < MinSteps) MaxSteps = MinSteps;
        return true;
    }

    public double Does(BotActivity a, BotDrive d) => _does[(int)a, (int)d];
    public double Base(BotActivity a) => _base[(int)a];

    /// <summary>The product of the weights of every <paramref name="who"/> the bot is, for one drive.</summary>
    public double Weight(BotDrive d, IReadOnlyCollection<string> who)
    {
        double f = 1.0;
        foreach (var (w, drive, factor) in _weights)
            if (drive == d && (w == "*" || who.Contains(w))) f *= factor;
        return f;
    }

    public bool Forbids(BotActivity a, IReadOnlyCollection<string> who)
    {
        foreach (var (w, act) in _forbid)
            if (act == a && (w == "*" || who.Contains(w))) return true;
        return false;
    }

    /// <summary>Gold a class likes to have, in the pack and the bank together.</summary>
    public int Keep(string cls) => _keep.TryGetValue(cls, out int g) ? g : 300;

    /// <summary>A rough price of one of a need (shopping.txt name).</summary>
    public int Price(string need) => _price.TryGetValue(need, out int g) ? g : 10;

    /// <summary>How much a need matters (shopping.txt name): alone, and when the bot has
    /// another answer for it. A need with no <c>need</c> line weighs by its kind: a trade's
    /// tool or material 0.8, a potion 0.3, a rune 0.3, food 0.4, supplies 1.0.</summary>
    public (double Alone, double Covered) Need(string name, Core.Enums.ItemType type)
    {
        if (_need.TryGetValue(name, out var w)) return w;
        double d = type switch
        {
            Core.Enums.ItemType.Bandage or Core.Enums.ItemType.Reagent or Core.Enums.ItemType.WeaponArrow or
                Core.Enums.ItemType.WeaponBolt => 1.0,
            Core.Enums.ItemType.Potion or Core.Enums.ItemType.Rune => 0.3,
            Core.Enums.ItemType.Food => 0.4,
            _ => 0.8,
        };
        return (d, d);
    }
}
