using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// RETURN 1 from @ClientTooltip means "do not add the built-in tooltip", not "send
/// nothing".
///
/// Upstream fires the trigger, and on TRUE skips the name entry and the default
/// entries while KEEPING what the script added during it
/// (CClientMsg_AOSTooltip.cpp:107-119). This threw the script's own entries away and
/// sent no tooltip at all.
///
/// The reference distribution's equipment tooltip ends with exactly that RETURN 1 and
/// says so in its own comment - "When you RETURN 1 in this trigger, the built-in
/// tooltip is prevented" - so with the trigger finally reachable, every piece of gear
/// would have shown a blank tooltip instead of the one the script built.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TooltipReturnOneTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_tt_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Build the tooltip for an item whose ITEMDEF hooks @ClientTooltip with
    /// the given body, and hand back what the cache ended up holding.</summary>
    private (uint Cliloc, string Args)[] Build(params string[] triggerBody)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "t.scp");
        var lines = new List<string>
        {
            "[ITEMDEF 013b9]",
            "DEFNAME=i_probe_blade",
            "TYPE=t_weapon_sword",
            "NAME=probe blade",
            "ON=@ClientTooltip",
        };
        lines.AddRange(triggerBody);
        File.WriteAllLines(file, lines);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(),
            lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 5200);
        client.SetEngines(triggerDispatcher: dispatcher);
        client.NetState.ClientVersionNumber = 70_020_000;   // AOS tooltips on

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        var item = world.CreateItem();
        item.BaseId = 0x13B9;
        item.ItemType = ItemType.WeaponSword;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));

        client.SkillUse.SendAosTooltip(item, requested: true);
        return item.TooltipCache?.Properties ?? [];
    }

    /// <summary>RETURN 1 keeps the script's own entries and drops the built-in
    /// ones.</summary>
    [Fact]
    public void ReturnOneKeepsTheScriptEntriesAndDropsTheDefaults()
    {
        var props = Build("ADDCLILOC 1060658,Access,Owner Only", "RETURN 1");

        Assert.Single(props);
        Assert.Equal(1060658u, props[0].Cliloc);
        Assert.Equal("Access\tOwner Only", props[0].Args);
    }

    /// <summary>RETURN 0 merges: the name entry the engine adds, then the script's.
    /// That is the path this engine already took, and it must not move.</summary>
    [Fact]
    public void ReturnZeroKeepsBoth()
    {
        var props = Build("ADDCLILOC 1060658,Access,Owner Only", "RETURN 0");

        Assert.True(props.Length >= 2, $"expected the name entry and the script's, got {props.Length}");
        Assert.Contains(props, p => p.Cliloc == 1050045);      // the name header
        Assert.Contains(props, p => p.Cliloc == 1060658);      // the script's line
    }

    /// <summary>A trigger that adds nothing and returns 1 leaves an empty tooltip -
    /// which is what the script asked for, rather than the engine's default.</summary>
    [Fact]
    public void ReturnOneWithNothingAddedIsAnEmptyTooltip()
    {
        Assert.Empty(Build("RETURN 1"));
    }

    /// <summary>An item with no trigger at all still gets the built-in tooltip.</summary>
    [Fact]
    public void AnItemWithNoTriggerKeepsTheDefault()
    {
        var props = Build("RETURN 0");
        Assert.Contains(props, p => p.Cliloc == 1050045);
    }
}
