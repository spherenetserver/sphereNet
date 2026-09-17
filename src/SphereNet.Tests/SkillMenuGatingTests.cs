using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// What a crafting menu shows, and what it hides.
///
/// The 86 SKILLMENU sections in the crafting tree gate their entries two ways:
/// TEST names a skill and the value needed, TESTIF an expression that has to be
/// true (CClientUse.cpp:890-910). Both decide whether an entry EXISTS in the menu
/// the player is sent, so getting either wrong shows a recipe nobody can make or
/// hides one they can.
///
/// The skill value is where this can go quietly wrong: "TEST=BLACKSMITHING 1.0" is
/// ten in the engine's tenths, not one, and a parser that read it as one would let
/// everybody see everything.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SkillMenuGatingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_sm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    /// <summary>Open the menu as a character with the given blacksmithing value (in
    /// tenths) and report how many entries the client was offered.</summary>
    private int EntriesOffered(string menuBody, int blacksmithingTenths)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "sm.scp");
        File.WriteAllText(file, menuBody);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 4955);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        me.SetSkillRuntime(SkillType.Blacksmithing, blacksmithingTenths);
        TestHarness.AttachCharacter(client, me);

        // TESTIF is evaluated through the interpreter the dispatcher carries, so
        // without one the gate cannot run at all - and the code's fallback is to
        // SHOW the entry. A test that left it unwired would report a gate that
        // works when it is simply absent.
        var interpreter = new SphereNet.Scripting.Execution.ScriptInterpreter(
            new SphereNet.Scripting.Expressions.ExpressionParser(),
            lf.CreateLogger<SphereNet.Scripting.Execution.ScriptInterpreter>());
        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher
        {
            Resources = resources,
            Runner = new SphereNet.Scripting.Execution.TriggerRunner(
                interpreter, resources,
                lf.CreateLogger<SphereNet.Scripting.Execution.TriggerRunner>()),
        };
        client.SetEngines(triggerDispatcher: dispatcher);

        client.TryExecuteScriptCommand(me, "SKILLMENU", "sm_gate_probe", null);
        return ((SphereNet.Game.Clients.IClientContext)client).PendingMenuOptions?.Count ?? 0;
    }

    private const string TwoEntriesGatedBySkill =
        "[SKILLMENU sm_gate_probe]" + Nl +
        "Make something" + Nl +
        "ON=0 An easy thing" + Nl +
        "TEST=BLACKSMITHING 1.0" + Nl +
        "MAKEITEM=0" + Nl +
        "ON=0 A hard thing" + Nl +
        "TEST=BLACKSMITHING 90.0" + Nl +
        "MAKEITEM=0" + Nl;

    /// <summary>"1.0" is ten tenths. A smith at 5.0 clears the easy entry and not the
    /// hard one - the whole point of the gate.</summary>
    [Fact]
    public void TestHidesTheEntryWhoseSkillTheCharacterLacks()
    {
        Assert.Equal(1, EntriesOffered(TwoEntriesGatedBySkill, blacksmithingTenths: 50));
    }

    /// <summary>Enough skill for both, and both are offered.</summary>
    [Fact]
    public void TestShowsEveryEntryTheCharacterCanMake()
    {
        Assert.Equal(2, EntriesOffered(TwoEntriesGatedBySkill, blacksmithingTenths: 1000));
    }

    /// <summary>And the decimal really is the difference: at 9 tenths - just under
    /// "1.0" - even the easy entry is hidden. A parser that read 1.0 as 1 would show
    /// it, which is how a gate silently stops gating.</summary>
    [Fact]
    public void TheSkillValueIsReadInTenths()
    {
        Assert.Equal(0, EntriesOffered(TwoEntriesGatedBySkill, blacksmithingTenths: 9));
    }

    /// <summary>TESTIF gates on an expression instead
    /// (CClientUse.cpp:902).</summary>
    [Fact]
    public void TestifHidesTheEntryWhoseConditionIsFalse()
    {
        string body =
            "[SKILLMENU sm_gate_probe]" + Nl +
            "Make something" + Nl +
            "ON=0 Always" + Nl +
            "TESTIF=1" + Nl +
            "MAKEITEM=0" + Nl +
            "ON=0 Never" + Nl +
            "TESTIF=0" + Nl +
            "MAKEITEM=0" + Nl;

        Assert.Equal(1, EntriesOffered(body, blacksmithingTenths: 1000));
    }
}
