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
            if (o.Suite is "all" or "world")
                await WorldSuite(o, root, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "house")
                await HouseSuite(o, root, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "law")
                await LawSuite(o, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "travel")
                await TravelSuite(o, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "healing")
                await HealingSuite(server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "thief")
                await ThiefSuite(server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "party")
                await PartySuite(server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "ship")
                await ShipSuite(root, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "spawn")
                await SpawnSuite(server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "guild")
                await GuildSuite(root, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "pvendor")
                await PlayerVendorSuite(root, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "mount")
                await MountSuite(root, server, port, outcomes, cts.Token);
            if (o.Suite is "all" or "lock")
                await LockSuite(root, server, port, outcomes, cts.Token);
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
            // A vendor buys back only some of what it sells, and its stock changes, so
            // when nothing of ours is on the sell list at a price another item is bought
            // (up to three) until something is.
            BotVendorState list = null!;
            BotVendorItem? entry = null;
            var triedIds = new HashSet<ushort>();
            for (int round = 0; round < 3 && entry == null; round++)
            {
                bot.World.ActiveVendor = null;
                await SayAsync(bot, "vendor sell", ct);
                list = await WaitAsync(() => bot.World.ActiveVendor is { IsBuyList: false } v ? v : null,
                    8000, "a sell list (0x9E)");
                entry = list.Items.FirstOrDefault(i => bought != null && i.ItemId == bought.ItemId && i.Price > 0)
                    ?? list.Items.FirstOrDefault(i => i.Price > 0);
                if (entry != null) break;
                if (bought != null) triedIds.Add(bought.ItemId);
                bot.World.ActiveVendor = null;
                await SayAsync(bot, "vendor buy", ct);
                var shop = await WaitAsync(() => bot.World.ActiveVendor is { IsBuyList: true, VendorSerial: not 0 } v && v.Items.Count > 0
                    ? bot.World.ActiveVendor : null, 8000, "a buy window");
                var next = shop.Items.Where(i => i.Serial != 0 && i.Price >= 10 && !triedIds.Contains(i.ItemId))
                    .OrderBy(i => i.Price).FirstOrDefault();
                if (next == null) break;
                await bot.Actions.Buy(shop.VendorSerial, next.Serial, 1, ct);
                await Task.Delay(1500, ct);
                bought = next;
            }
            int goldBefore = await RefreshGoldAsync(bot, ct);
            entry = entry
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

    // ---- trade, doors, pets -------------------------------------------------------------

    private static async Task WorldSuite(Options o, string root, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var a = await LoginAsync(951, port, ct);
        var b = await LoginAsync(952, port, ct);
        a.AutoAcceptTrades = false;
        b.AutoAcceptTrades = false;
        string[] parts = o.Vendor.Point.Split(',');
        int px = int.Parse(parts[0], CultureInfo.InvariantCulture), py = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int pz = int.Parse(parts[2], CultureInfo.InvariantCulture);

        await RunScenario("world", "setup: two players side by side", outcomes, async () =>
        {
            await AsStaff(server, a.AccountName, async () =>
            {
                await SayAsync(a, $".go {px},{py},{pz}", ct);
                await SayAsync(a, ".newgold 500", ct);
                await AddIntoPackAsync(a, "i_gears", ct);
            });
            await AsStaff(server, b.AccountName, () => SayAsync(b, $".go {px + 1},{py},{pz}", ct));
            await WaitAsync(() => { lock (a.World.SyncRoot) return a.World.Mobiles.ContainsKey(b.World.CharUid) ? (object)true : null; },
                5000, "each other in view");
            return $"A has {await PackGoldAsync(a, ct)} gold and gears";
        });

        await RunScenario("world", "trade: gears and 100 gold change hands", outcomes, async () =>
        {
            var packA = await PackContentsAsync(a, ct);
            int gearsA = CountOf(packA, Gears), goldA = await PackGoldAsync(a, ct);
            int gearsB = CountOf(await PackContentsAsync(b, ct), Gears), goldB = await PackGoldAsync(b, ct);
            var gears = packA.First(i => i.ItemId == Gears);

            await a.Actions.PickUp(gears.Serial, 1, 2000, ct);
            await a.Actions.DropToContainer(gears.Serial, b.World.CharUid, 2000, ct);
            await WaitAsync(() => a.World.TradeContainer != 0 && b.World.TradeContainer != 0 ? (object)true : null,
                5000, "a trade window on both sides (0x6F)");
            var pile = (await PackContentsAsync(a, ct)).Where(i => i.ItemId == 0x0EED).OrderByDescending(i => i.Amount).First();
            await a.Actions.PickUp(pile.Serial, 100, 2000, ct);
            await a.Actions.DropToContainer(pile.Serial, a.World.TradeContainer, 2000, ct);
            await Task.Delay(800, ct);
            a.SendRaw(BotPacketBuilder.BuildTradeAccept(a.World.TradeContainer, accept: true));
            b.SendRaw(BotPacketBuilder.BuildTradeAccept(b.World.TradeContainer, accept: true));
            await WaitAsync(() => a.World.TradeContainer == 0 && b.World.TradeContainer == 0 ? (object)true : null,
                6000, "the trade to close after both accepted");

            int gearsA2 = CountOf(await PackContentsAsync(a, ct), Gears), goldA2 = await PackGoldAsync(a, ct);
            int gearsB2 = CountOf(await PackContentsAsync(b, ct), Gears), goldB2 = await PackGoldAsync(b, ct);
            string detail = $"A gears {gearsA} -> {gearsA2}, gold {goldA} -> {goldA2}; B gears {gearsB} -> {gearsB2}, gold {goldB} -> {goldB2}";
            if (gearsA2 != gearsA - 1 || gearsB2 != gearsB + 1 || goldA2 != goldA - 100 || goldB2 != goldB + 100)
                throw new ScenarioFailed(detail);
            return detail;
        });

        await RunScenario("world", "a cancelled trade returns the offer", outcomes, async () =>
        {
            var packB = await PackContentsAsync(b, ct);
            var gears = packB.FirstOrDefault(i => i.ItemId == Gears) ?? throw new ScenarioFailed("B has no gears to offer");
            int gearsA = CountOf(await PackContentsAsync(a, ct), Gears), gearsB = CountOf(packB, Gears);
            await b.Actions.PickUp(gears.Serial, 1, 2000, ct);
            await b.Actions.DropToContainer(gears.Serial, a.World.CharUid, 2000, ct);
            await WaitAsync(() => b.World.TradeContainer != 0 ? (object)true : null, 5000, "a trade window (0x6F)");
            await Task.Delay(500, ct);
            b.SendRaw(BotPacketBuilder.BuildTradeClose(b.World.TradeContainer));
            await WaitAsync(() => a.World.TradeContainer == 0 && b.World.TradeContainer == 0 ? (object)true : null,
                5000, "the trade to close");
            int gearsA2 = CountOf(await PackContentsAsync(a, ct), Gears), gearsB2 = CountOf(await PackContentsAsync(b, ct), Gears);
            string detail = $"A gears {gearsA} -> {gearsA2}; B gears {gearsB} -> {gearsB2}";
            if (gearsA2 != gearsA || gearsB2 != gearsB) throw new ScenarioFailed(detail);
            return detail;
        });

        await RunScenario("world", "a door opens and closes by itself", outcomes, async () =>
        {
            string mul = ReadIniValue(Path.Combine(root, "sphere.ini"), "MulFiles") ?? "";
            if (!Path.IsPathRooted(mul)) mul = Path.Combine(root, mul);
            using var map = new SphereNet.MapData.MapDataManager(mul);
            map.Load();
            bool IsDoor(ushort id) => (map.GetItemTileData(id).Flags & SphereNet.MapData.Tiles.TileFlag.Door) != 0;
            await Task.Delay(1000, ct);
            BotKnownItem? door;
            lock (a.World.SyncRoot)
                door = a.World.KnownItems.Values.Where(i => i.ContainerSerial == 0 && IsDoor(i.ItemId))
                    .OrderBy(i => Math.Max(Math.Abs(i.X - a.World.X), Math.Abs(i.Y - a.World.Y))).FirstOrDefault();
            if (door == null) throw new ScenarioFailed("no door item in view");
            await AsStaff(server, a.AccountName, () => SayAsync(a, $".go {door.X},{door.Y + 1},{door.Z}", ct));
            ushort closed = door.ItemId;
            await a.Actions.DoubleClick(door.Serial, 1000, ct);
            var opened = await WaitAsync(() => { lock (a.World.SyncRoot) return a.World.KnownItems.GetValueOrDefault(door.Serial) is { } d && d.ItemId != closed ? d : null; },
                4000, $"door 0x{door.Serial:X} (0x{closed:X4}) to change graphic");
            ushort openId = opened.ItemId;
            var sw = Stopwatch.StartNew();
            await WaitAsync(() => { lock (a.World.SyncRoot) return a.World.KnownItems.GetValueOrDefault(door.Serial) is { } d && d.ItemId == closed ? d : null; },
                75_000, $"door 0x{door.Serial:X} to close again (still 0x{openId:X4})");
            return $"door 0x{door.Serial:X} 0x{closed:X4} -> 0x{openId:X4}, closed again after {sw.Elapsed.TotalSeconds:F0}s";
        });

        await RunScenario("world", "tame a horse, it follows its master", outcomes, async () =>
        {
            HashSet<uint> before;
            lock (a.World.SyncRoot) before = a.World.Mobiles.Keys.ToHashSet();
            await AsStaff(server, a.AccountName, async () =>
            {
                // Open ground west of Britain (the tree drill's forest edge), not the
                // vendor's shop, so the pet has a clear path after its master.
                await SayAsync(a, ".go 1253,1500,0", ct);
                await SayAsync(a, ".taming 1000", ct); // Source-X skill key TAMING
                await SayAsync(a, ".animallore 1000", ct);
                await a.Actions.Say(".add c_horse_tan", ct);
                if (await a.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed("no target cursor for .add c_horse_tan");
                await a.Actions.TargetLocation(a.World.X + 2, a.World.Y, a.World.Z, ct);
                await Task.Delay(800, ct);
            });
            var horse = await WaitAsync(() => { lock (a.World.SyncRoot) return a.World.Mobiles.Values.FirstOrDefault(m => !before.Contains(m.Serial) && m.Body == 0x00C8); },
                5000, "the horse (body 0xC8) to appear");
            string where = $"bot at {a.World.X},{a.World.Y}, horse at {horse.X},{horse.Y}";
            long since = Environment.TickCount64;
            bool tamed = false;
            for (int attempt = 0; attempt < 6 && !tamed; attempt++)
            {
                await ApproachAsync(a, horse.Serial, ct);
                await a.Actions.UseSkill(35, 3000, ct); // Animal Taming
                if (await a.Actions.WaitForTarget(3000, ct) == BotActionResult.Success)
                    await a.Actions.TargetObject(horse.Serial, ct);
                for (int i = 0; i < 24 && !tamed; i++)
                {
                    await Task.Delay(500, ct);
                    tamed = a.World.Journal.ToArray().Any(e => e.TimestampMs >= since &&
                        e.Text.Contains("accept", StringComparison.OrdinalIgnoreCase) && e.Text.Contains("master", StringComparison.OrdinalIgnoreCase));
                }
            }
            string said = string.Join(" | ", a.World.Journal.ToArray().Where(e => e.TimestampMs >= since).TakeLast(6).Select(e => e.Text));
            if (!tamed) throw new ScenarioFailed($"not tamed ({where}); journal: {said}");

            await SayAsync(a, "all follow me", ct);
            // Walk away; the pet should keep up.
            short startX = a.World.X, startY = a.World.Y;
            for (int step = 0; step < 6; step++)
                await a.Actions.MoveDirection(2, ct); // east
            await Task.Delay(500, ct);
            int dist = 99;
            var trail = new List<string>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15_000)
            {
                await Task.Delay(1000, ct);
                lock (a.World.SyncRoot)
                    if (a.World.Mobiles.TryGetValue(horse.Serial, out var h))
                    {
                        dist = a.World.DistanceTo(h.X, h.Y);
                        trail.Add($"{h.X},{h.Y}");
                    }
                if (dist <= 2) break;
            }
            if (dist > 2)
                throw new ScenarioFailed($"tamed, but after 'all follow me' the horse stayed {dist} tiles away; " +
                                         $"master walked {startX},{startY} -> {a.World.X},{a.World.Y}; horse: {string.Join(" ", trail)}");
            return $"tamed ({said.Split(" | ").Last()}), followed to {dist} tile(s) in {sw.Elapsed.TotalSeconds:F0}s";
        });

        a.Disconnect();
        b.Disconnect();
    }

    // ---- housing ------------------------------------------------------------------------

    // Scripts-X i_deed_small_brick_house -> m_small_brick_house: MULTIREGION -3,-3,3,4,
    // door i_door_wood at 0,3,7, sign at 2,4,5.
    private const ushort HouseDeed = 0x14EF;
    private static bool IsKey(ushort id) => id is >= 0x100E and <= 0x1013 || id == 0x1176;

    /// <summary>Open ground for a 7x8 house: every land tile of the footprint and a
    /// margin within 2 of the centre's Z, passable, and no static on it.</summary>
    private static IEnumerable<(int X, int Y, int Z)> HouseSites(SphereNet.MapData.MapDataManager map, int cx, int cy, int radius)
    {
        for (int r = 0; r <= radius; r += 3)
        for (int dx = -r; dx <= r; dx += 3)
        for (int dy = -r; dy <= r; dy += 3)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            int x = cx + dx, y = cy + dy;
            int z = map.GetTerrainTile(0, x, y).Z;
            bool ok = true;
            for (int ix = x - 5; ix <= x + 5 && ok; ix++)
            for (int iy = y - 5; iy <= y + 9 && ok; iy++)
            {
                var land = map.GetTerrainTile(0, ix, iy);
                if (Math.Abs(land.Z - z) > 2 || map.GetStatics(0, ix, iy).Length > 0 ||
                    !map.IsPassable(0, ix, iy, land.Z))
                    ok = false;
            }
            if (ok) yield return (x, y, z);
        }
    }

    private static async Task HouseSuite(Options o, string root, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        string mul = ReadIniValue(Path.Combine(root, "sphere.ini"), "MulFiles") ?? "";
        if (!Path.IsPathRooted(mul)) mul = Path.Combine(root, mul);
        using var map = new SphereNet.MapData.MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);
        bool IsDoor(ushort id) => (map.GetItemTileData(id).Flags & SphereNet.MapData.Tiles.TileFlag.Door) != 0;

        var owner = await LoginAsync(961, port, ct);
        var stranger = await LoginAsync(962, port, ct);
        (int X, int Y, int Z) site = default;
        BotKnownItem? door = null;

        await RunScenario("house", "place a house from a deed", outcomes, async () =>
        {
            var tried = new List<string>();
            foreach (var (cx, cy) in new[] { (1150, 1650), (1260, 1840), (1650, 1900), (1700, 1450), (1100, 1450) })
            {
                foreach (var cand in HouseSites(map, cx, cy, 45).Take(3))
                {
                    await AsStaff(server, owner.AccountName, async () =>
                    {
                        await SayAsync(owner, $".go {cand.X},{cand.Y + 7},{cand.Z}", ct);
                        await AddIntoPackAsync(owner, "i_deed_small_brick_house", ct);
                    });
                    var pack = await PackContentsAsync(owner, ct);
                    var deed = pack.FirstOrDefault(i => i.ItemId == HouseDeed)
                        ?? throw new ScenarioFailed("no house deed in the pack after .add");
                    int keysBefore = pack.Count(i => IsKey(i.ItemId));
                    long since = Environment.TickCount64;
                    await owner.Actions.DoubleClick(deed.Serial, 1000, ct);
                    if (await owner.Actions.WaitForTarget(4000, ct) != BotActionResult.Success)
                        throw new ScenarioFailed("no placement cursor (0x99) from the deed");
                    owner.SendRaw(BotPacketBuilder.BuildTargetLocation(owner.World.TargetCursorId,
                        (short)cand.X, (short)cand.Y, (sbyte)cand.Z));
                    owner.World.HasPendingTarget = false;
                    await Task.Delay(2500, ct);
                    var after = await PackContentsAsync(owner, ct);
                    if (after.Any(i => i.Serial == deed.Serial))
                    {
                        string said = string.Join(" | ", owner.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));
                        tried.Add($"{cand.X},{cand.Y},{cand.Z}: {said}");
                        continue; // refused here; the deed is still ours, try the next site
                    }
                    site = cand;
                    int keys = after.Count(i => IsKey(i.ItemId)) - keysBefore;
                    lock (owner.World.SyncRoot)
                        door = owner.World.KnownItems.Values.Where(i => i.ContainerSerial == 0 && !i.IsMulti && IsDoor(i.ItemId) &&
                                Math.Abs(i.X - cand.X) <= 3 && Math.Abs(i.Y - cand.Y) <= 4)
                            .OrderBy(i => Math.Abs(i.X - cand.X) + Math.Abs(i.Y - (cand.Y + 3))).FirstOrDefault();
                    if (door == null) throw new ScenarioFailed($"the deed was used at {cand.X},{cand.Y} but no house door is in view");
                    if (keys < 1) throw new ScenarioFailed($"house placed at {cand.X},{cand.Y} but no key came into the pack");
                    return $"house at {cand.X},{cand.Y},{cand.Z}: deed used, {keys} key(s) in the pack, door 0x{door.Serial:X} at {door.X},{door.Y}" +
                           (tried.Count > 0 ? $"; refused first at: {string.Join(" || ", tried)}" : "");
                }
            }
            throw new ScenarioFailed(tried.Count == 0 ? "no open site found on the map" : "every site refused: " + string.Join(" || ", tried));
        });

        await RunScenario("house", "the owner opens the door", outcomes, async () =>
        {
            if (door == null) throw new ScenarioFailed("no house");
            // In front of the door, outside (the door faces south).
            await AsStaff(server, owner.AccountName, () => SayAsync(owner, $".go {door.X},{door.Y + 1},{site.Z}", ct));
            ushort closed = door.ItemId;
            await owner.Actions.DoubleClick(door.Serial, 1000, ct);
            var opened = await WaitAsync(() => { lock (owner.World.SyncRoot) return owner.World.KnownItems.GetValueOrDefault(door.Serial) is { } d && d.ItemId != closed ? d : null; },
                4000, $"the door 0x{door.Serial:X} to open for its owner");
            return $"0x{closed:X4} -> 0x{opened.ItemId:X4}";
        });

        await RunScenario("house", "the house door closes by itself", outcomes, async () =>
        {
            if (door == null) throw new ScenarioFailed("no house");
            var sw = Stopwatch.StartNew();
            await WaitAsync(() => { lock (owner.World.SyncRoot) return owner.World.KnownItems.GetValueOrDefault(door.Serial) is { } d && d.ItemId == door.ItemId ? d : null; },
                75_000, $"the house door 0x{door.Serial:X} to close again");
            return $"closed after {sw.Elapsed.TotalSeconds:F0}s";
        });

        await RunScenario("house", "a stranger cannot open the locked door", outcomes, async () =>
        {
            if (door == null) throw new ScenarioFailed("no house");
            await AsStaff(server, stranger.AccountName, () => SayAsync(stranger, $".go {door.X + 1},{door.Y + 1},{site.Z}", ct));
            await Task.Delay(1500, ct);
            ushort closed = door.ItemId;
            long since = Environment.TickCount64;
            await stranger.Actions.DoubleClick(door.Serial, 1000, ct);
            await Task.Delay(2000, ct);
            ushort now;
            lock (stranger.World.SyncRoot) now = stranger.World.KnownItems.GetValueOrDefault(door.Serial)?.ItemId ?? 0;
            string said = string.Join(" | ", stranger.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));
            if (now != closed) throw new ScenarioFailed($"the door opened for a stranger (0x{closed:X4} -> 0x{now:X4}); journal: {said}");
            return $"stayed shut ({said})";
        });

        owner.Disconnect();
        stranger.Disconnect();
    }

    // ---- notoriety and guards ---------------------------------------------------------

    // Notoriety byte of 0x77/0x78, as the viewer sees the mobile.
    private const byte NotoInnocent = 1, NotoCriminal = 4;

    private static byte NotorietyOf(BotClient viewer, uint serial)
    {
        lock (viewer.World.SyncRoot)
            return viewer.World.Mobiles.TryGetValue(serial, out var m) ? m.Notoriety : (byte)0;
    }

    private static async Task LawSuite(Options o, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var attacker = await LoginAsync(971, port, ct);
        var victim = await LoginAsync(972, port, ct);
        var second = await LoginAsync(973, port, ct);
        string[] parts = o.Vendor.Point.Split(',');
        int px = int.Parse(parts[0], CultureInfo.InvariantCulture), py = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int pz = int.Parse(parts[2], CultureInfo.InvariantCulture);
        uint a = attacker.World.CharUid, v = victim.World.CharUid, w = second.World.CharUid;
        // Out of town, on open ground where houses may be built: no guard ends a fight
        // before it can be looked at, and nobody watches unless the drill puts someone.
        const int Wx = 1135, Wy = 1640, Wz = 0;

        await RunScenario("law", "setup: innocents out of town, nobody watching", outcomes, async () =>
        {
            await AsStaff(server, attacker.AccountName, () => SayAsync(attacker, $".go {Wx},{Wy},{Wz}", ct));
            await AsStaff(server, victim.AccountName, () => SayAsync(victim, $".go {Wx + 1},{Wy},{Wz}", ct));
            await WaitAsync(() => NotorietyOf(victim, a) != 0 && NotorietyOf(attacker, v) != 0 ? (object)true : null,
                5000, "each other in view");
            byte seenA = NotorietyOf(victim, a), seenV = NotorietyOf(attacker, v);
            if (seenA != NotoInnocent || seenV != NotoInnocent)
                throw new ScenarioFailed($"not both innocent: the victim sees the attacker as {seenA}, the attacker sees the victim as {seenV}");
            string others;
            lock (attacker.World.SyncRoot)
                others = string.Join(", ", attacker.World.Mobiles.Values.Where(m => m.Serial != a && m.Serial != v && m.IsHumanBody)
                    .Select(m => $"0x{m.Serial:X}"));
            if (others.Length > 0) throw new ScenarioFailed($"someone else is in view: {others}");
            return "both innocent (notoriety 1), no other person in view";
        });

        await RunScenario("law", "an attack nobody sees is no crime", outcomes, async () =>
        {
            await attacker.Actions.SetWarMode(true, ct);
            await attacker.Actions.Attack(v, ct);
            await Task.Delay(3000, ct);
            await attacker.Actions.SetWarMode(false, ct);
            byte seen = NotorietyOf(victim, a);
            if (seen != NotoInnocent) throw new ScenarioFailed($"the victim sees the attacker as {seen} with no witness around");
            return "the attacker stayed innocent (CheckCrimeSeen: the victim is not a witness)";
        });

        await RunScenario("law", "an attack an NPC sees makes the attacker criminal", outcomes, async () =>
        {
            await AsStaff(server, second.AccountName, async () =>
            {
                await SayAsync(second, $".go {Wx + 6},{Wy + 6},{Wz}", ct);
                await second.Actions.Say(".add c_man", ct);
                if (await second.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed("no target cursor for .add c_man");
                await second.Actions.TargetLocation(second.World.X + 2, second.World.Y + 2, second.World.Z, ct);
                await Task.Delay(800, ct);
            });
            await AsStaff(server, attacker.AccountName, () => SayAsync(attacker, $".go {Wx + 5},{Wy + 6},{Wz}", ct));
            await WaitAsync(() => NotorietyOf(second, a) != 0 ? (object)true : null, 5000, "the attacker in the second victim's view");
            if (NotorietyOf(second, a) != NotoInnocent)
                throw new ScenarioFailed($"the attacker already shows as {NotorietyOf(second, a)}");
            await attacker.Actions.SetWarMode(true, ct);
            await attacker.Actions.Attack(w, ct);
            var sw = Stopwatch.StartNew();
            await WaitAsync(() => NotorietyOf(second, a) == NotoCriminal ? (object)true : null,
                8000, $"the victim to see the attacker as criminal (sees {NotorietyOf(second, a)})");
            return $"criminal {sw.Elapsed.TotalSeconds:F1}s after the attack in front of the NPC";
        });

        await RunScenario("law", "the victim strikes back and stays innocent", outcomes, async () =>
        {
            await second.Actions.SetWarMode(true, ct);
            await second.Actions.Attack(a, ct);
            await Task.Delay(4000, ct);
            byte seen = NotorietyOf(attacker, w);
            await second.Actions.SetWarMode(false, ct);
            await attacker.Actions.SetWarMode(false, ct);
            if (seen != NotoInnocent) throw new ScenarioFailed($"the attacker sees the defending victim as {seen}");
            return "self-defence left the victim innocent";
        });

        await RunScenario("law", "the guards kill a criminal in town", outcomes, async () =>
        {
            if (NotorietyOf(second, a) != NotoCriminal)
                throw new ScenarioFailed($"the attacker is no longer criminal (seen as {NotorietyOf(second, a)})");
            int screens = attacker.World.DeathScreens;
            long since = Environment.TickCount64;
            await AsStaff(server, second.AccountName, () => SayAsync(second, $".go {px + 3},{py + 2},{pz}", ct));
            await AsStaff(server, attacker.AccountName, () => SayAsync(attacker, $".go {px + 2},{py + 2},{pz}", ct));
            var sw = Stopwatch.StartNew();
            await second.Actions.Say("guards", ct);
            await WaitAsync(() => attacker.World.DeathScreens > screens ? (object)true : null, 20_000,
                "the criminal to die (death screen 0x2C)");
            if (GhostBodies.Contains(second.World.Body)) throw new ScenarioFailed("the victim died too");
            string said = string.Join(" | ", second.World.Journal.ToArray()
                .Where(e => e.TimestampMs >= since && !e.Text.StartsWith('.') && e.Text != "guards").Select(e => e.Text).TakeLast(3));
            return $"the criminal died {sw.Elapsed.TotalSeconds:F1}s after reaching town; heard: {said}";
        });

        attacker.Disconnect();
        victim.Disconnect();
        second.Disconnect();
    }

    // ---- travel magic ------------------------------------------------------------------

    // Scripts-X: i_rune_marker 0x1F14 (t_rune), i_moongate_blue 0x0F6C; [SPELL 45] s_mark,
    // [SPELL 32] s_recall, [SPELL 52] s_gate_travel.
    private const ushort Rune = 0x1F14, Moongate = 0x0F6C;
    private const ushort BlackPearl = 0x0F7A, BloodMoss = 0x0F7B, MandrakeRoot = 0x0F86, SulfurAsh = 0x0F8C;
    private const int MarkSpell = 45, RecallSpell = 32, GateSpell = 52;

    private static async Task TravelSuite(Options o, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var bot = await LoginAsync(981, port, ct);
        string[] parts = o.Vendor.Point.Split(',');
        int hx = int.Parse(parts[0], CultureInfo.InvariantCulture) + 2, hy = int.Parse(parts[1], CultureInfo.InvariantCulture) + 2;
        int hz = int.Parse(parts[2], CultureInfo.InvariantCulture);
        const string Away = "1135,1640,0";
        uint rune = 0;

        bool Near(int x, int y) => Math.Max(Math.Abs(bot.World.X - x), Math.Abs(bot.World.Y - y)) <= 1;
        string JournalSince(long since) =>
            string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));

        async Task GoAway()
        {
            await AsStaff(server, bot.AccountName, async () =>
            {
                await SayAsync(bot, $".go {Away}", ct);
                await SayAsync(bot, ".mana 100", ct);
            });
            if (Near(hx, hy)) throw new ScenarioFailed("still at the marked spot after .go");
        }

        await RunScenario("travel", "setup: spellbook, reagents, a blank rune", outcomes, async () =>
        {
            await AsStaff(server, bot.AccountName, async () =>
            {
                await SayAsync(bot, $".go {hx},{hy},{hz}", ct);
                await SayAsync(bot, ".magery 1000", ct);
                await SayAsync(bot, ".int 100", ct);
                await SayAsync(bot, ".mana 100", ct);
                await AddIntoPackAsync(bot, "i_spellbook", ct);
                await AddIntoPackAsync(bot, "i_rune_marker", ct);
                for (int i = 0; i < 3; i++)
                    foreach (string def in new[] { "i_reag_black_pearl", "i_reag_blood_moss", "i_reag_mandrake_root", "i_reag_sulfur_ash" })
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
            });
            var pack = await PackContentsAsync(bot, ct);
            rune = pack.FirstOrDefault(i => i.ItemId == Rune)?.Serial ?? throw new ScenarioFailed("no rune in the pack after .add");
            return $"rune 0x{rune:X}, pearl/moss/mandrake/ash {CountOf(pack, BlackPearl)}/{CountOf(pack, BloodMoss)}/" +
                   $"{CountOf(pack, MandrakeRoot)}/{CountOf(pack, SulfurAsh)}, mana {bot.World.Mana}/{bot.World.MaxMana}";
        });

        await RunScenario("travel", "mark the rune here", outcomes, async () =>
        {
            var pack = await PackContentsAsync(bot, ct);
            int pearls = CountOf(pack, BlackPearl), mana = bot.World.Mana;
            long since = Environment.TickCount64;
            var cast = await bot.Actions.CastSpell(MarkSpell, rune, 5000, ct);
            await WaitAsync(() => bot.World.Mana < mana ? (object)true : null, 8000,
                $"mana to be spent on Mark (cast {cast}; journal: {JournalSince(since)})");
            await Task.Delay(500, ct);
            int pearlsAfter = CountOf(await PackContentsAsync(bot, ct), BlackPearl);
            string said = JournalSince(since);
            if (pearlsAfter != pearls - 1) throw new ScenarioFailed($"black pearl {pearls} -> {pearlsAfter}; journal: {said}");
            // DEFMSG_SPELL_MARK_CONT is the refusal for a rune not carried, never the success.
            if (said.Contains("must be on your person", StringComparison.OrdinalIgnoreCase))
                throw new ScenarioFailed($"a successful mark said the refusal line; journal: {said}");
            return $"marked at {bot.World.X},{bot.World.Y}; mana {mana} -> {bot.World.Mana}{(said.Length > 0 ? $"; {said}" : "")}";
        });

        await RunScenario("travel", "recall to the rune", outcomes, async () =>
        {
            await GoAway();
            long since = Environment.TickCount64;
            var cast = await bot.Actions.CastSpell(RecallSpell, rune, 5000, ct);
            await WaitAsync(() => Near(hx, hy) ? (object)true : null, 8000,
                $"to arrive at {hx},{hy} (cast {cast}, at {bot.World.X},{bot.World.Y}; journal: {JournalSince(since)})");
            return $"recalled from {Away} to {bot.World.X},{bot.World.Y}";
        });

        await RunScenario("travel", "a gate to the rune, walked through", outcomes, async () =>
        {
            await GoAway();
            short ax = bot.World.X, ay = bot.World.Y;
            long since = Environment.TickCount64;
            var cast = await bot.Actions.CastSpell(GateSpell, rune, 5000, ct);
            var gate = await WaitAsync(() =>
            {
                lock (bot.World.SyncRoot)
                    return bot.World.KnownItems.Values.FirstOrDefault(i => i.ContainerSerial == 0 && i.ItemId == Moongate &&
                        Math.Max(Math.Abs(i.X - ax), Math.Abs(i.Y - ay)) <= 3);
            }, 10_000, $"a moongate (0x0F6C) beside the caster (cast {cast}; journal: {JournalSince(since)})");
            // The gate opens on the caster's own tile; a gate takes whoever steps onto it.
            // MoveTo stops a tile short, so single steps: east off the gate, west back on
            // (the first press in a new direction only turns).
            for (int i = 0; i < 3 && bot.World.X == gate.X; i++)
            {
                await bot.Actions.MoveDirection(2, ct);
                await Task.Delay(300, ct);
            }
            string offAt = $"{bot.World.X},{bot.World.Y}";
            for (int i = 0; i < 3 && bot.World.X != gate.X && !Near(hx, hy); i++)
            {
                await bot.Actions.MoveDirection(6, ct);
                await Task.Delay(300, ct);
            }
            await WaitAsync(() => Near(hx, hy) ? (object)true : null, 6000,
                $"the gate at {gate.X},{gate.Y} to take the bot to {hx},{hy} (stepped off to {offAt}; " +
                $"at {bot.World.X},{bot.World.Y}; journal: {JournalSince(since)})");
            bool otherEnd;
            lock (bot.World.SyncRoot)
                otherEnd = bot.World.KnownItems.Values.Any(i => i.ContainerSerial == 0 && i.ItemId == Moongate &&
                    Math.Max(Math.Abs(i.X - hx), Math.Abs(i.Y - hy)) <= 2);
            return $"gate at {gate.X},{gate.Y} took the bot to {bot.World.X},{bot.World.Y}" +
                   (otherEnd ? ", a gate stands at this end too" : ", no gate seen at this end");
        });

        bot.Disconnect();
    }

    // ---- healing and poison ------------------------------------------------------------

    // Scripts-X i_bandage 0x0E21; Source-X ITEMID_BANDAGES_BLOODY1/2 0x0E20 / 0x0E22.
    private const ushort Bandage = 0x0E21;
    private static bool IsBloodyBandage(ushort id) => id is 0x0E20 or 0x0E22;

    private static async Task HealingSuite(ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var healer = await LoginAsync(991, port, ct);
        var patient = await LoginAsync(992, port, ct);
        uint p = patient.World.CharUid;
        // Out of town: no healer NPC raises the ghost before the bandage can.
        const string Wild = "1135,1640,0";

        string JournalSince(BotClient bot, long since) =>
            string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));

        // Bandage the patient: double-click a bandage, target the patient, wait for the
        // skill's delay. Returns the healer's journal for the attempt.
        async Task<string> BandageAsync(long since)
        {
            var band = (await PackContentsAsync(healer, ct)).FirstOrDefault(i => i.ItemId == Bandage)
                ?? throw new ScenarioFailed("no bandage left in the healer's pack");
            await healer.Actions.DoubleClick(band.Serial, 1000, ct);
            if (await healer.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                throw new ScenarioFailed($"no target cursor from the bandage; journal: {JournalSince(healer, since)}");
            await healer.Actions.TargetObject(p, ct);
            return JournalSince(healer, since);
        }

        async Task StaffOnPatient(string verb)
        {
            await AsStaff(server, healer.AccountName, async () =>
            {
                await healer.Actions.Say(verb, ct);
                if (await healer.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed($"no target cursor for {verb}");
                await healer.Actions.TargetObject(p, ct);
                await Task.Delay(800, ct);
            });
        }

        await RunScenario("healing", "setup: a healer with bandages, a wounded patient", outcomes, async () =>
        {
            await AsStaff(server, healer.AccountName, async () =>
            {
                await SayAsync(healer, $".go {Wild}", ct);
                await SayAsync(healer, ".healing 1000", ct);
                await SayAsync(healer, ".anatomy 1000", ct);
                for (int i = 0; i < 6; i++)
                    await AddIntoPackAsync(healer, "i_bandage", ct);
            });
            await AsStaff(server, patient.AccountName, async () =>
            {
                await SayAsync(patient, $".go {Wild}", ct);
                await SayAsync(patient, ".hits 40", ct);
            });
            int bandages = CountOf(await PackContentsAsync(healer, ct), Bandage);
            if (bandages < 3) throw new ScenarioFailed($"{bandages} bandage(s) in the pack");
            if (patient.World.Hits > 40) throw new ScenarioFailed($"patient hits {patient.World.Hits}");
            return $"{bandages} bandages; patient hits {patient.World.Hits}/{patient.World.MaxHits}";
        });

        await RunScenario("healing", "a bandage heals the wounded", outcomes, async () =>
        {
            var before = await PackContentsAsync(healer, ct);
            int bandages = CountOf(before, Bandage), bloody = before.Where(i => IsBloodyBandage(i.ItemId)).Sum(i => Math.Max(1, (int)i.Amount));
            int hits = patient.World.Hits;
            long since = Environment.TickCount64;
            await BandageAsync(since);
            await WaitAsync(() => patient.World.Hits > hits ? (object)true : null, 12_000,
                $"the patient's hits to rise from {hits}; healer journal: {JournalSince(healer, since)}");
            await Task.Delay(500, ct);
            var after = await PackContentsAsync(healer, ct);
            int bandages2 = CountOf(after, Bandage), bloody2 = after.Where(i => IsBloodyBandage(i.ItemId)).Sum(i => Math.Max(1, (int)i.Amount));
            string detail = $"hits {hits} -> {patient.World.Hits}, bandages {bandages} -> {bandages2}, bloody {bloody} -> {bloody2}";
            if (bandages2 != bandages - 1 || bloody2 != bloody + 1) throw new ScenarioFailed(detail);
            return detail;
        });

        await RunScenario("healing", "a bandage cures poison", outcomes, async () =>
        {
            await StaffOnPatient(".xpoison 300");
            await WaitAsync(() => { lock (patient.World.SyncRoot) return patient.World.Poisoned.Contains(p) ? (object)true : null; },
                5000, "the patient's health bar to turn green (0x17 poison)");
            long since = Environment.TickCount64;
            int hits = patient.World.Hits;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await BandageAsync(since);
                for (int i = 0; i < 24; i++)
                {
                    await Task.Delay(500, ct);
                    lock (patient.World.SyncRoot)
                        if (!patient.World.Poisoned.Contains(p))
                            return $"cured after {attempt + 1} bandage(s); hits {hits} -> {patient.World.Hits}; healer: {JournalSince(healer, since)}";
                }
            }
            throw new ScenarioFailed($"still poisoned; healer: {JournalSince(healer, since)}; patient: {JournalSince(patient, since)}");
        });

        await RunScenario("healing", "a bandage raises a ghost", outcomes, async () =>
        {
            int screens = patient.World.DeathScreens;
            await StaffOnPatient(".xkill");
            await WaitAsync(() => patient.World.DeathScreens > screens && GhostBodies.Contains(patient.World.Body) ? (object)true : null,
                6000, "the patient to die");
            long since = Environment.TickCount64;
            // An unmanifested ghost is insubstantial (Source-X sets STATF_INSUBSTANTIAL at
            // death out of war mode): the living cannot see it. War mode manifests it.
            await BandageAsync(since);
            await Task.Delay(1500, ct);
            string unmanifested = JournalSince(healer, since);
            if (!unmanifested.Contains("manifest", StringComparison.OrdinalIgnoreCase))
                throw new ScenarioFailed($"an unmanifested ghost was not refused; healer: {unmanifested}");
            await patient.Actions.SetWarMode(true, ct);
            await Task.Delay(800, ct);
            since = Environment.TickCount64;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await BandageAsync(since);
                for (int i = 0; i < 24; i++)
                {
                    await Task.Delay(500, ct);
                    if (!GhostBodies.Contains(patient.World.Body))
                        return $"refused until manifested; alive after {attempt + 1} bandage(s) in war mode: body 0x{patient.World.Body:X}, " +
                               $"hits {patient.World.Hits}/{patient.World.MaxHits}; healer: {JournalSince(healer, since)}";
                }
            }
            throw new ScenarioFailed($"still a ghost; healer: {JournalSince(healer, since)}; patient: {JournalSince(patient, since)}");
        });

        healer.Disconnect();
        patient.Disconnect();
    }

    // ---- hiding, snooping, stealing --------------------------------------------------

    private const int HidingSkill = 21, StealingSkill = 33;

    private static async Task ThiefSuite(ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var thief = await LoginAsync(1001, port, ct);
        var mark = await LoginAsync(1002, port, ct);
        uint t = thief.World.CharUid;
        const int Wx = 1135, Wy = 1640, Wz = 0;
        uint gears = 0;

        string JournalSince(BotClient bot, long since) =>
            string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));
        bool MarkSeesThief() { lock (mark.World.SyncRoot) return mark.World.Mobiles.ContainsKey(t); }

        await RunScenario("thief", "setup: a thief beside a mark with gears", outcomes, async () =>
        {
            await AsStaff(server, thief.AccountName, async () =>
            {
                await SayAsync(thief, $".go {Wx},{Wy},{Wz}", ct);
                foreach (string skill in new[] { ".hiding 1000", ".stealing 1000", ".snooping 1000", ".stealth 1000" })
                    await SayAsync(thief, skill, ct);
            });
            await AsStaff(server, mark.AccountName, async () =>
            {
                await SayAsync(mark, $".go {Wx + 1},{Wy},{Wz}", ct);
                await AddIntoPackAsync(mark, "i_gears", ct);
            });
            gears = (await PackContentsAsync(mark, ct)).FirstOrDefault(i => i.ItemId == Gears)?.Serial
                ?? throw new ScenarioFailed("no gears in the mark's pack");
            await WaitAsync(() => MarkSeesThief() ? (object)true : null, 5000, "the thief in the mark's view");
            if (mark.World.BackpackSerial == 0) throw new ScenarioFailed("the mark's backpack serial is unknown");
            return $"gears 0x{gears:X} in the mark's pack 0x{mark.World.BackpackSerial:X}";
        });

        await RunScenario("thief", "hiding takes the thief out of sight", outcomes, async () =>
        {
            long since = Environment.TickCount64;
            for (int attempt = 0; attempt < 4 && MarkSeesThief(); attempt++)
            {
                await thief.Actions.UseSkill(HidingSkill, 3000, ct);
                for (int i = 0; i < 16 && MarkSeesThief(); i++)
                    await Task.Delay(250, ct);
            }
            if (MarkSeesThief()) throw new ScenarioFailed($"the mark still sees the thief; thief: {JournalSince(thief, since)}");
            return $"gone from the mark's view; thief: {JournalSince(thief, since)}";
        });

        await RunScenario("thief", "speaking reveals the hidden thief", outcomes, async () =>
        {
            await thief.Actions.Say("hello", ct);
            await WaitAsync(() => MarkSeesThief() ? (object)true : null, 4000, "the thief to reappear after speaking");
            return "back in the mark's view after speaking";
        });

        await RunScenario("thief", "snooping opens the mark's pack", outcomes, async () =>
        {
            uint pack = mark.World.BackpackSerial;
            long since = Environment.TickCount64;
            await thief.Actions.DoubleClick(pack, 1000, ct);
            var seen = await WaitAsync(() =>
            {
                lock (thief.World.SyncRoot)
                    return thief.World.OpenContainers.TryGetValue(pack, out var c) && c.Items.Any(i => i.Serial == gears) ? c : null;
            }, 5000, $"the mark's pack to open for the thief with the gears in it; thief: {JournalSince(thief, since)}");
            return $"pack open, {seen.Items.Count} item(s) seen; mark heard: {JournalSince(mark, since)}";
        });

        await RunScenario("thief", "stealing takes the gears", outcomes, async () =>
        {
            long since = Environment.TickCount64;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await thief.Actions.UseSkill(StealingSkill, 3000, ct);
                if (await thief.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed($"no target cursor from Stealing; thief: {JournalSince(thief, since)}");
                await thief.Actions.TargetObject(gears, ct);
                await Task.Delay(2500, ct);
                var thiefPack = await PackContentsAsync(thief, ct);
                if (thiefPack.Any(i => i.Serial == gears || i.ItemId == Gears))
                {
                    var markPack = await PackContentsAsync(mark, ct);
                    int left = CountOf(markPack, Gears);
                    if (left != 0)
                    {
                        string theirs = string.Join(",", thiefPack.Where(i => i.ItemId == Gears).Select(i => $"0x{i.Serial:X}"));
                        string mine = string.Join(",", markPack.Where(i => i.ItemId == Gears).Select(i => $"0x{i.Serial:X}"));
                        throw new ScenarioFailed($"the thief has gears ({theirs}) but the mark still shows {left} ({mine}); stolen 0x{gears:X}");
                    }
                    return $"stolen on attempt {attempt + 1}; thief: {JournalSince(thief, since)}; mark: {JournalSince(mark, since)}";
                }
            }
            throw new ScenarioFailed($"not stolen in 5 tries; thief: {JournalSince(thief, since)}; mark: {JournalSince(mark, since)}");
        });

        thief.Disconnect();
        mark.Disconnect();
    }

    // ---- party -----------------------------------------------------------------------

    private static async Task PartySuite(ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var leader = await LoginAsync(1011, port, ct);
        var member = await LoginAsync(1012, port, ct);
        uint l = leader.World.CharUid, m = member.World.CharUid;
        const string Wild = "1135,1640,0";

        string JournalSince(BotClient bot, long since) =>
            string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));
        bool InParty(BotClient bot, params uint[] who) { lock (bot.World.SyncRoot) return who.All(bot.World.PartyMembers.Contains); }

        await RunScenario("party", "setup: two players side by side", outcomes, async () =>
        {
            await AsStaff(server, leader.AccountName, () => SayAsync(leader, $".go {Wild}", ct));
            await AsStaff(server, member.AccountName, () => SayAsync(member, $".go {Wild}", ct));
            if (leader.World.PartyMembers.Count != 0 || member.World.PartyMembers.Count != 0)
                throw new ScenarioFailed("already in a party");
            return "both out of any party";
        });

        await RunScenario("party", "the party menu's Add asks who, and invites", outcomes, async () =>
        {
            long since = Environment.TickCount64;
            // The client's party gump sends Add with serial 0 and waits for a cursor.
            leader.SendRaw(BotPacketBuilder.BuildParty(0x01, 0));
            if (await leader.Actions.WaitForTarget(4000, ct) != BotActionResult.Success)
                throw new ScenarioFailed($"no target cursor after Add; leader: {JournalSince(leader, since)}");
            await leader.Actions.TargetObject(m, ct);
            await WaitAsync(() => member.World.PartyInviteFrom == l ? (object)true : null, 4000,
                $"the invitation (0xBF.06.07) from the leader; leader: {JournalSince(leader, since)}; member: {JournalSince(member, since)}");
            return $"cursor, then invited; member: {JournalSince(member, since)}";
        });

        await RunScenario("party", "accepting forms a party of two", outcomes, async () =>
        {
            member.SendRaw(BotPacketBuilder.BuildParty(0x08, l));
            await WaitAsync(() => InParty(leader, l, m) && InParty(member, l, m) ? (object)true : null, 4000,
                $"both party lists to hold both (leader {leader.World.PartyMembers.Count}, member {member.World.PartyMembers.Count})");
            return "both see a party of two";
        });

        await RunScenario("party", "party chat reaches the member", outcomes, async () =>
        {
            long since = Environment.TickCount64;
            leader.SendRaw(BotPacketBuilder.BuildPartyMessage("meet at the bank"));
            await WaitAsync(() => member.World.Journal.ToArray().Any(e => e.TimestampMs >= since && e.SpeakerSerial == l &&
                    e.Speaker == "[party]" && e.Text == "meet at the bank") ? (object)true : null, 4000,
                $"the party message at the member; member: {JournalSince(member, since)}");
            return "member heard the party message";
        });

        await RunScenario("party", "leaving ends a party of two", outcomes, async () =>
        {
            member.SendRaw(BotPacketBuilder.BuildParty(0x02, m));
            await WaitAsync(() => member.World.PartyMembers.Count == 0 ? (object)true : null, 4000,
                $"the member's party list to empty ({member.World.PartyMembers.Count})");
            await WaitAsync(() => leader.World.PartyMembers.Count == 0 ? (object)true : null, 4000,
                $"the leader's party list to empty, a party of one disbands ({leader.World.PartyMembers.Count})");
            return "both out of the party";
        });

        leader.Disconnect();
        member.Disconnect();
    }

    // ---- ships -----------------------------------------------------------------------

    private const ushort ShipDeed = 0x14F1;

    /// <summary>Open sea for a small ship facing north: wet land with no statics over the
    /// hull and a clear lane ahead of it.</summary>
    private static IEnumerable<(int X, int Y, int Z)> WaterSites(SphereNet.MapData.MapDataManager map, int cx, int cy, int radius)
    {
        for (int r = 0; r <= radius; r += 4)
        for (int dx = -r; dx <= r; dx += 4)
        for (int dy = -r; dy <= r; dy += 4)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            int x = cx + dx, y = cy + dy;
            bool ok = true;
            for (int ix = x - 5; ix <= x + 5 && ok; ix++)
            for (int iy = y - 30; iy <= y + 8 && ok; iy++)
            {
                var land = map.GetTerrainTile(0, ix, iy);
                if (!map.GetLandTileData(land.TileId).IsWet || map.GetStatics(0, ix, iy).Length > 0)
                    ok = false;
            }
            if (ok) yield return (x, y, map.GetTerrainTile(0, x, y).Z);
        }
    }

    private static async Task ShipSuite(string root, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        string mul = ReadIniValue(Path.Combine(root, "sphere.ini"), "MulFiles") ?? "";
        if (!Path.IsPathRooted(mul)) mul = Path.Combine(root, mul);
        using var map = new SphereNet.MapData.MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);

        var captain = await LoginAsync(1021, port, ct);
        // A pre-High-Seas client aboard: it cannot parse 0xF6 and must be moved with the
        // ship by ordinary updates (CCMultiMovable.cpp:333).
        var deckhand = await LoginAsync(1022, port, ct, ReferenceClientProtocol.Pack(5, 0, 1, 0));
        int deckhandF6 = 0;
        deckhand.ServerPacketObserver = p => { if (p[0] == 0xF6) Interlocked.Increment(ref deckhandF6); };
        BotKnownItem? ship = null;

        string JournalSince(long since) =>
            string.Join(" | ", captain.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));
        BotKnownItem? Ship() { lock (captain.World.SyncRoot) return ship == null ? null : captain.World.KnownItems.GetValueOrDefault(ship.Serial); }

        await RunScenario("ship", "place a ship from a deed", outcomes, async () =>
        {
            var tried = new List<string>();
            foreach (var (cx, cy) in new[] { (1500, 1780), (1560, 1700), (1420, 1900), (1650, 1620) })
            {
                foreach (var cand in WaterSites(map, cx, cy, 80).Take(2))
                {
                    await AsStaff(server, captain.AccountName, async () =>
                    {
                        await SayAsync(captain, $".go {cand.X},{cand.Y + 10},{cand.Z}", ct);
                        await AddIntoPackAsync(captain, "i_deed_small_ship_n", ct);
                    });
                    var pack = await PackContentsAsync(captain, ct);
                    var deed = pack.FirstOrDefault(i => i.ItemId == ShipDeed)
                        ?? throw new ScenarioFailed("no ship deed in the pack after .add");
                    int keysBefore = pack.Count(i => IsKey(i.ItemId));
                    long since = Environment.TickCount64;
                    await captain.Actions.DoubleClick(deed.Serial, 1000, ct);
                    if (await captain.Actions.WaitForTarget(4000, ct) != BotActionResult.Success)
                        throw new ScenarioFailed($"no placement cursor from the deed; journal: {JournalSince(since)}");
                    captain.SendRaw(BotPacketBuilder.BuildTargetLocation(captain.World.TargetCursorId,
                        (short)cand.X, (short)cand.Y, (sbyte)cand.Z));
                    captain.World.HasPendingTarget = false;
                    await Task.Delay(2500, ct);
                    var after = await PackContentsAsync(captain, ct);
                    if (after.Any(i => i.Serial == deed.Serial))
                    {
                        tried.Add($"{cand.X},{cand.Y},{cand.Z}: {JournalSince(since)}");
                        continue;
                    }
                    lock (captain.World.SyncRoot)
                        ship = captain.World.KnownItems.Values.FirstOrDefault(i => i.IsMulti && i.ContainerSerial == 0 &&
                            Math.Abs(i.X - cand.X) <= 8 && Math.Abs(i.Y - cand.Y) <= 8);
                    if (ship == null)
                    {
                        string near;
                        lock (captain.World.SyncRoot)
                            near = string.Join(", ", captain.World.KnownItems.Values.Where(i => i.ContainerSerial == 0 &&
                                    Math.Abs(i.X - cand.X) <= 12 && Math.Abs(i.Y - cand.Y) <= 12)
                                .Select(i => $"0x{i.ItemId:X}{(i.IsMulti ? "M" : "")}@{i.X},{i.Y},{i.Z}"));
                        throw new ScenarioFailed($"the deed was used at {cand.X},{cand.Y} but no ship multi is in view; near: {near}; journal: {JournalSince(since)}");
                    }
                    int keys = after.Count(i => IsKey(i.ItemId)) - keysBefore;
                    if (keys < 1) throw new ScenarioFailed($"ship placed at {cand.X},{cand.Y} but no key came into the pack");
                    return $"ship 0x{ship.Serial:X} (multi 0x{ship.ItemId & 0x3FFF:X}) at {ship.X},{ship.Y},{ship.Z}, {keys} key(s)" +
                           (tried.Count > 0 ? $"; refused first at: {string.Join(" || ", tried)}" : "");
                }
            }
            throw new ScenarioFailed(tried.Count == 0 ? "no open water found" : "every site refused: " + string.Join(" || ", tried));
        });

        await RunScenario("ship", "'forward' sails the ship with its captain aboard", outcomes, async () =>
        {
            if (ship == null) throw new ScenarioFailed("no ship");
            await AsStaff(server, captain.AccountName, () => SayAsync(captain, $".go {ship.X},{ship.Y + 1},{ship.Z + 3}", ct));
            await AsStaff(server, deckhand.AccountName, () => SayAsync(deckhand, $".go {ship.X},{ship.Y - 1},{ship.Z + 3}", ct));
            await Task.Delay(1000, ct);
            short sy = Ship()?.Y ?? ship.Y, cy = captain.World.Y, dy = deckhand.World.Y;
            long since = Environment.TickCount64;
            await captain.Actions.Say("forward", ct);
            await WaitAsync(() => Ship() is { } s && s.Y < sy - 2 ? (object)true : null, 10_000,
                $"the ship to move north from y {sy} (ship y {Ship()?.Y}, captain {captain.World.X},{captain.World.Y}; journal: {JournalSince(since)})");
            await Task.Delay(1500, ct);
            int shipMoved = sy - (Ship()?.Y ?? sy), captainMoved = cy - captain.World.Y, deckhandMoved = dy - deckhand.World.Y;
            if (captainMoved < shipMoved - 2)
                throw new ScenarioFailed($"the ship went {shipMoved} tiles but its captain (7.0.20, smooth) only {captainMoved}");
            if (deckhandMoved < shipMoved - 2)
                throw new ScenarioFailed($"the ship went {shipMoved} tiles but the 5.0.1 deckhand only {deckhandMoved}");
            if (deckhandF6 > 0)
                throw new ScenarioFailed($"the 5.0.1 deckhand was sent {deckhandF6} smooth-move packet(s) (0xF6) it cannot read");
            return $"ship sailed {shipMoved} tiles north; captain carried {captainMoved} (0xF6), 5.0.1 deckhand {deckhandMoved} (no 0xF6); journal: {JournalSince(since)}";
        });

        await RunScenario("ship", "'stop' halts it", outcomes, async () =>
        {
            if (ship == null) throw new ScenarioFailed("no ship");
            await captain.Actions.Say("stop", ct);
            await Task.Delay(2000, ct);
            short y1 = Ship()?.Y ?? 0;
            await Task.Delay(3000, ct);
            short y2 = Ship()?.Y ?? 0;
            if (y1 != y2) throw new ScenarioFailed($"still moving after 'stop': y {y1} -> {y2}");
            return $"stopped at y {y2}";
        });

        captain.Disconnect();
        deckhand.Disconnect();
    }

    // ---- spawns ----------------------------------------------------------------------

    private const ushort SpawnGem = 0x1EA7, RabbitBody = 0x00CD;

    private static async Task SpawnSuite(ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        var gm = await LoginAsync(1031, port, ct);
        const int Gx = 1160, Gy = 1660, Gz = 0;
        uint gem = 0;
        HashSet<uint> before = new();

        List<KnownMobile> Rabbits()
        {
            lock (gm.World.SyncRoot)
                return gm.World.Mobiles.Values.Where(m => !before.Contains(m.Serial) && m.Body == RabbitBody &&
                    Math.Max(Math.Abs(m.X - Gx), Math.Abs(m.Y - Gy)) <= 6).ToList();
        }

        async Task OnGem(string verb)
        {
            await gm.Actions.Say(verb, ct);
            if (await gm.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                throw new ScenarioFailed($"no target cursor for {verb}");
            await gm.Actions.TargetObject(gem, ct);
            await Task.Delay(400, ct);
        }

        // Upstream spawns ONE creature per timer (CCSpawn::OnTickComponent) and START fires
        // the gem now (SetTimeout(0)): amount 2 with a one-minute timer is one at once and
        // the second a minute later.
        await RunScenario("spawn", "START spawns at once, the timer fills the gem", outcomes, async () =>
        {
            await AsStaff(server, gm.AccountName, async () =>
            {
                await SayAsync(gm, $".go {Gx},{Gy + 3},{Gz}", ct);
                lock (gm.World.SyncRoot) before = gm.World.Mobiles.Keys.ToHashSet();
                await gm.Actions.Say(".add i_worldgem_bit", ct);
                if (await gm.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed("no target cursor for .add i_worldgem_bit");
                await gm.Actions.TargetLocation(Gx, Gy, Gz, ct);
                var found = await WaitAsync(() => { lock (gm.World.SyncRoot) return gm.World.KnownItems.Values.FirstOrDefault(i =>
                    i.ItemId == SpawnGem && i.ContainerSerial == 0 && i.X == Gx && i.Y == Gy); }, 4000, "the spawn gem on the ground");
                gem = found.Serial;
                foreach (string verb in new[] { ".xspawnid c_rabbit", ".xamount 2", ".xmaxdist 4", ".xtimelo 1", ".xtimehi 1", ".xstart" })
                    await OnGem(verb);
            });
            var sw = Stopwatch.StartNew();
            await WaitAsync(() => Rabbits().Count >= 1 ? (object)true : null, 10_000,
                $"the first rabbit right after START (have {Rabbits().Count})");
            double first = sw.Elapsed.TotalSeconds;
            await WaitAsync(() => Rabbits().Count >= 2 ? (object)true : null, 100_000,
                $"the second rabbit on the one-minute timer (have {Rabbits().Count})");
            double second = sw.Elapsed.TotalSeconds;
            await Task.Delay(5000, ct);
            int n = Rabbits().Count;
            if (n != 2) throw new ScenarioFailed($"{n} rabbits around a gem of amount 2");
            return $"gem 0x{gem:X}: first rabbit after {first:F0}s, second after {second:F0}s, none beyond the amount";
        });

        await RunScenario("spawn", "a killed spawn is replaced on the gem's timer", outcomes, async () =>
        {
            var victim = Rabbits().FirstOrDefault() ?? throw new ScenarioFailed("no rabbit to kill");
            await AsStaff(server, gm.AccountName, async () =>
            {
                await gm.Actions.Say(".xkill", ct);
                if (await gm.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed("no target cursor for .xkill");
                await gm.Actions.TargetObject(victim.Serial, ct);
            });
            await WaitAsync(() => Rabbits().All(r => r.Serial != victim.Serial) ? (object)true : null, 5000, "the killed rabbit to go");
            int after = Rabbits().Count;
            if (after != 1) throw new ScenarioFailed($"{after} rabbits after one was killed");
            var sw = Stopwatch.StartNew();
            // TIMELO/TIMEHI 1: one minute, as Source-X reads them.
            await WaitAsync(() => Rabbits().Count >= 2 ? (object)true : null, 100_000,
                $"the gem to replace the rabbit (have {Rabbits().Count})");
            return $"replaced after {sw.Elapsed.TotalSeconds:F0}s (TIMELO=TIMEHI=1 minute)";
        });

        await AsStaff(server, gm.AccountName, async () =>
        {
            await OnGem(".xremove");
            foreach (var r in Rabbits())
            {
                await gm.Actions.Say(".xremove", ct);
                if (await gm.Actions.WaitForTarget(3000, ct) == BotActionResult.Success)
                    await gm.Actions.TargetObject(r.Serial, ct);
                await Task.Delay(300, ct);
            }
        });
        gm.Disconnect();
    }

    // ---- guilds ----------------------------------------------------------------------

    private static async Task GuildSuite(string root, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        string mul = ReadIniValue(Path.Combine(root, "sphere.ini"), "MulFiles") ?? "";
        if (!Path.IsPathRooted(mul)) mul = Path.Combine(root, mul);
        int Sx, Sy, Sz;
        using (var map = new SphereNet.MapData.MapDataManager(mul))
        {
            map.Load();
            map.InitMap(0, 7168, 4096);
            // Open, flat, walkable ground away from the house drill's site.
            (Sx, Sy, Sz) = HouseSites(map, 1180, 1660, 60).FirstOrDefault();
        }
        var founder = await LoginAsync(1041, port, ct);
        uint stone = 0;

        string JournalSince(long since) =>
            string.Join(" | ", founder.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));

        async Task<BotKnownItem> NewDeed()
        {
            List<BotKnownItem> before = await PackContentsAsync(founder, ct);
            await AsStaff(server, founder.AccountName, () => AddIntoPackAsync(founder, "i_deed_guildstone", ct));
            var after = await PackContentsAsync(founder, ct);
            return after.FirstOrDefault(i => before.All(b => b.Serial != i.Serial))
                ?? throw new ScenarioFailed("no guildstone deed in the pack after .add");
        }

        await RunScenario("guild", "a guildstone deed places the stone and asks for a name", outcomes, async () =>
        {
            if (Sx == 0) throw new ScenarioFailed("no open ground found for the stone");
            await AsStaff(server, founder.AccountName, () => SayAsync(founder, $".go {Sx},{Sy + 2},{Sz}", ct));
            var deed = await NewDeed();
            HashSet<uint> seen;
            lock (founder.World.SyncRoot) seen = founder.World.KnownItems.Keys.ToHashSet();
            long since = Environment.TickCount64;
            founder.World.PromptId = 0;
            await founder.Actions.DoubleClick(deed.Serial, 1000, ct);
            if (await founder.Actions.WaitForTarget(4000, ct) != BotActionResult.Success)
                throw new ScenarioFailed($"no placement cursor from the deed; journal: {JournalSince(since)}");
            // On the ground the founder stands on, two tiles north.
            await founder.Actions.TargetLocation(Sx, Sy, Sz, ct);
            BotKnownItem? placed = null;
            try
            {
                placed = await WaitAsync(() => { lock (founder.World.SyncRoot) return founder.World.KnownItems.Values.FirstOrDefault(i =>
                        !seen.Contains(i.Serial) && i.ContainerSerial == 0 && i.X == Sx && i.Y == Sy); }, 4000, "the stone");
            }
            catch (TimeoutException)
            {
                throw new ScenarioFailed($"no stone on the ground at {Sx},{Sy}; journal: {JournalSince(since)}");
            }
            stone = placed.Serial;
            await WaitAsync(() => founder.World.PromptId != 0 ? (object)true : null, 4000,
                $"the name prompt (0x9A); journal: {JournalSince(since)}");
            var pack = await PackContentsAsync(founder, ct);
            if (pack.Any(i => i.Serial == deed.Serial)) throw new ScenarioFailed("the deed is still in the pack");
            return $"stone 0x{stone:X} (0x{placed.ItemId:X4}) placed, deed used up; {JournalSince(since)}";
        });

        await RunScenario("guild", "the answer names the stone", outcomes, async () =>
        {
            if (stone == 0) throw new ScenarioFailed("no stone");
            long since = Environment.TickCount64;
            founder.SendRaw(BotPacketBuilder.BuildPromptResponse(founder.World.PromptSerial, founder.World.PromptId, "Drill Guild"));
            await Task.Delay(800, ct);
            founder.SendRaw(BotPacketBuilder.BuildSingleClick(stone));
            var label = await WaitAsync(() => founder.World.Journal.ToArray().FirstOrDefault(e => e.TimestampMs >= since &&
                    e.SpeakerSerial == stone && e.Text.Contains("Drill Guild", StringComparison.Ordinal)), 4000,
                $"the stone's name to carry 'Drill Guild'; journal: {JournalSince(since)}");
            return $"'{label.Text}'; {JournalSince(since)}";
        });

        await RunScenario("guild", "a guild member cannot found another guild", outcomes, async () =>
        {
            if (stone == 0) throw new ScenarioFailed("no stone");
            var deed = await NewDeed();
            long since = Environment.TickCount64;
            await founder.Actions.DoubleClick(deed.Serial, 1000, ct);
            await Task.Delay(1500, ct);
            string said = JournalSince(since);
            if (founder.World.HasPendingTarget)
                throw new ScenarioFailed($"a placement cursor was offered to a guild master; journal: {said}");
            if (!said.Contains("guild", StringComparison.OrdinalIgnoreCase))
                throw new ScenarioFailed($"no refusal; journal: {said}");
            return $"refused: {said}";
        });

        await AsStaff(server, founder.AccountName, async () =>
        {
            if (stone == 0) return;
            await founder.Actions.Say(".xremove", ct);
            if (await founder.Actions.WaitForTarget(3000, ct) == BotActionResult.Success)
                await founder.Actions.TargetObject(stone, ct);
            await Task.Delay(500, ct);
        });
        founder.Disconnect();
    }

    // ---- player vendors --------------------------------------------------------------

    private static async Task PlayerVendorSuite(string root, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        string mul = ReadIniValue(Path.Combine(root, "sphere.ini"), "MulFiles") ?? "";
        if (!Path.IsPathRooted(mul)) mul = Path.Combine(root, mul);
        string Wild;
        using (var map = new SphereNet.MapData.MapDataManager(mul))
        {
            map.Load();
            map.InitMap(0, 7168, 4096);
            // Open ground away from town, so no other vendor hears "vendor buy".
            var site = HouseSites(map, 1220, 1700, 80).FirstOrDefault();
            Wild = $"{site.X},{site.Y},{site.Z}";
        }
        var owner = await LoginAsync(1051, port, ct);
        var buyer = await LoginAsync(1052, port, ct);
        uint vendor = 0, gears = 0;
        const int Price = 100;

        string Said(BotClient bot, long since) =>
            string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => $"{e.Speaker}: {e.Text}"));

        await RunScenario("pvendor", "a vendor deed hires a vendor for its owner", outcomes, async () =>
        {
            await AsStaff(server, owner.AccountName, async () =>
            {
                await SayAsync(owner, $".go {Wild}", ct);
                await AddIntoPackAsync(owner, "i_deed_vendor", ct);
                await AddIntoPackAsync(owner, "i_gears", ct);
            });
            await AsStaff(server, buyer.AccountName, async () =>
            {
                await SayAsync(buyer, $".go {Wild}", ct);
                await SayAsync(buyer, ".newgold 1000", ct);
            });
            var pack = await PackContentsAsync(owner, ct);
            var deed = pack.FirstOrDefault(i => i.ItemId == 0x14EF) ?? throw new ScenarioFailed("no vendor deed in the pack");
            gears = pack.FirstOrDefault(i => i.ItemId == Gears)?.Serial ?? throw new ScenarioFailed("no gears in the pack");
            HashSet<uint> before;
            lock (owner.World.SyncRoot) before = owner.World.Mobiles.Keys.ToHashSet();
            long since = Environment.TickCount64;
            await owner.Actions.DoubleClick(deed.Serial, 1000, ct);
            var hired = await WaitAsync(() => { lock (owner.World.SyncRoot) return owner.World.Mobiles.Values.FirstOrDefault(m =>
                    !before.Contains(m.Serial) && m.IsHumanBody && owner.World.DistanceTo(m.X, m.Y) <= 2); }, 5000,
                "a vendor beside its owner");
            vendor = hired.Serial;
            return $"vendor 0x{vendor:X}; {Said(owner, since)}";
        });

        await RunScenario("pvendor", "the owner stocks and prices an item", outcomes, async () =>
        {
            if (vendor == 0) throw new ScenarioFailed("no vendor");
            long since = Environment.TickCount64;
            await owner.Actions.PickUp(gears, 1, 2000, ct);
            await owner.Actions.DropToContainer(gears, vendor, 2000, ct);
            await Task.Delay(1000, ct);
            // The vendor takes it into its stock and says so (NPC_PET_SELL); the bot keeps
            // a lifted item in its own model, so its pack is no witness here.
            if (!Said(owner, since).Contains("sell", StringComparison.OrdinalIgnoreCase))
                throw new ScenarioFailed($"the vendor did not take the gears; {Said(owner, since)}");
            // The pet command PRICE with a number: the cursor picks the item (PC_PRICE).
            await owner.Actions.Say($"all price {Price}", ct);
            if (await owner.Actions.WaitForTarget(4000, ct) != BotActionResult.Success)
                throw new ScenarioFailed($"no cursor for 'price'; {Said(owner, since)}");
            await owner.Actions.TargetObject(gears, ct);
            await Task.Delay(1200, ct);
            string said = Said(owner, since);
            if (!said.Contains(Price.ToString(), StringComparison.Ordinal))
                throw new ScenarioFailed($"no price confirmation; {said}");
            return said;
        });

        await RunScenario("pvendor", "a customer buys the item at its price", outcomes, async () =>
        {
            if (vendor == 0) throw new ScenarioFailed("no vendor");
            int goldBefore = await RefreshGoldAsync(buyer, ct);
            long since = Environment.TickCount64;
            buyer.World.ActiveVendor = null;
            await SayAsync(buyer, "vendor buy", ct);
            var shop = await WaitAsync(() => buyer.World.ActiveVendor is { IsBuyList: true } v && v.VendorSerial == vendor
                ? buyer.World.ActiveVendor : null, 8000, $"the player vendor's buy window; {Said(buyer, since)}");
            var entry = shop.Items.FirstOrDefault(i => i.Serial == gears)
                ?? throw new ScenarioFailed($"the gears are not on offer: {string.Join(", ", shop.Items.Select(i => $"0x{i.Serial:X}={i.Price}"))}");
            if (entry.Price != Price) throw new ScenarioFailed($"listed at {entry.Price}, priced at {Price}");
            await buyer.Actions.Buy(vendor, gears, 1, ct);
            int goldAfter = await WaitGoldChangeAsync(buyer, goldBefore, ct);
            var pack = await PackContentsAsync(buyer, ct);
            if (goldAfter != goldBefore - Price) throw new ScenarioFailed($"gold {goldBefore} -> {goldAfter}");
            if (!pack.Any(i => i.ItemId == Gears)) throw new ScenarioFailed("no gears in the buyer's pack");
            return $"bought for {Price}: gold {goldBefore} -> {goldAfter}";
        });

        owner.Disconnect();
        buyer.Disconnect();
    }

    // ---- mounts ----------------------------------------------------------------------

    private const byte MountLayer = 0x19;

    private static bool IsRiding(BotClient bot)
    {
        lock (bot.World.SyncRoot)
            return bot.World.Equipment.TryGetValue(MountLayer, out var m) && bot.World.KnownItems.ContainsKey(m.Serial);
    }

    private static async Task MountSuite(string root, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        string mul = ReadIniValue(Path.Combine(root, "sphere.ini"), "MulFiles") ?? "";
        if (!Path.IsPathRooted(mul)) mul = Path.Combine(root, mul);
        string spot;
        using (var map = new SphereNet.MapData.MapDataManager(mul))
        {
            map.Load();
            map.InitMap(0, 7168, 4096);
            var site = HouseSites(map, 1260, 1720, 80).FirstOrDefault();
            spot = $"{site.X},{site.Y},{site.Z}";
        }
        var rider = await LoginAsync(1061, port, ct);
        var stranger = await LoginAsync(1062, port, ct);
        uint horse = 0;

        string Said(BotClient bot, long since) =>
            string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));
        bool Sees(BotClient bot, uint serial) { lock (bot.World.SyncRoot) return bot.World.Mobiles.ContainsKey(serial); }
        // Riding takes a horse within reach: step up next to it first.
        async Task NextTo(BotClient bot, uint serial)
        {
            var m = await WaitAsync(() => { lock (bot.World.SyncRoot) return bot.World.Mobiles.GetValueOrDefault(serial); }, 4000, "the horse in view");
            if (bot.World.DistanceTo(m.X, m.Y) > 1)
                await bot.Actions.MoveTo(m.X, m.Y, 6000, ct);
        }

        await RunScenario("mount", "setup: a tamed horse", outcomes, async () =>
        {
            HashSet<uint> before;
            await AsStaff(server, rider.AccountName, async () =>
            {
                await SayAsync(rider, $".go {spot}", ct);
                await SayAsync(rider, ".taming 1000", ct);
                await SayAsync(rider, ".animallore 1000", ct);
                // A pack whose taming wants a staff in the pack (Sphere 56T custom version)
                // defines i_staff_taming; elsewhere the .add simply finds nothing.
                try { await AddIntoPackAsync(rider, "i_staff_taming", ct); } catch (ScenarioFailed) { }
                lock (rider.World.SyncRoot) before = rider.World.Mobiles.Keys.ToHashSet();
                await rider.Actions.Say(".add c_horse_tan", ct);
                if (await rider.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed("no target cursor for .add c_horse_tan");
                await rider.Actions.TargetLocation(rider.World.X + 1, rider.World.Y, rider.World.Z, ct);
                await Task.Delay(800, ct);
            });
            lock (rider.World.SyncRoot) before = rider.World.Mobiles.Keys.Where(k => rider.World.Mobiles[k].Body != 0x00C8).ToHashSet();
            var found = await WaitAsync(() => { lock (rider.World.SyncRoot) return rider.World.Mobiles.Values.FirstOrDefault(m =>
                m.Body == 0x00C8 && rider.World.DistanceTo(m.X, m.Y) <= 2); }, 5000, "the horse");
            horse = found.Serial;
            await AsStaff(server, stranger.AccountName, () => SayAsync(stranger, $".go {spot}", ct));
            long since = Environment.TickCount64;
            // A pack may tame in timed stages (the 56T pack does); let each attempt run.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await NextTo(rider, horse);
                await rider.Actions.UseSkill(35, 3000, ct);
                if (await rider.Actions.WaitForTarget(3000, ct) == BotActionResult.Success)
                    await rider.Actions.TargetObject(horse, ct);
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500, ct);
                    // Stay beside it: the animal wanders while a staged taming runs.
                    if (i % 4 == 3) await NextTo(rider, horse);
                    string heard = Said(rider, since);
                    // "accept you as master" (Source-X) or the 56T pack's "sizi sahibi olarak kabul etti".
                    if (heard.Contains("master", StringComparison.OrdinalIgnoreCase) ||
                        heard.Contains("sahibi", StringComparison.OrdinalIgnoreCase))
                        return $"horse 0x{horse:X} tamed";
                }
            }
            throw new ScenarioFailed($"not tamed; {Said(rider, since)}");
        });

        await RunScenario("mount", "a stranger cannot ride another's horse", outcomes, async () =>
        {
            if (horse == 0) throw new ScenarioFailed("no horse");
            await NextTo(stranger, horse);
            long since = Environment.TickCount64;
            await stranger.Actions.DoubleClick(horse, 1000, ct);
            await Task.Delay(1500, ct);
            if (IsRiding(stranger)) throw new ScenarioFailed($"the stranger rides it; {Said(stranger, since)}");
            if (!Sees(stranger, horse)) throw new ScenarioFailed("the horse vanished for the stranger");
            // Horse_Mount: MSG_MOUNT_DONTOWN, "You dont own that horse."
            if (!Said(stranger, since).Contains("own", StringComparison.OrdinalIgnoreCase))
                throw new ScenarioFailed($"not refused for ownership; {Said(stranger, since)}");
            return $"refused: {Said(stranger, since)}";
        });

        await RunScenario("mount", "the owner mounts by double-click", outcomes, async () =>
        {
            if (horse == 0) throw new ScenarioFailed("no horse");
            await NextTo(rider, horse);
            long since = Environment.TickCount64;
            await rider.Actions.DoubleClick(horse, 1000, ct);
            await WaitAsync(() => IsRiding(rider) ? (object)true : null, 4000, $"a mount on layer 0x19; {Said(rider, since)}");
            await WaitAsync(() => !Sees(stranger, horse) ? (object)true : null, 4000, "the horse to leave the stranger's view (now ridden)");
            return "riding; the bystander sees the horse gone into the rider";
        });

        await RunScenario("mount", "double-clicking oneself dismounts", outcomes, async () =>
        {
            if (horse == 0) throw new ScenarioFailed("no horse");
            if (!IsRiding(rider)) throw new ScenarioFailed("not riding to begin with");
            long since = Environment.TickCount64;
            await rider.Actions.DoubleClick(rider.World.CharUid, 1000, ct);
            await WaitAsync(() => !IsRiding(rider) ? (object)true : null, 4000, $"the mount item to go; {Said(rider, since)}");
            await WaitAsync(() => Sees(rider, horse) && Sees(stranger, horse) ? (object)true : null, 4000,
                "the horse back on the ground for both");
            return "dismounted; the horse stands again";
        });

        rider.Disconnect();
        stranger.Disconnect();
    }

    // ---- locks and keys --------------------------------------------------------------

    private static async Task LockSuite(string root, ServerHost server, int port, List<Outcome> outcomes, CancellationToken ct)
    {
        string mul = ReadIniValue(Path.Combine(root, "sphere.ini"), "MulFiles") ?? "";
        if (!Path.IsPathRooted(mul)) mul = Path.Combine(root, mul);
        string spot;
        using (var map = new SphereNet.MapData.MapDataManager(mul))
        {
            map.Load();
            map.InitMap(0, 7168, 4096);
            var site = HouseSites(map, 1300, 1740, 80).FirstOrDefault();
            spot = $"{site.X},{site.Y},{site.Z}";
        }
        var owner = await LoginAsync(1071, port, ct);
        var stranger = await LoginAsync(1072, port, ct);
        uint chest = 0, key = 0, blank = 0;

        string Said(BotClient bot, long since) =>
            string.Join(" | ", bot.World.Journal.ToArray().Where(e => e.TimestampMs >= since).Select(e => e.Text));

        // Whether a double-click opens the chest for this bot.
        async Task<bool> Opens(BotClient bot)
        {
            lock (bot.World.SyncRoot) bot.World.OpenContainers.Remove(chest);
            await bot.Actions.DoubleClick(chest, 1000, ct);
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(50, ct);
                lock (bot.World.SyncRoot) if (bot.World.OpenContainers.ContainsKey(chest)) return true;
            }
            return false;
        }

        async Task UseOn(BotClient bot, uint used, uint target)
        {
            long since = Environment.TickCount64;
            await bot.Actions.DoubleClick(used, 1000, ct);
            if (await bot.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                throw new ScenarioFailed($"no cursor from 0x{used:X}; {Said(bot, since)}");
            await bot.Actions.TargetObject(target, ct);
            await Task.Delay(800, ct);
        }

        await RunScenario("lock", "setup: a chest and its key, a blank key", outcomes, async () =>
        {
            await AsStaff(server, owner.AccountName, async () =>
            {
                await SayAsync(owner, $".go {spot}", ct);
                var before = (await PackContentsAsync(owner, ct)).Select(i => i.Serial).ToHashSet();
                await AddIntoPackAsync(owner, "i_key_iron", ct);
                await AddIntoPackAsync(owner, "i_key_iron", ct);
                var keys = (await PackContentsAsync(owner, ct)).Where(i => !before.Contains(i.Serial)).ToList();
                if (keys.Count != 2) throw new ScenarioFailed($"{keys.Count} new keys in the pack");
                (key, blank) = (keys[0].Serial, keys[1].Serial);
                HashSet<uint> seen;
                lock (owner.World.SyncRoot) seen = owner.World.KnownItems.Keys.ToHashSet();
                await owner.Actions.Say(".add i_chest_metal", ct);
                if (await owner.Actions.WaitForTarget(3000, ct) != BotActionResult.Success)
                    throw new ScenarioFailed("no cursor for .add i_chest_metal");
                await owner.Actions.TargetLocation(owner.World.X + 1, owner.World.Y, owner.World.Z, ct);
                chest = (await WaitAsync(() => { lock (owner.World.SyncRoot) return owner.World.KnownItems.Values.FirstOrDefault(i =>
                    !seen.Contains(i.Serial) && i.ContainerSerial == 0 && owner.World.DistanceTo(i.X, i.Y) <= 2); }, 4000, "the chest")).Serial;
                // .LINK cuts the key to the chest (MORE1 = the lock code).
                await owner.Actions.Say(".link", ct);
                if (await owner.Actions.WaitForTarget(3000, ct) != BotActionResult.Success) throw new ScenarioFailed("no cursor for .link");
                await owner.Actions.TargetObject(key, ct);
                if (await owner.Actions.WaitForTarget(3000, ct) != BotActionResult.Success) throw new ScenarioFailed("no second cursor for .link");
                await owner.Actions.TargetObject(chest, ct);
                await Task.Delay(500, ct);
                await SayAsync(owner, ".tinkering 1000", ct);
            });
            await AsStaff(server, stranger.AccountName, async () =>
            {
                await SayAsync(stranger, $".go {spot}", ct);
                await AddIntoPackAsync(stranger, "i_lockpick", ct);
                await SayAsync(stranger, ".lockpicking 1000", ct);
            });
            return $"chest 0x{chest:X}, key 0x{key:X}, blank 0x{blank:X}";
        });

        await RunScenario("lock", "the key locks the chest; a stranger cannot open it", outcomes, async () =>
        {
            if (chest == 0) throw new ScenarioFailed("no chest");
            if (!await Opens(stranger)) throw new ScenarioFailed("the unlocked chest did not open for the stranger");
            long since = Environment.TickCount64;
            await UseOn(owner, key, chest);
            string said = Said(owner, since);
            long since2 = Environment.TickCount64;
            if (await Opens(stranger)) throw new ScenarioFailed($"the stranger opened the chest; key: {said}");
            return $"key: '{said}'; stranger: '{Said(stranger, since2)}'";
        });

        // Upstream does not open a locked container for its key's carrier: it says the
        // key is there to be used (LOCK_HAS_KEY, CClientUse.cpp:187).
        await RunScenario("lock", "the key's carrier is told to use it", outcomes, async () =>
        {
            if (chest == 0) throw new ScenarioFailed("no chest");
            long since = Environment.TickCount64;
            if (await Opens(owner)) throw new ScenarioFailed($"a locked chest opened; {Said(owner, since)}");
            await Task.Delay(300, ct);
            string said = Said(owner, since);
            if (said.Count(c => c == '|') < 1) throw new ScenarioFailed($"no 'locked' + 'you have the key' lines; {said}");
            return said;
        });

        await RunScenario("lock", "a blank key cut from the first unlocks it", outcomes, async () =>
        {
            if (chest == 0) throw new ScenarioFailed("no chest");
            long since = Environment.TickCount64;
            await UseOn(owner, blank, chest);
            string blankSaid = Said(owner, since);
            if (await Opens(stranger)) throw new ScenarioFailed("the blank key unlocked the chest");
            since = Environment.TickCount64;
            await UseOn(owner, blank, key);
            string copySaid = Said(owner, since);
            since = Environment.TickCount64;
            await UseOn(owner, blank, chest);
            string unlockSaid = Said(owner, since);
            if (!await Opens(stranger)) throw new ScenarioFailed($"the copy did not unlock it; copy: '{copySaid}', unlock: '{unlockSaid}'");
            await UseOn(owner, blank, chest); // locked again, for the lockpick
            return $"blank: '{blankSaid}'; copy: '{copySaid}'; unlock: '{unlockSaid}'";
        });

        await RunScenario("lock", "a lockpick opens it for the stranger", outcomes, async () =>
        {
            if (chest == 0) throw new ScenarioFailed("no chest");
            if (await Opens(stranger)) throw new ScenarioFailed("the chest was not locked again");
            var pick = (await PackContentsAsync(stranger, ct)).FirstOrDefault(i => i.ItemId is >= 0x14FB and <= 0x14FE)
                ?? throw new ScenarioFailed("no lockpick in the stranger's pack");
            long since = Environment.TickCount64;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await UseOn(stranger, pick.Serial, chest);
                await Task.Delay(4000, ct);
                if (await Opens(stranger)) return $"picked; {Said(stranger, since)}";
            }
            throw new ScenarioFailed($"still locked; {Said(stranger, since)}");
        });

        owner.Disconnect();
        stranger.Disconnect();
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

    private static async Task<BotClient> LoginAsync(int id, int port, CancellationToken ct, uint? clientVersion = null)
    {
        var bot = new BotClient(id, NullLogger.Instance)
        {
            ClientVersion = clientVersion ?? ReferenceClientProtocol.Pack(7, 0, 20, 0),
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
