using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using SphereNet.Server.Ipc;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Player bots in the staff views: the script list path (SERV.ALLBOTS,
/// ISPLAYERBOT) next to an unchanged SERV.ALLCLIENTS, and the web panel's online
/// list, locally and across the host bridge.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotStaffViewTests
{
    private static GameWorld World()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        return world;
    }

    private static Character Online(GameWorld world, string name, bool bot, short x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        ch.IsOnline = true;
        ch.IsPlayerBot = bot;
        world.AddOnlinePlayer(ch);
        return ch;
    }

    private static GameClient Client(GameWorld world, Character ch)
    {
        var c = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), world,
            new AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
        c.AttachServerDriven(ch);
        return c;
    }

    private static List<Character> Snapshot(string method, params object?[] args)
    {
        var m = typeof(SphereNet.Server.Program)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length);
        return ((System.Collections.IEnumerable)m.Invoke(null, args)!).Cast<object>()
            .Select(e => (Character)e.GetType().GetField("Item1")!.GetValue(e)!)
            .ToList();
    }

    [Fact]
    public void AllBotsListsOnlyTheBotsAndAllClientsStillLeavesThemOut()
    {
        var world = World();
        var player = Online(world, "Player", bot: false, 100);
        var bot = Online(world, "Bot", bot: true, 101);
        var botClient = Client(world, bot);
        var byChar = new Dictionary<Serial, GameClient> { [bot.Uid] = botClient };

        Assert.Equal([player], Snapshot("BuildAllClientsSnapshot", world, Array.Empty<GameClient>(), byChar));
        Assert.Equal([bot], Snapshot("BuildAllBotsSnapshot", world, byChar));
    }

    [Fact]
    public void ServAllBotsRoutesThroughTheServerIteratorBridge()
    {
        var interpreter = new ScriptInterpreter(new ExpressionParser(),
            LoggerFactory.Create(_ => { }).CreateLogger<ScriptInterpreter>());
        var requests = new List<string>();
        interpreter.ServerPropertyResolver = r => { requests.Add(r); return "1"; };
        var gm = new Character { Name = "GM" };
        gm.SetUid(new Serial(0x100));
        var key = new ScriptKey();
        key.Parse("Serv.AllBots f_Admin_GetPlayers");

        interpreter.Execute([key], gm, null, new TriggerArgs { Source = gm }, new ScriptScope());

        Assert.True(gm.TryGetProperty("UID", out var uid));
        Assert.Contains($"_ALLBOTS={uid}|f_Admin_GetPlayers", requests);
        Assert.DoesNotContain(requests, r => r.StartsWith("_ALLCLIENTS=", StringComparison.Ordinal));
    }

    [Fact]
    public void IsPlayerBotReadsTheRuntimeFlag()
    {
        var ch = new Character { IsPlayer = true };
        Assert.True(ch.TryGetProperty("ISPLAYERBOT", out var v));
        Assert.Equal("0", v);
        ch.IsPlayerBot = true;
        Assert.True(ch.TryGetProperty("ISPLAYERBOT", out v));
        Assert.Equal("1", v);
    }

    [Fact]
    public void PanelListCarriesBotsFlaggedWithoutAConnection()
    {
        var world = World();
        var player = Online(world, "Player", bot: false, 100);
        var bot = Online(world, "Bot", bot: true, 101);
        // The bot's own socketless client is never one of the panel's connections,
        // but even handed in it must not turn the bot into a player row.
        var list = SphereNet.Server.Program.BuildPanelPlayerList(
            [Client(world, player), Client(world, bot)], world);

        Assert.Equal(2, list.Count);
        var p = Assert.Single(list, i => !i.IsBot);
        Assert.Equal((player.Uid.Value, "Player"), (p.Serial, p.CharName));
        var b = Assert.Single(list, i => i.IsBot);
        Assert.Equal((bot.Uid.Value, "Bot", "", "bot", 101), (b.Serial, b.CharName, b.Ip, b.ClientVersion, b.X));
    }

    [Fact]
    public async Task BotFlagCrossesTheHostBridge()
    {
        string pipeName = "sn-players-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new IpcServer(pipeName);
        server.SetContext(new PanelContext
        {
            GetOnlinePlayers = () =>
            [
                new PlayerInfo("Player", "acct", 0, 1, 2, "127.0.0.1", 0x10, 1, "7.0.20", 5),
                new PlayerInfo("Bot", "", 0, 3, 4, "", 0x20, 1, "bot", 0, IsBot: true),
            ],
        });
        Task serverTask = server.RunAsync(cts.Token);

        using var bridge = new IpcBridge();
        await bridge.ConnectAsync(pipeName);
        var ctx = HostPanelContext.Build(
            new ServerProcess("x.exe", bridge, new SphereNet.Panel.Logging.PanelLogSink()), bridge, null, null);

        var players = await Task.Run(() => ctx.GetOnlinePlayers!());
        Assert.Equal(2, players.Count);
        Assert.False(players.Single(p => p.Serial == 0x10).IsBot);
        var bot = players.Single(p => p.Serial == 0x20);
        Assert.True(bot.IsBot);
        Assert.Equal("bot", bot.ClientVersion);

        cts.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
    }
}
