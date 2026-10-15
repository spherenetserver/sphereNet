using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Diagnostics.Behaviors;

namespace SphereNet.LoadTest;

/// <summary>
/// Functional drill: what a player does - buy from a vendor, sell back, use the bank -
/// played by a bot client against the real server on a copy of a shard, and checked by
/// what that client is shown (the status window's gold, container contents, journal).
///
/// Setup is done the way staff would do it, not by reaching into the server: the bot's
/// account is raised to GM on the console, the bot teleports itself (.go) and gives
/// itself gold (.newgold), and the account goes back to player before the scenario
/// runs, so the scenario itself is played with player rights.
///
///   dotnet SphereNet.LoadTest.dll functional --root &lt;shard copy&gt; [--suite economy]
///       [--vendor 0xAE65@1468,1670,0] [--banker 0xD0E4@1425,1690,0]
///   dotnet SphereNet.LoadTest.dll functional --root &lt;shard copy&gt; --probe "cmd;cmd"
/// </summary>
internal static class FunctionalDrill
{
    private sealed class Options
    {
        public string? Root;
        public string Suite = "all";
        public string? Probe;
        public string City = "Britain";
        public (uint Serial, string Point) Vendor = (0xAE65, "1468,1670,0");
        public (uint Serial, string Point) Banker = (0xD0E4, "1425,1690,0");
        public bool PayFromPackOnly;
        public (uint Serial, string Point) Healer = (0xD0E6, "1472,1610,20");
        public string ServerDll = Path.Combine(AppContext.BaseDirectory, "SphereNet.Server.dll");
        public string OutDir = Path.GetFullPath("functional-results");
    }

    private sealed record Outcome(string Suite, string Name, bool Passed, string Detail);

    private sealed class Idle : IBotBehavior
    {
        public string Name => "Idle";
        public Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private sealed class ScenarioFailed(string message) : Exception(message);

    public static async Task<int> RunAsync(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        Directory.CreateDirectory(o.OutDir);
        string root = Path.GetFullPath(o.Root!);
        int port = ReadIniPort(Path.Combine(root, "sphere.ini")) ?? 2593;
        o.PayFromPackOnly = ReadIniValue(Path.Combine(root, "sphere.ini"), "PayFromPackOnly") is "1";

        using var server = new ServerHost(o.ServerDll, root, ["--headless", "--trustloopback", "--botcity", o.City],
            Path.Combine(o.OutDir, "server-functional.log"));
        var outcomes = new List<Outcome>();
        using var cts = new CancellationTokenSource();
        try
        {
            await server.WaitForLineAsync(l => l.Contains("Type 'help' for commands", StringComparison.Ordinal),
                TimeSpan.FromMinutes(5), "server start");
            if (o.Probe != null)
            {
                foreach (string cmd in o.Probe.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    foreach (string line in await ConsoleAsync(server, cmd.Trim()))
                        Console.WriteLine($"[{cmd.Trim()}] {line}");
                return 0;
            }

            if (o.Suite is "all" or "economy")
                await EconomySuite(o, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "death")
                await DeathSuite(o, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "combat")
                await CombatSuite(o, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "craft")
                await CraftSuite(o, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "gather")
                await GatherSuite(o, root, server, port, outcomes, cts.Token);
        }
        catch (Exception ex)
        {
            outcomes.Add(new Outcome("drill", "setup", false, ex.Message.Split('\n')[0]));
        }
        finally
        {
            cts.Cancel();
            await server.StopAsync();
        }

        Console.WriteLine();
        foreach (var r in outcomes)
            Console.WriteLine($"[functional] {(r.Passed ? "PASS" : "FAIL")} {r.Suite}/{r.Name}: {r.Detail}");
        string outPath = Path.Combine(o.OutDir, "functional-report.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(outcomes, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[functional] {outcomes.Count(r => r.Passed)}/{outcomes.Count} passed - report: {outPath}");
        return outcomes.All(r => r.Passed) ? 0 : 1;
    }

    // ---- economy ---------------------------------------------------------------

    private static async Task EconomySuite(Options o, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var bot = await LoginAsync(901, port, ct);
        string account = bot.AccountName;
        const int StartGold = 5000;

        await RunScenario("economy", "setup: gold and position", outcomes, async () =>
        {
            await AsStaff(server, account, async () =>
            {
                await SayAsync(bot, $".go {o.Vendor.Point}", ct);
                await SayAsync(bot, $".newgold {StartGold}", ct);
            });
            int gold = await RefreshGoldAsync(bot, ct);
            if (gold < StartGold) throw new ScenarioFailed($"status shows {gold} gold after .newgold {StartGold}");
            return $"{gold} gold in the pack";
        });

        BotVendorItem? bought = null;
        await RunScenario("economy", "buy from a vendor", outcomes, async () =>
        {
            await ApproachAsync(bot, o.Vendor.Serial, ct);
            int goldBefore = await RefreshGoldAsync(bot, ct);
            bot.World.ActiveVendor = null;
            await SayAsync(bot, "vendor buy", ct);
            var shop = await WaitAsync(() => bot.World.ActiveVendor is { IsBuyList: true, VendorSerial: not 0 } v && v.Items.Count > 0
                ? bot.World.ActiveVendor : null, 8000, "a buy window (0x74 + 0x24 gump 0x30)");
            // The cheapest at 10 or more: a 1-gold item sells back for 0 (Source-X
            // writes the computed price, 0 included), and the sell scenario needs a price.
            var affordable = shop.Items.Where(i => i.Serial != 0 && i.Price > 0 && i.Price <= goldBefore).ToList();
            var item = affordable.Where(i => i.Price >= 10).OrderBy(i => i.Price).FirstOrDefault()
                ?? affordable.OrderBy(i => i.Price).FirstOrDefault()
                ?? throw new ScenarioFailed($"no affordable entry with a serial among {shop.Items.Count}");
            var packBefore = await PackContentsAsync(bot, ct);
            int haveBefore = packBefore.Where(i => i.ItemId == item.ItemId).Sum(i => Math.Max(1, (int)i.Amount));

            await bot.Actions.Buy(shop.VendorSerial, item.Serial, 1, ct);
            int goldAfter = await WaitGoldChangeAsync(bot, goldBefore, ct);
            var packAfter = await PackContentsAsync(bot, ct);
            int haveAfter = packAfter.Where(i => i.ItemId == item.ItemId).Sum(i => Math.Max(1, (int)i.Amount));
            var added = packAfter.Where(a => !packBefore.Any(b => b.Serial == a.Serial))
                .Select(a => $"0x{a.ItemId:X4}x{a.Amount}").ToList();
            Console.WriteLine($"[functional]   shop {shop.Items.Count} entries; chose '{item.Name}' 0x{item.ItemId:X4} serial 0x{item.Serial:X} price {item.Price}; " +
                              $"new in pack: [{string.Join(", ", added)}]");

            if (goldAfter != goldBefore - item.Price)
                throw new ScenarioFailed($"'{item.Name}' listed at {item.Price}: gold {goldBefore} -> {goldAfter} (charged {goldBefore - goldAfter})");
            if (haveAfter < haveBefore + 1)
            {
                string seen;
                lock (bot.World.SyncRoot)
                    seen = string.Join(", ", bot.World.KnownItems.Values.Where(k => k.ItemId == item.ItemId)
                        .Select(k => $"0x{k.Serial:X} in 0x{k.ContainerSerial:X} at {k.X},{k.Y}"));
                string said = string.Join(" | ", bot.World.Journal.ToArray().TakeLast(6).Select(e => $"{e.Speaker}: {e.Text}"));
                string pack = string.Join(", ", packAfter.Select(a => $"0x{a.Serial:X}=0x{a.ItemId:X4}x{a.Amount}"));
                throw new ScenarioFailed($"'{item.Name}' (0x{item.ItemId:X4}) in pack {haveBefore} -> {haveAfter}; " +
                                         $"gold {goldBefore} -> {goldAfter}; seen anywhere: [{seen}]; " +
                                         $"backpack 0x{bot.World.BackpackSerial:X} holds [{pack}]; journal: {said}");
            }
            bought = item;
            return $"'{item.Name}' 0x{item.ItemId:X4} for {item.Price}: gold {goldBefore} -> {goldAfter}, in pack {haveBefore} -> {haveAfter}";
        });

        await RunScenario("economy", "sell to the vendor", outcomes, async () =>
        {
            int goldBefore = await RefreshGoldAsync(bot, ct);
            bot.World.ActiveVendor = null;
            await SayAsync(bot, "vendor sell", ct);
            var list = await WaitAsync(() => bot.World.ActiveVendor is { IsBuyList: false } v ? v : null,
                8000, "a sell list (0x9E)");
            // What the vendor offers to buy: the item just bought if it is among them,
            // otherwise anything listed (a vendor buys only some of what it sells).
            var entry = list.Items.FirstOrDefault(i => bought != null && i.ItemId == bought.ItemId)
                ?? list.Items.FirstOrDefault(i => i.Price > 0)
                ?? throw new ScenarioFailed($"the sell list offers nothing at a price: [" +
                    string.Join(", ", list.Items.Select(i => $"'{i.Name}' 0x{i.ItemId:X4} serial 0x{i.Serial:X} x{i.Amount} @{i.Price}")) + "]");
            int haveBefore = await CountInPackAsync(bot, entry.ItemId, ct);
            uint vendor = list.VendorSerial != 0 ? list.VendorSerial : o.Vendor.Serial;
            await bot.Actions.Sell(vendor, entry.Serial, 1, ct);
            int goldAfter = await WaitGoldChangeAsync(bot, goldBefore, ct);
            int haveAfter = await CountInPackAsync(bot, entry.ItemId, ct);
            if (goldAfter != goldBefore + entry.Price)
                throw new ScenarioFailed($"'{entry.Name}' listed at {entry.Price}: gold {goldBefore} -> {goldAfter}");
            if (haveAfter != haveBefore - 1)
                throw new ScenarioFailed($"'{entry.Name}' (0x{entry.ItemId:X4}) in pack {haveBefore} -> {haveAfter}");
            return $"'{entry.Name}' 0x{entry.ItemId:X4} sold for {entry.Price}: gold {goldBefore} -> {goldAfter}, in pack {haveBefore} -> {haveAfter}";
        });

        await RunScenario("economy", "bank: open, balance, deposit", outcomes, async () =>
        {
            await AsStaff(server, account, () => SayAsync(bot, $".go {o.Banker.Point}", ct));
            await ApproachAsync(bot, o.Banker.Serial, ct);
            int balanceBefore = await BalanceAsync(bot, ct);
            uint box = await OpenBankAsync(bot, ct);

            // The status window's gold is everything carried that is not locked - the
            // bank box included - unless PAYFROMPACKONLY (Source-X PacketObjectStatus,
            // CContainer::ContentConsumeTest): moving gold into the bank changes it only
            // when the pack alone counts.
            int statusBefore = await RefreshGoldAsync(bot, ct);
            int packBefore = await PackGoldAsync(bot, ct);
            var pile = await GoldPileAsync(bot, 100, ct);
            int moved = Math.Min(100, (int)pile.Amount);
            await bot.Actions.PickUp(pile.Serial, moved, 2000, ct);
            await bot.Actions.DropToContainer(pile.Serial, box, 2000, ct);
            await Task.Delay(1000, ct);
            int packAfter = await PackGoldAsync(bot, ct);
            int statusAfter = await RefreshGoldAsync(bot, ct);
            int balanceAfter = await BalanceAsync(bot, ct);

            if (packAfter != packBefore - moved)
                throw new ScenarioFailed($"gold in the pack {packBefore} -> {packAfter} after moving {moved} to the bank");
            if (balanceAfter != balanceBefore + moved)
                throw new ScenarioFailed($"balance {balanceBefore} -> {balanceAfter} after depositing {moved}");
            int expectedStatus = o.PayFromPackOnly ? statusBefore - moved : statusBefore;
            if (statusAfter != expectedStatus)
                throw new ScenarioFailed($"status gold {statusBefore} -> {statusAfter}, expected {expectedStatus} " +
                                         $"(PAYFROMPACKONLY={(o.PayFromPackOnly ? 1 : 0)})");
            int goldBefore = packBefore, goldAfter = packAfter;
            return $"deposited {moved}: pack {goldBefore} -> {goldAfter}, balance {balanceBefore} -> {balanceAfter}, " +
                   $"status {statusBefore} -> {statusAfter}";
        });

        bot.Disconnect();
    }

    // ---- death and resurrection ---------------------------------------------------

    private static readonly ushort[] GhostBodies = [0x0192, 0x0193, 0x025F, 0x0260, 0x02B6, 0x02B7];

    private static async Task DeathSuite(Options o, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var victim = await LoginAsync(911, port, ct);
        var staff = await LoginAsync(912, port, ct);
        await ConsoleAsync(server, $"ACCOUNT {staff.AccountName} PLEVEL 7");
        const int Gold = 777;
        int packGoldAtDeath = 0;
        ushort aliveBody = 0;

        // The death happens out of the healer's sight: a healer next to the body raises
        // the ghost at once, and a ghost raised beside its corpse takes its things back
        // (Source-X), which leaves no corpse to look at.
        string deathPoint = o.Vendor.Point;
        await RunScenario("death", "setup: away from the healer, gold in the pack", outcomes, async () =>
        {
            await AsStaff(server, victim.AccountName, async () =>
            {
                await SayAsync(victim, $".go {deathPoint}", ct);
                await SayAsync(victim, $".newgold {Gold}", ct);
            });
            await SayAsync(staff, $".go {deathPoint}", ct);
            packGoldAtDeath = await PackGoldAsync(victim, ct);
            aliveBody = victim.World.Body;
            if (packGoldAtDeath < Gold) throw new ScenarioFailed($"{packGoldAtDeath} gold in the pack");
            return $"{packGoldAtDeath} gold in the pack, body 0x{aliveBody:X}";
        });

        uint corpse = 0;
        await RunScenario("death", "die: death screen, ghost body, corpse", outcomes, async () =>
        {
            int screens = victim.World.DeathScreens;
            await staff.Actions.Say(".xkill", ct);
            if (await staff.Actions.WaitForTarget(4000, ct) != BotActionResult.Success)
                throw new ScenarioFailed("no target cursor after .xkill");
            await staff.Actions.TargetObject(victim.World.CharUid, ct);

            await WaitAsync(() => victim.World.DeathScreens > screens ? (object)true : null, 6000, "a death screen (0x2C)");
            await WaitAsync(() => GhostBodies.Contains(victim.World.Body) ? (object)true : null, 6000,
                $"a ghost body (body is 0x{victim.World.Body:X})");
            short x = victim.World.X, y = victim.World.Y;
            var found = await WaitAsync(() =>
            {
                lock (staff.World.SyncRoot)
                    return staff.World.KnownItems.Values.FirstOrDefault(i => i.ItemId == 0x2006 && i.ContainerSerial == 0 &&
                        Math.Abs(i.X - x) <= 2 && Math.Abs(i.Y - y) <= 2);
            }, 6000, "a corpse (0x2006) where the character died");
            corpse = found.Serial;
            return $"death screen, ghost body 0x{victim.World.Body:X}, corpse 0x{corpse:X} at {found.X},{found.Y}";
        });

        await RunScenario("death", "the pack's gold is on the corpse", outcomes, async () =>
        {
            if (corpse == 0) throw new ScenarioFailed("no corpse");
            staff.World.LastContents.Remove(corpse);
            await staff.Actions.DoubleClick(corpse, 2000, ct);
            var items = await WaitAsync(() => staff.World.LastContents.GetValueOrDefault(corpse), 5000, "corpse contents (0x3C)");
            await Task.Delay(500, ct);
            int onCorpse;
            lock (staff.World.SyncRoot)
            {
                var known = staff.World.KnownItems;
                bool Under(BotKnownItem i)
                {
                    uint c = i.ContainerSerial;
                    for (int d = 0; c != 0 && d < 8; d++)
                    {
                        if (c == corpse) return true;
                        c = known.TryGetValue(c, out var p) ? p.ContainerSerial : 0;
                    }
                    return false;
                }
                onCorpse = known.Values.Where(i => i.ItemId == 0x0EED && Under(i)).Sum(i => (int)i.Amount);
            }
            int leftInPack = await PackGoldAsync(victim, ct).ContinueWith(t => t.IsFaulted ? -1 : t.Result);
            if (onCorpse < packGoldAtDeath)
                throw new ScenarioFailed($"{onCorpse} gold visible on the corpse, {packGoldAtDeath} was in the pack " +
                                         $"(the ghost's pack shows {leftInPack}; corpse lists {items.Count} items at the top)");
            return $"{onCorpse} gold on the corpse ({packGoldAtDeath} was in the pack)";
        });

        await RunScenario("death", "resurrected by the healer", outcomes, async () =>
        {
            await AsStaff(server, victim.AccountName, () => SayAsync(victim, $".go {o.Healer.Point}", ct));
            await ApproachAsync(victim, o.Healer.Serial, ct);
            var sw = Stopwatch.StartNew();
            uint answeredGump = 0;
            while (sw.ElapsedMilliseconds < 30_000)
            {
                if (!GhostBodies.Contains(victim.World.Body) && victim.World.Hits > 0)
                    return $"alive again after {sw.Elapsed.TotalSeconds:F1}s: body 0x{victim.World.Body:X}, " +
                           $"hits {victim.World.Hits}/{victim.World.MaxHits}" +
                           (answeredGump != 0 ? $" (accepted gump 0x{answeredGump:X})" : "");
                // A resurrection offer comes as a gump; accept it (button 1).
                if (victim.World.ActiveGump is { IsOpen: true } g && g.GumpId != answeredGump)
                {
                    answeredGump = g.GumpId;
                    await victim.Actions.RespondGump(g.GumpId, g.Serial, 1, ct: ct);
                }
                await Task.Delay(250, ct);
            }
            string said = string.Join(" | ", victim.World.Journal.ToArray().TakeLast(5).Select(e => $"{e.Speaker}: {e.Text}"));
            throw new ScenarioFailed($"still a ghost after 30 s next to the healer (body 0x{victim.World.Body:X}, " +
                                     $"hits {victim.World.Hits}); journal: {said}");
        });

        victim.Disconnect();
        staff.Disconnect();
    }

    // ---- magic and melee --------------------------------------------------------------

    private const ushort Garlic = 0x0F84, Ginseng = 0x0F85, SpiderSilk = 0x0F8D, Spellbook = 0x0EFA;
    private const int HealSpell = 4, HealMana = 4; // Scripts-X [SPELL 4] MANAUSE=4

    private static int CountOf(List<BotKnownItem> pack, ushort id) =>
        pack.Where(i => i.ItemId == id).Sum(i => Math.Max(1, (int)i.Amount));

    private static async Task CombatSuite(Options o, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var bot = await LoginAsync(921, port, ct);
        string point = o.Vendor.Point;

        await RunScenario("combat", "setup: spellbook, reagents, wounded", outcomes, async () =>
        {
            await AsStaff(server, bot.AccountName, async () =>
            {
                await SayAsync(bot, $".go {point}", ct);
                foreach (string def in new[] { "i_spellbook", "i_reag_garlic", "i_reag_ginseng", "i_reag_spider_silk",
                                               "i_reag_garlic", "i_reag_ginseng", "i_reag_spider_silk" })
                    await AddIntoPackAsync(bot, def, ct);
                var book = (await PackContentsAsync(bot, ct)).FirstOrDefault(i => i.ItemId == Spellbook)
                    ?? throw new ScenarioFailed("no spellbook in the pack after .add");
                foreach (string verb in new[] { ".xmore1 0ffffffff", ".xmore2 0ffffffff" })
                {
                    await bot.Actions.Say(verb, ct);
                    if (await bot.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                        throw new ScenarioFailed($"no target cursor for {verb}");
                    await bot.Actions.TargetObject(book.Serial, ct);
                    await Task.Delay(500, ct);
                }
                await SayAsync(bot, ".hits 40", ct);
            });
            var pack = await PackContentsAsync(bot, ct);
            if (CountOf(pack, Garlic) < 2 || CountOf(pack, Ginseng) < 2 || CountOf(pack, SpiderSilk) < 2)
                throw new ScenarioFailed($"reagents in the pack: garlic {CountOf(pack, Garlic)}, ginseng {CountOf(pack, Ginseng)}, silk {CountOf(pack, SpiderSilk)}");
            return $"book, reagents {CountOf(pack, Garlic)}/{CountOf(pack, Ginseng)}/{CountOf(pack, SpiderSilk)}, " +
                   $"hits {bot.World.Hits}/{bot.World.MaxHits}, mana {bot.World.Mana}/{bot.World.MaxMana}";
        });

        await RunScenario("combat", "heal: mana, reagents, hit points", outcomes, async () =>
        {
            var pack = await PackContentsAsync(bot, ct);
            int manaBefore = bot.World.Mana, hitsBefore = bot.World.Hits;
            var regsBefore = (CountOf(pack, Garlic), CountOf(pack, Ginseng), CountOf(pack, SpiderSilk));

            var cast = await bot.Actions.CastSpell(HealSpell, bot.World.CharUid, 5000, ct);
            await WaitAsync(() => bot.World.Hits > hitsBefore + 2 ? (object)true : null, 8000,
                $"hit points to rise after Heal (cast {cast}, hits {bot.World.Hits})");
            await Task.Delay(300, ct);
            int manaAfter = bot.World.Mana, hitsAfter = bot.World.Hits;
            pack = await PackContentsAsync(bot, ct);
            var regsAfter = (CountOf(pack, Garlic), CountOf(pack, Ginseng), CountOf(pack, SpiderSilk));

            int spent = manaBefore - manaAfter;
            // Mana may regenerate a point while the cast completes; the cost itself is
            // what the spell defines.
            if (spent < HealMana - 1 || spent > HealMana)
                throw new ScenarioFailed($"mana {manaBefore} -> {manaAfter} (spent {spent}, the spell costs {HealMana})");
            if (regsAfter != (regsBefore.Item1 - 1, regsBefore.Item2 - 1, regsBefore.Item3 - 1))
                throw new ScenarioFailed($"reagents garlic/ginseng/silk {regsBefore} -> {regsAfter}, each should drop by 1");
            return $"mana {manaBefore} -> {manaAfter}, reagents {regsBefore} -> {regsAfter}, hits {hitsBefore} -> {hitsAfter}";
        });

        await RunScenario("combat", "heal without reagents is refused", outcomes, async () =>
        {
            foreach (var g in (await PackContentsAsync(bot, ct)).Where(i => i.ItemId == Ginseng).ToList())
            {
                await bot.Actions.PickUp(g.Serial, Math.Max(1, (int)g.Amount), 2000, ct);
                await bot.Actions.DropToWorld(g.Serial, bot.World.X + 1, bot.World.Y, bot.World.Z, 2000, ct);
            }
            var pack = await PackContentsAsync(bot, ct);
            if (pack.Any(i => i.ItemId == Ginseng)) throw new ScenarioFailed("could not take the ginseng out of the pack");
            int manaBefore = bot.World.Mana, hitsBefore = bot.World.Hits;
            long since = Environment.TickCount64;
            await bot.Actions.CastSpell(HealSpell, bot.World.CharUid, 3000, ct);
            await Task.Delay(3000, ct);
            int manaAfter = bot.World.Mana, hitsAfter = bot.World.Hits;
            string said = string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));
            if (manaAfter < manaBefore)
                throw new ScenarioFailed($"mana {manaBefore} -> {manaAfter} with no ginseng; journal: {said}");
            if (hitsAfter - hitsBefore >= 3)
                throw new ScenarioFailed($"hits {hitsBefore} -> {hitsAfter} with no ginseng");
            if (!said.Contains("reagent", StringComparison.OrdinalIgnoreCase))
                throw new ScenarioFailed($"no word about reagents; journal: {said}");
            return $"refused ({said}); mana {manaBefore} -> {manaAfter}";
        });

        await RunScenario("combat", "melee: kill a rabbit, it leaves a corpse", outcomes, async () =>
        {
            HashSet<uint> before;
            lock (bot.World.SyncRoot) before = bot.World.Mobiles.Keys.ToHashSet();
            await AsStaff(server, bot.AccountName, async () =>
            {
                await bot.Actions.Say(".add c_rabbit", ct);
                if (await bot.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed("no target cursor for .add c_rabbit");
                await bot.Actions.TargetLocation(bot.World.X + 1, bot.World.Y + 1, bot.World.Z, ct);
                await Task.Delay(800, ct);
            });
            var rabbit = await WaitAsync(() =>
            {
                lock (bot.World.SyncRoot)
                    return bot.World.Mobiles.Values.FirstOrDefault(m => !before.Contains(m.Serial) && m.Body == 0x00CD);
            }, 5000, "the rabbit (body 0xCD) to appear");
            await bot.Actions.SetWarMode(true, ct);
            var sw = Stopwatch.StartNew();
            short rx = rabbit.X, ry = rabbit.Y;
            while (sw.ElapsedMilliseconds < 45_000)
            {
                bool gone;
                lock (bot.World.SyncRoot)
                {
                    gone = !bot.World.Mobiles.TryGetValue(rabbit.Serial, out var live);
                    if (live != null) { rx = live.X; ry = live.Y; }
                }
                if (gone) break;
                if (bot.World.DistanceTo(rx, ry) > 1) await bot.Actions.MoveTo(rx, ry, 2000, ct);
                await bot.Actions.Attack(rabbit.Serial, ct);
                await Task.Delay(1500, ct);
            }
            await bot.Actions.SetWarMode(false, ct);
            var corpse = await WaitAsync(() =>
            {
                lock (bot.World.SyncRoot)
                    return bot.World.KnownItems.Values.FirstOrDefault(i => i.ItemId == 0x2006 && i.ContainerSerial == 0 &&
                        Math.Abs(i.X - rx) <= 2 && Math.Abs(i.Y - ry) <= 2);
            }, 5000, $"a corpse where the rabbit was (rabbit {(bot.World.Mobiles.ContainsKey(rabbit.Serial) ? "still alive" : "gone")})");
            return $"rabbit 0x{rabbit.Serial:X} killed in {sw.Elapsed.TotalSeconds:F0}s, corpse 0x{corpse.Serial:X}";
        });

        bot.Disconnect();
    }

    // ---- crafting ----------------------------------------------------------------------

    // The pack's legacy tinkering menu (sm_tinker -> sm_parts / sm_tools): the
    // crafting dialog is off in this pack (crafting_settings.scp CraftingSystem=0).
    private const ushort TinkerTools = 0x1EBC, IronIngot = 0x1BEF, Log = 0x1BDD;
    private const ushort Gears = 0x1053, Shovel = 0x0F39, IronWire = 0x1876;
    private const int Tinkering = 37;

    private static async Task CraftSuite(Options o, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var bot = await LoginAsync(931, port, ct);

        await RunScenario("craft", "setup: tinker tools, ingots, logs", outcomes, async () =>
        {
            await AsStaff(server, bot.AccountName, async () =>
            {
                await SayAsync(bot, $".go {o.Vendor.Point}", ct);
                await AddIntoPackAsync(bot, "i_tinker_tools", ct);
                for (int i = 0; i < 16; i++) await AddIntoPackAsync(bot, "i_ingot_iron", ct);
                for (int i = 0; i < 2; i++) await AddIntoPackAsync(bot, "i_log", ct);
                await SayAsync(bot, ".tinkering 1000", ct);
            });
            var pack = await PackContentsAsync(bot, ct);
            if (!pack.Any(i => i.ItemId == TinkerTools) || CountOf(pack, IronIngot) < 16 || CountOf(pack, Log) < 2)
                throw new ScenarioFailed($"pack: tools {pack.Any(i => i.ItemId == TinkerTools)}, ingots {CountOf(pack, IronIngot)}, logs {CountOf(pack, Log)}");
            return $"tools, {CountOf(pack, IronIngot)} ingots, {CountOf(pack, Log)} logs";
        });

        await RunScenario("craft", "tinker gears: one ingot in, gears out", outcomes, async () =>
            await CraftAndCheck(bot, "Parts", Gears, [(IronIngot, 1)], ct));

        await RunScenario("craft", "tinker a shovel: four ingots and two logs", outcomes, async () =>
            await CraftAndCheck(bot, "Tools", Shovel, [(IronIngot, 4), (Log, 2)], ct));

        await RunScenario("craft", "skill gain while crafting near the difficulty", outcomes, async () =>
        {
            // Just above the SKILLMAKE minimum (41.7): below it the attempt is refused outright.
            await AsStaff(server, bot.AccountName, () => SayAsync(bot, ".tinkering 420", ct));
            int before = await SkillAsync(bot, Tinkering, ct);
            int attempts = 0, made = 0;
            for (; attempts < 10; attempts++)
            {
                var pack = await PackContentsAsync(bot, ct);
                if (CountOf(pack, IronIngot) < 1) break;
                int wires = CountOf(pack, IronWire);
                await PressCraftAsync(bot, "Parts", IronWire, ct);
                await Task.Delay(4000, ct);
                if (CountOf(await PackContentsAsync(bot, ct), IronWire) > wires) made++;
                if (await SkillAsync(bot, Tinkering, ct) > before) break;
            }
            int after = await SkillAsync(bot, Tinkering, ct);
            if (after <= before)
            {
                string said = string.Join(" | ", bot.World.Journal.ToArray().TakeLast(8).Select(e => e.Text));
                throw new ScenarioFailed($"tinkering stayed at {before / 10.0:F1} over {attempts} attempts ({made} made) " +
                                         $"at difficulty 41.7; journal: {said}");
            }
            return $"tinkering {before / 10.0:F1} -> {after / 10.0:F1} (base {bot.World.SkillBases.GetValueOrDefault(Tinkering) / 10.0:F1}) " +
                   $"after {attempts + 1} attempt(s), {made} made";
        });

        bot.Disconnect();
    }

    private static async Task<string> CraftAndCheck(BotClient bot, string category, ushort product,
        (ushort Id, int Amount)[] resources, CancellationToken ct)
    {
        var before = await PackContentsAsync(bot, ct);
        int productBefore = CountOf(before, product);
        await PressCraftAsync(bot, category, product, ct);
        await WaitAsync(() =>
        {
            lock (bot.World.SyncRoot)
                return bot.World.KnownItems.Values.Count(i => i.ItemId == product) > productBefore ? (object)true : null;
        }, 15_000, $"0x{product:X4} to be made");
        var after = await PackContentsAsync(bot, ct);
        var report = new List<string>();
        foreach (var (id, amount) in resources)
        {
            int b = CountOf(before, id), a = CountOf(after, id);
            report.Add($"0x{id:X4} {b} -> {a}");
            if (a != b - amount)
                throw new ScenarioFailed($"0x{id:X4} {b} -> {a}, the recipe takes {amount}; {string.Join(", ", report)}");
        }
        if (CountOf(after, product) != productBefore + 1)
            throw new ScenarioFailed($"0x{product:X4} in pack {productBefore} -> {CountOf(after, product)}");
        return $"0x{product:X4} {productBefore} -> {productBefore + 1}; {string.Join(", ", report)}";
    }

    /// <summary>Use the tool, pick the category entry, pick the product entry - the
    /// pack's [SKILLMENU] as the item menu (0x7C) shows them, chosen by text and by
    /// model, since a menu may leave out what the character cannot make.</summary>
    private static async Task PressCraftAsync(BotClient bot, string category, ushort product, CancellationToken ct)
    {
        var tool = (await PackContentsAsync(bot, ct)).FirstOrDefault(i => i.ItemId == TinkerTools)
            ?? throw new ScenarioFailed("no tinker tools in the pack");
        long t0 = Environment.TickCount64;
        await bot.Actions.DoubleClick(tool.Serial, 1000, ct);
        var top = await WaitMenuAsync(bot, t0, "the tinkering menu (0x7C)");
        int ci = top.Entries.FindIndex(e => e.Text.Contains(category, StringComparison.OrdinalIgnoreCase));
        if (ci < 0) throw new ScenarioFailed($"no '{category}' in '{top.Title}': {string.Join(" / ", top.Entries.Select(e => e.Text))}");
        long t1 = Environment.TickCount64;
        ChooseMenu(bot, top, ci);
        var sub = await WaitMenuAsync(bot, t1, $"the '{category}' menu");
        int pi = sub.Entries.FindIndex(e => e.Model == product);
        if (pi < 0) throw new ScenarioFailed($"no 0x{product:X4} in '{sub.Title}': {string.Join(" / ", sub.Entries.Select(e => $"0x{e.Model:X4} {e.Text}"))}");
        ChooseMenu(bot, sub, pi);
    }

    private static Task<BotMenuState> WaitMenuAsync(BotClient bot, long sinceMs, string what) =>
        WaitAsync(() => bot.World.ActiveMenu is { } m && m.ReceivedMs >= sinceMs ? m : null, 5000, what);

    /// <summary>0x7D: [serial:4][menu id:2][index:2, from 1][model:2][color:2].</summary>
    private static void ChooseMenu(BotClient bot, BotMenuState menu, int index)
    {
        var e = menu.Entries[index];
        var p = new byte[13];
        p[0] = 0x7D;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(1), menu.Serial);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(5), menu.MenuId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(7), (ushort)(index + 1));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(9), e.Model);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(11), e.Color);
        bot.SendRaw(p);
    }

    private static Task<BotGumpState> WaitGumpAsync(BotClient bot, long sinceMs, string what) =>
        WaitAsync(() => bot.World.ActiveGump is { IsOpen: true } g && g.ReceivedMs >= sinceMs ? g : null, 5000, what);

    /// <summary>Ask for the skill list (0x34 type 5) and return one skill in tenths.</summary>
    private static async Task<int> SkillAsync(BotClient bot, int skill, CancellationToken ct)
    {
        bot.World.Skills.Remove(skill);
        var p = new byte[10];
        p[0] = 0x34; p[1] = 0xED; p[2] = 0xED; p[3] = 0xED; p[4] = 0xED; p[5] = 0x05;
        uint uid = bot.World.CharUid;
        p[6] = (byte)(uid >> 24); p[7] = (byte)(uid >> 16); p[8] = (byte)(uid >> 8); p[9] = (byte)uid;
        bot.SendRaw(p);
        var v = await WaitAsync(() => bot.World.Skills.TryGetValue(skill, out int x) ? (object)x : null, 5000, "the skill list (0x3A)");
        await Task.Delay(0, ct);
        return (int)v;
    }

    // ---- gathering --------------------------------------------------------------------

    private static readonly ushort[] Logs = [0x1BDD, 0x1BDE, 0x1BDF, 0x1BE0, 0x1BE1, 0x1BE2];
    private static readonly ushort[] Ores = [0x19B7, 0x19B8, 0x19B9, 0x19BA];
    private const int Lumberjacking = 44, Mining = 45;

    private sealed record Spot(int X, int Y, int Z, ushort Graphic, bool IsStatic, int StandX, int StandY, int StandZ, string Name);

    /// <summary>The nearest resource tile to (cx, cy) whose tiledata name matches, with a
    /// walkable tile next to it to stand on. Whether it really yields is the server's
    /// call (the tile's item type, the region's resources).</summary>
    private static Spot? FindSpot(SphereNet.MapData.MapDataManager map, int cx, int cy, int radius,
        Func<string, bool> staticName, Func<string, bool> landName)
    {
        for (int r = 0; r <= radius; r++)
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            int x = cx + dx, y = cy + dy;
            Spot? hit = null;
            foreach (var st in map.GetStatics(0, x, y))
            {
                string n = map.GetItemTileData(st.TileId).Name ?? "";
                if (staticName(n)) { hit = new Spot(x, y, st.Z, st.TileId, true, 0, 0, 0, n); break; }
            }
            if (hit == null)
            {
                var land = map.GetTerrainTile(0, x, y);
                string n = map.GetLandTileData(land.TileId).Name ?? "";
                if (landName(n)) hit = new Spot(x, y, land.Z, land.TileId, false, 0, 0, 0, n);
            }
            if (hit == null) continue;
            foreach (var (sx, sy) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1), (x + 1, y + 1), (x - 1, y - 1) })
            {
                int sz = map.GetEffectiveZ(0, sx, sy);
                if (map.IsPassable(0, sx, sy, sz))
                    return hit with { StandX = sx, StandY = sy, StandZ = sz };
            }
        }
        return null;
    }

    private static async Task GatherSuite(Options o, string root, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        string mul = ReadIniValue(Path.Combine(root, "sphere.ini"), "MulFiles") ?? "";
        if (!Path.IsPathRooted(mul)) mul = Path.Combine(root, mul);
        using var map = new SphereNet.MapData.MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);

        var bot = await LoginAsync(941, port, ct);

        await RunScenario("gather", "lumberjacking: a tree gives logs", outcomes, async () =>
        {
            // Scripts-X r_default_tree: a tree yields nothing 60% of the time
            // (RESOURCES=60.0 mr_nothing), decided per tree, so up to ten different
            // trees are tried; all ten empty is a 0.6% chance.
            await AsStaff(server, bot.AccountName, async () =>
            {
                await AddIntoPackAsync(bot, "i_hatchet", ct);
                await SayAsync(bot, ".lumberjacking 1000", ct);
            });
            var trees = new List<Spot>();
            foreach (var (cx, cy) in new[] { (1300, 1640), (1250, 1500), (1360, 1720), (1200, 1600), (1320, 1550) })
            {
                foreach (int ring in new[] { 0, 8, 16 })
                {
                    var t = FindSpot(map, cx + ring, cy + ring, 40, n => n.Contains("tree", StringComparison.OrdinalIgnoreCase), _ => false);
                    if (t != null && !trees.Any(k => k.X == t.X && k.Y == t.Y)) trees.Add(t);
                    if (trees.Count >= 10) break;
                }
                if (trees.Count >= 10) break;
            }
            if (trees.Count == 0) throw new ScenarioFailed("no tree found on the map near Britain");
            int empty = 0;
            foreach (var tree in trees)
            {
                await AsStaff(server, bot.AccountName, () => SayAsync(bot, $".go {tree.StandX},{tree.StandY},{tree.StandZ}", ct));
                try
                {
                    string got = await GatherAt(bot, tree, 0x0F43, Logs, "logs", ct, uses: 2);
                    return empty == 0 ? got : $"{got}; {empty} tree(s) before it had nothing to chop";
                }
                catch (ScenarioFailed f) when (f.Message.Contains("nothing here", StringComparison.OrdinalIgnoreCase)) { empty++; }
            }
            throw new ScenarioFailed($"all {trees.Count} trees tried had nothing to chop");
        });

        await RunScenario("gather", "mining: rock gives ore", outcomes, async () =>
        {
            var rock = FindSpot(map, 2560, 480, 120,
                _ => false,
                n => n.Contains("mountain", StringComparison.OrdinalIgnoreCase) || n.Equals("rock", StringComparison.OrdinalIgnoreCase))
                ?? throw new ScenarioFailed("no mountain or rock found on the map near Minoc");
            await AsStaff(server, bot.AccountName, async () =>
            {
                await SayAsync(bot, $".go {rock.StandX},{rock.StandY},{rock.StandZ}", ct);
                await AddIntoPackAsync(bot, "i_pickaxe", ct);
                await SayAsync(bot, ".mining 1000", ct);
            });
            return await GatherAt(bot, rock, 0x0E85, Ores, "ore", ct);
        });

        bot.Disconnect();
    }

    /// <summary>Use the tool on the spot up to five times; pass once the pack holds more
    /// of the product than before.</summary>
    private static async Task<string> GatherAt(BotClient bot, Spot spot, ushort tool, ushort[] products, string what,
        CancellationToken ct, int uses = 5)
    {
        int Count(List<BotKnownItem> pack) => pack.Where(i => products.Contains(i.ItemId)).Sum(i => Math.Max(1, (int)i.Amount));
        var start = await PackContentsAsync(bot, ct);
        // Using an axe puts it in hand (Source-X equips a weapon tool first), so after
        // the first tree it is no longer in the pack.
        BotKnownItem? toolItem = start.FirstOrDefault(i => i.ItemId == tool);
        if (toolItem == null)
            lock (bot.World.SyncRoot) toolItem = bot.World.Equipment.Values.FirstOrDefault(i => i.ItemId == tool);
        if (toolItem == null) throw new ScenarioFailed($"no tool 0x{tool:X4} in the pack or in hand");
        int before = Count(start);
        long since = Environment.TickCount64;
        for (int attempt = 1; attempt <= uses; attempt++)
        {
            await bot.Actions.DoubleClick(toolItem.Serial, 1000, ct);
            if (await bot.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                throw new ScenarioFailed("no target cursor from the tool");
            // A static is picked with its graphic, the ground with graphic 0.
            var p = BotPacketBuilder.BuildTargetLocation(bot.World.TargetCursorId, (short)spot.X, (short)spot.Y,
                (sbyte)spot.Z, spot.IsStatic ? spot.Graphic : (ushort)0);
            bot.SendRaw(p);
            bot.World.HasPendingTarget = false;
            await Task.Delay(8000, ct);
            int now = Count(await PackContentsAsync(bot, ct));
            if (now > before)
                return $"{now - before} {what} after {attempt} use(s) on '{spot.Name}' 0x{spot.Graphic:X4} at {spot.X},{spot.Y},{spot.Z}";
        }
        string said = string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).TakeLast(6).Select(e => e.Text));
        throw new ScenarioFailed($"no {what} after {uses} uses on '{spot.Name}' 0x{spot.Graphic:X4} at {spot.X},{spot.Y},{spot.Z} " +
                                 $"(standing {spot.StandX},{spot.StandY},{spot.StandZ}); journal: {said}");
    }

    /// <summary>.add a definition, targeting the bot's own backpack.</summary>
    private static async Task AddIntoPackAsync(BotClient bot, string defname, CancellationToken ct)
    {
        await bot.Actions.Say($".add {defname}", ct);
        if (await bot.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
            throw new ScenarioFailed($"no target cursor for .add {defname}");
        await bot.Actions.TargetObject(bot.World.BackpackSerial, ct);
        await Task.Delay(400, ct);
    }

    // ---- scenario helpers --------------------------------------------------------

    private static async Task RunScenario(string suite, string name, List<Outcome> outcomes, Func<Task<string>> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string detail = await body();
            outcomes.Add(new Outcome(suite, name, true, detail));
            Console.WriteLine($"[functional] PASS {suite}/{name} ({sw.Elapsed.TotalSeconds:F1}s): {detail}");
        }
        catch (Exception ex) when (ex is ScenarioFailed or TimeoutException)
        {
            outcomes.Add(new Outcome(suite, name, false, ex.Message));
            Console.WriteLine($"[functional] FAIL {suite}/{name} ({sw.Elapsed.TotalSeconds:F1}s): {ex.Message}");
        }
    }

    private static async Task<BotClient> LoginAsync(int id, int port, CancellationToken ct)
    {
        var bot = new BotClient(id, NullLogger.Instance)
        {
            ClientVersion = ReferenceClientProtocol.Pack(7, 0, 20, 0),
            RawGameSeed = true,
        };
        if (!await bot.ConnectAndLoginAsync("127.0.0.1", port, ct))
            throw new ScenarioFailed($"bot {id} could not log in");
        // FUNCTIONAL_DUMP_GUMPS=<dir>: keep every 0xDD the bot receives, for working out
        // a script pack's dialog buttons.
        if (Environment.GetEnvironmentVariable("FUNCTIONAL_DUMP_GUMPS") is { Length: > 0 } dumpDir)
        {
            Directory.CreateDirectory(dumpDir);
            int n = 0;
            bot.ServerPacketObserver = p =>
            {
                if (p[0] == 0xDD)
                    File.WriteAllBytes(Path.Combine(dumpDir, $"bot{id}-{Interlocked.Increment(ref n):D3}.dd"), p);
            };
        }
        bot.StartBehavior(new Idle(), ct);
        await Task.Delay(1500, ct); // the login burst
        return bot;
    }

    /// <summary>Run <paramref name="action"/> with the account at Owner, then back to
    /// player. Owner, not GM: a verb no [PLEVEL] section lists (NEWGOLD) needs the
    /// default command level, 7 (Source-X m_iDefaultCommandLevel).</summary>
    private static async Task AsStaff(ServerHost server, string account, Func<Task> action)
    {
        await ConsoleAsync(server, $"ACCOUNT {account} PLEVEL 7");
        try { await action(); }
        finally { await ConsoleAsync(server, $"ACCOUNT {account} PLEVEL 1"); }
    }

    private static async Task SayAsync(BotClient bot, string text, CancellationToken ct)
    {
        await bot.Actions.Say(text, ct);
        await Task.Delay(800, ct);
    }

    private static async Task<T> WaitAsync<T>(Func<T?> probe, int timeoutMs, string what) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            T? value;
            try { value = probe(); }
            catch (InvalidOperationException) { value = null; } // collection changed under us
            if (value != null) return value;
            await Task.Delay(50);
        }
        throw new TimeoutException($"timed out after {timeoutMs} ms waiting for {what}");
    }

    private static async Task ApproachAsync(BotClient bot, uint serial, CancellationToken ct)
    {
        var npc = await WaitAsync(() => { lock (bot.World.SyncRoot) return bot.World.Mobiles.GetValueOrDefault(serial); },
            6000, $"NPC 0x{serial:X} in view");
        if (bot.World.DistanceTo(npc.X, npc.Y) > 2)
            await bot.Actions.MoveTo(npc.X, npc.Y, 8000, ct);
    }

    /// <summary>Ask for this character's status (0x34 type 4) and return the gold it shows.</summary>
    private static async Task<int> RefreshGoldAsync(BotClient bot, CancellationToken ct)
    {
        bot.World.StatusGold = -1;
        var p = new byte[10];
        p[0] = 0x34;
        p[1] = 0xED; p[2] = 0xED; p[3] = 0xED; p[4] = 0xED;
        p[5] = 0x04;
        uint uid = bot.World.CharUid;
        p[6] = (byte)(uid >> 24); p[7] = (byte)(uid >> 16); p[8] = (byte)(uid >> 8); p[9] = (byte)uid;
        bot.SendRaw(p);
        var gold = await WaitAsync(() => bot.World.StatusGold >= 0 ? (object)bot.World.StatusGold : null, 5000, "a status window (0x11)");
        await Task.Delay(0, ct);
        return (int)gold;
    }

    private static async Task<int> WaitGoldChangeAsync(BotClient bot, int before, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000)
        {
            await Task.Delay(500, ct);
            int now = await RefreshGoldAsync(bot, ct);
            if (now != before) return now;
        }
        return before;
    }

    /// <summary>Everything the client knows to be in the backpack, nested bags
    /// included - what its own 0x3C / 0x25 / 0x1D bookkeeping says, as the 2D client
    /// keeps it. The pack is opened once so its contents have been sent.</summary>
    private static async Task<List<BotKnownItem>> PackContentsAsync(BotClient bot, CancellationToken ct)
    {
        uint pack = bot.World.BackpackSerial;
        if (pack == 0) throw new ScenarioFailed("the bot never saw its backpack");
        if (!bot.World.LastContents.ContainsKey(pack))
        {
            await bot.Actions.DoubleClick(pack, 2000, ct);
            await WaitAsync(() => bot.World.LastContents.GetValueOrDefault(pack), 5000, "backpack contents (0x3C)");
        }
        await Task.Delay(700, ct); // let item updates already on the way land
        lock (bot.World.SyncRoot)
        {
            var known = bot.World.KnownItems;
            bool InPack(BotKnownItem i)
            {
                uint c = i.ContainerSerial;
                for (int depth = 0; c != 0 && depth < 8; depth++)
                {
                    if (c == pack) return true;
                    c = known.TryGetValue(c, out var parent) ? parent.ContainerSerial : 0;
                }
                return false;
            }
            return known.Values.Where(InPack).ToList();
        }
    }

    private static async Task<int> CountInPackAsync(BotClient bot, ushort itemId, CancellationToken ct) =>
        (await PackContentsAsync(bot, ct)).Where(i => i.ItemId == itemId).Sum(i => Math.Max(1, (int)i.Amount));

    private static async Task<int> PackGoldAsync(BotClient bot, CancellationToken ct) =>
        (await PackContentsAsync(bot, ct)).Where(i => i.ItemId == 0x0EED).Sum(i => (int)i.Amount);

    private static async Task<BotKnownItem> GoldPileAsync(BotClient bot, int atLeast, CancellationToken ct) =>
        (await PackContentsAsync(bot, ct)).Where(i => i.ItemId == 0x0EED).OrderByDescending(i => i.Amount).FirstOrDefault()
        ?? throw new ScenarioFailed($"no gold pile in the pack to move {atLeast} from");

    private static async Task<uint> OpenBankAsync(BotClient bot, CancellationToken ct)
    {
        var before = bot.World.OpenContainers.Keys.ToHashSet();
        await SayAsync(bot, "bank", ct);
        return await WaitAsync(() =>
        {
            var opened = bot.World.OpenContainers.Keys.Where(k => !before.Contains(k) && k != bot.World.BackpackSerial).ToList();
            return opened.Count > 0 ? (object)opened[0] : null;
        }, 5000, "the bank box to open (0x24)") is uint box ? box : 0;
    }

    /// <summary>Say "balance" and read the amount from the answer.</summary>
    private static async Task<int> BalanceAsync(BotClient bot, CancellationToken ct)
    {
        long since = Environment.TickCount64;
        await SayAsync(bot, "balance", ct);
        var line = await WaitAsync(() =>
        {
            foreach (var e in bot.World.Journal.ToArray())
                if (e.TimestampMs >= since && e.SpeakerSerial != bot.World.CharUid && Regex.IsMatch(e.Text, @"\d"))
                    return e.Text;
            return null;
        }, 5000, "an answer to 'balance'");
        var m = Regex.Match(line.Replace(",", "").Replace(".", ""), @"(\d+)");
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Send one console line and return what the server logged in the next
    /// second and a half, noise left out.</summary>
    internal static async Task<List<string>> ConsoleAsync(ServerHost server, string command)
    {
        int mark = server.LineCount;
        server.Send(command);
        await Task.Delay(1500);
        return server.LinesSince(mark)
            .Where(l => !l.Contains("Sector too complex", StringComparison.Ordinal) &&
                        !l.Contains("[slow_tick]", StringComparison.Ordinal) &&
                        !l.Contains("[loop_stall]", StringComparison.Ordinal))
            .ToList();
    }

    private static string? ReadIniValue(string ini, string key)
    {
        if (!File.Exists(ini)) return null;
        string? value = null;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var l = raw.Trim();
            if (l.StartsWith(key, StringComparison.OrdinalIgnoreCase) && l.Length > key.Length &&
                (l[key.Length] == '=' || char.IsWhiteSpace(l[key.Length])) && l.Contains('='))
                value = l[(l.IndexOf('=') + 1)..].Split("//")[0].Trim();
        }
        return value;
    }

    private static int? ReadIniPort(string ini)
    {
        if (!File.Exists(ini)) return null;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var l = raw.Trim();
            if (l.StartsWith("ServPort", StringComparison.OrdinalIgnoreCase) && l.Contains('='))
                if (int.TryParse(l[(l.IndexOf('=') + 1)..].Trim(), out int p)) return p;
        }
        return null;
    }

    private static (uint, string) ParseNpc(string text)
    {
        var parts = text.Split('@');
        if (parts.Length != 2) throw new ArgumentException($"'{text}' must be <serial>@x,y,z");
        string s = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0];
        return (uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture), parts[1]);
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--root": o.Root = Next(); break;
                case "--suite": o.Suite = Next().ToLowerInvariant(); break;
                case "--probe": o.Probe = Next(); break;
                case "--city": o.City = Next(); break;
                case "--vendor": o.Vendor = ParseNpc(Next()); break;
                case "--banker": o.Banker = ParseNpc(Next()); break;
                case "--healer": o.Healer = ParseNpc(Next()); break;
                case "--server": o.ServerDll = Path.GetFullPath(Next()); break;
                case "--out": o.OutDir = Path.GetFullPath(Next()); break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (o.Root == null) throw new ArgumentException("functional needs --root <shard copy>");
        if (!File.Exists(o.ServerDll)) throw new ArgumentException($"Server not found: {o.ServerDll}");
        return o;
    }
}
