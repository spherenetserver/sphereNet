using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.Party;
using SphereNet.Game.Guild;
using SphereNet.Game.Trade;
using SphereNet.Game.Gumps;
using SphereNet.Game.Death;
using SphereNet.Game.Crafting;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Interfaces;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using SphereNet.Game.Scripting;
using SphereNet.Game.Movement;
using SphereNet.Game.Accounts;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

public class GameSystemTests
{
    private static GameWorld CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    private static Queue<PacketBuffer> GetQueuedPackets(NetState state)
    {
        return (Queue<PacketBuffer>)(typeof(NetState)
            .GetField("_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!);
    }

    private static void SetNetStateInUse(NetState state, bool value)
    {
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, value);
    }

    private static void AttachCharacter(SphereNet.Game.Clients.GameClient client, Character ch, Account? account = null)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);
        if (account != null)
        {
            typeof(SphereNet.Game.Clients.GameClient)
                .GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, account);
        }
    }

    private static bool InvokePetCommand(SphereNet.Game.Clients.GameClient client, string text)
    {
        return (bool)typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod("TryHandlePetCommand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [text])!;
    }

    private static void InvokeEnterWorld(SphereNet.Game.Clients.GameClient client)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod("EnterWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, []);
    }

    private static void InvokePrivate(SphereNet.Game.Clients.GameClient client, string methodName, params object[] args)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, args);
    }

    private static void SetPrivateField<T>(SphereNet.Game.Clients.GameClient client, string fieldName, T value)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, value);
    }

    // --- Party ---

    [Fact]
    public void Party_CreateAndAddMembers()
    {
        var pm = new PartyManager();
        var party = pm.CreateParty(new Serial(1));
        Assert.Equal(1, party.MemberCount);

        bool added = party.AddMember(new Serial(2));
        Assert.True(added);
        Assert.Equal(2, party.MemberCount);
    }

    [Fact]
    public void Party_MaxSize10()
    {
        var pm = new PartyManager();
        var party = pm.CreateParty(new Serial(1));
        for (uint i = 2; i <= 10; i++)
            party.AddMember(new Serial(i));
        Assert.True(party.IsFull);
        Assert.False(party.AddMember(new Serial(11)));
    }

    [Fact]
    public void Party_LeavePromotesNewMaster()
    {
        var pm = new PartyManager();
        pm.CreateParty(new Serial(1));
        pm.AcceptInvite(new Serial(1), new Serial(2));
        pm.AcceptInvite(new Serial(1), new Serial(3)); // need 3+ so party survives
        pm.Leave(new Serial(1));
        var party = pm.FindParty(new Serial(2));
        Assert.NotNull(party);
        Assert.Equal(new Serial(2), party.Master);
    }

    // --- Guild ---

    [Fact]
    public void Guild_CreateWithMaster()
    {
        var gm = new GuildManager();
        var guild = gm.CreateGuild(new Serial(100), "Test Guild", new Serial(1));
        Assert.Equal("Test Guild", guild.Name);
        var master = guild.GetMaster();
        Assert.NotNull(master);
        Assert.Equal(new Serial(1), master.CharUid);
        Assert.Equal(GuildPriv.Master, master.Priv);
    }

    [Fact]
    public void Guild_RecruitAndAccept()
    {
        var gm = new GuildManager();
        var guild = gm.CreateGuild(new Serial(100), "TG", new Serial(1));
        guild.AddRecruit(new Serial(2));
        Assert.False(guild.IsMember(new Serial(2))); // still candidate
        guild.AcceptMember(new Serial(2));
        Assert.True(guild.IsMember(new Serial(2)));
    }

    [Fact]
    public void Guild_War()
    {
        var gm = new GuildManager();
        var g1 = gm.CreateGuild(new Serial(100), "G1", new Serial(1));
        var g2 = gm.CreateGuild(new Serial(200), "G2", new Serial(2));

        // One-sided war: not yet mutual enemy
        g1.DeclareWar(g2.StoneUid);
        Assert.False(g1.IsAtWarWith(g2.StoneUid)); // needs both sides

        // Other side declares too: now mutual enemy
        var rel = g1.GetOrCreateRelation(g2.StoneUid);
        rel.TheyDeclaredWar = true;
        Assert.True(g1.IsAtWarWith(g2.StoneUid));

        // Peace
        g1.DeclarePeace(g2.StoneUid);
        Assert.False(g1.IsAtWarWith(g2.StoneUid));
    }

    // --- Trade ---

    [Fact]
    public void Trade_StartAndComplete()
    {
        var tm = new TradeManager();
        var world = CreateWorld();
        var ch1 = world.CreateCharacter(); ch1.Name = "A";
        var ch2 = world.CreateCharacter(); ch2.Name = "B";
        var cont1 = world.CreateItem(); cont1.Name = "C1";
        var cont2 = world.CreateItem(); cont2.Name = "C2";
        var trade = tm.StartTrade(ch1, ch2, cont1, cont2);
        Assert.False(trade.IsCompleted);

        trade.ToggleAccept(ch1);
        Assert.False(trade.InitiatorAccepted && trade.PartnerAccepted);
        bool both = trade.ToggleAccept(ch2);
        Assert.True(both);
    }

    // --- Gump ---

    [Fact]
    public void GumpBuilder_BuildsLayoutString()
    {
        var gump = new GumpBuilder(0x1234, 0x5678, 400, 300);
        gump.SetPage(0)
            .AddResizePic(0, 0, 9200, 400, 300)
            .AddText(20, 20, 0, "Hello World")
            .AddButton(20, 260, 4005, 4007, 1);

        string layout = gump.BuildLayoutString();
        Assert.Contains("page", layout);
        Assert.Contains("resizepic", layout);
        Assert.Contains("text", layout);
        Assert.Contains("button", layout);
        Assert.Single(gump.Texts); // "Hello World"
    }

    // --- Death ---

    [Fact]
    public void Death_CreatesCorpse()
    {
        var world = CreateWorld();
        var engine = new DeathEngine(world);
        var victim = world.CreateCharacter();
        victim.Name = "Victim";
        victim.Str = 50; victim.MaxHits = 50; victim.Hits = 50;
        world.PlaceCharacter(victim, new Point3D(1000, 1000, 0, 0));

        var corpse = engine.ProcessDeath(victim);
        Assert.NotNull(corpse);
        Assert.Contains("corpse", corpse.Name.ToLower());
        Assert.True(victim.IsDead);
    }

    [Fact]
    public void Death_LootingOwnCorpseNotCriminal()
    {
        var world = CreateWorld();
        var engine = new DeathEngine(world);
        var ch = world.CreateCharacter();
        ch.Name = "Player"; ch.IsPlayer = true;
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.SetUid(new Serial(42));
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        var corpse = engine.ProcessDeath(ch);
        Assert.NotNull(corpse);
        Assert.False(engine.IsLootingCriminal(ch, corpse));
    }

    [Fact]
    public void Death_CarveCorpse_FiresItemTriggerAndCanBeBlocked()
    {
        var world = CreateWorld();
        var engine = new DeathEngine(world);
        var carver = world.CreateCharacter();
        carver.IsPlayer = true;
        world.PlaceCharacter(carver, new Point3D(100, 100, 0, 0));

        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.Name = "corpse";
        world.PlaceItem(corpse, carver.Position);

        var dispatcher = new TriggerDispatcher();
        int carveCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "CarveCorpse", (_, args) =>
        {
            carveCount++;
            Assert.Same(carver, args.CharSrc);
            Assert.Same(corpse, args.ItemSrc);
            return TriggerResult.True;
        });
        engine.TriggerDispatcher = dispatcher;

        var results = engine.CarveCorpse(carver, corpse);

        Assert.Equal(1, carveCount);
        Assert.Empty(results);
        Assert.False(corpse.TryGetTag("CARVED", out _));
    }

    // --- Crafting ---

    [Fact]
    public void Crafting_CanCraft_NoResources_ReturnsFalse()
    {
        var world = CreateWorld();
        var engine = new CraftingEngine(world);
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Blacksmithing, 1000);

        var recipe = new CraftRecipe
        {
            ResultItemId = 0x1000,
            ResultName = "Iron Ingot",
            PrimarySkill = SkillType.Blacksmithing,
            Difficulty = 50,
        };
        recipe.Resources.Add(new CraftResource { ItemId = 0x1BF2, Amount = 10 });
        engine.RegisterRecipe(recipe);

        Assert.False(engine.CanCraft(ch, recipe));
    }

    [Fact]
    public void Crafting_TryCraft_ConsumesNestedResourcesAndCreatesItem()
    {
        var world = CreateWorld();
        var engine = new CraftingEngine(world);
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Blacksmithing, 1000);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        var pouch = world.CreateItem();
        pouch.ItemType = ItemType.Container;
        pack.AddItem(pouch);
        var ingots = world.CreateItem();
        ingots.BaseId = 0x1BF2;
        ingots.Amount = 12;
        pouch.AddItem(ingots);

        var recipe = new CraftRecipe
        {
            ResultItemId = 0x13B9,
            ResultName = "Viking Sword",
            PrimarySkill = SkillType.Blacksmithing,
            Difficulty = 0,
        };
        recipe.Resources.Add(new CraftResource { ItemId = 0x1BF2, Amount = 10 });

        Assert.True(engine.CanCraft(ch, recipe));
        var crafted = engine.TryCraft(ch, recipe);

        Assert.NotNull(crafted);
        Assert.Equal(0x13B9, crafted!.BaseId);
        Assert.Equal(2, ingots.Amount);
        Assert.False(ingots.IsDeleted);
    }

    // --- Point3D ---

    [Fact]
    public void Point3D_DistanceTo_Works()
    {
        var a = new Point3D(0, 0, 0, 0);
        var b = new Point3D(3, 4, 0, 0);
        Assert.Equal(4, a.GetDistanceTo(b)); // Chebyshev would be 4
    }

    [Fact]
    public void Point3D_GetDirectionTo_Works()
    {
        var a = new Point3D(0, 0, 0, 0);
        var east = new Point3D(5, 0, 0, 0);
        Assert.Equal(Direction.East, a.GetDirectionTo(east));
    }

    [Fact]
    public void Character_WarModeFlag_Toggles()
    {
        var ch = new Character();
        Assert.False(ch.IsInWarMode);
        ch.SetStatFlag(StatFlag.War);
        Assert.True(ch.IsInWarMode);
        ch.ClearStatFlag(StatFlag.War);
        Assert.False(ch.IsInWarMode);
    }

    [Fact]
    public void CommandHandler_PrivilegeRejects_CounselCommandForPlayer()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(10, 10, 0, 0));

        var result = commands.TryExecute(player, "GO 100 100 0");
        Assert.Equal(CommandResult.InsufficientPriv, result);
    }

    [Fact]
    public void CommandHandler_Executes_GoForCounsel()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.Counsel;
        world.PlaceCharacter(gm, new Point3D(10, 10, 0, 0));

        var result = commands.TryExecute(gm, "GO 100 120 5");
        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal((short)100, gm.X);
        Assert.Equal((short)120, gm.Y);
        Assert.Equal((sbyte)5, gm.Z);
    }

    [Fact]
    public void CommandHandler_ScriptFallback_ExecutesWhenBuiltinMissing()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.Owner;
        world.PlaceCharacter(gm, new Point3D(10, 10, 0, 0));

        string? forwardedLine = null;
        commands.ScriptFallbackExecutor = (ch, line) =>
        {
            Assert.Same(gm, ch);
            forwardedLine = line;
            ch.Name = "ScriptRenamed";
            return true;
        };

        var result = commands.TryExecute(gm, "MTELE 1500,1600,10");
        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal("MTELE 1500,1600,10", forwardedLine);
        Assert.Equal("ScriptRenamed", gm.Name);
    }

    [Fact]
    public void CommandHandler_ScriptPrivilegeMatrix_RejectsLowPrivAndAllowsHighPriv()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        // Simulate script-loaded privilege matrix (as loaded from [PLEVEL] sections).
        commands.LoadScriptCommandPrivileges(BuildResourcesWithPlevel("GETIR", PrivLevel.GM));

        bool executed = false;
        commands.ScriptFallbackExecutor = (_, line) =>
        {
            if (line.StartsWith("GETIR", StringComparison.OrdinalIgnoreCase))
            {
                executed = true;
                return true;
            }
            return false;
        };

        var low = world.CreateCharacter();
        low.IsPlayer = true;
        low.PrivLevel = PrivLevel.Counsel;

        var high = world.CreateCharacter();
        high.IsPlayer = true;
        high.PrivLevel = PrivLevel.GM;

        var lowResult = commands.TryExecute(low, "GETIR mortal");
        var highResult = commands.TryExecute(high, "GETIR mortal");

        Assert.Equal(CommandResult.InsufficientPriv, lowResult);
        Assert.Equal(CommandResult.Executed, highResult);
        Assert.True(executed);
        Assert.Equal(PrivLevel.GM, commands.GetRequiredPrivLevel("GETIR"));
    }

    private static SphereNet.Scripting.Resources.ResourceHolder BuildResourcesWithPlevel(string commandName, PrivLevel level)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_plevel_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, $"[PLEVEL {(int)level}]{Environment.NewLine}{commandName}{Environment.NewLine}");

        var loggerFactory = LoggerFactory.Create(b => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    [Fact]
    public void ScriptInterpreter_Resolves_ArgvBracketNotation()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var args = new ExecTriggerArgs(null, 0, 0, "alpha beta");
        var scope = new ScriptScope();

        var lines = ParseKeys(
            "LOCAL.v=<ARGV[0]>",
            "TAG.RESULT=<LOCAL.v>");

        var result = interpreter.Execute(lines, target, new TestConsole(), args, scope);
        Assert.Equal(TriggerResult.Default, result);
        Assert.True(target.TryGetProperty("TAG.RESULT", out var value));
        Assert.Equal("alpha", value);
    }

    [Fact]
    public void ScriptInterpreter_Resolves_AngleBracketFunctionCalls()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_func_expr_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION SetProcessDelay]\n" +
            "TAG.FUNC_ARG0=<ARGV[0]>\n" +
            "TAG.FUNC_ARG1=<ARGV[1]>\n" +
            "RETURN 1\n\n" +
            "[FUNCTION f_test_angle]\n" +
            "LOCAL.RESULT <SetProcessDelay HelpPage,50>\n" +
            "TAG.RESULT=<LOCAL.RESULT>\n" +
            "RETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        interpreter.CallFunction = (name, target, source, args) =>
            runner.TryRunFunction(name, target, source, args, out var callResult) ? callResult : TriggerResult.Default;
        interpreter.ResolveFunctionExpression = (name, argString, target, source, args) =>
            runner.TryEvaluateFunction(name, argString, target, source, args, out var value) ? value : null;

        var target = new Character();
        bool handled = runner.TryRunFunction("f_test_angle", target, new TestConsole(), new ExecTriggerArgs(), out var result);

        Assert.True(handled);
        Assert.Equal(TriggerResult.True, result);
        Assert.True(target.TryGetProperty("TAG.RESULT", out var localResult));
        Assert.Equal("1", localResult);
        Assert.True(target.TryGetProperty("TAG.FUNC_ARG0", out var arg0));
        Assert.True(target.TryGetProperty("TAG.FUNC_ARG1", out var arg1));
        Assert.Equal("HelpPage", arg0);
        Assert.Equal("50", arg1);
    }

    [Fact]
    public void WeatherEngine_Configure_KeepsWorldSeasonInSync()
    {
        var world = CreateWorld();
        var engine = new WeatherEngine(world);

        engine.Configure(SphereNet.Core.Configuration.SeasonMode.Manual, SeasonType.Winter, intervalMs: 0);

        Assert.Equal(SeasonType.Winter, engine.CurrentSeason);
        Assert.Equal((byte)SeasonType.Winter, world.CurrentSeason);
        Assert.Equal(SphereNet.Core.Configuration.SeasonMode.Manual, engine.CurrentSeasonMode);
    }

    [Fact]
    public void WeatherEngine_ManualMode_DoesNotAdvanceOnTick()
    {
        var world = CreateWorld();
        var engine = new WeatherEngine(world);
        engine.Configure(SphereNet.Core.Configuration.SeasonMode.Manual, SeasonType.Fall, intervalMs: 0);

        bool changed = engine.OnTick();

        Assert.False(changed);
        Assert.Equal(SeasonType.Fall, engine.CurrentSeason);
        Assert.Equal((byte)SeasonType.Fall, world.CurrentSeason);
    }

    [Fact]
    public void WeatherEngine_AutoMode_AdvancesWhenIntervalElapsed()
    {
        var world = CreateWorld();
        var engine = new WeatherEngine(world);
        engine.Configure(SphereNet.Core.Configuration.SeasonMode.Auto, SeasonType.Spring, intervalMs: 1);
        typeof(WeatherEngine)
            .GetField("_lastSeasonChangeTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(engine, Environment.TickCount64 - 5);

        bool changed = engine.OnTick();

        Assert.True(changed);
        Assert.Equal(SeasonType.Summer, engine.CurrentSeason);
        Assert.Equal((byte)SeasonType.Summer, world.CurrentSeason);
    }

    [Fact]
    public void ScriptInterpreter_ServSeason_Command_UsesServerSetterBridge()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var args = new ExecTriggerArgs();
        var scope = new ScriptScope();
        string? captured = null;

        interpreter.ServerPropertyResolver = property =>
        {
            captured = property;
            return "2";
        };

        var lines = ParseKeys("SERV.SEASON 3");
        interpreter.Execute(lines, target, new TestConsole(), args, scope);

        Assert.Equal("_SET_SEASON=3", captured);
    }

    [Fact]
    public void GameClient_Resync_ReSendsSeasonPacket()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        world.CurrentSeason = (byte)SeasonType.Winter;
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 42 };
        SetNetStateInUse(netState, true);

        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);

        var queue = GetQueuedPackets(netState);
        queue.Clear();

        client.Resync();

        Assert.Contains(queue, pkt => pkt.Span.Length > 0 && pkt.Span[0] == 0xBC && pkt.Span[1] == (byte)SeasonType.Winter);
    }

    [Fact]
    public void Character_PrivLevel_SyncsBoundAccount()
    {
        var world = CreateWorld();
        var account = new Account { Name = "tester", PrivLevel = PrivLevel.Player };
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var oldResolver = Character.ResolveAccountForChar;
        Character.ResolveAccountForChar = uid => uid == ch.Uid ? account : null;
        try
        {
            ch.PrivLevel = PrivLevel.GM;
            Assert.Equal(PrivLevel.GM, account.PrivLevel);
            Assert.True(ch.TrySetProperty("PRIVLEVEL", ((int)PrivLevel.Owner).ToString()));
            Assert.Equal(PrivLevel.Owner, account.PrivLevel);
        }
        finally
        {
            Character.ResolveAccountForChar = oldResolver;
        }
    }

    [Fact]
    public void EnterWorld_NormalizesMissingPlayerSkillClassToZero()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var account = accountManager.CreateAccount("tester", "pw")!;
        account.PrivLevel = PrivLevel.Counsel;
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 77 };
        SetNetStateInUse(netState, true);

        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.SkillClass = 99;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        AttachCharacter(client, ch, account);

        InvokeEnterWorld(client);

        Assert.Equal(0, ch.SkillClass);
        Assert.Equal(PrivLevel.Counsel, ch.PrivLevel);
    }

    [Fact]
    public void Character_OwnershipMemorySurface_ResolvesOwnerControllerAndFriends()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.Name = "Owner";
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var friend = world.CreateCharacter();
        friend.Name = "Friend";
        friend.IsPlayer = true;
        world.PlaceCharacter(friend, new Point3D(101, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Wolf";
        world.PlaceCharacter(pet, new Point3D(102, 100, 0, 0));

        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));
        Assert.True(pet.AddFriend(friend));

        Assert.True(pet.TryGetProperty("OWNER", out var ownerVal));
        Assert.Equal($"0{owner.Uid.Value:X}", ownerVal);
        Assert.True(pet.TryGetProperty("CONTROLLER", out var controllerVal));
        Assert.Equal($"0{owner.Uid.Value:X}", controllerVal);
        Assert.True(pet.TryGetProperty("MemoryFindType.memory_ipet.isValid", out var ownerMemValid));
        Assert.Equal("1", ownerMemValid);
        Assert.True(pet.TryGetProperty("MemoryFindType.memory_friend.link.name", out var friendName));
        Assert.Equal("Friend", friendName);
        Assert.Single(pet.GetMemoryEntriesByType("memory_friend"));
    }

    [Fact]
    public void GameClient_ForCharMemoryType_ReturnsOwnershipEntries()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 100 };
        SetNetStateInUse(netState, true);

        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var owner = world.CreateCharacter();
        owner.Name = "Owner";
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, owner);

        var pet = world.CreateCharacter();
        pet.Name = "Drake";
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));

        var entries = client.QueryScriptObjects("FORCHARMEMORYTYPE", pet, "memory_ipet", null);
        Assert.Single(entries);
        Assert.True(entries[0].TryGetProperty("LINK.NAME", out var linkName));
        Assert.Equal("Owner", linkName);
    }

    [Fact]
    public void StableEngine_PersistsStabledPetsViaOwnerTags()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.Name = "Owner";
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var friend = world.CreateCharacter();
        friend.Name = "Friend";
        friend.IsPlayer = true;
        world.PlaceCharacter(friend, new Point3D(101, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Mare";
        world.PlaceCharacter(pet, new Point3D(102, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));
        pet.AddFriend(friend);
        pet.NpcFood = 37;

        var stableA = new SphereNet.Game.NPCs.StableEngine();
        Assert.True(stableA.StablePet(owner, pet, world));

        var stableB = new SphereNet.Game.NPCs.StableEngine();
        var claimed = stableB.ClaimPet(owner, 0, world, owner.Position);
        Assert.NotNull(claimed);
        Assert.True(claimed!.HasOwner(owner.Uid));
        Assert.True(claimed.IsFriendOf(friend.Uid));
        Assert.Equal((ushort)37, claimed.NpcFood);
    }

    [Fact]
    public void MountEngine_TryMount_AssignsOwnerAndController()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var mountEngine = new SphereNet.Game.Mounts.MountEngine(world);

        var rider = world.CreateCharacter();
        rider.Name = "Rider";
        rider.IsPlayer = true;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));

        var horse = world.CreateCharacter();
        horse.Name = "Horse";
        horse.BodyId = 0x00C8;
        world.PlaceCharacter(horse, new Point3D(101, 100, 0, 0));

        Assert.True(mountEngine.TryMount(rider, horse));
        Assert.True(horse.HasOwner(rider.Uid));
        Assert.True(horse.HasController(rider.Uid));
    }

    [Fact]
    public void GameClient_DoubleClickMount_FiresMountTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 701 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var rider = world.CreateCharacter();
        rider.IsPlayer = true;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));
        var horse = world.CreateCharacter();
        horse.BodyId = 0x00C8;
        world.PlaceCharacter(horse, new Point3D(101, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int calls = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Mount", (_, args) =>
        {
            calls++;
            Assert.Same(horse, args.O1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher, mountEngine: new SphereNet.Game.Mounts.MountEngine(world));
        AttachCharacter(client, rider);

        client.HandleDoubleClick(horse.Uid.Value);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ScriptInterpreter_ForPlayers_UsesConsoleQueryObjects()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var args = new ExecTriggerArgs();
        var scope = new ScriptScope();

        var c1 = new Character { Name = "P1", IsPlayer = true };
        var c2 = new Character { Name = "P2", IsPlayer = true };
        var console = new TestConsole
        {
            QueryHandler = (query, _, _, _) =>
                query.Equals("FORPLAYERS", StringComparison.OrdinalIgnoreCase)
                    ? [c1, c2]
                    : []
        };

        // Source-X: loop body runs with each iterated object as target.
        // TAG.VISITED is set on each iterated character, not the original target.
        var lines = ParseKeys(
            "FORPLAYERS 9999",
            "TAG.VISITED=1",
            "ENDFOR");

        interpreter.Execute(lines, target, console, args, scope);
        Assert.True(c1.TryGetProperty("TAG.VISITED", out var v1));
        Assert.Equal("1", v1);
        Assert.True(c2.TryGetProperty("TAG.VISITED", out var v2));
        Assert.Equal("1", v2);
    }

    [Fact]
    public void ScriptInterpreter_UnhandledVerb_UsesConsoleScriptBridge()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var args = new ExecTriggerArgs();
        var scope = new ScriptScope();
        var console = new TestConsole
        {
            CommandHandler = (_, key, value, _) => key.Equals("TARGETFG", StringComparison.OrdinalIgnoreCase) && value == "f_mtele"
        };

        var lines = ParseKeys("TARGETFG f_mtele");

        interpreter.Execute(lines, target, console, args, scope);
        Assert.Equal("TARGETFG", console.LastCommandKey);
        Assert.Equal("f_mtele", console.LastCommandArgs);
    }

    private static List<ScriptKey> ParseKeys(params string[] lines)
    {
        var keys = new List<ScriptKey>(lines.Length);
        foreach (var line in lines)
        {
            var key = new ScriptKey();
            key.Parse(line);
            keys.Add(key);
        }
        return keys;
    }

    private sealed class TestConsole : ITextConsole
    {
        public Func<string, IScriptObj, string, ITriggerArgs?, IReadOnlyList<IScriptObj>>? QueryHandler { get; set; }
        public Func<IScriptObj, string, string, ITriggerArgs?, bool>? CommandHandler { get; set; }
        public string LastCommandKey { get; private set; } = "";
        public string LastCommandArgs { get; private set; } = "";

        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "TEST";

        public bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs)
        {
            LastCommandKey = key;
            LastCommandArgs = args;
            return CommandHandler?.Invoke(target, key, args, triggerArgs) ?? false;
        }

        public IReadOnlyList<IScriptObj> QueryScriptObjects(string query, IScriptObj target, string args, ITriggerArgs? triggerArgs) =>
            QueryHandler?.Invoke(query, target, args, triggerArgs) ?? [];
    }

    [Fact]
    public void MovementEngine_RegionChange_CallsGlobalRegionFunctions()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);

        var dispatcher = new TriggerDispatcher();
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        dispatcher.Runner = runner;
        dispatcher.Resources = resources;

        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_region_hooks_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_onchar_regionleave]\nTAG.REGION_LEAVE=1\nRETURN 1\n\n" +
            "[FUNCTION f_onchar_regionenter]\nTAG.REGION_ENTER=1\nRETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var mover = new MovementEngine(world, dispatcher);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM; // bypass collision/map checks
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        ch.SetTag("CURRENT_REGION", "old_region");

        mover.TryMove(ch, Direction.East, running: false, sequence: 0);

        Assert.True(ch.TryGetProperty("TAG.REGION_LEAVE", out var leaveVal));
        Assert.Equal("1", leaveVal);
    }

    [Fact]
    public void CommandHandler_NotFound_FiresScriptParityWarning()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var gm = world.CreateCharacter();
        gm.PrivLevel = PrivLevel.Owner;

        string? warnedVerb = null;
        commands.OnScriptParityWarning += (_, verb, _) => warnedVerb = verb;

        var res = commands.TryExecute(gm, "UNKNOWNCMD 1");
        Assert.Equal(CommandResult.NotFound, res);
        Assert.Equal("UNKNOWNCMD", warnedVerb);
    }

    [Fact]
    public void ScriptSystemHooks_DispatchesServerAndObjectFunctions()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_hooks_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_onserver_start]\nTAG.SERVER_HOOK=1\nRETURN 1\n\n" +
            "[FUNCTION f_onobj_create]\nSRC.TAG.OBJ_CREATED=1\nRETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        var hooks = new ScriptSystemHooks(runner);

        var serverObj = new Character();
        var obj = new Character();

        bool handled = hooks.DispatchServer("start", serverObj);
        hooks.DispatchObject("create", obj);

        Assert.True(handled);
        Assert.True(serverObj.TryGetProperty("TAG.SERVER_HOOK", out var sv) && sv == "1");
        Assert.True(obj.TryGetProperty("TAG.OBJ_CREATED", out var ov) && ov == "1");
    }

    [Fact]
    public void ScriptDbAdapter_ConnectQueryExecuteAndRows_Work()
    {
        SqliteFactory.Instance.ToString(); // ensure assembly load
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
        string dbFile = Path.Combine(Path.GetTempPath(), $"spherenet_db_{Guid.NewGuid():N}.db");
        string conn = $"Data Source={dbFile}";
        var loggerFactory = LoggerFactory.Create(_ => { });
        var db = new ScriptDbAdapter(loggerFactory.CreateLogger<ScriptDbAdapter>())
        {
            DefaultProvider = "Microsoft.Data.Sqlite",
            DefaultConnectionString = conn
        };

        Assert.True(db.ConnectDefault(out var connectErr), connectErr);
        Assert.True(db.Execute("CREATE TABLE test (id INTEGER PRIMARY KEY, name TEXT);", out _, out var createErr), createErr);
        Assert.True(db.Execute("INSERT INTO test(name) VALUES ('alice'),('bob');", out int affected, out var insErr), insErr);
        Assert.True(affected >= 2);
        Assert.True(db.Query("SELECT id,name FROM test ORDER BY id;", out int rows, out var queryErr), queryErr);
        Assert.Equal(2, rows);
        Assert.True(db.TryResolveRowValue("db.row.numrows", out var numRows));
        Assert.Equal("2", numRows);
        Assert.True(db.TryResolveRowValue("db.row.0.name", out var firstName));
        Assert.Equal("alice", firstName);
        db.Close();
    }

    [Fact]
    public void PacketHook_PilotOpcodes_CanShortCircuit()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var network = new NetworkManager(2, loggerFactory);
        var state = new NetState(loggerFactory.CreateLogger<NetState>())
        {
            Id = 1
        };
        byte[] op03 = [0x03, 0x00, 0x0A, 0x00, 0x35, 0x00, 0x03, 0x74, 0x65, 0x73, 0x74, 0x00];
        byte[] opad = [0xAD, 0x00, 0x0E, 0x00, 0x35, 0x00, 0x03, 0x49, 0x56, 0x4C, 0x00, 0x00, 0x00, 0x00];
        byte[] op6c = [0x6C, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 10, 0, 20, 0, 0, 0, 0, 0];
        byte[] op72 = [0x72, 0x01, 0x00, 0x00, 0x00];
        byte[] op22 = [0x22, 0x00, 0x01];
        bool called03 = false;
        bool calledAd = false;
        bool called6c = false;
        bool called72 = false;
        bool called22 = false;
        network.PacketScriptHook = (_, opcode, _) =>
        {
            switch (opcode)
            {
                case 0x03: called03 = true; break;
                case 0xAD: calledAd = true; break;
                case 0x6C: called6c = true; break;
                case 0x72: called72 = true; break;
                case 0x22: called22 = true; break;
            }
            return true;
        };

        Assert.True(network.InvokePacketScriptHook(state, 0x03, op03));
        Assert.True(network.InvokePacketScriptHook(state, 0xAD, opad));
        Assert.True(network.InvokePacketScriptHook(state, 0x6C, op6c));
        Assert.True(network.InvokePacketScriptHook(state, 0x72, op72));
        Assert.True(network.InvokePacketScriptHook(state, 0x22, op22));
        Assert.True(called03 && calledAd && called6c && called72 && called22);
    }

    [Fact]
    public void DialogAndDefMessage_Bridge_WorksInClient()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>())
        {
            Id = 7
        };
        // Keep socket null in tests; we only verify state transitions and callbacks.
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager, loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var dispatcher = new TriggerDispatcher();
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_dialog_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_dialogclose_testdlg]\nSRC.TAG.DIALOG_CLOSED=1\nRETURN 1\n\n" +
            "[DEFMESSAGE]\nHELLO_MSG=Merhaba SourceX\n");
        resources.LoadResourceFile(tempFile);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        dispatcher.Runner = runner;
        dispatcher.Resources = resources;
        var hooks = new ScriptSystemHooks(runner);

        client.SetEngines(triggerDispatcher: dispatcher);
        client.SetScriptServices(hooks, null, k => resources.TryGetDefMessage(k, out var v) ? v : null);

        var acc = accountManager.CreateAccount("tester", "pw")!;
        // attach internals via login flow
        client.HandleLoginRequest(acc.Name, "pw");
        client.HandleGameLogin(acc.Name, "pw", 1);
        client.HandleCharSelect(-1, "Tester");

        var activeChar = client.Character!;
        Assert.True(client.TryExecuteScriptCommand(activeChar, "DIALOG", "testdlg", null));
        client.HandleGumpResponse(0, (uint)Math.Abs("testdlg".GetHashCode()), 1, [], []);
        Assert.True(activeChar.TryGetProperty("TAG.DIALOG_CLOSED", out var closedVal));
        Assert.Equal("1", closedVal);
    }

    [Fact]
    public void CharacterOwnedMultiTokens_UseRuntimeResolvers()
    {
        var ch = new Character();
        ch.SetUid(new Serial(0x01));

        Character.ResolveHouseUidsByOwner = owner =>
            owner == ch.Uid
                ? [new Serial(0x40001000), new Serial(0x40001001)]
                : [];
        Character.ResolveShipUidsByOwner = owner =>
            owner == ch.Uid
                ? [new Serial(0x40002000)]
                : [];

        try
        {
            Assert.True(ch.TryGetProperty("HOUSES", out var houses));
            Assert.Equal("2", houses);
            Assert.True(ch.TryGetProperty("HOUSE.0", out var firstHouse));
            Assert.Equal("040001000", firstHouse);
            Assert.True(ch.TryGetProperty("HOUSE.5", out var missingHouse));
            Assert.Equal("0", missingHouse);

            Assert.True(ch.TryGetProperty("SHIPS", out var ships));
            Assert.Equal("1", ships);
            Assert.True(ch.TryGetProperty("SHIP.0", out var firstShip));
            Assert.Equal("040002000", firstShip);
        }
        finally
        {
            Character.ResolveHouseUidsByOwner = null;
            Character.ResolveShipUidsByOwner = null;
        }
    }

    [Fact]
    public void TriggerDispatcher_UsesSourceCompatibleItemTriggerNames()
    {
        var dispatcher = new TriggerDispatcher();
        var item = new Item();
        var ch = new Character();
        int itemCalls = 0;
        int charCalls = 0;

        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Trade", (_, _) =>
        {
            itemCalls++;
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPET", "itemDropOn_Trade", (_, _) =>
        {
            charCalls++;
            return TriggerResult.Default;
        });

        dispatcher.FireItemTrigger(item, ItemTrigger.DropOnTrade, new SphereNet.Game.Scripting.TriggerArgs());
        dispatcher.FireCharTrigger(ch, CharTrigger.itemDropOnTrade, new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(1, itemCalls);
        Assert.Equal(1, charCalls);
    }

    [Fact]
    public void GameClient_TradeLifecycle_FiresCreateAcceptedAndCloseTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 801 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));
        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        var tradeManager = new TradeManager();
        var dispatcher = new TriggerDispatcher();
        int createCount = 0;
        int acceptedCount = 0;
        int closeCount = 0;

        dispatcher.RegisterCharEvent("EVENTSPLAYER", "TradeCreate", (_, _) =>
        {
            createCount++;
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "TradeAccepted", (_, _) =>
        {
            acceptedCount++;
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "TradeClose", (_, _) =>
        {
            closeCount++;
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher, tradeManager: tradeManager);
        AttachCharacter(client, initiator);

        client.InitiateTrade(partner);
        var trade = tradeManager.FindTradeFor(initiator);
        Assert.NotNull(trade);
        Assert.Equal(2, createCount);

        trade!.ToggleAccept(partner);
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 0);

        Assert.Equal(2, acceptedCount);
        Assert.Equal(2, closeCount);
        Assert.Null(tradeManager.FindTradeFor(initiator));
    }

    [Fact]
    public void GameClient_DropItemOnTradeContainer_FiresDropOnTradeTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 851 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));
        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, initiator.Position);

        var tradeManager = new TradeManager();
        var dispatcher = new TriggerDispatcher();
        int dropOnTradeCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Trade", (_, args) =>
        {
            dropOnTradeCount++;
            Assert.Same(initiator, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            Assert.Same(partner, args.O1);
            Assert.NotEqual(0, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher, tradeManager: tradeManager);
        AttachCharacter(client, initiator);

        client.InitiateTrade(partner);
        var trade = tradeManager.FindTradeFor(initiator);
        Assert.NotNull(trade);

        client.HandleItemPickup(item.Uid.Value, 1);
        client.HandleItemDrop(item.Uid.Value, 30, 30, 0, trade!.InitiatorContainer.Uid.Value);

        Assert.Equal(1, dropOnTradeCount);
        Assert.Equal(trade.InitiatorContainer.Uid, item.ContainedIn);
        Assert.Contains(item, trade.InitiatorContainer.Contents);
    }

    [Fact]
    public void GameClient_DropItemOnTradeContainer_ReturnTrueRejectsTradeDrop()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 852 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        initiator.Equip(pack, Layer.Pack);
        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, initiator.Position);

        var tradeManager = new TradeManager();
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Trade", (_, _) => TriggerResult.True);

        client.SetEngines(triggerDispatcher: dispatcher, tradeManager: tradeManager);
        AttachCharacter(client, initiator);

        client.InitiateTrade(partner);
        var trade = tradeManager.FindTradeFor(initiator);
        Assert.NotNull(trade);

        client.HandleItemPickup(item.Uid.Value, 1);
        client.HandleItemDrop(item.Uid.Value, 30, 30, 0, trade!.InitiatorContainer.Uid.Value);

        Assert.Equal(pack.Uid, item.ContainedIn);
        Assert.Contains(item, pack.Contents);
        Assert.DoesNotContain(item, trade.InitiatorContainer.Contents);
        Assert.Equal(0x28, GetQueuedPackets(netState).Last().Span[0]);
    }

    [Fact]
    public void GameClient_VendorBuyAndSell_FireItemTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var oldVendorWorld = VendorEngine.World;
        VendorEngine.World = world;
        try
        {
            var accountManager = new AccountManager(loggerFactory);
            var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 901 };
            SetNetStateInUse(netState, true);
            var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
                loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

            var player = world.CreateCharacter();
            player.IsPlayer = true;
            player.PrivLevel = PrivLevel.GM; // skip gold checks; this test is about trigger visibility.
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            player.Equip(pack, Layer.Pack);

            var vendor = world.CreateCharacter();
            vendor.NpcBrain = NpcBrainType.Vendor;
            world.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));

            var stock = world.CreateItem();
            stock.BaseId = 0x0F7A;
            stock.Name = "Black Pearl";
            stock.Amount = 1;
            world.PlaceItem(stock, vendor.Position);

            var sellItem = world.CreateItem();
            sellItem.BaseId = 0x0F7B;
            sellItem.Name = "Blood Moss";
            sellItem.Amount = 1;
            pack.AddItem(sellItem);

            var dispatcher = new TriggerDispatcher();
            int buyCount = 0;
            int sellCount = 0;
            dispatcher.RegisterItemEvent("EVENTSITEM", "Buy", (_, args) =>
            {
                buyCount++;
                Assert.Equal(1, args.N1);
                Assert.True(args.N2 > 0);
                return TriggerResult.Default;
            });
            dispatcher.RegisterItemEvent("EVENTSITEM", "Sell", (_, args) =>
            {
                sellCount++;
                Assert.Equal(1, args.N1);
                Assert.True(args.N2 > 0);
                return TriggerResult.Default;
            });

            client.SetEngines(triggerDispatcher: dispatcher);
            AttachCharacter(client, player);

            client.HandleVendorBuy(vendor.Uid.Value, 1,
            [
                new SphereNet.Network.Packets.Incoming.VendorBuyEntry
                {
                    ItemSerial = stock.Uid.Value,
                    Amount = 1
                }
            ]);
            client.HandleVendorSell(vendor.Uid.Value,
            [
                new SphereNet.Network.Packets.Incoming.VendorSellEntry
                {
                    ItemSerial = sellItem.Uid.Value,
                    Amount = 1
                }
            ]);

            Assert.Equal(1, buyCount);
            Assert.Equal(1, sellCount);
        }
        finally
        {
            VendorEngine.World = oldVendorWorld;
        }
    }

    [Fact]
    public void GameClient_ContextMenu_FiresRequestAndSelectTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1001 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Context Item";
        world.PlaceItem(item, player.Position);

        var dispatcher = new TriggerDispatcher();
        int charRequestCount = 0;
        int charSelectCount = 0;
        int itemRequestCount = 0;
        int itemSelectCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ContextMenuRequest", (_, args) =>
        {
            charRequestCount++;
            Assert.Equal(0, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ContextMenuSelect", (_, args) =>
        {
            charSelectCount++;
            Assert.Equal(1, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ContextMenuRequest", (_, args) =>
        {
            itemRequestCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            Assert.Equal(0, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ContextMenuSelect", (_, args) =>
        {
            itemSelectCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            Assert.Equal(42, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        InvokePrivate(client, "SendContextMenu", target.Uid.Value);
        InvokePrivate(client, "HandleContextMenuResponse", target.Uid.Value, (ushort)1);
        InvokePrivate(client, "SendContextMenu", item.Uid.Value);
        InvokePrivate(client, "HandleContextMenuResponse", item.Uid.Value, (ushort)42);

        Assert.Equal(1, charRequestCount);
        Assert.Equal(1, charSelectCount);
        Assert.Equal(1, itemRequestCount);
        Assert.Equal(1, itemSelectCount);
    }

    [Fact]
    public void GameClient_AOSTooltip_FiresItemTooltipTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        world.ToolTipMode = 1;
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1101 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Tooltip Item";
        item.ItemType = ItemType.Container;
        world.PlaceItem(item, player.Position);
        var targetChar = world.CreateCharacter();
        targetChar.IsPlayer = true;
        targetChar.Name = "Tooltip Character";
        world.PlaceCharacter(targetChar, new Point3D(101, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int beforeCount = 0;
        int afterCount = 0;
        int charTooltipCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ClientTooltip", (_, args) =>
        {
            charTooltipCount++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ClientTooltip", (_, args) =>
        {
            beforeCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ClientTooltipAfterDefault", (_, args) =>
        {
            afterCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleAOSTooltip(item.Uid.Value);
        client.HandleAOSTooltip(targetChar.Uid.Value);

        Assert.Equal(1, beforeCount);
        Assert.Equal(1, afterCount);
        Assert.Equal(1, charTooltipCount);
        Assert.Contains(GetQueuedPackets(netState), p => p.Span[0] == 0xDC);
        Assert.Contains(GetQueuedPackets(netState), p => p.Span[0] == 0xD6);
    }

    [Fact]
    public void GameClient_AOSTooltip_ReturnTrueSuppressesTooltipPackets()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        world.ToolTipMode = 1;
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1102 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Hidden Tooltip Item";
        world.PlaceItem(item, player.Position);

        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "ClientTooltip", (_, _) => TriggerResult.True);

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleAOSTooltip(item.Uid.Value);

        Assert.DoesNotContain(GetQueuedPackets(netState), p => p.Span[0] == 0xDC);
        Assert.DoesNotContain(GetQueuedPackets(netState), p => p.Span[0] == 0xD6);
    }

    [Fact]
    public void GameClient_TargetFunction_FiresItemTargOnItemTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1201 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var sourceItem = world.CreateItem();
        sourceItem.Name = "Targeting Wand";
        world.PlaceItem(sourceItem, player.Position);
        var targetItem = world.CreateItem();
        targetItem.Name = "Target Item";
        world.PlaceItem(targetItem, new Point3D(101, 100, 0, 0));

        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_target_block_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_never_run]\n" +
            "TAG.NEVER_RUN=1\n" +
            "RETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var dispatcher = new TriggerDispatcher
        {
            Runner = new TriggerRunner(
                new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>()),
                resources,
                loggerFactory.CreateLogger<TriggerRunner>())
        };
        int targetItemCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "TargOn_Item", (_, args) =>
        {
            targetItemCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(sourceItem, args.ItemSrc);
            Assert.Same(targetItem, args.O1);
            Assert.Equal(10, args.N1);
            Assert.Equal(11, args.N2);
            Assert.Equal(12, args.N3);
            Assert.Equal("0", args.S1);
            return TriggerResult.True;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);
        SetPrivateField(client, "_pendingTargetFunction", "f_never_run");
        SetPrivateField(client, "_pendingTargetItemUid", sourceItem.Uid);

        client.HandleTargetResponse(0, 0, targetItem.Uid.Value, 10, 11, 12, 0);

        Assert.Equal(1, targetItemCount);
        Assert.False(player.TryGetProperty("TAG.NEVER_RUN", out var neverRun) && neverRun == "1");
    }

    [Fact]
    public void GameClient_TargetCancel_FiresItemTargOnCancelTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1202 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var sourceItem = world.CreateItem();
        sourceItem.Name = "Targeting Wand";
        world.PlaceItem(sourceItem, player.Position);

        var dispatcher = new TriggerDispatcher();
        int cancelCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "TargOn_Cancel", (_, args) =>
        {
            cancelCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(sourceItem, args.ItemSrc);
            Assert.Null(args.O1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);
        SetPrivateField(client, "_pendingTargetFunction", "f_cancelled");
        SetPrivateField(client, "_pendingTargetItemUid", sourceItem.Uid);

        client.HandleTargetResponse(0, 0, 0xFFFFFFFF, 0, 0, 0, 0);

        Assert.Equal(1, cancelCount);
    }

    [Fact]
    public void GameClient_TargetFunction_FiresItemTargOnCharAndGroundTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1203 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var sourceItem = world.CreateItem();
        sourceItem.Name = "Targeting Wand";
        world.PlaceItem(sourceItem, player.Position);
        var targetChar = world.CreateCharacter();
        targetChar.IsPlayer = true;
        world.PlaceCharacter(targetChar, new Point3D(101, 100, 0, 0));

        var dispatcher = new TriggerDispatcher
        {
            Runner = new TriggerRunner(
                new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>()),
                new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>()),
                loggerFactory.CreateLogger<TriggerRunner>())
        };
        int charCount = 0;
        int groundCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "TargOn_Char", (_, args) =>
        {
            charCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(sourceItem, args.ItemSrc);
            Assert.Same(targetChar, args.O1);
            return TriggerResult.True;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "TargOn_Ground", (_, args) =>
        {
            groundCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(sourceItem, args.ItemSrc);
            Assert.Null(args.O1);
            Assert.Equal(20, args.N1);
            Assert.Equal(21, args.N2);
            Assert.Equal(22, args.N3);
            Assert.Equal("0", args.S1);
            return TriggerResult.True;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);
        SetPrivateField(client, "_pendingTargetFunction", "f_char");
        SetPrivateField(client, "_pendingTargetItemUid", sourceItem.Uid);
        client.HandleTargetResponse(0, 0, targetChar.Uid.Value, 10, 11, 12, 0);

        SetPrivateField(client, "_pendingTargetFunction", "f_ground");
        SetPrivateField(client, "_pendingTargetAllowGround", true);
        SetPrivateField(client, "_pendingTargetItemUid", sourceItem.Uid);
        client.HandleTargetResponse(1, 0, 0, 20, 21, 22, 0);

        Assert.Equal(1, charCount);
        Assert.Equal(1, groundCount);
    }

    [Fact]
    public void GameClient_TargetCancel_FiresSpellTargetCancelTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1204 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int cancelCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SpellTargetCancel", (_, args) =>
        {
            cancelCount++;
            Assert.Same(player, args.CharSrc);
            Assert.NotEqual(0, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);
        player.SetTag("CAST_SPELL", SpellType.MagicArrow.ToString());

        client.HandleTargetResponse(0, 0, 0xFFFFFFFF, 0, 0, 0, 0);

        Assert.Equal(1, cancelCount);
        Assert.False(player.TryGetTag("CAST_SPELL", out _));
    }

    [Fact]
    public void GameClient_RenameProfileAndDye_FireUiActionTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1301 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Name = "Old Name";
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Dyed Item";
        item.Hue = new SphereNet.Core.Types.Color(1);
        world.PlaceItem(item, gm.Position);

        var account = accountManager.CreateAccount("gm", "pw")!;
        account.PrivLevel = PrivLevel.GM;

        var dispatcher = new TriggerDispatcher();
        int renameCount = 0;
        int profileCount = 0;
        int dyeCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Rename", (_, args) =>
        {
            renameCount++;
            Assert.Same(gm, args.CharSrc);
            Assert.Equal("New Name", args.S1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Profile", (_, args) =>
        {
            profileCount++;
            Assert.Same(gm, args.CharSrc);
            Assert.Equal(1, args.N1);
            Assert.Equal("blocked profile", args.S1);
            return TriggerResult.True;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "Dye", (_, args) =>
        {
            dyeCount++;
            Assert.Same(gm, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            Assert.Equal(0x0456, args.N1);
            return TriggerResult.True;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, gm, account);

        client.HandleRename(target.Uid.Value, "New Name");
        client.HandleProfileRequest(1, target.Uid.Value, "blocked profile");
        client.HandleDyeResponse(item.Uid.Value, 0x0456);

        Assert.Equal(1, renameCount);
        Assert.Equal("New Name", target.Name);
        Assert.Equal(1, profileCount);
        Assert.False(target.TryGetTag("PROFILE_BIO", out _));
        Assert.Equal(1, dyeCount);
        Assert.Equal((ushort)1, item.Hue.Value);
    }

    [Fact]
    public void GameClient_DyeVatApply_FiresDyeTriggerAndCanApplyHue()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1302 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var vat = world.CreateItem();
        vat.ItemType = ItemType.DyeVat;
        vat.SetTag("DYE_HUE", "1110");
        world.PlaceItem(vat, player.Position);
        var dest = world.CreateItem();
        dest.Hue = new SphereNet.Core.Types.Color(1);
        world.PlaceItem(dest, player.Position);

        var dispatcher = new TriggerDispatcher();
        int dyeCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "Dye", (_, args) =>
        {
            dyeCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(dest, args.ItemSrc);
            Assert.Same(vat, args.O1);
            Assert.Equal(1110, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        InvokePrivate(client, "HandleDyeApply", vat, dest.Uid);

        Assert.Equal(1, dyeCount);
        Assert.Equal((ushort)1110, dest.Hue.Value);
    }

    [Fact]
    public void GameClient_SingleClick_FiresAfterClickAfterNamePacket()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1401 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Name = "Clicked Character";
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Clicked Item";
        world.PlaceItem(item, player.Position);

        var dispatcher = new TriggerDispatcher();
        int charAfterClickCount = 0;
        int itemAfterClickCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "AfterClick", (_, args) =>
        {
            charAfterClickCount++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "AfterClick", (_, args) =>
        {
            itemAfterClickCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleSingleClick(target.Uid.Value);
        client.HandleSingleClick(item.Uid.Value);

        Assert.Equal(1, charAfterClickCount);
        Assert.Equal(1, itemAfterClickCount);
        Assert.Equal(2, GetQueuedPackets(netState).Count(p => p.Span[0] == 0xAE));
    }

    [Fact]
    public void GameClient_UserPacketHooks_FireSkillsStatsAndButtonTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1501 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int skillsCount = 0;
        int statsCount = 0;
        int chatCount = 0;
        int guildCount = 0;
        int questCount = 0;
        int virtueCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserSkills", (_, args) =>
        {
            skillsCount++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserStats", (_, args) =>
        {
            statsCount++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserChatButton", (_, args) =>
        {
            chatCount++;
            Assert.Equal(0x000B, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserGuildButton", (_, args) =>
        {
            guildCount++;
            Assert.Equal(0x0028, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserQuestButton", (_, args) =>
        {
            questCount++;
            Assert.Equal(0x0032, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserVirtueInvoke", (_, args) =>
        {
            virtueCount++;
            Assert.Equal(0x002C, args.N1);
            Assert.Equal(7, args.N2);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleStatusRequest(5, player.Uid.Value);
        client.HandleStatusRequest(4, player.Uid.Value);
        client.HandleExtendedCommand(0x000B, []);
        client.HandleExtendedCommand(0x0028, []);
        client.HandleExtendedCommand(0x0032, []);
        client.HandleExtendedCommand(0x002C, [7]);

        Assert.Equal(1, skillsCount);
        Assert.Equal(1, statsCount);
        Assert.Equal(1, chatCount);
        Assert.Equal(1, guildCount);
        Assert.Equal(1, questCount);
        Assert.Equal(1, virtueCount);
    }
}
