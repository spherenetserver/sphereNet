using Microsoft.Extensions.Logging;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.Clients;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;

namespace SphereNet.Server;

public static partial class Program
{
    private static PlayerBotEngine? _playerBots;

    private static string PlayerBotDataDir(string basePath) => ResolvePath(basePath, _config.PlayerBotDataDir);

    /// <summary>The bot archetype CHARDEFs ship with the bot data (scripts/*.scp) and
    /// load with the pack, so ON=@Create can use the pack's own item defnames.</summary>
    private static int LoadPlayerBotScripts(string basePath)
    {
        if (!_config.PlayerBots) return 0;
        string dir = Path.Combine(PlayerBotDataDir(basePath), "scripts");
        if (!Directory.Exists(dir)) return 0;
        _log.LogInformation("Loading player bot scripts from: {Dir}", dir);
        return LoadAllScripts(dir);
    }

    /// <summary>After the world is loaded: build the engine, take back the bots the
    /// save holds, and let the tick fill the population up to PlayerBotCount.</summary>
    private static void InitializePlayerBots(string basePath)
    {
        if (!_config.PlayerBots) return;
        string dataDir = PlayerBotDataDir(basePath);
        var nav = BotNavGraph.Load(dataDir, _world.MapData);
        var chat = BotChatBook.Load(dataDir, _config.PlayerBotLanguage);
        var engine = new PlayerBotEngine(_world, _movement, _resources, nav, chat,
            _loggerFactory.CreateLogger("PlayerBots"))
        {
            TargetCount = _config.PlayerBotCount,
            RunCreateScript = ch => _triggerDispatcher?.FireNpcLoadScript(ch, restock: true),
            Say = PlayerBotSay,
            OnAppeared = ch => MarkNearbyClientsRefresh(ch.Position),
            CreateClient = CreatePlayerBotClient,
            Parties = _partyManager,
            RedRespawnRegion = _config.PlayerBotRedRespawn,
            ReclaimRange = _config.PlayerBotReclaimRange,
            DropClient = ch => _clientsByCharUid.Remove(ch.Uid),
            Intents = BotIntents.Load(dataDir),
            Scenes = BotSceneBook.Load(dataDir),
            Gravity = _config.PlayerBotGravity,
            CrowdCap = _config.PlayerBotCrowd,
            ChatPaceSeconds = _config.PlayerBotChatPace,
            NamePrefix = _config.PlayerBotNamePrefix,
            PkPercent = _config.PlayerBotPkPercent,
            ThiefPercent = _config.PlayerBotThiefPercent,
            SearchesPerTick = _config.PlayerBotPathSearchesPerTick,
            ResRobeName = SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.SpellResRobename),
        };
        engine.LoadArchetypes(dataDir);
        engine.LoadDistribution(dataDir);
        engine.LoadMounts(dataDir);
        engine.LoadShopping(dataDir);
        // Deaths become the news bots tell.
        _deathEngine.OnDeath += engine.OnDeath;
        engine.BadSpotFile = Path.Combine(ResolvePath(basePath, _config.WorldSaveDir), "playerbots_badspots.txt");
        int badSpots = engine.LoadBadSpots();
        engine.CorridorFile = Path.Combine(ResolvePath(basePath, _config.WorldSaveDir), "playerbots_corridors.txt");
        int corridors = engine.LoadCorridors();
        int adopted = engine.AdoptSaved();
        _playerBots = engine;
        _log.LogInformation(
            "Player bots: {Nodes} waypoints ({Reseated} re-seated, {Dropped} dropped), {Dests} destinations, " +
            "{Cats} chat categories ({Lang}; languages {Langs}), {Scenes} scenes, {BadSpots} known bad spots, {Corridors} kept ways, adopted {Adopted} from the save, target {Target}",
            nav.Waypoints.Count, nav.ReseatedNodes, nav.DroppedNodes, nav.Destinations.Count,
            chat.CategoryCount, chat.Language, string.Join(",", chat.Languages), engine.Scenes.Scenes.Count, badSpots, corridors, adopted,
            engine.TargetCount);
        if (nav.Waypoints.Count == 0)
            _log.LogWarning("Player bots: no road network under {Dir}/nav - bots will stand where they spawn", dataDir);
    }

    private static long _nextPlayerBotReportMs;

    private static void TickPlayerBots()
    {
        var bots = _playerBots;
        if (bots == null) return;
        long now = Environment.TickCount64;
        bots.Tick(now);
        if (now < _nextPlayerBotReportMs) return;
        _nextPlayerBotReportMs = now + 30_000;
        if (bots.WindowTicks > 0)
            _log.LogInformation(
                "[playerbots] online={Count} tick avg={Avg}us max={Max}us deferred={Deferred} steps={Steps} " +
                "searches={Searches} (failed {Failed}, slowest {SlowSearch}us) doors={Doors} fights={Fights} deaths={Deaths} reclaims={Reclaims} handbacks={HandBacks} rescues={Rescues} badspots={BadSpots} slowest act {SlowAct}us ({SlowWhat}) route {Route}us step {Step}us spawn {Spawn}us moves {Moves} avg {MoveAvg}us max {MoveMax}us at {MoveWhere} " +
                "watchers={Watchers} seen avg={SeenAvg} max={SeenMax} lines={Lines} replies={Replies} scenes={Scenes} moved={Moved} mounts={Mounts} regears={Regears} robes={Robes} stalldoors={StallDoors} spotswitches={SpotSwitches} stallsearches={StallSearches} " +
                "purchases={Purchases} meditations={Meditations} pkattacks={PkAttacks} thefts={Thefts} badlinks={BadLinks} searchtimeouts={Timeouts} " +
                "searchq={Queued} denied={Denied} dropped={Dropped} notrun={NotRun} backlog={Backlog} corridors={Corridors} corridoruses={CorridorUses} cornersplits={CornerSplits} " +
                "waits={Waits} sidesteps={SideSteps} doorway={Doorway} portals={Portals} portalmisses={PortalMisses} noroute={NoRoute} stranded={Stranded} maprescues={MapRescues} " +
                "unreachable={Unreachable} giveups={GiveUps} faults={Faults} staticdoors={StaticDoors} deposits={Deposits} withdrawals={Withdrawals} marks={Marks} (taken {MarksTaken}, failed {MarksFailed}) recalls={Recalls} companions={Companions} pauses={Pauses} stalled [{Stalled}] refusals [{Refusals}]",
                bots.Count, bots.WindowTotalUs / bots.WindowTicks, bots.WindowMaxUs, bots.WindowDeferred,
                bots.WindowSteps, bots.WindowSearches, bots.WindowFailedSearches, bots.WindowMaxSearchUs, bots.WindowDoors,
                bots.WindowFights, bots.WindowDeaths, bots.WindowReclaims, bots.WindowHandBacks, bots.WindowRescues, bots.BadSpots, bots.WindowSlowestActUs, bots.WindowSlowestAct,
                bots.WindowMaxRouteUs, bots.WindowMaxStepUs, bots.WindowSpawnUs, bots.WindowMoves,
                bots.WindowMoves > 0 ? bots.WindowMoveUs / bots.WindowMoves : 0, bots.WindowMaxMoveUs, bots.WindowMaxMoveWhere,
                bots.Watching.Count, bots.WindowSeenSamples > 0 ? (bots.WindowSeenSum * 10 / bots.WindowSeenSamples) / 10.0 : 0,
                bots.WindowSeenMax, bots.WindowLines, bots.WindowReplies, bots.WindowScenes, bots.WindowMovedHouse, bots.WindowMounts, bots.WindowRegears, bots.WindowRobesShed, bots.WindowStallDoors, bots.WindowSpotSwitches, bots.WindowStallSearches,
                bots.WindowPurchases, bots.WindowMeditations, bots.WindowPkAttacks, bots.WindowThefts, bots.BadLinks,
                bots.SearchTimeouts,
                bots.WindowSearchQueued, bots.WindowSearchDenied, bots.WindowSearchDropped, bots.WindowSearchesNotRun, bots.WindowSearchBacklog,
                bots.CorridorCount, bots.WindowCorridorUses, bots.WindowCornerSplits,
                bots.WindowMobileWaits, bots.WindowSideSteps, bots.WindowDoorwaySteps, bots.WindowPortalCrossings,
                bots.WindowPortalMisses, bots.WindowNoRoute, bots.WindowStrandedRescues, bots.WindowMapRescues,
                bots.WindowUnreachableFoes, bots.WindowGiveUps, bots.WindowActFaults, bots.WindowStaticDoors,
                bots.WindowDeposits, bots.WindowWithdrawals, bots.WindowMarks, bots.WindowMarksTaken, bots.WindowMarksFailed, bots.WindowRecalls, bots.WindowCompanions,
                bots.WindowPauses, bots.StallReport(), bots.RefusalReport());
        bots.ResetWindow();
    }

    /// <summary>The client a bot is played through: wired like a network client, its
    /// NetState without a socket, kept out of _clients (it is no connection) but
    /// filed under the character so the world finds it as that character's client
    /// (death and resurrection sequences, trade partner, messages).</summary>
    private static GameClient CreatePlayerBotClient(Character bot)
    {
        var client = CreateWiredClient(new NetState(_loggerFactory.CreateLogger<NetState>()));
        client.AttachServerDriven(bot);
        _clientsByCharUid[bot.Uid] = client;
        return client;
    }

    /// <summary>Console PLAYERBOTS: STALLS [n] lists the bots stalled longest, TRACE
    /// &lt;uid|name&gt; one bot's last decisions, NAVCHECK [LINKS [LEARN]] the road data
    /// against the running world. Runs on the main loop (it reads bot state).</summary>
    private static void HandlePlayerBotsCommand(string args, Action<string> output)
    {
        var bots = _playerBots;
        if (bots == null)
        {
            output("Player bots are off (PlayerBots=0).");
            return;
        }
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string sub = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        IReadOnlyList<string> lines;
        switch (sub)
        {
            case "STALLS":
                lines = bots.ListStalls(parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 20);
                break;
            case "TRACE" when parts.Length > 1:
                lines = bots.TraceOf(string.Join(' ', parts.Skip(1)));
                break;
            case "NAVCHECK":
            {
                bool links = parts.Any(p => p.Equals("LINKS", StringComparison.OrdinalIgnoreCase));
                bool learn = parts.Any(p => p.Equals("LEARN", StringComparison.OrdinalIgnoreCase));
                lines = bots.NavCheck(links, learn);
                // The full list goes to a file beside the bad-spot file; the console
                // gets the head of it.
                if (bots.BadSpotFile is { } badSpots)
                {
                    string report = Path.Combine(Path.GetDirectoryName(badSpots) ?? ".", "playerbots_navcheck.txt");
                    try
                    {
                        File.WriteAllLines(report, lines);
                        output($"Full report: {report}");
                    }
                    catch (IOException ex) { output($"Could not write {report}: {ex.Message}"); }
                }
                lines = lines.Take(40).ToList();
                break;
            }
            default:
                output("PLAYERBOTS STALLS [n] | PLAYERBOTS TRACE <uid|name> | PLAYERBOTS NAVCHECK [LINKS [LEARN]]");
                output($"online {bots.Count}, portals {bots.PortalPlaces} (+{bots.PortallessPlaces} without), corridors {bots.CorridorCount}, rescues {bots.StuckRescues}, stranded {bots.StrandedRescues}");
                return;
        }
        foreach (string line in lines) output(line);
    }

    private static void PlayerBotSay(Character bot, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var packet = new PacketSpeechUnicodeOut(
            bot.Uid.Value,
            bot.BodyId,
            0x00, // regular speech, as a player types it
            bot.SpeechColor != 0 ? bot.SpeechColor : (ushort)0x03B2,
            3,
            PacketSpeechUnicodeOut.SystemLanguage,
            bot.GetName(),
            line);
        BroadcastNearby(bot.Position, 18, packet, 0);
    }
}
