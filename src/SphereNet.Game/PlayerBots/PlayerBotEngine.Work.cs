using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Working life. A bot with a trade earns its keep the way a player does: it walks to
/// a mine, a grove or a dock and works it with its tool (a double click on the tool,
/// its target cursor answered on a resource tile near by, the skill's own strokes
/// waited out), carries ore to a forge and smelts it, makes things of its craft where
/// the craft is plied, and sells what it has to a vendor (the Sell part). Nothing is
/// written into its pack or its skills: ore, logs, fish, ingots and goods come out of
/// the engine's own gathering, smelting and crafting, gains included.
///
/// The trades are data (<c>work.txt</c>): which skill works where with which tool.
/// A bot has a trade when it has the skill (<see cref="WorkSkillMin"/>); the tool it
/// then lacks is a need like any other (<c>shopping.txt</c>) and is bought.
///
/// The outcome of every action is read as a player reads it: from what the client is
/// told ("there is no ore here to mine") and from what turns up in the pack.
///
/// Entry points for whoever plans a bot's day: <see cref="CanWork"/>,
/// <see cref="StartWork"/>, <see cref="HasGoodsToSell"/>, <see cref="StartSelling"/>;
/// the Planner part chooses when (<see cref="ResumeWorkingLife"/> takes up the job a
/// restart left). A job in hand is the mind's <see cref="BotMind.Work"/> and the character's
/// <see cref="WorkTag"/>, from which it is taken up again after a restart.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>The job in hand: <c>job|skill|place|sale type</c>.</summary>
    public const string WorkTag = "PLAYERBOT.WORK";

    /// <summary>Skill (in tenths) from which a bot plies a trade.</summary>
    public int WorkSkillMin { get; set; } = 300;
    /// <summary>Percent of its weight limit at which a bot stops gathering or making.</summary>
    public int WorkLoadPercent { get; set; } = 75;
    /// <summary>A shift lasts between these (rolled per shift).</summary>
    public int WorkMinMs { get; set; } = 300_000;
    public int WorkMaxMs { get; set; } = 720_000;
    /// <summary>How far (tiles) a bot goes to gather (and back to a forge with its
    /// ore), and how far to craft or to sell.</summary>
    public int WorkTripRange { get; set; } = 400;
    public int WorkErrandRange { get; set; } = 300;
    /// <summary>Percent of pauses between two actions that turn into a look about.</summary>
    public int WorkLookAroundPercent { get; set; } = 8;

    /// <summary>How far about itself a bot looks for a tile to work.</summary>
    private const int GatherScanRadius = 7;
    /// <summary>The least time between two such looks by one bot.</summary>
    private const long WorkScanMs = 3000;
    /// <summary>Tiles in a row that gave nothing before a place is given up.</summary>
    private const int MaxDryTiles = 14;
    /// <summary>Swings in a row on one tile that ran and gave nothing before it is left.</summary>
    private const int MaxFailedSwings = 10;
    /// <summary>Walks on into a place that showed nothing to work before it is given up.</summary>
    private const int MaxRoams = 5;
    /// <summary>How long a place found useless for a job is left alone (by every bot).</summary>
    private const long WorkMemoMs = 1_800_000;
    /// <summary>How near a place a bot counts as there already.</summary>
    private const int WorkNearRange = 10;
    /// <summary>Recipes looked at closely (materials counted) per choice.</summary>
    private const int RecipeTries = 12;
    /// <summary>A recipe asking more than this of one material is nobody's daily work.</summary>
    private const int MaxRecipeMaterial = 60;

    private readonly List<BotGatherTrade> _gatherTrades = [];
    private readonly List<BotCraftTrade> _craftTrades = [];
    private string[] _smeltPlaces = ["Forge"];

    public long WindowWorkTrips { get; private set; }
    public long WindowGathers { get; private set; }
    public long WindowSmelts { get; private set; }
    public long WindowCrafts { get; private set; }
    public long WindowSales { get; private set; }
    public long WindowSaleGold { get; private set; }
    public long WindowWorkGiveUps { get; private set; }

    private void ResetWorkWindow() =>
        WindowWorkTrips = WindowGathers = WindowSmelts = WindowCrafts = WindowSales = WindowSaleGold = WindowWorkGiveUps = 0;

    // ------------------------------------------------------------------
    //  The trades
    // ------------------------------------------------------------------

    /// <summary>Read <c>work.txt</c>:
    /// <code>
    /// gather &lt;skill&gt; &lt;tool item type&gt; &lt;place type&gt;...
    /// craft  &lt;skill&gt; &lt;tool item type&gt; hand|pack &lt;material type,...|-&gt; &lt;place type&gt;...
    /// smelt  &lt;place type&gt;...
    /// sell   &lt;item type | prefix* | *&gt; &lt;place type&gt;...
    /// </code>
    /// Skills and item types by their names (SkillType, ItemType).</summary>
    public void LoadWork(string dataDir)
    {
        string path = Path.Combine(dataDir, "work.txt");
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var p = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            switch (p[0].ToLowerInvariant())
            {
                case "gather" when p.Length >= 4 && Enum.TryParse(p[1], true, out SkillType skill) &&
                                   Enum.TryParse(p[2], true, out ItemType tool):
                    AddGatherTrade(new BotGatherTrade(skill, tool, p[3..]));
                    break;
                case "craft" when p.Length >= 6 && Enum.TryParse(p[1], true, out SkillType craft) &&
                                  Enum.TryParse(p[2], true, out ItemType craftTool):
                    var materials = new List<ItemType>();
                    foreach (string name in p[4].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (Enum.TryParse(name, true, out ItemType material)) materials.Add(material);
                    AddCraftTrade(new BotCraftTrade(craft, craftTool, p[3].Equals("hand", StringComparison.OrdinalIgnoreCase),
                        [.. materials], p[5..]));
                    break;
                case "smelt" when p.Length >= 2:
                    _smeltPlaces = p[1..];
                    break;
                case "sell" when p.Length >= 3:
                    if (p[1] == "*")
                        AddSaleRule(new BotSaleRule(null, p[2..]));
                    else if (p[1].EndsWith('*'))
                    {
                        // A family of types: Weapon* is every weapon type.
                        foreach (string name in Enum.GetNames<ItemType>())
                            if (name.StartsWith(p[1][..^1], StringComparison.OrdinalIgnoreCase))
                                AddSaleRule(new BotSaleRule(Enum.Parse<ItemType>(name), p[2..]));
                    }
                    else if (Enum.TryParse(p[1], true, out ItemType sold))
                        AddSaleRule(new BotSaleRule(sold, p[2..]));
                    break;
            }
        }
    }

    public void AddGatherTrade(BotGatherTrade trade)
    {
        _gatherTrades.Add(trade);
        // What the pack's script of the skill asks of the tool is read now, at load,
        // not in a bot's act.
        SkillToolRules(trade.Skill);
    }
    public void AddCraftTrade(BotCraftTrade trade) => _craftTrades.Add(trade);
    /// <summary>The kinds of place ore is smelted at.</summary>
    public void SetSmeltPlaces(params string[] places) => _smeltPlaces = places;

    private bool HasSkillFor(Character ch, SkillType skill) => ch.GetSkill(skill) >= WorkSkillMin;

    /// <summary>A bot with the skill of a trade, with its tool or not: the one that
    /// works for a living, and buys the tool it lacks.</summary>
    internal bool IsTradesman(BotMind mind)
    {
        var ch = mind.Char;
        foreach (var g in _gatherTrades)
            if (HasSkillFor(ch, g.Skill)) return true;
        foreach (var c in _craftTrades)
            if (HasSkillFor(ch, c.Skill)) return true;
        return false;
    }

    /// <summary>The tool of a type the bot holds or carries: in a hand, else loose in
    /// its pack. With <paramref name="fits"/>, only a tool that passes it.</summary>
    private Item? FindTool(Character ch, ItemType type, Func<Item, bool>? fits = null)
    {
        if (ch.GetEquippedItem(Layer.OneHanded) is { IsDeleted: false } one && one.ItemType == type && (fits == null || fits(one)))
            return one;
        if (ch.GetEquippedItem(Layer.TwoHanded) is { IsDeleted: false } two && two.ItemType == type && (fits == null || fits(two)))
            return two;
        if (ch.Backpack is not { } pack) return null;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.ItemType == type && (fits == null || fits(item))) return item;
        return null;
    }

    /// <summary>The tool a gathering trade can really be plied with: of the trade's
    /// type and, where the pack's skill script reads the tool from the hand
    /// (<c>SRC.WEAPON</c>, as Scripts-X Mining and Lumberjacking do in @PreStart), one
    /// that goes into a hand and - when the script asks for its charges - has some.
    /// A shovel is a <c>t_weapon_mace_pick</c> too, but no hand holds it: digging with
    /// it would swing whatever the hand holds.</summary>
    private Item? GatherTool(Character ch, BotGatherTrade g) =>
        FindTool(ch, g.Tool, ToolInHand(g.Skill) ? item => FitsHand(item, ToolCharges(g.Skill)) : null);

    /// <summary>The tool of a craft: a craft plied with the tool in hand (a smith's
    /// hammer) takes one a hand can hold.</summary>
    private Item? CraftTool(Character ch, BotCraftTrade c) =>
        FindTool(ch, c.Tool, c.InHand ? item => FitsHand(item, charges: false) : null);

    /// <summary>Whether an item goes into a hand (its layer, as the double click that
    /// equips it reads it: <see cref="Clients.ClientItemUseHandler.ResolveWearableLayer"/>,
    /// the ITEMDEF's LAYER else the tiledata's - Source-X GetEquipLayer) and, with
    /// <paramref name="charges"/>, has charges left (USESCUR, its own or its ITEMDEF's).</summary>
    private bool FitsHand(Item item, bool charges)
    {
        // In a hand already, it goes into one.
        Layer layer = item.IsEquipped && item.EquipLayer is Layer.OneHanded or Layer.TwoHanded
            ? item.EquipLayer
            : DefinitionLoader.GetItemDef(item.BaseId)?.Layer ?? Layer.None;
        if (layer == Layer.None && _world.MapData != null)
        {
            var tile = _world.MapData.GetItemTileData(item.BaseId);
            if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Wearable) != 0 && tile.Quality > 0 &&
                tile.Quality <= (byte)Layer.Horse)
                layer = (Layer)tile.Quality;
        }
        if (layer is not (Layer.OneHanded or Layer.TwoHanded)) return false;
        return !charges || (item.TryGetProperty("USESCUR", out string uses) && int.TryParse(uses, out int left) && left > 0);
    }

    private readonly Dictionary<SkillType, (bool Hand, bool Charges)> _skillToolRules = [];

    /// <summary>Whether the pack's script of a gathering skill reads the tool from the
    /// hand (<c>SRC.WEAPON</c>); then the tool must be taken into the hand before use.</summary>
    private bool ToolInHand(SkillType skill) => SkillToolRules(skill).Hand;

    /// <summary>Whether that script also asks for the tool's charges (<c>WEAPON.USESCUR</c>).</summary>
    private bool ToolCharges(SkillType skill) => SkillToolRules(skill).Charges;

    /// <summary>What a skill's [SKILL] section of the script pack asks of the tool, read
    /// once per skill: its trigger lines are looked through for <c>SRC.WEAPON</c> (the
    /// item in hand, CChar::m_uidWeapon) and <c>WEAPON.USESCUR</c>. No script, no rule.</summary>
    private (bool Hand, bool Charges) SkillToolRules(SkillType skill)
    {
        if (_skillToolRules.TryGetValue(skill, out var rules)) return rules;
        bool hand = false, charges = false;
        if (_resources?.GetResource(ResType.SkillDef, (int)skill) is SphereNet.Scripting.Resources.ResourceLink link)
        {
            IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey>? keys = link.StoredKeys;
            if (keys == null)
            {
                using var file = link.OpenAtStoredPosition();
                keys = file?.ReadNextSection()?.Keys;
            }
            foreach (var key in keys ?? [])
            {
                string line = key.Key + " " + key.Arg;
                if (line.Contains("SRC.WEAPON", StringComparison.OrdinalIgnoreCase)) hand = true;
                if (line.Contains("WEAPON.USESCUR", StringComparison.OrdinalIgnoreCase)) charges = true;
            }
        }
        return _skillToolRules[skill] = (hand, hand && charges);
    }

    /// <summary>How many usable tools of a type a tradesman has for the trades it has
    /// the skill of (0 or 1): what a tool need counts. -1 when the type is no tool of
    /// its trades.</summary>
    private int UsableToolCount(Character ch, ItemType type)
    {
        bool tool = false;
        foreach (var g in _gatherTrades)
        {
            if (g.Tool != type || !HasSkillFor(ch, g.Skill)) continue;
            tool = true;
            if (GatherTool(ch, g) != null) return 1;
        }
        foreach (var c in _craftTrades)
        {
            if (c.Tool != type || !HasSkillFor(ch, c.Skill)) continue;
            tool = true;
            if (CraftTool(ch, c) != null) return 1;
        }
        return tool ? 0 : -1;
    }

    /// <summary>Whether a vendor's piece answers a tool need the way the trade needs
    /// it: one a hand holds where the trade's tool is taken into the hand, with its
    /// charges where the script counts them (a bought piece carries the stock piece's
    /// state over, TradeEngine's CopyStackInstanceStateFrom).</summary>
    private bool ToolStockFits(Character ch, BotNeed need, Item stock)
    {
        foreach (var g in _gatherTrades)
            if (g.Tool == need.Type && HasSkillFor(ch, g.Skill) && ToolInHand(g.Skill) && !FitsHand(stock, ToolCharges(g.Skill)))
                return false;
        foreach (var c in _craftTrades)
            if (c.Tool == need.Type && HasSkillFor(ch, c.Skill) && c.InHand && !FitsHand(stock, charges: false))
                return false;
        return true;
    }

    // ------------------------------------------------------------------
    //  A trade that will not start
    // ------------------------------------------------------------------

    /// <summary>Jobs of one trade in a row that would not start (the tool not taken
    /// up, the swing or the craft never starting) before the bot leaves the trade
    /// alone for <see cref="WorkMemoMs"/>: the planner stops offering it work.</summary>
    private const int MisstartsToRest = 2;

    /// <summary>A job of the trade would not start: counted, and the second in a row
    /// rests the trade.</summary>
    private void NoteMisstart(BotMind mind, SkillType skill, long now)
    {
        int n = mind.TradeMisstarts.GetValueOrDefault(skill) + 1;
        if (n < MisstartsToRest)
        {
            mind.TradeMisstarts[skill] = n;
            return;
        }
        mind.TradeMisstarts.Remove(skill);
        mind.TradeRestUntil[skill] = now + WorkMemoMs;
        mind.Note(now, $"its {SkillWords(skill)} will not start; leaves it for a while");
    }

    /// <summary>Whether the bot has left a trade alone lately (see <see cref="NoteMisstart"/>).</summary>
    private static bool TradeResting(BotMind mind, SkillType skill, long now) =>
        mind.TradeRestUntil.Count > 0 && mind.TradeRestUntil.TryGetValue(skill, out long until) && now < until;

    /// <summary>1 when a hand holds an item of the type (a tool in use is not in the pack).</summary>
    private static int CountInHand(Character ch, ItemType type) =>
        ch.GetEquippedItem(Layer.OneHanded) is { IsDeleted: false } one && one.ItemType == type ||
        ch.GetEquippedItem(Layer.TwoHanded) is { IsDeleted: false } two && two.ItemType == type
            ? 1 : 0;

    /// <summary>Whether an item type is something this bot's trade wants it to have: the
    /// tool of a trade it has the skill for, or the material of a craft it can ply
    /// while it has no way of gathering for itself (shopping.txt names how many).</summary>
    private bool WorkNeeds(BotMind mind, ItemType type)
    {
        var ch = mind.Char;
        if (_gatherTrades.Count == 0 && _craftTrades.Count == 0) return false;
        foreach (var g in _gatherTrades)
            if (g.Tool == type && HasSkillFor(ch, g.Skill))
                return !IsRogueBot(ch) && CanBeBought(mind, type);
        foreach (var c in _craftTrades)
        {
            if (!HasSkillFor(ch, c.Skill)) continue;
            if (c.Tool == type) return !IsRogueBot(ch) && CanBeBought(mind, type);
            if (Array.IndexOf(c.Materials, type) >= 0 && CraftTool(ch, c) != null &&
                !IsRogueBot(ch) && !GathersForItself(mind) && CanBeBought(mind, type))
                return true;
        }
        return false;
    }

    /// <summary>Whether a shop for the need of this item type stands on the bot's road
    /// that is not known to be out of it. A tool nobody sells is no need: it would
    /// stand before every other one for good (the first need is the one shopped for).</summary>
    private bool CanBeBought(BotMind mind, ItemType type)
    {
        BotNeed? need = null;
        foreach (var n in _needs)
            if (n.Type == type) { need = n; break; }
        if (need == null) return false;
        int here = Nav.FindNearest(mind.Char.Position, 64);
        if (here < 0) return false;
        int component = Nav.Waypoints[here].Component;
        long now = NowMs();
        foreach (var d in Nav.Destinations)
        {
            if (d.City.Length == 0 || Nav.ComponentOf(d) != component || !IsUsable(d) || SoldOut(d, need, now)) continue;
            foreach (string t in need.Places)
                if (t.Equals(d.Type, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether a gathering trade of the bot's can be plied from where it is:
    /// the skill, the tool, and a place for it within reach.</summary>
    private bool GathersForItself(BotMind mind)
    {
        var ch = mind.Char;
        long now = NowMs();
        foreach (var g in _gatherTrades)
            if (HasSkillFor(ch, g.Skill) && !TradeResting(mind, g.Skill, now) && GatherTool(ch, g) != null &&
                WorkPlace(mind, g.Places, MemoKey(BotJob.Gather, g.Skill), now, WorkTripRange) != null)
                return true;
        return false;
    }

    /// <summary>Whether the bot is free to go about its own work: no rogue, not going
    /// about with a player, no favour in hand, in no party, above ground.</summary>
    private bool MayWork(BotMind mind)
    {
        var ch = mind.Char;
        return mind.Client != null && !ch.IsDead && !IsUnderground(ch.Position) && FreeForWork(mind) && !IsRogueBot(ch);
    }

    /// <summary>Nobody has a call on the bot right now.</summary>
    private bool FreeForWork(BotMind mind) =>
        mind.Companion == null && mind.Favor == null && mind.Guiding == null && mind.Leader == null &&
        mind.SellTo == null && mind.GiveBackTo == null && Parties?.FindParty(mind.Char.Uid) == null;

    /// <summary>Whether this bot has work it could do now: ore to smelt, a craft with
    /// its tool and materials, or a gathering trade with its tool and room in the
    /// pack. Says nothing of whether a place for it is near: <see cref="StartWork"/>
    /// answers that.</summary>
    public bool CanWork(BotMind mind)
    {
        if (!MayWork(mind)) return false;
        var ch = mind.Char;
        long now = NowMs();
        if (FirstOre(mind) != null) return true;
        foreach (var c in _craftTrades)
            if (!TradeResting(mind, c.Skill, now) && CanCraftNow(mind, c)) return true;
        if (PackLoadPercent(ch) >= WorkLoadPercent) return false;
        foreach (var g in _gatherTrades)
            if (HasSkillFor(ch, g.Skill) && !TradeResting(mind, g.Skill, now) && GatherTool(ch, g) != null) return true;
        return false;
    }

    /// <summary>Send the bot to work: ore to a forge first, then a craft it has the
    /// materials for, then a gathering shift. False when it has no work, or no place
    /// for it on its road within reach; then nothing has changed.</summary>
    public bool StartWork(BotMind mind, long now)
    {
        if (!MayWork(mind)) return false;
        var ch = mind.Char;
        // As far back with the ore as it went out for it.
        if (FirstOre(mind) != null &&
            WorkPlace(mind, _smeltPlaces, MemoKey(BotJob.Smelt, SkillType.Mining), now, WorkTripRange) is { } forge)
            return BeginJob(mind, BotJob.Smelt, SkillType.Mining, forge, now);
        foreach (var c in _craftTrades)
            if (!TradeResting(mind, c.Skill, now) && CanCraftNow(mind, c) &&
                WorkPlace(mind, c.Places, MemoKey(BotJob.Craft, c.Skill), now, WorkErrandRange) is { } shop)
                return BeginJob(mind, BotJob.Craft, c.Skill, shop, now);
        if (PackLoadPercent(ch) >= WorkLoadPercent) return false;
        // The gathering trade it is best at; the next one when that has no place near.
        foreach (var g in _gatherTrades.OrderByDescending(t => ch.GetSkill(t.Skill)))
            if (HasSkillFor(ch, g.Skill) && !TradeResting(mind, g.Skill, now) && GatherTool(ch, g) != null &&
                WorkPlace(mind, g.Places, MemoKey(BotJob.Gather, g.Skill), now, WorkTripRange, spread: true) is { } spot)
                return BeginJob(mind, BotJob.Gather, g.Skill, spot, now);
        return false;
    }

    /// <summary>Where a stay ends, before the planner: a job that did not end by
    /// itself is let go, and a job the save left unfinished is taken up again. True
    /// when the bot has set off (or started on the spot).</summary>
    private bool ResumeWorkingLife(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (mind.Client == null || ch.IsDead) return false;
        if (mind.Work is { } left)
        {
            // A job that did not end by itself: the trip found no road (the place is
            // this bot's to stay away from), or something else took the bot away.
            DropWork(mind);
            if (left.Stage == BotWorkStage.Trip && mind.NoRouteCount > 0)
                RememberUseless(left.Place, OwnKey(mind, MemoKey(left)), now);
        }
        else if (ch.TryGetTag(WorkTag, out string? saved) && !string.IsNullOrEmpty(saved))
        {
            ch.RemoveTag(WorkTag);
            if (ResumeWork(mind, saved, now)) return true;
        }
        return false;
    }

    /// <summary>Take up the job a saved <see cref="WorkTag"/> names, from wherever the
    /// bot stands now.</summary>
    private bool ResumeWork(BotMind mind, string saved, long now)
    {
        var p = saved.Split('|');
        if (p.Length < 3 || !Enum.TryParse(p[0], true, out BotJob job) || !MayWork(mind)) return false;
        if (!Enum.TryParse(p[1], true, out SkillType skill)) skill = SkillType.None;
        ItemType saleType = p.Length > 3 && Enum.TryParse(p[3], true, out ItemType st) ? st : ItemType.Normal;
        if (Nav.FindDestination(p[2]) is not { } place || place.Pos.Map != mind.Char.MapIndex) return false;
        bool sense = job switch
        {
            BotJob.Gather => _gatherTrades.Any(g => g.Skill == skill && GatherTool(mind.Char, g) != null),
            BotJob.Smelt => FirstOre(mind) != null,
            BotJob.Craft => _craftTrades.Any(c => c.Skill == skill && CanCraftNow(mind, c)),
            _ => GoodsToSell(mind).Count > 0,
        };
        if (!sense) return false;
        mind.Note(now, $"takes up its work again: {saved}");
        return BeginJob(mind, job, skill, place, now, saleType);
    }

    private static string MemoKey(BotJob job, SkillType skill, ItemType saleType = ItemType.Normal) =>
        job == BotJob.Sell ? "sell:" + saleType : job + ":" + skill;

    private static string MemoKey(BotWork w) => MemoKey(w.Job, w.Skill, w.SaleType);

    /// <summary>Places found useless for a job - no road, nothing to work, nobody
    /// buying - and until when no bot goes there for it.</summary>
    private readonly Dictionary<(string Place, string What), long> _uselessPlaces = [];

    private void RememberUseless(BotDestination place, string what, long now)
    {
        if (_uselessPlaces.Count > 512)
            foreach (var old in _uselessPlaces.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                _uselessPlaces.Remove(old);
        _uselessPlaces[(place.Name, what)] = now + WorkMemoMs;
        WindowWorkGiveUps++;
    }

    /// <summary>The key of a job for one bot alone: what failed because of the bot
    /// itself (a tool it cannot use, a craft that will not start for it) is its own
    /// to stay away from, not the town's.</summary>
    private static string OwnKey(BotMind mind, string what) => what + "@" + mind.Char.Uid.Value;

    private bool IsUseless(BotDestination place, string what, long now)
    {
        if (_uselessPlaces.Count == 0 || !_uselessPlaces.TryGetValue((place.Name, what), out long until)) return false;
        if (now < until) return true;
        _uselessPlaces.Remove((place.Name, what));
        return false;
    }

    /// <summary>The nearest place of one of the <paramref name="types"/> on the bot's
    /// piece of road within <paramref name="range"/> tiles that is not known useless
    /// for <paramref name="what"/>. With <paramref name="spread"/>, now and then the
    /// second nearest, so that a town's miners do not all stand in one pit.
    /// <paramref name="except"/> leaves further places out.</summary>
    private BotDestination? WorkPlace(BotMind mind, string[] types, string what, long now, int range, bool spread = false,
        Func<BotDestination, bool>? except = null)
    {
        var ch = mind.Char;
        int here = Nav.FindNearest(ch.Position, 24);
        if (here < 0) here = Nav.FindNearest(ch.Position, 64);
        if (here < 0) return null;
        int component = Nav.Waypoints[here].Component;
        BotDestination? best = null, second = null;
        int bestDist = int.MaxValue, secondDist = int.MaxValue;
        foreach (var d in Nav.Destinations)
        {
            if (d.Pos.Map != ch.MapIndex) continue;
            bool ofType = false;
            foreach (string t in types)
                if (t.Equals(d.Type, StringComparison.OrdinalIgnoreCase)) { ofType = true; break; }
            if (!ofType || Nav.ComponentOf(d) != component || !IsUsable(d)) continue;
            int dist = d.Pos.GetDistanceTo(ch.Position);
            if (dist > range || IsUseless(d, what, now) || IsUseless(d, OwnKey(mind, what), now)) continue;
            if (except != null && except(d)) continue;
            if (dist < bestDist)
            {
                (second, secondDist) = (best, bestDist);
                (best, bestDist) = (d, dist);
            }
            else if (dist < secondDist)
                (second, secondDist) = (d, dist);
        }
        return spread && second != null && _rng.Next(100) < 30 ? second : best;
    }

    /// <summary>What a trip to <paramref name="place"/> is planned to: the place, when
    /// the data gives it a stopping spot; else the end of its road (the middle of a
    /// grove or a mountain side is no tile to walk to), from where the bot goes on by
    /// what it sees.</summary>
    private BotDestination TripGoalFor(BotDestination place)
    {
        if (place.Arrivals.Count > place.BadArrivals.Count || place.NearestWaypoint < 0 ||
            place.NearestWaypoint >= Nav.Waypoints.Count)
            return place;
        return new BotDestination
        {
            Name = place.Name,
            Type = place.Type,
            City = place.City,
            Pos = Nav.Waypoints[place.NearestWaypoint].Pos,
            NearestWaypoint = place.NearestWaypoint,
        };
    }

    private static bool AtPlace(Point3D pos, BotWork w)
    {
        var place = w.Place;
        if (pos.Map != place.Pos.Map) return false;
        return pos.GetDistanceTo(place.Pos) <= WorkNearRange || pos.GetDistanceTo(w.TripGoal.Pos) <= 3 ||
               place.ArrivalAt(pos) >= 0 || (place.Outline != null && place.Encloses(pos.X, pos.Y));
    }

    private bool BeginJob(BotMind mind, BotJob job, SkillType skill, BotDestination place, long now,
        ItemType saleType = ItemType.Normal)
    {
        var ch = mind.Char;
        var w = new BotWork
        {
            Job = job,
            Skill = skill,
            Place = place,
            TripGoal = TripGoalFor(place),
            SaleType = saleType,
            StartedMs = now,
        };
        mind.Work = w;
        ch.SetTag(WorkTag, $"{job}|{skill}|{place.Name}|{saleType}");
        WindowWorkTrips++;
        if (AtPlace(ch.Position, w))
        {
            mind.Note(now, $"{JobWords(w)} here, at {place.Name}");
            EnterSite(mind, w, now);
            return true;
        }
        // The trip is the ordinary one; the job's tag, not the goal's, is what a
        // restart takes it up from.
        mind.Goal = w.TripGoal;
        ch.RemoveTag(GoalTag);
        mind.Mode = BotMode.Travel;
        mind.Route = null;
        mind.Haunting = false;
        mind.NoGoalCount = 0;
        mind.NextActMs = now + 200;
        mind.Note(now, $"off to {place.Name}: {JobWords(w)}");
        return true;
    }

    /// <summary>The trip of a job has arrived: the job starts. False for any other trip.</summary>
    private bool ArriveAtWork(BotMind mind, BotDestination goal, long now)
    {
        if (mind.Work is not { Stage: BotWorkStage.Trip } w || !ReferenceEquals(goal, w.TripGoal)) return false;
        EnterSite(mind, w, now);
        return true;
    }

    private void EnterSite(BotMind mind, BotWork w, long now)
    {
        mind.BeginLinger(w.Place, now, _rng, short_: true);
        mind.Mode = w.Job == BotJob.Sell ? BotMode.Sell : BotMode.Work;
        w.Stage = BotWorkStage.Site;
        w.UntilMs = now + (w.Job is BotJob.Gather or BotJob.Craft ? _rng.Next(WorkMinMs, WorkMaxMs + 1) : 240_000);
        mind.ResetLeg(now);
        mind.NextActMs = now + _rng.Next(800, 2500);
    }

    /// <summary>Forget the job in hand without a word (the bot's plans were reset).</summary>
    private static void DropWork(BotMind mind)
    {
        if (mind.Work == null) return;
        mind.Work = null;
        mind.Char.RemoveTag(WorkTag);
    }

    /// <summary>The job is over: the bot stands a few moments, then chooses again -
    /// which takes ore on to the forge and goods on to a vendor.</summary>
    private void EndJob(BotMind mind, long now, string why)
    {
        var w = mind.Work;
        var ch = mind.Char;
        mind.Work = null;
        ch.RemoveTag(WorkTag);
        if (mind.Client is { HasPendingTarget: true } client) CancelCursor(client);
        mind.Note(now, w == null ? $"work over: {why}" : $"{JobWords(w)} at {w.Place.Name} over ({w.Done} done): {why}");
        if (mind.Mode is not (BotMode.Work or BotMode.Sell)) return;
        mind.BeginLinger(w?.Place ?? mind.At, now, _rng, short_: true);
        mind.LingerUntilMs = now + _rng.Next(3000, 9000);
        // Back on its horse before it leaves.
        mind.NextMountCheckMs = now;
    }

    /// <summary>The Esc a player presses on a target cursor.</summary>
    private static void CancelCursor(Clients.GameClient client) =>
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, 0, -1, -1, 0, 0);

    private static string JobWords(BotWork w) => w.Job switch
    {
        BotJob.Gather => "works its " + SkillWords(w.Skill),
        BotJob.Smelt => "smelts its ore",
        BotJob.Craft => "plies its " + SkillWords(w.Skill),
        _ => "sells its goods",
    };

    /// <summary>What it is doing, for the bot's brain.</summary>
    private static string WorkWords(BotMind m) => m.Work switch
    {
        { Job: BotJob.Gather } w => "at work: " + SkillWords(w.Skill),
        { Job: BotJob.Smelt } => "smelting ore at the forge",
        { Job: BotJob.Craft } w => "at your craft: " + SkillWords(w.Skill),
        { Job: BotJob.Sell } => "selling your goods to a vendor",
        _ => "working",
    };

    /// <summary><c>{work}</c> for "what are you doing": the word of the language's
    /// <c>token_work</c> list for the job (<c>Mining=mining</c>, <c>Smelt=...</c>,
    /// <c>Sell=...</c>), else the skill's own name.</summary>
    private Dictionary<string, string> WorkTokens(BotMind m, string lang)
    {
        string key = m.Work switch
        {
            { Job: BotJob.Smelt } => "Smelt",
            { Job: BotJob.Sell } => "Sell",
            { } w => w.Skill.ToString(),
            _ => "Work",
        };
        string? word = null;
        for (int i = 0; Chat.Line("token_work", i, lang) is { } line; i++)
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                word = line[(eq + 1)..].Trim();
                break;
            }
        }
        return new Dictionary<string, string> { ["work"] = word ?? SkillWords(m.Work?.Skill ?? SkillType.None) };
    }

    private static int PackLoadPercent(Character ch)
    {
        long max = (long)Math.Max(0, ch.MaxWeight) * Item.WeightUnits;
        return max <= 0 ? 100 : (int)(ch.GetTotalWeightTenths() * 100L / max);
    }

    /// <summary>How much the pack holds, gold aside: a swing, a smelt or a craft that
    /// adds to it has made something.</summary>
    private int PackUnits(Character ch)
    {
        if (ch.Backpack is not { } pack) return 0;
        int n = 0;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.ItemType != ItemType.Gold) n += Math.Max(1, (int)item.Amount);
        return n;
    }

    // ------------------------------------------------------------------
    //  One act of work
    // ------------------------------------------------------------------

    private void ActWork(BotMind mind, long now)
    {
        var ch = mind.Char;
        var w = mind.Work;
        if (w == null || mind.Client == null || w.Stage != BotWorkStage.Site || w.Job == BotJob.Sell)
        {
            EndJob(mind, now, "no job in hand");
            return;
        }
        // Somebody has a call on it: a player it goes about with, a favour, a party.
        if (!FreeForWork(mind))
        {
            EndJob(mind, now, "called away");
            return;
        }
        if (ch.IsPoisoned && now >= mind.NextCureMs && TryCure(mind, now))
        {
            mind.NextActMs = now + 600;
            return;
        }
        switch (w.Job)
        {
            case BotJob.Gather:
                ActGather(mind, w, now);
                break;
            case BotJob.Smelt:
                ActSmelt(mind, w, now);
                break;
            default:
                ActCraft(mind, w, now);
                break;
        }
    }

    /// <summary>The pause between two actions, as a person takes it: a breath, now
    /// and then a look about or a word to whoever stands by.</summary>
    private void WorkPause(BotMind mind, long now, int minMs, int maxMs, string? talk)
    {
        long pause = _rng.Next(minMs, maxMs);
        if (_rng.Next(100) < WorkLookAroundPercent)
        {
            var ch = mind.Char;
            ch.FaceToward(new Point3D((short)(ch.X + _rng.Next(-3, 4)), (short)(ch.Y + _rng.Next(-3, 4)), ch.Z, ch.MapIndex));
            pause += _rng.Next(2000, 6000);
            if (talk != null && _rng.Next(100) < 50) Speak(mind, talk, fallback: null);
        }
        mind.NextActMs = now + pause;
    }

    // ------------------------------------------------------------------
    //  Gathering
    // ------------------------------------------------------------------

    private static readonly string[] SpentTileMessages =
    [
        Msg.Mining1, Msg.Mining2, Msg.Mining3, Msg.Mining4, Msg.MiningReach, Msg.MiningLos, Msg.MiningClose,
        Msg.Fishing1, Msg.Fishing2, Msg.Fishing4, Msg.FishingReach, Msg.FishingLos, Msg.FishingClose,
        Msg.Lumberjacking1, Msg.Lumberjacking2, Msg.Lumberjacking3, Msg.Lumberjacking4, Msg.Lumberjacking6,
        Msg.LumberjackingReach, Msg.LumberjackingLos, Msg.LumberjackingClose,
    ];

    private static readonly string[] NoToolMessages = [Msg.MiningTool, Msg.LumberjackingTool];
    private static readonly string[] NoPlaceMessages = [Msg.Fishing3];

    /// <summary>Whether the bot was told one of the engine's messages (by its key).</summary>
    private static bool Heard(BotWork w, string[] keys)
    {
        foreach (string text in w.Told)
            foreach (string key in keys)
                if (string.Equals(text, ServerMessages.Get(key), StringComparison.Ordinal))
                    return true;
        return false;
    }

    private void ActGather(BotMind mind, BotWork w, long now)
    {
        var ch = mind.Char;
        var client = mind.Client!;
        // The swing under way: its strokes are the engine's to time.
        if (w.Busy)
        {
            if (ch.HasActiveSkillPending())
            {
                mind.NextActMs = now + 500;
                return;
            }
            w.Busy = false;
            FinishSwing(mind, w, now, ran: true);
            return;
        }
        BotGatherTrade? trade = null;
        foreach (var g in _gatherTrades)
            if (g.Skill == w.Skill) { trade = g; break; }
        if (trade == null)
        {
            EndJob(mind, now, "no such trade");
            return;
        }
        if (now >= w.UntilMs)
        {
            if (w.Done > 0 && _rng.Next(100) < 40) Speak(mind, "gather_haul", fallback: null);
            EndJob(mind, now, "shift over");
            return;
        }
        if (GatherTool(ch, trade) is not { } tool)
        {
            EndJob(mind, now, "no tool");
            return;
        }
        // On foot, as the work wants it.
        if (ch.IsMounted && w.ToolTries < 2)
        {
            w.ToolTries++;
            client.HandleDoubleClick(ch.Uid.Value);
            mind.NextActMs = now + _rng.Next(800, 1600);
            return;
        }
        // The pack's skill reads the tool from the hand (SRC.WEAPON): it is taken up
        // first, with the double click that equips a tool from the pack (Cmd_Use_Item,
        // CClientUse.cpp:113-131), and the cursor that opens is let go.
        if (ToolInHand(w.Skill) && !tool.IsEquipped)
        {
            if (++w.EquipTries > 2)
            {
                RememberUseless(w.Place, OwnKey(mind, MemoKey(w)), now);
                NoteMisstart(mind, w.Skill, now);
                EndJob(mind, now, "the tool will not be taken up");
                return;
            }
            client.HandleDoubleClick(tool.Uid.Value);
            if (client.HasPendingTarget) CancelCursor(client);
            mind.NextActMs = now + _rng.Next(800, 1600);
            return;
        }

        if (w.Tile == null && !FindWork(mind, w, now)) return;
        var tile = w.Tile!.Value;
        int dist = ch.Position.GetDistanceTo(tile);
        int reach = SkillEngine.GetUseRange(w.Skill, w.Skill == SkillType.Fishing ? 4 : 2);
        if (dist < 1)
        {
            // Standing on it: another tile.
            w.Spent.Add((tile.X, tile.Y));
            w.Tile = null;
            mind.NextActMs = now + 300;
            return;
        }
        if (dist > reach)
        {
            bool walked = StepToward(mind, tile, run: false, searchWithin: 16);
            if (mind.NoGainMs("work", tile, dist, now) > 12_000)
            {
                mind.Note(now, $"cannot get to {tile}; another tile");
                TileGaveNothing(mind, w, now);
                return;
            }
            mind.NextActMs = now + StepDelay(mind, walked, false, now);
            return;
        }

        // The swing: a double click on the tool, its cursor answered on the tile.
        w.ForgetTold();
        w.UnitsBefore = PackUnits(ch);
        client.HandleDoubleClick(tool.Uid.Value);
        if (!client.HasPendingTarget)
        {
            FinishSwing(mind, w, now, ran: false);
            return;
        }
        client.HandleTargetResponse(1, client.ActiveTargetCursorId, 0, tile.X, tile.Y, tile.Z, w.TileGraphic);
        if (ch.HasActiveSkillPending())
        {
            w.Busy = true;
            mind.NextActMs = now + 600;
            return;
        }
        FinishSwing(mind, w, now, ran: false);
    }

    /// <summary>The swing is over: what came of it. A gain in the pack is a success;
    /// "nothing here" and "cannot reach" spend the tile; a swing that ran and gave
    /// nothing was a failed skill check and is simply tried again; one that never
    /// started and said nothing the bot knows counts toward giving the place up.</summary>
    private void FinishSwing(BotMind mind, BotWork w, long now, bool ran)
    {
        var ch = mind.Char;
        bool gained = PackUnits(ch) > w.UnitsBefore;
        bool spent = Heard(w, SpentTileMessages);
        bool noTool = Heard(w, NoToolMessages);
        bool noPlace = Heard(w, NoPlaceMessages);
        w.ForgetTold();
        if (gained)
        {
            w.Done++;
            w.Misfires = 0;
            w.DryTiles = 0;
            mind.TradeMisstarts.Remove(w.Skill);
            WindowGathers++;
            if (PackLoadPercent(ch) >= WorkLoadPercent)
            {
                if (_rng.Next(100) < 50) Speak(mind, "gather_haul", fallback: null);
                EndJob(mind, now, "pack full");
                return;
            }
        }
        if (noTool)
        {
            NoteMisstart(mind, w.Skill, now);
            EndJob(mind, now, "no tool");
            return;
        }
        if (noPlace)
        {
            RememberUseless(w.Place, MemoKey(w), now);
            EndJob(mind, now, "no working here");
            return;
        }
        if (spent)
        {
            if (!gained) w.DryTiles++;
            if (w.Tile is { } t) w.Spent.Add((t.X, t.Y));
            w.Tile = null;
        }
        else if (!gained && !ran && ++w.Misfires >= 3)
        {
            // Nothing happens and nothing the bot understands is said ("The tool is out
            // of charges", a script's own refusal): not here today, and twice in a row
            // not this trade for a while.
            RememberUseless(w.Place, OwnKey(mind, MemoKey(w)), now);
            NoteMisstart(mind, w.Skill, now);
            EndJob(mind, now, "the work will not start");
            return;
        }
        else if (!gained && ran && ++w.Fails >= MaxFailedSwings)
        {
            // Swing after swing with nothing to show for it (a hard vein, or a catch
            // the pack has no room for): somewhere else.
            w.Fails = 0;
            w.DryTiles++;
            if (w.Tile is { } hard) w.Spent.Add((hard.X, hard.Y));
            w.Tile = null;
        }
        if (gained) w.Fails = 0;
        if (w.DryTiles >= MaxDryTiles)
        {
            RememberUseless(w.Place, MemoKey(w), now);
            EndJob(mind, now, "worked out");
            return;
        }
        WorkPause(mind, now, 700, 2200, w.Skill == SkillType.Fishing ? "fishing" : "gather_talk");
    }

    private void TileGaveNothing(BotMind mind, BotWork w, long now)
    {
        if (w.Tile is { } t) w.Spent.Add((t.X, t.Y));
        w.Tile = null;
        if (++w.DryTiles >= MaxDryTiles)
        {
            RememberUseless(w.Place, MemoKey(w), now);
            EndJob(mind, now, "nothing in reach");
            return;
        }
        mind.NextActMs = now + _rng.Next(400, 1000);
    }

    /// <summary>Find a tile to work: one in sight, else a few steps on into the place
    /// and another look. False when this act was spent on it (or the job ended).</summary>
    private bool FindWork(BotMind mind, BotWork w, long now)
    {
        var ch = mind.Char;
        if (now >= w.NextScanMs)
        {
            w.NextScanMs = now + WorkScanMs;
            if (FindResourceTile(mind, w) is { } found)
            {
                w.Tile = found.Point;
                w.TileGraphic = found.Graphic;
                w.RoamTo = null;
                return true;
            }
            if (w.RoamTo == null)
            {
                if (++w.Roams > MaxRoams)
                {
                    RememberUseless(w.Place, MemoKey(w), now);
                    EndJob(mind, now, "nothing to work here");
                    return false;
                }
                w.RoamTo = RoamPoint(mind, w);
            }
        }
        if (w.RoamTo is { } to)
        {
            int dist = ch.Position.GetDistanceTo(to);
            if (dist <= 1 || mind.NoGainMs("roam", to, dist, now) > 8000)
            {
                w.RoamTo = null;
                mind.NextActMs = now + 400;
                return false;
            }
            bool walked = StepToward(mind, to, run: false, searchWithin: 24);
            mind.NextActMs = now + StepDelay(mind, walked, false, now);
            return false;
        }
        mind.NextActMs = Math.Max(now + 400, w.NextScanMs);
        return false;
    }

    /// <summary>Somewhere further into the place to look from: a tile inside its
    /// outline when the data draws one, else about its middle.</summary>
    private Point3D RoamPoint(BotMind mind, BotWork w)
    {
        var place = w.Place;
        var here = mind.Char.Position;
        for (int i = 0; i < 6; i++)
        {
            int x, y;
            if (place.Outline is { Count: > 2 } outline)
            {
                var a = outline[_rng.Next(outline.Count)];
                // A point between a corner of the outline and the middle lies inside most shapes.
                x = (a.X + place.Pos.X) / 2 + _rng.Next(-2, 3);
                y = (a.Y + place.Pos.Y) / 2 + _rng.Next(-2, 3);
                if (!place.Encloses(x, y)) continue;
            }
            else
            {
                x = place.Pos.X + _rng.Next(-6, 7);
                y = place.Pos.Y + _rng.Next(-6, 7);
            }
            var raw = new Point3D((short)x, (short)y, here.Z, here.Map);
            if (BotNavGraph.TrySeat(_world.MapData, raw, out var seat) && FindTeleport(seat) == null)
                return seat;
        }
        return place.Pos;
    }

    private static readonly (sbyte Dx, sbyte Dy)[] ScanOffsets = BuildScanOffsets(GatherScanRadius);

    /// <summary>The tiles about a point, nearest first.</summary>
    private static (sbyte, sbyte)[] BuildScanOffsets(int radius)
    {
        var list = new List<(sbyte Dx, sbyte Dy)>();
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                if (dx != 0 || dy != 0) list.Add(((sbyte)dx, (sbyte)dy));
        return [.. list.OrderBy(o => Math.Max(Math.Abs(o.Dx), Math.Abs(o.Dy))).ThenBy(o => o.Dx * o.Dx + o.Dy * o.Dy)];
    }

    private readonly List<(Point3D Point, ushort Graphic)> _tilePicks = [];

    /// <summary>A tile of the skill's resource - rock, a tree, water - within
    /// <see cref="GatherScanRadius"/> that the bot has not found empty: one of the
    /// nearest few. What a tile is, is read as the gathering engine reads it: an item
    /// on it, a static of the map, the land itself (the pack's TYPEDEF TERRAIN).
    /// One bounded look, asked for at most every <see cref="WorkScanMs"/>.</summary>
    private (Point3D Point, ushort Graphic)? FindResourceTile(BotMind mind, BotWork w)
    {
        var type = GatheringEngine.ResourceTypeFor(w.Skill);
        if (type == ItemType.Invalid) return null;
        var here = mind.Char.Position;
        var map = _world.MapData;
        Dictionary<(int, int), Item>? lying = null;
        foreach (var item in _world.GetItemsInRange(here, GatherScanRadius))
        {
            if (item.IsDeleted || !item.IsOnGround || item.MapIndex != here.Map) continue;
            // The engine's own resource bit lies on a worked tile and is of the type
            // too: nobody sees it, and it tells nothing a player would know.
            if (item.BaseId == GatheringEngine.MarkerGraphic) continue;
            if (item.ItemType != type && DefinitionLoader.GetItemDef(item.BaseId)?.Type != type) continue;
            (lying ??= [])[(item.X, item.Y)] = item;
        }
        _tilePicks.Clear();
        int firstDist = int.MaxValue;
        foreach (var (dx, dy) in ScanOffsets)
        {
            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (_tilePicks.Count > 0 && dist > firstDist + 1) break;
            int x = here.X + dx, y = here.Y + dy;
            if (x < 0 || y < 0 || w.Spent.Contains((x, y))) continue;
            Point3D point = default;
            ushort graphic = 0;
            bool hit = false;
            if (lying != null && lying.TryGetValue((x, y), out var on))
            {
                point = on.Position;
                graphic = on.DispIdFull;
                hit = true;
            }
            else if (map != null)
            {
                foreach (var s in map.GetStatics(here.Map, x, y))
                {
                    if (DefinitionLoader.GetItemDef(s.TileId)?.Type != type) continue;
                    point = new Point3D((short)x, (short)y, s.Z, here.Map);
                    graphic = s.TileId;
                    hit = true;
                    break;
                }
                if (!hit)
                {
                    var cell = map.GetTerrainTile(here.Map, x, y);
                    if (NaturalResourceTiles.TerrainIsType(cell.TileId, type))
                    {
                        point = new Point3D((short)x, (short)y, cell.Z, here.Map);
                        hit = true;
                    }
                }
            }
            if (!hit) continue;
            // Water is fished where it is the top of the tile, not under a pier.
            if (type == ItemType.Water && !NaturalResourceTiles.IsTypeNearTop(_world, point, type)) continue;
            _tilePicks.Add((point, graphic));
            if (dist < firstDist) firstDist = dist;
            if (_tilePicks.Count >= 4) break;
        }
        return _tilePicks.Count == 0 ? null : _tilePicks[_rng.Next(_tilePicks.Count)];
    }

    // ------------------------------------------------------------------
    //  The forge, the fire
    // ------------------------------------------------------------------

    private static readonly ItemType[] ForgeTypes = [ItemType.Forge];
    private static readonly ItemType[] HeatTypes = [ItemType.Forge, ItemType.Fire, ItemType.Campfire];

    /// <summary>The nearest tile within eight that holds a thing of one of the types:
    /// an item lying there or a static of the map (a forge, a fire). One bounded look.</summary>
    private Point3D? FindSite(Point3D here, ItemType[] types)
    {
        const int radius = 8;
        Point3D? best = null;
        int bestDist = int.MaxValue;
        foreach (var item in _world.GetItemsInRange(here, radius))
        {
            if (item.IsDeleted || !item.IsOnGround || item.MapIndex != here.Map) continue;
            if (Array.IndexOf(types, item.ItemType) < 0 &&
                (DefinitionLoader.GetItemDef(item.BaseId) is not { } def || Array.IndexOf(types, def.Type) < 0))
                continue;
            int d = here.GetDistanceTo(item.Position);
            if (d < bestDist) { bestDist = d; best = item.Position; }
        }
        if (_world.MapData is not { } map) return best;
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (d >= bestDist) continue;
                int x = here.X + dx, y = here.Y + dy;
                if (x < 0 || y < 0) continue;
                foreach (var s in map.GetStatics(here.Map, x, y))
                {
                    if (DefinitionLoader.GetItemDef(s.TileId) is not { } def || Array.IndexOf(types, def.Type) < 0) continue;
                    bestDist = d;
                    best = new Point3D((short)x, (short)y, s.Z, here.Map);
                    break;
                }
            }
        return best;
    }

    /// <summary>Stand within <paramref name="reach"/> of the forge or fire of the
    /// place. True when there; false when this act went on finding or walking to it
    /// (or the job ended: no such thing here, or no way to it).</summary>
    private bool GoToSite(BotMind mind, BotWork w, long now, int reach, ItemType[] types)
    {
        var ch = mind.Char;
        if (w.Site == null)
        {
            if (now < w.NextScanMs)
            {
                mind.NextActMs = w.NextScanMs;
                return false;
            }
            w.NextScanMs = now + WorkScanMs;
            w.Site = FindSite(ch.Position, types);
            if (w.Site == null)
            {
                if (++w.SiteTries > 3)
                {
                    RememberUseless(w.Place, MemoKey(w), now);
                    EndJob(mind, now, "nothing to work at here");
                    return false;
                }
                // Nearer the middle of the place, and another look.
                bool stepped = StepToward(mind, w.Place.Pos, run: false, searchWithin: 16);
                mind.NextActMs = now + (stepped ? StepDelay(mind, true, false, now) : WorkScanMs);
                w.NextScanMs = now + 1200;
                return false;
            }
        }
        var site = w.Site.Value;
        int dist = ch.Position.GetDistanceTo(site);
        if (dist <= reach) return true;
        bool walked = StepToward(mind, site, run: false, searchWithin: 16);
        if (mind.NoGainMs("site", site, dist, now) > 15_000)
        {
            RememberUseless(w.Place, MemoKey(w), now);
            EndJob(mind, now, $"cannot get to {site}");
            return false;
        }
        mind.NextActMs = now + StepDelay(mind, walked, false, now);
        return false;
    }

    // ------------------------------------------------------------------
    //  Smelting
    // ------------------------------------------------------------------

    private static readonly string[] NoForgeMessages = [Msg.MiningForge];

    /// <summary>Whether the bot's Mining is enough to smelt this ore: the ingot its
    /// definition names (TDATA1) asks a least skill (the ingot's TDATA1). Ore whose
    /// ingot is unknown is tried.</summary>
    private bool CanSmeltOre(Character ch, Item ore)
    {
        if (_wontMelt.Count > 0 && _wontMelt.TryGetValue(ore.Uid.Value, out long until) && NowMs() < until) return false;
        var def = DefinitionLoader.GetItemDef(ResourceMatch.ItemDefIndexOf(ore)) ?? DefinitionLoader.GetItemDef(ore.BaseId);
        if (def == null) return true;
        int ingotIndex = !string.IsNullOrWhiteSpace(def.TData1Name)
            ? DefinitionLoader.ResolveItemDefIndexByName(def.TData1Name)
            : 0;
        if (ingotIndex == 0) ingotIndex = (int)def.TData1;
        var ingot = ingotIndex != 0 ? DefinitionLoader.GetItemDef(ingotIndex) : null;
        return ingot == null || ch.GetSkill(SkillType.Mining) >= ingot.TData1;
    }

    /// <summary>Piles a forge took nothing of, and until when they are not carried to
    /// one again (they are sold instead).</summary>
    private readonly Dictionary<uint, long> _wontMelt = [];

    /// <summary>A pile of ore in the pack this bot would smelt: it mines for a living
    /// (the skill of a trade) and the ore is within its skill.</summary>
    private Item? FirstOre(BotMind mind)
    {
        var ch = mind.Char;
        if (ch.Backpack is not { } pack || !HasSkillFor(ch, SkillType.Mining)) return null;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.ItemType == ItemType.Ore && CanSmeltOre(ch, item))
                return item;
        return null;
    }

    private void ActSmelt(BotMind mind, BotWork w, long now)
    {
        var ch = mind.Char;
        var client = mind.Client!;
        if (FirstOre(mind) is not { } ore || now >= w.UntilMs)
        {
            EndJob(mind, now, "ore smelted");
            return;
        }
        if (!GoToSite(mind, w, now, 2, ForgeTypes)) return;

        // As a player does it: a double click on the ore, beside the forge.
        w.ForgetTold();
        uint uid = ore.Uid.Value;
        int before = ore.Amount;
        client.HandleDoubleClick(uid);
        if (Heard(w, NoForgeMessages))
        {
            // Not near enough after all, or no line to it: look again.
            w.Site = null;
            if (++w.SiteTries > 3)
            {
                RememberUseless(w.Place, MemoKey(w), now);
                EndJob(mind, now, "the forge will not take it");
                return;
            }
        }
        else if (!ore.IsDeleted && ore.Amount == before)
        {
            // The forge took nothing of it (an ore above its skill after all, a pile
            // that may not be melted): that pile is goods from now on, not work.
            if (_wontMelt.Count > 256)
                foreach (uint old in _wontMelt.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                    _wontMelt.Remove(old);
            _wontMelt[uid] = now + 4 * WorkMemoMs;
        }
        else
        {
            w.Done++;
            WindowSmelts++;
        }
        w.ForgetTold();
        WorkPause(mind, now, 1500, 3200, "smith_talk");
    }

    // ------------------------------------------------------------------
    //  Crafting
    // ------------------------------------------------------------------

    private readonly Dictionary<SkillType, List<CraftRecipe>> _tradeRecipes = [];
    private CraftingEngine? _recipesOf;
    private int _recipesCount;

    /// <summary>The recipes of a craft that are made of its trade's materials alone,
    /// hardest first. Built once per craft (again when the recipes were reloaded).</summary>
    private List<CraftRecipe> TradeRecipes(CraftingEngine engine, BotCraftTrade trade)
    {
        if (!ReferenceEquals(_recipesOf, engine) || _recipesCount != engine.AllRecipes.Count)
        {
            _tradeRecipes.Clear();
            _recipesOf = engine;
            _recipesCount = engine.AllRecipes.Count;
        }
        if (_tradeRecipes.TryGetValue(trade.Skill, out var list)) return list;
        list = [];
        var offered = MenuPieces(trade.Skill);
        foreach (var recipe in engine.AllRecipes.Values)
        {
            if (recipe.PrimarySkill != trade.Skill || recipe.Resources.Count == 0) continue;
            if (offered != null && !offered.Contains(recipe.ResultDefId)) continue;
            bool fits = true;
            foreach (var res in recipe.Resources)
            {
                if (res.Rid.Type == ResType.SkillDef) continue;
                ItemType type = res.Type ?? DefinitionLoader.GetItemDef(res.Rid.Index)?.Type ?? ItemType.Invalid;
                if (Array.IndexOf(trade.Materials, type) >= 0 && res.Amount <= MaxRecipeMaterial) continue;
                fits = false;
                break;
            }
            if (fits) list.Add(recipe);
        }
        list.Sort((a, b) => b.Difficulty.CompareTo(a.Difficulty));
        _tradeRecipes[trade.Skill] = list;
        return list;
    }

    /// <summary>The pieces the script pack's own menu of a craft offers: every
    /// MAKEITEM of the skill's SKILLMENU and the menus under it (a tailor has two:
    /// cloth and leather). A bot makes what a player picks from that menu, not every
    /// definition that happens to carry a SKILLMAKE line. Null when the pack has no
    /// such menu (or no scripts are loaded): then every recipe of the craft counts.</summary>
    private HashSet<int>? MenuPieces(SkillType skill)
    {
        if (_resources == null) return null;
        var menus = new Queue<string>();
        menus.Enqueue(Clients.ClientWorldFeaturesHandler.DefaultSkillMenu(skill));
        if (skill == SkillType.Tailoring) menus.Enqueue("sm_tailor_leather");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pieces = new HashSet<int>();
        while (menus.Count > 0 && seen.Count < 256)
        {
            string name = menus.Dequeue();
            if (name.Length == 0 || !seen.Add(name)) continue;
            var rid = _resources.ResolveDefName(name);
            if (!rid.IsValid || rid.Type != ResType.SkillMenu || _resources.GetResource(rid)?.StoredKeys is not { } keys)
                continue;
            foreach (var key in keys)
            {
                string arg = key.Arg.Trim();
                int cut = arg.IndexOfAny([',', ' ', '\t']);
                if (cut > 0) arg = arg[..cut];
                if (key.KeyUpper == "SKILLMENU")
                    menus.Enqueue(arg);
                else if (key.KeyUpper == "MAKEITEM" && DefinitionLoader.ResolveItemDefIndexByName(arg) is > 0 and var index)
                    pieces.Add(index);
            }
        }
        return pieces.Count > 0 ? pieces : null;
    }

    // The trade's materials in the pack, by item type and by definition: what a
    // recipe is measured against before the engine is asked.
    private readonly Dictionary<ItemType, int> _stockByType = [];
    private readonly Dictionary<int, int> _stockByDef = [];

    private int CountMaterials(Character ch, BotCraftTrade trade)
    {
        _stockByType.Clear();
        _stockByDef.Clear();
        if (ch.Backpack is not { } pack) return 0;
        int n = 0;
        foreach (var item in _world.GetContainerContents(pack.Uid))
        {
            if (item.IsDeleted || Array.IndexOf(trade.Materials, item.ItemType) < 0) continue;
            int amount = Math.Max(1, (int)item.Amount);
            n += amount;
            _stockByType[item.ItemType] = _stockByType.GetValueOrDefault(item.ItemType) + amount;
            int def = ResourceMatch.ItemDefIndexOf(item);
            _stockByDef[def] = _stockByDef.GetValueOrDefault(def) + amount;
        }
        return n;
    }

    /// <summary>Whether the materials just counted may cover a recipe's bill: a TYPE
    /// entry by the type, a definition by that definition - or, for wood and hides,
    /// by the family (the engine takes boards for logs and leather for hides). A
    /// rough answer; the engine has the last word.</summary>
    private bool StockMayCover(CraftRecipe recipe)
    {
        foreach (var res in recipe.Resources)
        {
            var rid = res.Rid;
            if (rid.Type == ResType.SkillDef) continue;
            if (res.Type is { } type)
            {
                if (_stockByType.GetValueOrDefault(type) < res.Amount) return false;
                continue;
            }
            if (_stockByDef.GetValueOrDefault(rid.Index) >= res.Amount) continue;
            int family = DefinitionLoader.GetItemDef(rid.Index)?.Type switch
            {
                ItemType.Log or ItemType.Board =>
                    _stockByType.GetValueOrDefault(ItemType.Log) + _stockByType.GetValueOrDefault(ItemType.Board),
                ItemType.Hide or ItemType.Leather =>
                    _stockByType.GetValueOrDefault(ItemType.Hide) + _stockByType.GetValueOrDefault(ItemType.Leather),
                _ => 0,
            };
            if (family < res.Amount) return false;
        }
        return true;
    }

    /// <summary>What to make: of the trade's recipes within the bot's skill, the
    /// hardest it has the skills, the tools and the materials for (so it trains), now
    /// and then the next one down. A piece that lately found no buyer is made only
    /// when nothing else can be. Only a few are looked at closely.</summary>
    private CraftRecipe? PickRecipe(BotMind mind, BotCraftTrade trade, CraftingEngine engine)
    {
        var ch = mind.Char;
        int skill = ch.GetSkill(trade.Skill);
        if (CountMaterials(ch, trade) <= 0) return null;
        long now = NowMs();
        CraftRecipe? first = null, second = null, unsold = null;
        int tried = 0;
        foreach (var recipe in TradeRecipes(engine, trade))
        {
            if (recipe.Difficulty * 10 > skill || !StockMayCover(recipe)) continue;
            bool skilled = true;
            foreach (var (s, min) in recipe.SkillRequirements)
                if (ch.GetSkill(s) < min) { skilled = false; break; }
            if (!skilled) continue;
            bool poor = IsPoorSeller(recipe.ResultDefId, now);
            if (poor && unsold != null) continue;
            if (++tried > RecipeTries) break;
            if (!engine.CanCraft(ch, recipe, checkWorkSite: false)) continue;
            if (poor) unsold = recipe;
            else if (first == null) first = recipe;
            else { second = recipe; break; }
        }
        if (first == null) return unsold;
        return second != null && _rng.Next(100) < 35 ? second : first;
    }

    /// <summary>Whether the bot could ply this craft now: the skill, the tool, and
    /// the materials for something (for a cook, also a fish and the blade to cut it).</summary>
    private bool CanCraftNow(BotMind mind, BotCraftTrade trade)
    {
        var ch = mind.Char;
        return HasSkillFor(ch, trade.Skill) && mind.Client?.CraftE is { } engine && CraftTool(ch, trade) != null &&
               PackLoadPercent(ch) < 100 &&
               (PickRecipe(mind, trade, engine) != null || (trade.Skill == SkillType.Cooking && FishToCut(ch) != null));
    }

    private static readonly ItemType[] BladeTypes = [ItemType.WeaponFence, ItemType.WeaponSword];

    private Item? FindBlade(Character ch)
    {
        foreach (var type in BladeTypes)
            if (FindTool(ch, type) is { } blade) return blade;
        return null;
    }

    /// <summary>A whole fish in the pack, when the bot has a blade to cut it with.</summary>
    private Item? FishToCut(Character ch)
    {
        if (ch.Backpack is not { } pack || FindBlade(ch) == null) return null;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.ItemType == ItemType.Fish && item.BaseId != FishSteakGraphic) return item;
        return null;
    }

    /// <summary>The graphic a cut fish takes (raw fish steaks).</summary>
    private const ushort FishSteakGraphic = 0x097A;

    /// <summary>A cook cuts its catch into steaks before the pan: the blade double
    /// clicked, its cursor answered on the fish. True when this act went on that.</summary>
    private bool CutFish(BotMind mind, BotWork w, long now)
    {
        var ch = mind.Char;
        var client = mind.Client!;
        if (FishToCut(ch) is not { } fish || FindBlade(ch) is not { } blade || w.Skipped.Contains(fish.Uid.Value))
            return false;
        ushort whole = fish.BaseId;
        client.HandleDoubleClick(blade.Uid.Value);
        if (client.HasPendingTarget)
            client.HandleTargetResponse(0, client.ActiveTargetCursorId, fish.Uid.Value, fish.X, fish.Y, fish.Z, fish.DispIdFull);
        // A fish the blade left whole is not asked of it again.
        if (!fish.IsDeleted && fish.BaseId == whole) w.Skipped.Add(fish.Uid.Value);
        mind.NextActMs = now + _rng.Next(900, 2200);
        return true;
    }

    private int CountOfGraphic(Character ch, ushort graphic)
    {
        if (ch.Backpack is not { } pack) return 0;
        int n = 0;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.BaseId == graphic) n += Math.Max(1, (int)item.Amount);
        return n;
    }

    private static string CraftTalk(SkillType skill) => skill switch
    {
        SkillType.Blacksmithing => "smith_talk",
        SkillType.Tailoring => "tailor_talk",
        SkillType.Carpentry => "carpenter_talk",
        _ => "craft_talk",
    };

    /// <summary>What is made by the pile out of a pile.</summary>
    private static readonly ItemType[] PileTypes =
    [
        ItemType.Board, ItemType.Food, ItemType.FoodRaw, ItemType.MeatRaw, ItemType.Ingot, ItemType.Cloth,
        ItemType.Leather, ItemType.Kindling, ItemType.Shaft,
    ];

    private void ActCraft(BotMind mind, BotWork w, long now)
    {
        var ch = mind.Char;
        var client = mind.Client!;
        // The piece under way: its strokes are the engine's to time.
        if (w.Busy)
        {
            if (client.HasPendingCraft)
            {
                mind.NextActMs = now + 500;
                return;
            }
            w.Busy = false;
            // Made: one more of the piece in the pack than before.
            if (w.Recipe != null && CountOfGraphic(ch, w.Recipe.ResultItemId) > w.UnitsBefore)
            {
                w.Done++;
                WindowCrafts++;
                mind.TradeMisstarts.Remove(w.Skill);
            }
            w.ForgetTold();
            WorkPause(mind, now, 900, 2600, CraftTalk(w.Skill));
            return;
        }
        BotCraftTrade? trade = null;
        foreach (var c in _craftTrades)
            if (c.Skill == w.Skill) { trade = c; break; }
        if (trade == null || client.CraftE is not { } engine)
        {
            EndJob(mind, now, "no such craft");
            return;
        }
        if (now >= w.UntilMs)
        {
            EndJob(mind, now, "shift over");
            return;
        }
        if (CraftTool(ch, trade) is not { } tool)
        {
            EndJob(mind, now, "no tool");
            return;
        }
        // A smith's hammer is in the hand: taken up with a double click, as it is
        // for a player, and the cursor that opens is let go.
        if (trade.InHand && !tool.IsEquipped)
        {
            if (++w.ToolTries > 2)
            {
                RememberUseless(w.Place, OwnKey(mind, MemoKey(w)), now);
                NoteMisstart(mind, w.Skill, now);
                EndJob(mind, now, "the tool will not be taken up");
                return;
            }
            client.HandleDoubleClick(tool.Uid.Value);
            if (client.HasPendingTarget) CancelCursor(client);
            mind.NextActMs = now + _rng.Next(800, 1600);
            return;
        }
        int siteReach = CraftingEngine.WorkSiteRange(w.Skill);
        if (siteReach > 0 && !engine.HasRequiredWorkSite(ch, w.Skill) &&
            !GoToSite(mind, w, now, siteReach, w.Skill == SkillType.Cooking ? HeatTypes : ForgeTypes))
            return;

        var recipe = w.Recipe != null && engine.CanCraft(ch, w.Recipe, checkWorkSite: false)
            ? w.Recipe
            : PickRecipe(mind, trade, engine);
        if (recipe == null)
        {
            if (w.Skill == SkillType.Cooking && CutFish(mind, w, now)) return;
            EndJob(mind, now, "out of materials");
            return;
        }
        w.Recipe = recipe;
        // What picking the piece in the craft menu starts (the menu's MAKEITEM). Plain
        // work on a pile - boards of logs, steaks in the pan - is done several at a
        // time, as MAKEITEM's amount does it; a piece worth a quality roll one by one.
        int batch = recipe.Difficulty <= 0 &&
                    Array.IndexOf(PileTypes, DefinitionLoader.GetItemDef(recipe.ResultDefId)?.Type ?? ItemType.Invalid) >= 0
            ? _rng.Next(4, 11) : 1;
        w.ForgetTold();
        w.UnitsBefore = CountOfGraphic(ch, recipe.ResultItemId);
        if (!client.BeginPendingCraft(recipe, w.Skill, reopenGump: false, batch))
        {
            w.Recipe = null;
            w.Site = null;
            if (++w.Misfires >= 3)
            {
                RememberUseless(w.Place, OwnKey(mind, MemoKey(w)), now);
                NoteMisstart(mind, w.Skill, now);
                EndJob(mind, now, "the craft will not start");
                return;
            }
            mind.NextActMs = now + _rng.Next(1200, 2500);
            return;
        }
        w.Misfires = 0;
        w.Busy = true;
        mind.NextActMs = now + 500;
        // Full hands end the shift after this piece.
        if (PackLoadPercent(ch) >= WorkLoadPercent) w.UntilMs = now;
    }
}
