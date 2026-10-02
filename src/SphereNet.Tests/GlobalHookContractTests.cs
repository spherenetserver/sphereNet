using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Security;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using Xunit;
using Xunit.Abstractions;
using GameTriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// The global [FUNCTION] hooks and by-name triggers the engine calls at fixed points.
///
/// Source-X runs the f_onaccount_* hooks on the server object with ARGS = the account
/// name and a RETURN 1 veto (CAccount.cpp:212-249, 395-420, 947-955, 986-1006). The
/// Sphere 56T custom-version hooks (f_onchar_create_player_init / _player /
/// f_onchar_delete_player, f_onchar_armor_calculation, f_onmulti_create, @StatGain,
/// @SkillUse, @StatValChange, @PartyJoin, @RegionExit) run next to the Source-X ones,
/// only when a pack defines them. Every Source-X-only pack must behave exactly as
/// before - each group below has a test pinning that.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GlobalHookContractTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _files = [];

    public GlobalHookContractTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (string f in _files)
            try { File.Delete(f); } catch (IOException) { }
    }

    private ScriptRuntimeStack Stack(string script)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"hooks-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        _files.Add(path);
        stack.Resources.LoadResourceFile(path);
        return stack;
    }

    /// <summary>A server object that remembers what a script assigned on it.</summary>
    private sealed class Server : IScriptObj
    {
        public readonly Dictionary<string, string> Props = new(StringComparer.OrdinalIgnoreCase);
        public string GetName() => "SERVER";
        public bool TryGetProperty(string key, out string value)
        {
            if (Props.TryGetValue(key, out value!)) return true;
            value = "";
            return key.StartsWith("SEEN", StringComparison.OrdinalIgnoreCase);
        }
        public bool TryExecuteCommand(string key, string args, ITextConsole source) => false;
        public bool TrySetProperty(string key, string value) { Props[key] = value; return true; }
        public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) => TriggerResult.Default;
        public string Get(string key) => Props.TryGetValue(key, out var v) ? v : "";
    }

    private (AccountManager Accounts, Server Server) Accounts(string script)
    {
        var stack = Stack(script);
        var server = new Server();
        Account.ScriptHooks = new ScriptAccountHooks(new ScriptSystemHooks(stack.Runner), server);
        var accounts = new AccountManager(TestHarness.CreateLoggerFactory()) { Md5Passwords = false };
        return (accounts, server);
    }

    // ------------------------------------------------------------------ accounts

    [Fact]
    public void AccountCreate_RunsOnTheServerWithTheName_AndReturnOneRefusesIt()
    {
        var (accounts, server) = Accounts("""
            [FUNCTION f_onaccount_create]
            SEENCREATE=<SEENCREATE>[<ARGS>]
            IF (STRMATCH(bad*,<ARGS>))
                RETURN 1
            ENDIF

            [FUNCTION f_onaccount_delete]
            SEENDELETE=<SEENDELETE>[<ARGS>]
            """);

        Assert.NotNull(accounts.CreateAccount("goodname", "pw"));
        Assert.Null(accounts.CreateAccount("badname", "pw"));

        Assert.Equal("[goodname][badname]", server.Get("SEENCREATE"));
        Assert.Null(accounts.FindAccount("badname"));
        // A refused creation goes through Account_Delete, which asks the delete hook.
        Assert.Equal("[badname]", server.Get("SEENDELETE"));
    }

    [Fact]
    public void AccountDelete_ReturnOneKeepsTheAccount()
    {
        var (accounts, _) = Accounts("""
            [FUNCTION f_onaccount_delete]
            IF (STRMATCH(keep*,<ARGS>))
                RETURN 1
            ENDIF
            """);
        accounts.CreateAccount("keepme", "pw");
        accounts.CreateAccount("dropme", "pw");

        Assert.False(accounts.DeleteAccount("keepme"));
        Assert.True(accounts.DeleteAccount("dropme"));
        Assert.NotNull(accounts.FindAccount("keepme"));
        Assert.Null(accounts.FindAccount("dropme"));
    }

    [Fact]
    public void AccountBlock_RunsOnlyOnAStateChange_NotOnEveryRefusedLogin()
    {
        var (accounts, server) = Accounts("""
            [FUNCTION f_onaccount_block]
            SEENBLOCK=<SEENBLOCK>[<ARGS>]

            [FUNCTION f_onaccount_unblock]
            SEENUNBLOCK=<SEENUNBLOCK>[<ARGS>]
            """);
        accounts.CreateAccount("blocky", "pw");

        accounts.SetAccountBlocked("blocky", true);
        accounts.SetAccountBlocked("blocky", true);          // no change, no hook
        Assert.Null(accounts.Authenticate("blocky", "pw")); // refused, no hook
        Assert.Null(accounts.Authenticate("blocky", "pw"));
        accounts.SetAccountBlocked("blocky", false);
        accounts.SetAccountBlocked("blocky", false);

        Assert.Equal("[blocky]", server.Get("SEENBLOCK"));
        Assert.Equal("[blocky]", server.Get("SEENUNBLOCK"));
    }

    [Fact]
    public void AccountBlock_ReturnOneKeepsTheState_AndScriptBlockWritesGoThroughIt()
    {
        var (accounts, _) = Accounts("""
            [FUNCTION f_onaccount_block]
            RETURN 1
            """);
        var acc = accounts.CreateAccount("veto", "pw")!;

        accounts.SetAccountBlocked("veto", true);
        Assert.False(acc.IsBanned);
        Assert.True(acc.TrySetProperty("BLOCK", "1"));
        Assert.False(acc.IsBanned);
    }

    [Fact]
    public void AccountPwChange_SeesNewAndOldPassword_AndReturnOneKeepsTheOld()
    {
        var (accounts, server) = Accounts("""
            [FUNCTION f_onaccount_pwchange]
            SEENPW=<SEENPW>[<ARGS>:<LOCAL.PASSWORD>:<LOCAL.OLDPASSWORD>]
            IF (STRMATCH(forbidden,<LOCAL.PASSWORD>))
                RETURN 1
            ENDIF
            """);
        var acc = accounts.CreateAccount("pwuser", "first")!;   // creation sets it: old is empty
        Assert.True(accounts.SetAccountPassword("pwuser", "second"));
        Assert.False(accounts.SetAccountPassword("pwuser", "forbidden"));

        Assert.True(acc.CheckPassword("second"));
        Assert.False(acc.CheckPassword("forbidden"));
        Assert.Equal("[pwuser:first:][pwuser:second:first][pwuser:forbidden:second]", server.Get("SEENPW"));
    }

    [Fact]
    public void AccountConnect_RunsBeforeThePasswordCheck_ReturnOneDenies_ReturnSixSkipsIt()
    {
        var (accounts, server) = Accounts("""
            [FUNCTION f_onaccount_connect]
            SEENCONNECT=<SEENCONNECT>[<ARGS>|<LOCAL.ACCOUNT>:<LOCAL.PASSWORD>]
            IF (STRMATCH(denied,<LOCAL.ACCOUNT>))
                RETURN 1
            ELSEIF (STRMATCH(scripted,<LOCAL.ACCOUNT>))
                RETURN 6
            ENDIF
            """);
        accounts.CreateAccount("plain", "right");
        accounts.CreateAccount("denied", "right");
        accounts.CreateAccount("scripted", "right");
        accounts.CreateAccount("banned", "right");
        accounts.SetAccountBlocked("banned", true);

        Assert.NotNull(accounts.Authenticate("plain", "right"));
        Assert.Null(accounts.Authenticate("plain", "wrong"));
        Assert.Null(accounts.Authenticate("denied", "right"));   // RETURN 1: refused
        Assert.NotNull(accounts.Authenticate("scripted", "any")); // RETURN 6: no compare
        Assert.Null(accounts.Authenticate("banned", "right"));    // blocked: never asked

        Assert.Equal("[|plain:right][|plain:wrong][|denied:right][|scripted:any]",
            server.Get("SEENCONNECT"));
    }

    [Fact]
    public void AccountLifecycle_WithoutHookFunctions_BehavesAsBefore()
    {
        var (accounts, _) = Accounts("[FUNCTION f_unrelated]\nRETURN 1\n");
        var acc = accounts.CreateAccount("plain", "pw")!;
        Assert.NotNull(accounts.Authenticate("plain", "pw"));
        Assert.True(accounts.SetAccountPassword("plain", "pw2"));
        Assert.True(acc.CheckPassword("pw2"));
        accounts.SetAccountBlocked("plain", true);
        Assert.True(acc.IsBanned);
        Assert.True(accounts.DeleteAccount("plain"));
    }

    // ------------------------------------------------------------------ char create / delete

    private static Character? CreateViaClient(ScriptRuntimeStack stack, byte race = 1)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var state = TestHarness.CreateActiveNetState(lf, 19961);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        typeof(GameClient).GetField("_triggerDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, stack.Dispatcher);
        TestHarness.SetPrivateField(client, "_account", new Account { Name = "creator" });
        client.PendingCharCreate = new CharCreateInfo
        {
            Name = "Hooked", Race = race, Str = 30, Dex = 30, Int = 20,
            Skills = [(0, 30), (1, 30), (2, 30)],
        };
        client.HandleCharSelect(-1, "Hooked");
        return client.Character;
    }

    [Fact]
    public void CharCreate_56THooksRunNextToTheSourceXOne_OnTheFinishedCharacter()
    {
        var stack = Stack("""
            [FUNCTION f_onchar_create_player_init]
            SRC.TAG.ORDER=<SRC.TAG.ORDER>I
            SRC.TAG.INITBODY=<SRC.BODY>
            SRC.TAG.INITRACE=<ARGN3>

            [FUNCTION f_onchar_create]
            SRC.TAG.ORDER=<SRC.TAG.ORDER>X

            [FUNCTION f_onchar_create_player]
            SRC.TAG.ORDER=<SRC.TAG.ORDER>P
            SRC.TAG.ACCOUNT=<ARGS>
            """);

        var ch = CreateViaClient(stack, race: 2);

        Assert.NotNull(ch);
        Assert.Equal("IXP", ch!.TryGetTag("ORDER", out var order) ? order : "");
        Assert.True(ch.TryGetTag("INITRACE", out var initRace));
        Assert.Equal("2", initRace);
        Assert.True(ch.TryGetTag("ACCOUNT", out var acc));
        Assert.Equal("creator", acc);
        // The body is already the elf one when the _init hook looks at it.
        Assert.True(ch.TryGetTag("INITBODY", out var body));
        Assert.NotEqual("", body);
    }

    [Fact]
    public void CharCreate_56TPlayerHookReturnOneDeletesTheCharacter()
    {
        var stack = Stack("""
            [FUNCTION f_onchar_create_player]
            RETURN 1
            """);
        Assert.Null(CreateViaClient(stack));
    }

    [Fact]
    public void CharCreate_SourceXPackOnly_RunsJustItsOwnHooks()
    {
        var stack = Stack("""
            [FUNCTION f_onchar_create_init]
            ARGN3=1

            [FUNCTION f_onchar_create]
            SRC.TAG.ORDER=<SRC.TAG.ORDER>X
            SRC.TAG.RACE=<ARGN3>
            """);
        var ch = CreateViaClient(stack, race: 2);

        Assert.NotNull(ch);
        Assert.True(ch!.TryGetTag("ORDER", out var order));
        Assert.Equal("X", order);
        Assert.Equal((ushort)0x0190, ch.BodyId);   // the race read back from _init
    }

    [Fact]
    public void CharDelete_56TPlayerHook_SeesTheCharSelectClientAsArgo_AndReturnOneKeepsIt()
    {
        var stack = Stack("""
            [FUNCTION f_onchar_delete_player]
            SRC.TAG.ARGOACC=<ARGO.ACCOUNT.NAME>
            RETURN 1
            """);
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        var account = new Account { Name = "deleter" };
        account.SetPassword("pw");
        account.SetCharSlot(0, ch.Uid);
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19962);
        TestHarness.AttachCharacter(client, null!, account);
        world.PlayerDeleteAllowed = target =>
            GlobalHookCalls.PlayerDeleteAllowed(stack.Runner, target, null, null, GameClient.CharSelectDeleter);

        client.HandleCharDelete(0, "");

        Assert.Same(ch, world.FindChar(ch.Uid));
        Assert.True(ch.TryGetTag("ARGOACC", out var argoAccount));
        Assert.Equal("deleter", argoAccount);
        Assert.Null(GameClient.CharSelectDeleter);
    }

    [Fact]
    public void CharDelete_SourceXHookStillDecides_AndRunsFirst()
    {
        var stack = Stack("""
            [FUNCTION f_onchar_delete]
            SRC.TAG.ORDER=<SRC.TAG.ORDER>X
            RETURN 1

            [FUNCTION f_onchar_delete_player]
            SRC.TAG.ORDER=<SRC.TAG.ORDER>P
            """);
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;

        Assert.False(GlobalHookCalls.PlayerDeleteAllowed(stack.Runner, ch, null, null, null));
        Assert.True(ch.TryGetTag("ORDER", out var order));
        Assert.Equal("X", order);

        var plain = Stack("[FUNCTION f_unrelated]\nRETURN 1\n");
        Assert.True(GlobalHookCalls.PlayerDeleteAllowed(plain.Runner, ch, null, null, null));
    }

    // ------------------------------------------------------------------ armour

    private static (Character Attacker, Character Defender) Duel(GameWorld world)
    {
        var attacker = world.CreateCharacter();
        attacker.Str = 77; attacker.MaxHits = 100; attacker.Hits = 100;
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        var defender = world.CreateCharacter();
        defender.Str = 100; defender.MaxHits = 100; defender.Hits = 100;
        world.PlaceCharacter(defender, new Point3D(101, 100, 0, 0));
        return (attacker, defender);
    }

    [Theory]
    [InlineData("LOCAL.DAMAGE=7", 7)]              // the custom-version body: LOCAL.Damage
    [InlineData("ARGN2=5", 15)]                    // a stock 0.56T body: ARGN1 - ARGN2
    [InlineData("SRC.TAG.NOTHING=1", 20)]          // writes neither: the engine's own result
    public void ArmorCalculation_DecidesThePreAosArmourStage(string body, int expected)
    {
        var stack = Stack($"""
            [FUNCTION f_onchar_armor_calculation]
            SRC.TAG.ARGOSTR=<ARGO.STR>
            SRC.TAG.ARGN1=<ARGN1>
            {body}
            """);
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Duel(world);
        CombatEngine.OnArmorCalculation = ctx => GlobalHookCalls.RunArmorCalculation(stack.Runner, ctx);

        int dealt = CombatEngine.ApplyCharacterDamage(defender, 20, attacker, DamageType.HitBlunt,
            combatFlags: (CombatFlags)0);

        Assert.Equal(expected, dealt);
        Assert.Equal(100 - expected, defender.Hits);
        Assert.True(defender.TryGetTag("ARGOSTR", out var argoStr));
        Assert.Equal("77", argoStr);
        Assert.True(defender.TryGetTag("ARGN1", out var argn1));
        Assert.Equal("20", argn1);
    }

    [Fact]
    public void ArmorCalculation_WithoutTheFunction_LeavesTheSourceXRoll()
    {
        var stack = Stack("[FUNCTION f_unrelated]\nRETURN 1\n");
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Duel(world);
        CombatEngine.OnArmorCalculation = ctx => GlobalHookCalls.RunArmorCalculation(stack.Runner, ctx);

        // No armour at all: the pre-AOS roll takes nothing off.
        Assert.Equal(20, CombatEngine.ApplyCharacterDamage(defender, 20, attacker, DamageType.HitBlunt,
            combatFlags: (CombatFlags)0));
    }

    [Fact]
    public void ArmorCalculation_IsNotAskedForFixedOrDivineDamage()
    {
        var stack = Stack("""
            [FUNCTION f_onchar_armor_calculation]
            LOCAL.DAMAGE=1
            """);
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Duel(world);
        CombatEngine.OnArmorCalculation = ctx => GlobalHookCalls.RunArmorCalculation(stack.Runner, ctx);

        Assert.Equal(20, CombatEngine.ApplyCharacterDamage(defender, 20, attacker, DamageType.Fixed,
            combatFlags: (CombatFlags)0));
    }

    // ------------------------------------------------------------------ multi create

    [Fact]
    public void MultiCreate_RemovingTheMultiOrReturnOneTakesThePlacementBack()
    {
        var world = TestHarness.CreateWorld();
        var placer = world.CreateCharacter();
        placer.Str = 61;
        world.PlaceCharacter(placer, new Point3D(100, 100, 0, 0));

        Item NewMulti()
        {
            var m = world.CreateItem();
            m.ItemType = ItemType.Multi;
            m.More1 = placer.Uid.Value;
            world.PlaceItem(m, new Point3D(110, 110, 0, 0));
            return m;
        }

        var keep = Stack("""
            [FUNCTION f_onmulti_create]
            TAG.PLACERSTR=<SRC.STR>
            TAG.OWNERSTR=<UID.<MORE1>.STR>
            """);
        var kept = NewMulti();
        Assert.True(GlobalHookCalls.MultiCreateStands(keep.Runner, kept, placer, null));
        Assert.True(kept.TryGetTag("PLACERSTR", out var placerStr));
        Assert.Equal("61", placerStr);

        var removes = Stack("""
            [FUNCTION f_onmulti_create]
            REMOVE
            """);
        var removed = NewMulti();
        Assert.False(GlobalHookCalls.MultiCreateStands(removes.Runner, removed, placer, null));
        Assert.True(removed.IsDeleted);

        var refuses = Stack("""
            [FUNCTION f_onmulti_create]
            RETURN 1
            """);
        Assert.False(GlobalHookCalls.MultiCreateStands(refuses.Runner, NewMulti(), placer, null));

        var none = Stack("[FUNCTION f_unrelated]\nRETURN 1\n");
        Assert.True(GlobalHookCalls.MultiCreateStands(none.Runner, NewMulti(), placer, null));
    }

    // ------------------------------------------------------------------ 56T triggers

    [Fact]
    public void StatGain_ReturnOneKeepsTheStat()
    {
        var stack = Stack("""
            [EVENTS e_nogain]
            ON=@StatGain
            SRC.TAG.GAINARGS=<ARGN1>,<ARGN2>,<ARGN3>
            RETURN 1
            """);
        TestHarness.SeedSkillAdvRates();
        var def = new SphereNet.Scripting.Definitions.SkillDef(ResourceId.Invalid);
        def.LoadFromKey("ADV_RATE", "2.5,50.0,200.0");
        def.LoadFromKey("STAT_STR", "100");
        SphereNet.Game.Definitions.DefinitionLoader.SetSkillDef((int)SkillType.Hiding, def);
        var curve = SphereNet.Scripting.Definitions.ValueCurve.Parse("2.5,50.0,200.0");
        SkillEngine.StatAdvCurves = [curve, curve, curve];

        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 20; ch.Dex = 20; ch.Int = 20;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        Assert.True(ch.TryExecuteCommand("EVENTS", "+e_nogain", new ScriptConsole()));

        int asked = 0;
        SkillEngine.OnStatGainCheck = (c, stat, current, next) =>
        {
            asked++;
            return stack.Dispatcher.FireCharTriggerIfUsed(c, "StatGain",
                new GameTriggerArgs { CharSrc = c, N1 = stat, N2 = current, N3 = next }) == TriggerResult.True;
        };
        for (int i = 0; i < 20000 && asked == 0; i++)
            SkillEngine.GainExperience(ch, SkillType.Hiding, 50);

        Assert.True(asked > 0, "a stat gain should have been attempted");
        Assert.Equal(20, ch.Str);
        Assert.True(ch.TryGetTag("GAINARGS", out var gainArgs));
        Assert.Equal("0,20,21", gainArgs);   // STR in Source-X order, current, new
    }

    private sealed class ScriptConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "test";
        public void SysMessage(string message) { }
    }

    [Fact]
    public void SkillUse_ReturnOneRefusesTheClientRequest_BeforeTheSourceXChain()
    {
        var stack = Stack("""
            [EVENTS e_noskill]
            ON=@SkillUse
            SRC.TAG.USED=<ARGN1>
            RETURN 1

            ON=@SkillWait
            SRC.TAG.WAIT=1
            """);
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19963);
        typeof(GameClient).GetField("_triggerDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, stack.Dispatcher);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        Assert.True(ch.TryExecuteCommand("EVENTS", "+e_noskill", new ScriptConsole()));

        client.HandleUseSkill((int)SkillType.Hiding);

        Assert.True(ch.TryGetTag("USED", out var used));
        Assert.Equal(((int)SkillType.Hiding).ToString(), used);
        Assert.False(ch.TryGetTag("WAIT", out _));
    }

    [Fact]
    public void StatValChange_ReportsTheBlowWithTheAttackerAsArgo()
    {
        var stack = Stack("""
            [EVENTS e_count]
            ON=@StatValChange
            SRC.TAG.VAL=<ARGN1>,<ARGN2>,<ARGN3>,<ARGO.STR>
            """);
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Duel(world);
        Assert.True(defender.TryExecuteCommand("EVENTS", "+e_count", new ScriptConsole()));
        Character.OnStatValChange = (ch, stat, oldValue, newValue, cause) =>
            stack.Dispatcher.FireCharTriggerIfUsed(ch, "StatValChange",
                new GameTriggerArgs { CharSrc = ch, N1 = stat, N2 = oldValue, N3 = newValue, O1 = cause });

        CombatEngine.ApplyCharacterDamage(defender, 20, attacker, DamageType.Fixed, combatFlags: (CombatFlags)0);

        Assert.True(defender.TryGetTag("VAL", out var val));
        Assert.Equal("0,100,80,77", val);
    }

    [Fact]
    public void Sphere56TTriggerNames_AreDispatchable_AndSilentWhenUnhooked()
    {
        foreach (string name in TriggerDispatcher.Sphere56TCharTriggerNames)
            Assert.Contains(name, TriggerDispatcher.DispatchableTriggerNames);

        var stack = Stack("[EVENTS e_other]\nON=@Click\nRETURN 1\n");
        stack.Dispatcher.BuildUsedTriggerCache();
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        foreach (string name in TriggerDispatcher.Sphere56TCharTriggerNames)
            Assert.Equal(TriggerResult.Default,
                stack.Dispatcher.FireCharTriggerIfUsed(ch, name, new GameTriggerArgs { CharSrc = ch }));
    }

    // ------------------------------------------------------------------ f_onitem_create

    [Fact]
    public void ItemCreateFunction_RunsOncePerItem_WithTheItemAsArgo()
    {
        var stack = Stack("""
            [FUNCTION f_onitem_create]
            ARGO.TAG.CREATED=<ARGO.TAG.CREATED>1
            """);
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Create, new GameTriggerArgs { ItemSrc = item });

        Assert.True(item.TryGetTag("CREATED", out var created));
        Assert.Equal("1", created);
    }

    // ------------------------------------------------------------------ server hooks

    [Fact]
    public void BlockIp_ArgnOneIsTheTimeout_ReadBack()
    {
        var stack = Stack("""
            [FUNCTION f_onserver_blockip]
            SEENIP=<ARGS>:<ARGN1>
            ARGN1=-1
            """);
        var server = new Server();
        var hooks = new ScriptSystemHooks(stack.Runner);
        long now = 1_000_000;
        var list = new IPBlockList { NowMsProvider = () => now };
        list.BlockTimeout = (ip, seconds) =>
        {
            var args = new SphereNet.Scripting.Execution.TriggerArgs { ArgString = ip, Number1 = seconds };
            hooks.RunServerFunction("f_onserver_blockip", server, args, out _);
            return (int)args.Number1;
        };

        list.Add("10.0.0.1", 60);
        Assert.Equal("10.0.0.1:60", server.Get("SEENIP"));
        now += 3_600_000;
        Assert.True(list.IsBlocked("10.0.0.1"));   // the script made it permanent

        var timed = new IPBlockList { NowMsProvider = () => now };
        timed.Add("10.0.0.2", 60);                  // no hook: the requested minute
        now += 61_000;
        Assert.False(timed.IsBlocked("10.0.0.2"));
    }

    [Fact]
    public void BlockIp_ProgramHookPassesThroughWhenUndefined()
    {
        var stack = Stack("[FUNCTION f_unrelated]\nRETURN 1\n");
        var method = typeof(SphereNet.Server.Program).GetMethod("RunBlockIpHook",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        int result = (int)method.Invoke(null, [new ScriptSystemHooks(stack.Runner), new Server(), "1.2.3.4", 42])!;
        Assert.Equal(42, result);
    }
}
