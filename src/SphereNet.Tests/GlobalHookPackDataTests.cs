using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The global hook contracts against real packs' own sphere_serv_triggers.scp: a
/// Sphere 56T custom-version pack (SPHERENET_56T_SCRIPTS) and a Source-X pack
/// (SPHERENET_SOURCEX_SCRIPTS). Each skips cleanly when its pack is absent.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GlobalHookPackDataTests
{
    private const string ExternalPackDefault = @"C:\56T\scripts";
    private readonly ITestOutputHelper _out;

    public GlobalHookPackDataTests(ITestOutputHelper output) => _out = output;

    private static string? HookFile(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        string path = Path.Combine(root, "sphere_serv_triggers.scp");
        return File.Exists(path) ? path : null;
    }

    private static ScriptRuntimeStack Load(string file)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(file);
        return stack;
    }

    private sealed class Server : IScriptObj
    {
        public string GetName() => "SERVER";
        public bool TryGetProperty(string key, out string value) { value = ""; return false; }
        public bool TryExecuteCommand(string key, string args, ITextConsole source) => false;
        public bool TrySetProperty(string key, string value) => false;
        public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) => TriggerResult.Default;
    }

    private static (GameClient Client, Account Account) Client(ScriptRuntimeStack stack, int id)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var account = new Account { Name = "packtester" };
        Character.ResolveAccountForChar = _ => account;
        var state = TestHarness.CreateActiveNetState(lf, id);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        typeof(GameClient).GetField("_triggerDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, stack.Dispatcher);
        TestHarness.SetPrivateField(client, "_account", account);
        return (client, account);
    }

    private static Character? Create(GameClient client, byte race)
    {
        client.PendingCharCreate = new CharCreateInfo
        {
            Name = "Packchar", Race = race, Str = 30, Dex = 30, Int = 20,
            Skills = [(0, 30), (1, 30), (2, 30)],
        };
        client.HandleCharSelect(-1, "Packchar");
        return client.Character;
    }

    /// <summary>The 56T custom-version pack's f_onchar_create_player gives every new
    /// account its language (TAG.LANGUAGE, read by its 300+ language-keyed messages);
    /// that never happened while only the Source-X hook names were called.</summary>
    [Fact]
    public void CustomVersionPack_CharacterCreationRunsItsOwnHooks()
    {
        string? file = HookFile(Environment.GetEnvironmentVariable("SPHERENET_56T_SCRIPTS") ?? ExternalPackDefault);
        if (Gate.MissingValue(_out, "external script pack", file)) return;

        var stack = Load(file);
        var (client, account) = Client(stack, 19971);
        var ch = Create(client, race: 1);

        Assert.NotNull(ch);
        Assert.True(account.TryGetTag("LANGUAGE", out var language));
        Assert.Equal("TR", language);
    }

    /// <summary>The same pack's f_onchar_delete_player decides a player deletion: the
    /// engine's answer is the function's own (some versions of the pack end it in
    /// RETURN 1 and close deletion, others only log).</summary>
    [Fact]
    public void CustomVersionPack_DeletePlayerHookRefusesDeletion()
    {
        string? file = HookFile(Environment.GetEnvironmentVariable("SPHERENET_56T_SCRIPTS") ?? ExternalPackDefault);
        if (Gate.MissingValue(_out, "external script pack", file)) return;

        var stack = Load(file);
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        Assert.True(stack.Runner.TryRunFunction("f_onchar_delete_player", ch, null,
            new SphereNet.Scripting.Execution.TriggerArgs(ch), out var own));
        Assert.Equal(own != TriggerResult.True,
            GlobalHookCalls.PlayerDeleteAllowed(stack.Runner, ch, null, null, null));
    }

    /// <summary>A Source-X pack's own hooks: f_onchar_create_init forces ARGN3=1, so an
    /// elf request becomes a human body; account creation, password and connect run
    /// without refusing anything.</summary>
    [Fact]
    public void SourceXPack_HooksBehaveAsBefore()
    {
        string? file = HookFile(Environment.GetEnvironmentVariable("SPHERENET_SOURCEX_SCRIPTS"));
        if (Gate.MissingValue(_out, "external script pack", file)) return;

        var stack = Load(file);
        var (client, _) = Client(stack, 19972);
        var ch = Create(client, race: 2);
        Assert.NotNull(ch);
        Assert.Equal((ushort)0x0190, ch!.BodyId);
        Assert.True(ch.TryGetTag("CharCreateRace", out var race));
        Assert.Equal("1", race);

        Account.ScriptHooks = new ScriptAccountHooks(new ScriptSystemHooks(stack.Runner), new Server());
        var accounts = new AccountManager(TestHarness.CreateLoggerFactory()) { Md5Passwords = false };
        Assert.NotNull(accounts.CreateAccount("sxaccount", "pw"));
        Assert.NotNull(accounts.Authenticate("sxaccount", "pw"));
        Assert.True(accounts.SetAccountPassword("sxaccount", "pw2"));
        accounts.SetAccountBlocked("sxaccount", true);
        Assert.True(accounts.FindAccount("sxaccount")!.IsBanned);
    }
}
