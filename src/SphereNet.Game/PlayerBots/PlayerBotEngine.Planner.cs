using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Trade;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// What a bot does next, when a stay ends: a choice a person would make, from what it
/// needs. Its drives - money, supplies, safety, a heavy pack, gold to bank, skill to
/// grow, company, something new - are read from its real state (gold carried and
/// banked, what it is short of, hit points and mana, a recent death or fight, goods and
/// weight, its main skill, time since it was among people, time in one town). Each
/// activity it could take up (hunt, work, sell, shop, bank, rest, meet people, look
/// around, go home) is scored: what it does for each drive (<c>plans.txt</c>) times how
/// pressing that drive is times how much this kind of bot cares, less the road to it
/// and, for a hunt, the risk of the ground. One of the best few is picked (a softmax, so
/// bots differ), and two to four activities are strung into a day plan
/// (<c>PLAYERBOT.PLAN</c>) that the next stays follow - unless something more pressing
/// turns up, or the plan was interrupted (a death, a favour, a companion, being carried
/// off). The existing parts carry each activity out: the Work and Sell parts, the
/// shopping of the Supplies part, the bank errand, the trip.
///
/// Cost: the planner runs only where a stay ends (and on a console or chat question),
/// never per tick. One choice reads the pack once or twice, scans the destination list
/// once, and asks the road graph for distances from fields kept per target node (a
/// Dijkstra over the road nodes, at most two new ones per choice).
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string PlanTag = "PLAYERBOT.PLAN";
    /// <summary>Hunting grounds the bot died at lately: <c>name:minute</c>, <c>;</c>-joined, the last 4.</summary>
    public const string DreadTag = "PLAYERBOT.DREAD";

    /// <summary>The planner's tunables (<c>plans.txt</c>).</summary>
    public BotPlanBook Plans { get; set; } = BotPlanBook.Default();

    public void LoadPlans(string dataDir)
    {
        Plans = BotPlanBook.Load(dataDir);
        foreach (string bad in Plans.Rejected)
            _logger.LogWarning("[playerbots] plans.txt: line not understood: {Line}", bad);
    }

    private static readonly string[] SocialTypes = ["Bank", "Inn", "Tavern", "Healer"];
    private static readonly string[] RestTypes = ["Inn", "Tavern", "Healer"];
    private static readonly string[] HuntTypes = ["DungeonEntrance", "AdventureArea"];
    /// <summary>How long a hunting ground a bot died at is dreaded (minutes).</summary>
    private const long DreadMinutes = 4320;

    // Telemetry
    public long WindowPlansMade { get; private set; }
    public long WindowPlansKept { get; private set; }
    public long WindowPlansUrgent { get; private set; }
    public long WindowPlanInterrupts { get; private set; }
    public long WindowPlanFails { get; private set; }
    private readonly long[] _windowActivity = new long[BotPlanBook.ActivityCount];

    private void ResetPlanWindow()
    {
        WindowPlansMade = WindowPlansKept = WindowPlansUrgent = WindowPlanInterrupts = WindowPlanFails = 0;
        Array.Clear(_windowActivity);
    }

    /// <summary>The 30-second line: plans made, steps kept, urgent and interrupted
    /// re-plans, activities that would not start, and the mix of activities taken up.</summary>
    public string PlanMixReport()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"made={WindowPlansMade} kept={WindowPlansKept} urgent={WindowPlansUrgent} interrupted={WindowPlanInterrupts} failed={WindowPlanFails} mix");
        for (int i = 1; i < _windowActivity.Length; i++)
            sb.Append(' ').Append(((BotActivity)i).ToString().ToLowerInvariant()).Append('=').Append(_windowActivity[i]);
        return sb.ToString();
    }

    /// <summary>One thing the bot could do now, with what it is worth to it.</summary>
    internal sealed class BotOption
    {
        public BotActivity Activity { get; init; }
        public BotDestination? Target { get; init; }
        /// <summary>The part of the utility that is not the drives: road, fit, risk.</summary>
        public double Extra { get; init; }
        public double Utility { get; set; }
        public string? Note { get; init; }
    }

    /// <summary>What the bot is and has, read once per choice.</summary>
    internal sealed class BotSituation
    {
        public double[] Drives { get; } = new double[BotPlanBook.DriveCount];
        public double[] Weights { get; } = new double[BotPlanBook.DriveCount];
        public HashSet<string> Who { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Class { get; set; } = "crafter";
        public long PackGold { get; set; }
        public long BankGold { get; set; }
        public long Spendable { get; set; }
        public BotNeed? Need { get; set; }
        /// <summary>The need is something it fights with: only then does it hold a hunt back.</summary>
        public bool FightNeed { get; set; }
        public int NeedCost { get; set; }
        /// <summary>Short of something, with the gold for it in the bank but not on it.</summary>
        public bool Fund { get; set; }
        public bool Goods { get; set; }
        public long GoodsWorth { get; set; }
        public int LoadPercent { get; set; }
        public bool Tradesman { get; set; }
        public int Node { get; set; } = -1;
        public int Component { get; set; } = -1;
        public string Town { get; set; } = "";
    }

    // ------------------------------------------------------------------
    //  The plan in hand
    // ------------------------------------------------------------------

    /// <summary>The bot's plan, read from its tag the first time it is asked for.</summary>
    internal BotPlan PlanOf(BotMind mind, long now)
    {
        var plan = mind.Plan;
        if (plan.Loaded) return plan;
        plan.Loaded = true;
        if (mind.Char.TryGetTag(PlanTag, out string? raw)) plan.Read(raw);
        plan.LastSocialMs = now;
        plan.TownSinceMs = now;
        return plan;
    }

    private static void SavePlan(BotMind mind) => mind.Char.SetTag(PlanTag, mind.Plan.Write());

    /// <summary>A bot taken back from the save that was on its way to a plan's step: the
    /// trip goes on as that step (a shop's need, a hunt's ground).</summary>
    private void ResumePlan(BotMind mind)
    {
        var plan = PlanOf(mind, NowMs());
        if (mind.Goal is not { } goal || plan.Current is not { } cur ||
            !string.Equals(cur.Target, goal.Name, StringComparison.OrdinalIgnoreCase))
            return;
        if (cur.Activity == BotActivity.Shop) mind.ShoppingFor = FirstNeed(mind, buyable: true);
        if (cur.Activity == BotActivity.Hunt) plan.HuntAt = goal;
    }

    /// <summary>Something took the bot away from its plan - a death, a favour, a
    /// companion, being carried off: the next choice starts a new plan.</summary>
    internal void InterruptPlan(BotMind mind, long now, string why)
    {
        var plan = PlanOf(mind, now);
        if (why == "died") plan.DeathMinute = UnixMinute();
        if (plan.Steps.Count == 0 && why != "died") return;
        if (!plan.Interrupted) mind.Note(now, $"plan interrupted: {why}");
        plan.Interrupted = true;
        plan.Clear();
        plan.HuntAt = null;
        SavePlan(mind);
    }

    /// <summary>Called every act: a favour, a companion or a player led stands in for
    /// the plan, which is dropped.</summary>
    private void NotePlanBusy(BotMind mind, long now)
    {
        if (mind.Plan.Steps.Count > 0 && (mind.Favor != null || mind.Companion != null || mind.Guiding != null))
            InterruptPlan(mind, now, mind.Favor != null ? "a favour" : mind.Companion != null ? "a companion" : "a guide");
    }

    // ------------------------------------------------------------------
    //  Drives
    // ------------------------------------------------------------------

    private static string ClassOf(BotMind mind) =>
        mind.IsCaster ? "mage" : mind.IsArcher ? "archer" : mind.IsFighter ? "warrior" : "crafter";

    /// <summary>The town a bot is in: its place's, else the nearest town place's within 60 tiles.</summary>
    private string TownAt(BotMind mind)
    {
        if (mind.At is { City.Length: > 0 } at) return at.City;
        var pos = mind.Char.Position;
        if (IsUnderground(pos)) return "";
        string best = "";
        int bestD = 61;
        foreach (var d in Nav.Destinations)
        {
            if (d.City.Length == 0 || d.Pos.Map != pos.Map) continue;
            int dd = d.Pos.GetDistanceTo(pos);
            if (dd < bestD) { bestD = dd; best = d.City; }
        }
        return best;
    }

    private static readonly SkillType[] FightSkills =
        [SkillType.Swordsmanship, SkillType.MaceFighting, SkillType.Fencing, SkillType.Archery, SkillType.Magery, SkillType.Wrestling];

    /// <summary>What the bot is and wants right now.</summary>
    internal BotSituation Situation(BotMind mind, long now)
    {
        var ch = mind.Char;
        var plan = PlanOf(mind, now);
        var s = new BotSituation { Class = ClassOf(mind) };
        s.Who.Add(s.Class);
        s.Tradesman = IsTradesman(mind);
        if (s.Tradesman) s.Who.Add("tradesman");
        s.Who.Add("trait." + TraitOf(mind));
        string role = RoleOf(mind);
        if (role != "-") s.Who.Add("role." + role);
        if (mind.Nerve >= 1.45) s.Who.Add("bold");
        else if (mind.Nerve < 1.05) s.Who.Add("timid");

        s.PackGold = ch.Backpack is { } pack ? Objects.Items.ResourceMatch.Count(pack, Objects.Items.ResourceMatch.Gold) : 0;
        s.BankGold = ch.GetEquippedItem(Layer.BankBox) is { } box ? Objects.Items.ResourceMatch.Count(box, Objects.Items.ResourceMatch.Gold) : 0;
        // What a vendor takes: the pack, and the bank too unless purchases are paid from the pack only.
        s.Spendable = VendorEngine.CountGold(ch);
        var pos = ch.Position;
        s.Node = Nav.FindNearest(pos, 24);
        if (s.Node < 0) s.Node = Nav.FindNearest(pos, 64);
        if (s.Node >= 0) s.Component = Nav.Waypoints[s.Node].Component;
        s.Town = TownAt(mind);
        if (!string.Equals(s.Town, plan.Town, StringComparison.OrdinalIgnoreCase))
        {
            plan.Town = s.Town;
            plan.TownSinceMs = now;
        }
        // Among people now: a player near, or a crowd at the place.
        if (IsWatched(pos) || (mind.At != null && CrowdAt(mind.At, now) >= 3)) plan.LastSocialMs = now;

        var d = s.Drives;
        // Supplies: what it fights or works with and is short of.
        s.Need = _needs.Count > 0 ? FirstNeed(mind, buyable: true) : null;
        if (s.Need is { } need)
        {
            int have = NeedHave(mind, need);
            // How much the thing matters to this bot (a refresh potion to a warrior who
            // has bandages and Healing hardly), times how short of it it is.
            d[(int)BotDrive.Supplies] = NeedSeverity(mind, need) *
                                        (0.5 + 0.5 * (1 - Math.Clamp(have / (double)Math.Max(1, need.Low), 0, 1)));
            // Only what it fights with holds a hunt back: a tool, a material, food or
            // a blank rune does not.
            s.FightNeed = need.Type is ItemType.Bandage or ItemType.Reagent or ItemType.WeaponArrow or
                ItemType.WeaponBolt or ItemType.Potion;
            s.NeedCost = Plans.Price(need.Name) * Math.Max(1, need.Buy);
            // Short of gold to hand that the bank holds: the gold is fetched first.
            s.Fund = s.Spendable < Math.Max(20, s.NeedCost / 2) && s.BankGold >= 20;
        }
        // Money: gold to hand and banked against what this kind of bot likes to have,
        // and what it is about to spend.
        long want = Plans.Keep(s.Class) + s.NeedCost;
        long have2 = s.PackGold + s.BankGold;
        d[(int)BotDrive.Money] = Math.Clamp((want - have2) / (double)want, 0, 1);
        // Safety: wounds, a caster's mana, a recent death or fight.
        double hp = ch.MaxHits > 0 ? ch.Hits / (double)ch.MaxHits : 1;
        double safety = Math.Clamp((0.9 - hp) / 0.6, 0, 1);
        if (mind.IsCaster && ch.MaxMana > 0 && ch.Mana * 10 < ch.MaxMana * 4)
            safety = Math.Max(safety, 0.5 * (0.4 - ch.Mana / (double)ch.MaxMana) / 0.4 + 0.2);
        if (plan.DeathMinute > 0)
        {
            long ago = UnixMinute() - plan.DeathMinute;
            if (ago is >= 0 and < 30) safety += 0.6 * (1 - ago / 30.0);
        }
        if (mind.FightStartedMs > 0 && now - mind.FightStartedMs < 90_000) safety += 0.1;
        d[(int)BotDrive.Safety] = Math.Clamp(safety, 0, 1);
        // Load: goods to sell, a heavy pack.
        s.LoadPercent = PackLoadPercent(ch);
        if (mind.Client != null && _saleRules.Count > 0)
        {
            var goods = GoodsToSell(mind);
            foreach (var item in goods) s.GoodsWorth += Worth(item);
            s.Goods = goods.Count > 0 && (goods.Count >= SaleCountMin || s.GoodsWorth >= SaleWorthMin || s.LoadPercent >= 60);
        }
        double load = s.Goods ? 0.5 + Math.Min(0.5, s.GoodsWorth / 400.0) : 0;
        if (s.LoadPercent > 50) load = Math.Max(load, Math.Min(1, (s.LoadPercent - 50) / 40.0) * 0.8);
        d[(int)BotDrive.Load] = Math.Clamp(load, 0, 1);
        // Purse: more gold carried than is wise. Felt from what the bank errand puts
        // away (BankDepositAbove) and full at plans.txt's `pursefull`: a person with a
        // thousand gold on them thinks of the bank before the next dungeon.
        if (s.PackGold > BankDepositAbove)
            d[(int)BotDrive.Purse] = 0.3 + 0.7 * Math.Clamp((s.PackGold - BankDepositAbove) /
                                                            (double)Math.Max(1, Plans.PurseFull - BankDepositAbove), 0, 1);
        // Growth: its main skill against the top.
        int main = 0;
        if (mind.IsFighter)
            foreach (var sk in FightSkills) main = Math.Max(main, ch.GetSkill(sk));
        else if (s.Tradesman)
        {
            foreach (var g in _gatherTrades) main = Math.Max(main, ch.GetSkill(g.Skill));
            foreach (var c in _craftTrades) main = Math.Max(main, ch.GetSkill(c.Skill));
        }
        d[(int)BotDrive.Growth] = mind.IsFighter || s.Tradesman ? Math.Clamp((1000 - main) / 800.0, 0, 1) : 0;
        // Company and something new.
        d[(int)BotDrive.Social] = Math.Clamp((now - plan.LastSocialMs) / (Plans.SocialSeconds * 1000.0), 0, 1);
        d[(int)BotDrive.Adventure] = Math.Clamp((now - plan.TownSinceMs) / (Plans.BoredomSeconds * 1000.0) + 0.15 * plan.Repeats, 0, 1);

        for (int i = 0; i < s.Weights.Length; i++) s.Weights[i] = Plans.Weight((BotDrive)i, s.Who);
        return s;
    }

    // ------------------------------------------------------------------
    //  Options
    // ------------------------------------------------------------------

    /// <summary>What an activity is worth with these drives: its base, plus what it
    /// does for each drive times how pressing the drive is times how much the bot cares,
    /// plus the part that is not the drives.</summary>
    private double Utility(BotActivity a, double[] drives, BotSituation s, double extra)
    {
        double u = Plans.Base(a) + extra;
        for (int i = 0; i < drives.Length; i++)
        {
            // A hunt waits for what it fights with, not for a tool or a blank rune.
            if (a == BotActivity.Hunt && i == (int)BotDrive.Supplies && !s.FightNeed) continue;
            u += drives[i] * s.Weights[i] * Plans.Does(a, (BotDrive)i);
        }
        // With a pack full of goods the bank waits for the takings: one trip for all of it.
        if (a == BotActivity.Bank && drives[(int)BotDrive.Load] >= 0.5)
            u -= 0.5 * drives[(int)BotDrive.Purse] * s.Weights[(int)BotDrive.Purse] * Plans.Does(a, BotDrive.Purse);
        // The bank is where the gold for what it is short of is.
        if (a == BotActivity.Bank && s.Fund)
            u += drives[(int)BotDrive.Supplies] * s.Weights[(int)BotDrive.Supplies] * 0.9;
        return u;
    }

    private double TravelCost(int tiles) => tiles <= 0 ? 0 : Plans.Travel * tiles / 100.0;

    private bool OnRoad(BotDestination d, BotSituation s, Point3D pos) =>
        d.Pos.Map == pos.Map && (s.Component < 0 || Nav.ComponentOf(d) == s.Component) && IsUsable(d);

    /// <summary>Everything the bot could take up now, scored.</summary>
    internal List<BotOption> Options(BotMind mind, BotSituation s, long now)
    {
        var ch = mind.Char;
        var pos = ch.Position;
        var list = new List<BotOption>();
        var plan = mind.Plan;
        void Add(BotActivity a, BotDestination? target = null, double extra = 0, string? note = null)
        {
            if (Plans.Forbids(a, s.Who)) return;
            // An activity that would not start a little while ago is not offered again yet.
            if (a != BotActivity.Wander && now < plan.NoStartUntil[(int)a]) return;
            list.Add(new BotOption
            {
                Activity = a, Target = target, Extra = extra, Note = note,
                Utility = Utility(a, s.Drives, s, extra),
            });
        }

        // Hunting: the ground that fits it best, near enough.
        if (mind.IsFighter && !Plans.Forbids(BotActivity.Hunt, s.Who) && s.Node >= 0)
        {
            int budget = 2;
            BotDestination? best = null;
            double bestTerm = double.MinValue;
            string? bestNote = null;
            var power = PowerOf(mind, now);
            foreach (var d in Nav.Destinations)
            {
                if (!IsOfType(d, HuntTypes) || !OnRoad(d, s, pos) || d == mind.At) continue;
                int road = RoadDistance(s.Node, d, ref budget);
                if (road < 0) continue;
                var (fit, risk, why) = HuntFit(mind, d, power);
                double term = Plans.Fit * fit - risk - TravelCost(road);
                if (term > bestTerm)
                {
                    bestTerm = term;
                    best = d;
                    bestNote = $"{why}, road {road}";
                }
            }
            if (best != null) Add(BotActivity.Hunt, best, bestTerm, bestNote);
        }
        // Work and selling: the Work and Sell parts find the place.
        if (s.Tradesman && CanWork(mind)) Add(BotActivity.Work);
        if (s.Goods && MayWork(mind)) Add(BotActivity.Sell, note: $"goods worth {s.GoodsWorth}");
        // Shopping: with the gold on it.
        if (s.Need is { } need && s.Spendable >= 20 && FindShop(mind, need, now, note: false) is { } shop)
            Add(BotActivity.Shop, shop, -TravelCost(shop.Pos.GetDistanceTo(pos)), need.Name);
        // The bank: gold to put in, or the gold for a purchase to take out.
        if ((s.Drives[(int)BotDrive.Purse] > 0 || s.Fund) && !IsPk(mind) && NearestOfType(mind, s, ["Bank"], townFirst: true) is { } bank)
            Add(BotActivity.Bank, bank, -TravelCost(bank.Pos.GetDistanceTo(pos)), s.Fund ? "for the gold to buy with" : null);
        // A rest where it can sit and be looked after.
        if (s.Drives[(int)BotDrive.Safety] > 0.05 && RestPlace(mind, s) is { } inn)
            Add(BotActivity.Rest, inn, -TravelCost(inn.Pos.GetDistanceTo(pos)));
        // Company, a look about, home.
        Add(BotActivity.Social);
        Add(BotActivity.Wander);
        if (mind.Home.Length > 0 && s.Town.Length > 0 && !s.Town.Equals(mind.Home, StringComparison.OrdinalIgnoreCase))
            Add(BotActivity.Home, extra: Plans.Homesick, note: $"away in {s.Town}");
        list.Sort((a, b) => b.Utility.CompareTo(a.Utility));
        return list;
    }

    private static bool IsOfType(BotDestination d, string[] types)
    {
        foreach (string t in types)
            if (t.Equals(d.Type, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The nearest usable place of the types on the bot's road; its own town's
    /// first when <paramref name="townFirst"/>.</summary>
    private BotDestination? NearestOfType(BotMind mind, BotSituation s, string[] types, bool townFirst, int range = 400)
    {
        var pos = mind.Char.Position;
        BotDestination? best = null;
        int bestD = int.MaxValue;
        foreach (var d in Nav.Destinations)
        {
            if (!IsOfType(d, types) || !OnRoad(d, s, pos)) continue;
            int dd = d.Pos.GetDistanceTo(pos);
            if (dd > range) continue;
            if (townFirst && s.Town.Length > 0 && !d.City.Equals(s.Town, StringComparison.OrdinalIgnoreCase)) dd += 200;
            if (dd < bestD) { bestD = dd; best = d; }
        }
        return best;
    }

    /// <summary>Where it rests: here when it stands at an inn, a tavern or a healer's,
    /// else the nearest such place of the town.</summary>
    private BotDestination? RestPlace(BotMind mind, BotSituation s) =>
        mind.At is { } at && IsOfType(at, RestTypes) ? at : NearestOfType(mind, s, RestTypes, townFirst: true);

    /// <summary>Where a shop selling the need stands on the bot's road (the nearest
    /// not known to be out of it, with a stopping spot left); none further than
    /// <see cref="NeedShopRange"/>, and blank runes are not worth crossing the land for.</summary>
    private BotDestination? FindShop(BotMind mind, BotNeed need, long now, bool note)
    {
        int here = Nav.FindNearest(mind.Char.Position, 64);
        if (here < 0) return null;
        int component = Nav.Waypoints[here].Component;
        BotDestination? shop = null;
        int best = int.MaxValue;
        foreach (var d in Nav.Destinations)
        {
            if (Nav.ComponentOf(d) != component || d.City.Length == 0 || !IsOfType(d, need.Places) ||
                (d.Arrivals.Count > 0 && d.BadArrivals.Count >= d.Arrivals.Count) || SoldOut(d, need, now))
                continue;
            int dd = d.Pos.GetDistanceTo(mind.Char.Position);
            if (dd < best) { best = dd; shop = d; }
        }
        if (shop == null)
        {
            if (note) mind.Note(now, $"short of {need.Name}, no shop for it on this road");
            return null;
        }
        if (best > (need.Type == ItemType.Rune ? RuneShopRange : NeedShopRange))
        {
            if (note) mind.Note(now, $"short of {need.Name}; {shop.Name} is too far");
            return null;
        }
        return shop;
    }

    // ------------------------------------------------------------------
    //  Hunting grounds
    // ------------------------------------------------------------------

    /// <summary>What bots have met at a hunting ground (runtime, for every bot): how
    /// strong its creatures were (a running mean of their threat), the kills and the deaths.</summary>
    private sealed class HuntStats
    {
        public double Danger;
        public int Samples;
        public int Kills;
        public int Deaths;
    }

    private readonly Dictionary<string, HuntStats> _huntStats = new(StringComparer.OrdinalIgnoreCase);

    private HuntStats StatsOf(BotDestination ground)
    {
        string key = ShortPlaceName(ground.Name);
        if (!_huntStats.TryGetValue(key, out var st)) _huntStats[key] = st = new HuntStats();
        return st;
    }

    /// <summary>How well a ground fits the bot: the creatures met there against its
    /// strength (a fair fight for its nerve fits best, a walkover teaches little, a
    /// stronger foe is a risk), the deaths there, and its own dread of the place.</summary>
    private (double Fit, double Risk, string Why) HuntFit(BotMind mind, BotDestination d, BotPower power)
    {
        var st = _huntStats.GetValueOrDefault(ShortPlaceName(d.Name));
        double fit = 0.3;
        string why = "unknown ground";
        if (st is { Samples: > 0 })
        {
            double r = st.Danger / Math.Max(1.0, power.Strength) / (0.6 * Math.Max(0.5, mind.Nerve));
            fit = r <= 1 ? 0.2 + 0.6 * r : Math.Max(-1, 0.8 - (r - 1) * 1.2);
            why = string.Create(CultureInfo.InvariantCulture, $"danger {st.Danger:0} vs {power.Strength}");
        }
        double risk = 0;
        if (st != null) risk = st.Deaths / (double)(st.Kills + st.Deaths + 3) * Plans.Risk / Math.Max(0.5, mind.Nerve);
        if (Dreads(mind, d)) { risk += 0.5; why += ", died there"; }
        return (fit, risk, why);
    }

    private bool Dreads(BotMind mind, BotDestination d)
    {
        if (!mind.Char.TryGetTag(DreadTag, out string? raw) || string.IsNullOrEmpty(raw)) return false;
        string name = ShortPlaceName(d.Name);
        long minute = UnixMinute();
        foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int c = part.LastIndexOf(':');
            if (c > 0 && part[..c].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(part.AsSpan(c + 1), out long at) && minute - at < DreadMinutes)
                return true;
        }
        return false;
    }

    /// <summary>The ground of the hunt in hand while the bot is still there (below, or
    /// within 200 tiles of its mouth).</summary>
    private BotDestination? HuntGround(BotMind mind)
    {
        if (mind.Plan.HuntAt is not { } g) return null;
        var pos = mind.Char.Position;
        return IsUnderground(pos) || (pos.Map == g.Pos.Map && pos.GetDistanceTo(g.Pos) <= 200) ? g : null;
    }

    /// <summary>A fight begun at a hunting ground: how strong its creatures are.</summary>
    private void NoteHuntFoe(BotMind mind, Character foe)
    {
        if (foe.IsPlayer || HuntGround(mind) is not { } g) return;
        var t = ThreatOf(foe);
        double strength = t.Dps * t.Toughness / 10.0;
        var st = StatsOf(g);
        st.Danger = st.Samples == 0 ? strength : st.Danger * 0.8 + strength * 0.2;
        st.Samples++;
    }

    private void NoteHuntKill(BotMind mind)
    {
        if (HuntGround(mind) is { } g) StatsOf(g).Kills++;
    }

    /// <summary>A death at a hunting ground: counted against it, and dreaded by the bot.</summary>
    private void NoteHuntDeath(BotMind mind)
    {
        if (HuntGround(mind) is not { } g) return;
        StatsOf(g).Deaths++;
        string name = ShortPlaceName(g.Name);
        var parts = mind.Char.TryGetTag(DreadTag, out string? raw) && !string.IsNullOrEmpty(raw)
            ? raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        parts.Add($"{name}:{UnixMinute()}");
        while (parts.Count > 4) parts.RemoveAt(0);
        mind.Char.SetTag(DreadTag, string.Join(';', parts));
    }

    // ------------------------------------------------------------------
    //  Road distance
    // ------------------------------------------------------------------

    private readonly Dictionary<int, int[]> _roadFields = [];
    private int _roadFieldsBadLinks = -1;
    private const int MaxRoadFields = 96;

    /// <summary>Tiles of road from node <paramref name="from"/> to a place, from a field
    /// of distances to the place's node kept per node (a Dijkstra over the road graph).
    /// At most <paramref name="budget"/> new fields are made; past it the straight
    /// distance stands in. -1 when no road leads there.</summary>
    private int RoadDistance(int from, BotDestination d, ref int budget)
    {
        int to = d.NearestWaypoint;
        if (from < 0 || to < 0 || to >= Nav.Waypoints.Count) return -1;
        int tail = d.Pos.GetDistanceTo(Nav.Waypoints[to].Pos);
        if (_roadFieldsBadLinks != BadLinks)
        {
            _roadFields.Clear();
            _roadFieldsBadLinks = BadLinks;
        }
        if (!_roadFields.TryGetValue(to, out var field))
        {
            if (budget <= 0) return Nav.Waypoints[from].Pos.GetDistanceTo(d.Pos) * 13 / 10;
            budget--;
            if (_roadFields.Count >= MaxRoadFields) _roadFields.Clear();
            _roadFields[to] = field = RoadField(to);
        }
        return field[from] == int.MaxValue ? -1 : field[from] + tail;
    }

    private int[] RoadField(int to)
    {
        var wps = Nav.Waypoints;
        var dist = new int[wps.Count];
        Array.Fill(dist, int.MaxValue);
        dist[to] = 0;
        var open = new PriorityQueue<int, int>();
        open.Enqueue(to, 0);
        while (open.TryDequeue(out int cur, out int dc))
        {
            if (dc > dist[cur]) continue;
            var cp = wps[cur].Pos;
            foreach (int n in wps[cur].Links)
            {
                if (Nav.IsBadLink(cur, n)) continue;
                var np = wps[n].Pos;
                int cost = dc + Math.Max(Math.Abs(cp.X - np.X), Math.Abs(cp.Y - np.Y)) + Math.Abs(cp.Z - np.Z) / 4;
                if (cost >= dist[n]) continue;
                dist[n] = cost;
                open.Enqueue(n, cost);
            }
        }
        return dist;
    }

    // ------------------------------------------------------------------
    //  The choice
    // ------------------------------------------------------------------

    /// <summary>A stay has ended: what next. The plan's next step when it is still about
    /// as good as anything (hysteresis) and nothing more pressing has come up, else a new
    /// plan. Returns the trip to make, or null with <paramref name="started"/> when the
    /// activity began where the bot stands (or null with nothing begun: nowhere to go).
    /// Not used underground (a dungeon run goes room to room; a full pack or supplies run
    /// out end the run early), nor for a bot a player or a favour has a call on, nor for
    /// a player killer.</summary>
    internal BotDestination? PlanNext(BotMind mind, long now, out bool started)
    {
        started = false;
        var ch = mind.Char;
        if (mind.Client != null && IsUnderground(ch.Position) && now < mind.CrawlUntilMs && !IsPk(mind))
        {
            var below = Situation(mind, now);
            if (below.Drives[(int)BotDrive.Load] >= 0.95 ||
                below.Drives[(int)BotDrive.Supplies] * below.Weights[(int)BotDrive.Supplies] >= Plans.Urgent)
            {
                mind.CrawlUntilMs = 0;
                mind.Note(now, "pack full or supplies out: heads out of the dungeon");
            }
        }
        if (mind.Client == null || IsUnderground(ch.Position) || !FreeForWork(mind) || IsPk(mind))
            return PickDestination(mind, spawnable: false);
        var plan = PlanOf(mind, now);
        var s = Situation(mind, now);
        var options = Options(mind, s, now);
        if (options.Count == 0) return PickDestination(mind, spawnable: false);

        bool interrupted = plan.Interrupted;
        if (interrupted) WindowPlanInterrupts++;
        bool fresh = !interrupted && plan.Steps.Count > 0 && UnixMinute() - plan.MadeMinute <= Plans.StaleMinutes;
        BotOption? planned = null;
        if (fresh && plan.Next is { } next)
            planned = PlannedOption(mind, s, options, next, now);
        int urgent = UrgentDrive(s, options, planned);
        var best = options[0];
        bool keep = planned != null && urgent < 0 && planned.Utility + Plans.Stick >= best.Utility - Plans.Margin;
        if (planned != null && !keep && urgent >= 0) WindowPlansUrgent++;
        plan.Interrupted = false;
        plan.LastChoice = ChoiceText(s, options, planned);

        BotOption chosen = planned!;
        for (int tries = 0; tries < 3 && options.Count > 0; tries++)
        {
            if (keep)
                plan.Step++;
            else
            {
                chosen = PickOption(options);
                MakePlan(mind, s, options, chosen);
            }
            var goal = Execute(mind, s, chosen, now, out started);
            if (started || goal != null)
            {
                if (keep) WindowPlansKept++;
                else WindowPlansMade++;
                Took(mind, chosen, now, keep);
                SavePlan(mind);
                return goal;
            }
            WindowPlanFails++;
            mind.Note(now, $"plan: {chosen.Activity} would not start");
            // Not offered again for a while: a choice that cannot be carried out would
            // otherwise come out on top at every stay.
            plan.NoStartUntil[(int)chosen.Activity] = now + Plans.FailRestSeconds * 1000L;
            options.Remove(chosen);
            keep = false;
        }
        plan.Clear();
        SavePlan(mind);
        return PickDestination(mind, spawnable: false);
    }

    /// <summary>The option the plan's next step stands for, if it can be done now; a
    /// hunt keeps the ground it planned.</summary>
    private BotOption? PlannedOption(BotMind mind, BotSituation s, List<BotOption> options, BotPlanStep next, long now)
    {
        var o = options.FirstOrDefault(x => x.Activity == next.Activity);
        if (o == null) return null;
        if (next.Activity == BotActivity.Hunt && next.Target != null && o.Target?.Name != next.Target &&
            Nav.FindDestination(next.Target) is { } ground && OnRoad(ground, s, mind.Char.Position))
        {
            int budget = 1;
            int road = RoadDistance(s.Node, ground, ref budget);
            if (road < 0) return o;
            var (fit, risk, why) = HuntFit(mind, ground, PowerOf(mind, now));
            double extra = Plans.Fit * fit - risk - TravelCost(road);
            var planned = new BotOption
            {
                Activity = BotActivity.Hunt, Target = ground, Extra = extra, Note = why,
                Utility = Utility(BotActivity.Hunt, s.Drives, s, extra),
            };
            options.Add(planned);
            options.Sort((a, b) => b.Utility.CompareTo(a.Utility));
            return planned;
        }
        return o;
    }

    /// <summary>The drives that can press hard enough to set a plan aside: a thing it
    /// is short of, its wounds, a full pack, a heavy purse. Company, something new,
    /// growth and money are what the plan itself is made of; they wait for the plan.</summary>
    private static readonly BotDrive[] UrgentDrives = [BotDrive.Supplies, BotDrive.Safety, BotDrive.Load, BotDrive.Purse];

    /// <summary>A drive pressing enough to set the plan aside: one the planned step does
    /// nothing for while another option does, and that option is worth clearly more
    /// than the planned step (else the new plan would be the old one). -1 when none.</summary>
    private int UrgentDrive(BotSituation s, List<BotOption> options, BotOption? planned)
    {
        int found = -1;
        double worst = 0;
        foreach (var drive in UrgentDrives)
        {
            int i = (int)drive;
            double press = s.Drives[i] * s.Weights[i];
            if (press < Plans.Urgent || press <= worst) continue;
            if (planned != null && Plans.Does(planned.Activity, drive) > 0) continue;
            double bar = planned != null ? planned.Utility + Plans.Stick : double.MinValue;
            bool helps = options.Any(o => o.Utility > bar &&
                                          (Plans.Does(o.Activity, drive) > 0.3 ||
                                           (drive == BotDrive.Supplies && o.Activity == BotActivity.Bank && s.Fund)));
            if (!helps) continue;
            found = i;
            worst = press;
        }
        return found;
    }

    /// <summary>One of the best few, by a softmax over their utilities: the best most
    /// often, a close second now and then, so bots are not clones.</summary>
    private BotOption PickOption(List<BotOption> options)
    {
        var best = options[0];
        var pool = options.Take(Plans.Top).Where(o => o.Utility >= best.Utility - Plans.Margin).ToList();
        double total = 0;
        var w = new double[pool.Count];
        for (int i = 0; i < pool.Count; i++) total += w[i] = Math.Exp((pool[i].Utility - best.Utility) / Plans.Temperature);
        double roll = _rng.NextDouble() * total;
        for (int i = 0; i < pool.Count; i++)
        {
            roll -= w[i];
            if (roll <= 0) return pool[i];
        }
        return best;
    }

    /// <summary>A new day plan, beginning with <paramref name="first"/>: what each step
    /// is expected to do to the drives is played forward, and the best activity for what
    /// is left comes next (two to four steps, no activity twice).</summary>
    private void MakePlan(BotMind mind, BotSituation s, List<BotOption> options, BotOption first)
    {
        var plan = mind.Plan;
        plan.Clear();
        plan.Interrupted = false;
        plan.MadeMinute = UnixMinute();
        plan.Steps.Add(StepOf(first));
        plan.Step = 0;
        // Why: the drive the first step does most for.
        double most = 0;
        plan.Why = BotDrive.Adventure;
        for (int i = 0; i < s.Drives.Length; i++)
        {
            double part = s.Drives[i] * s.Weights[i] * Plans.Does(first.Activity, (BotDrive)i);
            if (first.Activity == BotActivity.Bank && s.Fund && i == (int)BotDrive.Supplies)
                part = s.Drives[i] * s.Weights[i] * 0.9;
            if (part > most) { most = part; plan.Why = (BotDrive)i; }
        }
        plan.WhyNeed = plan.Why == BotDrive.Supplies ? s.Need?.Name : null;

        var sim = (double[])s.Drives.Clone();
        Wear(sim, first.Activity);
        int steps = _rng.Next(Plans.MinSteps, Plans.MaxSteps + 1);
        var taken = new HashSet<BotActivity> { first.Activity };
        while (plan.Steps.Count < steps)
        {
            BotActivity bestAct = BotActivity.None;
            double bestU = 0.05;
            BotDestination? bestTarget = null;
            foreach (BotActivity a in Enum.GetValues<BotActivity>())
            {
                if (a == BotActivity.None || taken.Contains(a) || Plans.Forbids(a, s.Who)) continue;
                var o = options.FirstOrDefault(x => x.Activity == a);
                // What an earlier step makes possible: the gold out of the bank buys,
                // a hunt or a shift brings goods to sell, a sale gold to bank.
                bool enabled = o != null ||
                               (a == BotActivity.Shop && taken.Contains(BotActivity.Bank) && s.Need != null) ||
                               (a == BotActivity.Sell && (taken.Contains(BotActivity.Hunt) || taken.Contains(BotActivity.Work))) ||
                               (a == BotActivity.Bank && taken.Contains(BotActivity.Sell));
                if (!enabled) continue;
                double u = Utility(a, sim, s, o?.Extra ?? 0);
                if (a == BotActivity.Bank && !s.Fund && taken.Contains(BotActivity.Sell))
                    u += 0.1; // the takings go in
                if (u > bestU)
                {
                    bestU = u;
                    bestAct = a;
                    bestTarget = o?.Target;
                }
            }
            if (bestAct == BotActivity.None) break;
            plan.Steps.Add(new BotPlanStep(bestAct, bestAct is BotActivity.Hunt or BotActivity.Shop ? bestTarget?.Name : null));
            taken.Add(bestAct);
            Wear(sim, bestAct);
        }
    }

    private static BotPlanStep StepOf(BotOption o) =>
        new(o.Activity, o.Activity is BotActivity.Hunt or BotActivity.Shop ? o.Target?.Name : null);

    /// <summary>What an activity is expected to do to the drives: those it satisfies
    /// are met as far as it goes, those it wears on grow.</summary>
    private void Wear(double[] drives, BotActivity a)
    {
        for (int i = 0; i < drives.Length; i++)
        {
            double how = Plans.Does(a, (BotDrive)i);
            if (how > 0) drives[i] *= Math.Max(0, 1 - how);
            else if (how < 0) drives[i] = Math.Min(1, drives[i] - how * Plans.Wear);
        }
    }

    /// <summary>Set the bot about the chosen activity through the part that does it.
    /// Returns the trip to make; null with <paramref name="started"/> when it began on
    /// the spot; null otherwise when it cannot be done.</summary>
    private BotDestination? Execute(BotMind mind, BotSituation s, BotOption o, long now, out bool started)
    {
        started = false;
        var ch = mind.Char;
        var plan = mind.Plan;
        plan.HuntAt = o.Activity == BotActivity.Hunt ? o.Target : null;
        switch (o.Activity)
        {
            case BotActivity.Hunt:
                return o.Target;
            case BotActivity.Work:
                started = StartWork(mind, now);
                return null;
            case BotActivity.Sell:
                started = StartSelling(mind, now);
                return null;
            case BotActivity.Shop:
            {
                if (s.Need is not { } need || o.Target is not { } shop) return null;
                mind.ShoppingFor = need;
                mind.Note(now, $"short of {need.Name}; to {shop.Name}");
                if (mind.At != shop) return shop;
                mind.BeginLinger(shop, now, _rng, short_: true);
                StartShopping(mind, now);
                started = true;
                return null;
            }
            case BotActivity.Bank:
            {
                if (o.Target is not { } bank) return null;
                // Its errand is seen to as soon as it stands there.
                mind.NextBankMs = 0;
                if (mind.At != bank) return bank;
                mind.BeginLinger(bank, now, _rng, short_: true);
                started = true;
                return null;
            }
            case BotActivity.Rest:
            {
                if (o.Target is not { } inn) return null;
                if (mind.At != inn) return inn;
                mind.BeginLinger(inn, now, _rng, short_: false);
                mind.LingerUntilMs = now + _rng.Next(120_000, 240_000);
                mind.Note(now, $"rests at {inn.Name}");
                started = true;
                return null;
            }
            case BotActivity.Social:
            {
                var place = SocialPlace(mind, s, now);
                if (place == null) return null;
                if (place != mind.At) return place;
                // Among people already: it stays.
                mind.BeginLinger(place, now, _rng, short_: false);
                started = true;
                return null;
            }
            case BotActivity.Home:
            {
                if (!HomeOnThisRoad(mind))
                {
                    if (KnowsTravel(mind) && TryRecallHome(mind, now, "going home"))
                    {
                        started = true;
                        return null;
                    }
                    return PickDestination(mind, spawnable: false);
                }
                var home = Nav.Destinations.Where(d => d.City.Equals(mind.Home, StringComparison.OrdinalIgnoreCase) &&
                                                       IsOfType(d, SocialTypes) && OnRoad(d, s, ch.Position)).ToList();
                return home.Count > 0 ? PickPlace(home) : PickDestination(mind, spawnable: false);
            }
            default:
                return PickDestination(mind, spawnable: false);
        }
    }

    /// <summary>Where people are: a place near a player who is not already crowded
    /// (when bots are drawn to players at all), else a bank, an inn, a tavern or a
    /// healer's of its town, by weight and room.</summary>
    private BotDestination? SocialPlace(BotMind mind, BotSituation s, long now)
    {
        var pos = mind.Char.Position;
        if (Gravity > 0 && _watching.Count > 0)
        {
            var pulled = Nav.Destinations.Where(d => d.City.Length > 0 && OnRoad(d, s, pos) && Within(d.Pos, pos, 220) &&
                                                     WatcherNear(d.Pos, 30) is { } w && w.Crowd < CrowdCap).ToList();
            if (pulled.Count > 0 && PickPlace(pulled) is { } near) return near;
        }
        // Where it stands counts only when there are people about it; else it looks for them.
        var here = mind.At;
        bool peopleHere = here != null && (IsWatched(here.Pos) || CrowdAt(here, now) >= 3);
        bool Fits(BotDestination d) => IsOfType(d, SocialTypes) && OnRoad(d, s, pos) && (d != here || peopleHere);
        string town = s.Town.Length > 0 ? s.Town : mind.Home;
        var places = Nav.Destinations.Where(d => Fits(d) && d.City.Equals(town, StringComparison.OrdinalIgnoreCase)).ToList();
        if (places.Count == 0)
            places = Nav.Destinations.Where(d => Fits(d) && d.City.Length > 0).ToList();
        return places.Count > 0 ? PickPlace(places) : null;
    }

    /// <summary>The choice is made: noted, counted, and what it remembers of its days
    /// moves on (the same activity again wears on it).</summary>
    private void Took(BotMind mind, BotOption o, long now, bool kept)
    {
        var plan = mind.Plan;
        plan.Repeats = plan.LastActivity == o.Activity ? plan.Repeats + 1 : 0;
        plan.LastActivity = o.Activity;
        if (o.Activity == BotActivity.Hunt) plan.TownSinceMs = now;
        _windowActivity[(int)o.Activity]++;
        mind.Note(now, string.Create(CultureInfo.InvariantCulture,
            $"{(kept ? "plan goes on" : "new plan")}: {o.Activity}{(o.Target != null ? " at " + o.Target.Name : "")} " +
            $"(u {o.Utility:0.00}, for {plan.Why}) [{string.Join(" > ", plan.Steps.Select((st, i) => (i == plan.Step ? "*" : "") + st.Activity))}]"));
    }

    private string ChoiceText(BotSituation s, List<BotOption> options, BotOption? planned)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Drives.Length; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{(BotDrive)i}={s.Drives[i]:0.00}x{s.Weights[i]:0.0} ");
        sb.Append("| ");
        foreach (var o in options.Take(6))
            sb.Append(CultureInfo.InvariantCulture, $"{(o == planned ? "*" : "")}{o.Activity}={o.Utility:0.00} ");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    //  Explaining it
    // ------------------------------------------------------------------

    /// <summary>A word from a <c>token_*</c> list of <c>Key=word</c> lines.</summary>
    private string? TokenEntry(string list, string key, string lang)
    {
        for (int i = 0; Chat.Line(list, i, lang) is { } line; i++)
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }

    private string ActivityWord(BotActivity a, string lang) =>
        TokenEntry("token_activity", a.ToString(), lang) ?? a.ToString().ToLowerInvariant();

    private string NeedWord(BotPlan plan, string lang) =>
        (plan.WhyNeed != null ? TokenEntry("token_need", $"{plan.Why}.{plan.WhyNeed}", lang) : null) ??
        TokenEntry("token_need", plan.Why.ToString(), lang) ?? plan.Why.ToString().ToLowerInvariant();

    /// <summary>The tokens of a plan answer: <c>{now}</c> what it is about, <c>{goal}</c>
    /// where (when the step has a place), <c>{next}</c> what comes after it, <c>{why}</c>
    /// its reason.</summary>
    private Dictionary<string, string>? PlanTokens(BotMind m, string lang)
    {
        var plan = PlanOf(m, NowMs());
        if (plan.Current is not { } cur) return null;
        var tokens = new Dictionary<string, string>
        {
            ["now"] = ActivityWord(cur.Activity, lang),
            ["why"] = NeedWord(plan, lang),
        };
        var at = cur.Target != null ? Nav.FindDestination(cur.Target) : m.Mode == BotMode.Travel ? m.Goal : null;
        if (at != null) tokens["goal"] = PlacePhrase(at, lang);
        if (plan.Next is { } next) tokens["next"] = ActivityWord(next.Activity, lang);
        return tokens;
    }

    /// <summary>"What is your plan": its plan and why, or that it has none.</summary>
    private (string, Dictionary<string, string>?) AnswerPlan(BotMind m, string lang)
    {
        if (m.Companion != null || PartyLeader(m) != null) return ("respond_plan_none", null);
        return PlanTokens(m, lang) is { } tokens ? ("respond_plan", tokens) : ("respond_plan_none", null);
    }

    /// <summary>"What are you doing" on the way to a step that has a reason worth
    /// telling (a shop, the bank, a rest, company, home): what and why.</summary>
    private (string, Dictionary<string, string>?, string?, string?)? PlanDoing(BotMind m, BotDestination goal, string lang)
    {
        if (!Chat.Has("respond_doing_plan", lang)) return null;
        var plan = PlanOf(m, NowMs());
        if (plan.Current is not { Activity: BotActivity.Shop or BotActivity.Bank or BotActivity.Rest or BotActivity.Social or BotActivity.Home })
            return null;
        if (PlanTokens(m, lang) is not { } tokens) return null;
        string phrase = PlacePhrase(goal, lang);
        tokens["goal"] = phrase;
        return ("respond_doing_plan", tokens, "dest:" + goal.Name, phrase);
    }

    /// <summary>The plan in plain English, for the brain.</summary>
    private string? PlanWords(BotMind m)
    {
        var plan = m.Plan;
        if (plan.Steps.Count == 0 || plan.Current is not { } cur) return null;
        var rest = plan.Steps.Skip(plan.Step + 1).Select(st => st.Activity.ToString().ToLowerInvariant()).ToList();
        return $"Your plan: {cur.Activity.ToString().ToLowerInvariant()}{(rest.Count > 0 ? ", then " + string.Join(", then ", rest) : "")}, " +
               $"because of {plan.Why.ToString().ToLowerInvariant()}{(plan.WhyNeed != null ? " (" + plan.WhyNeed + ")" : "")}. ";
    }

    /// <summary>Console PLAYERBOTS PLAN: one bot's drives, the options weighed now and
    /// its plan. Nothing is changed.</summary>
    public IReadOnlyList<string> PlanReport(string key)
    {
        var mind = FindMind(key);
        if (mind == null) return [$"no bot '{key}'"];
        long now = NowMs();
        var ch = mind.Char;
        var plan = PlanOf(mind, now);
        var lines = new List<string>
        {
            $"0{ch.Uid.Value:X8} {BaseName(ch)} at {ch.Position} mode {mind.Mode} town {TownAt(mind)} home {mind.Home}",
            $"plan ({(plan.Steps.Count == 0 ? "none" : $"made {UnixMinute() - plan.MadeMinute} min ago, for {plan.Why}{(plan.WhyNeed != null ? ":" + plan.WhyNeed : "")}")}): " +
            string.Join(" > ", plan.Steps.Select((st, i) => (i == plan.Step ? "*" : "") + st.Activity + (st.Target != null ? "@" + st.Target : ""))),
        };
        if (mind.Client == null || ch.IsDead || IsUnderground(ch.Position) || IsPk(mind))
        {
            lines.Add("not planning now (no client, dead, underground or a player killer)");
            return lines;
        }
        var s = Situation(mind, now);
        lines.Add("who: " + string.Join(' ', s.Who) + string.Create(CultureInfo.InvariantCulture,
            $"; gold {s.PackGold} + bank {s.BankGold}; load {s.LoadPercent}%; need {s.Need?.Name ?? "-"}"));
        lines.Add("drives (urgency x weight):");
        for (int i = 0; i < s.Drives.Length; i++)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {(BotDrive)i,-10} {s.Drives[i]:0.00} x {s.Weights[i]:0.00} = {s.Drives[i] * s.Weights[i]:0.00}"));
        lines.Add("options:");
        foreach (var o in Options(mind, s, now))
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  {o.Activity,-7} {o.Utility,6:0.00}{(o.Target != null ? "  " + o.Target.Name : "")}{(o.Note != null ? "  (" + o.Note + ")" : "")}"));
        if (plan.LastChoice != null) lines.Add("last choice: " + plan.LastChoice);
        return lines;
    }
}
