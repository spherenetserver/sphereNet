using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A [MULTIDEF] section's triggers and TEVENTS belong to the multi it defines.
///
/// Upstream a MULTIDEF header IS an itemdef header: LoadResourceSection maps it to
/// RES_ITEMDEF and ResourceGetNewID offsets a numeric one by ITEMID_MULTI so it
/// cannot collide with the itemdef of the same number (CServerConfig.cpp:3276 and
/// 4394). Everything an ITEMDEF body can carry therefore works on a MULTIDEF, and
/// the reference distribution uses that - @Create, @DClick and @ClientTooltip blocks
/// and TEVENTS lines written straight into MULTIDEF sections.
///
/// SphereNet gives multis their own resource type keyed by the raw multi.mul index,
/// which is the right call here - 345 of the pack's 372 numeric multi headers are
/// also itemdef headers - but nothing ever read a definition body off that index.
/// The sections loaded, and then every trigger in them was unreachable.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MultiDefTriggerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_mdt_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";
    private const ushort MultiId = 0x0064;

    private (TriggerDispatcher Dispatcher, Item Multi) Build(string script)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "m.scp");
        File.WriteAllText(file, script);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(),
            lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

        var multi = new Item { BaseId = MultiId, ItemType = ItemType.Multi };
        return (dispatcher, multi);
    }

    /// <summary>A trigger written in the MULTIDEF body runs on the multi.</summary>
    [Fact]
    public void ATriggerWrittenInTheMultidefRuns()
    {
        var (dispatcher, multi) = Build(
            "[MULTIDEF 064]" + Nl +
            "NAME=probe house" + Nl +
            "TYPE=t_multi" + Nl +
            "ON=@DClick" + Nl +
            "TAG.MULTI_DCLICK=1" + Nl +
            "RETURN 1" + Nl);

        var result = dispatcher.FireItemTrigger(multi, ItemTrigger.DClick, new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        Assert.True(multi.TryGetProperty("TAG.MULTI_DCLICK", out string val));
        Assert.Equal("1", val);
    }

    /// <summary>And a TEVENTS line on the MULTIDEF reaches the block it names.
    /// TEVENTS resolves against [EVENTS] (CBase.cpp:386, RES_EVENTS), which is worth
    /// stating because the reference pack's own four MULTIDEF TEVENTS lines name a
    /// [TYPEDEF] instead and resolve to nothing in either engine.</summary>
    [Fact]
    public void ATeventsLineOnTheMultidefReachesItsBlock()
    {
        var (dispatcher, multi) = Build(
            "[EVENTS e_probe_forge]" + Nl +
            "ON=@DClick" + Nl +
            "TAG.FORGE_USED=1" + Nl +
            "RETURN 1" + Nl + Nl +
            "[MULTIDEF 064]" + Nl +
            "NAME=probe forge" + Nl +
            "TYPE=t_multi" + Nl +
            "TEVENTS=e_probe_forge" + Nl);

        var result = dispatcher.FireItemTrigger(multi, ItemTrigger.DClick, new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        Assert.True(multi.TryGetProperty("TAG.FORGE_USED", out string val));
        Assert.Equal("1", val);
    }

    /// <summary>The definition body is readable, and it is NOT the itemdef of the
    /// same number - the two spaces have to stay apart, because in the shipped pack
    /// they overlap almost completely.</summary>
    [Fact]
    public void TheMultiDefinitionDoesNotCollideWithTheItemdefOfTheSameNumber()
    {
        Build(
            "[ITEMDEF 064]" + Nl +
            "DEFNAME=i_probe_not_a_multi" + Nl +
            "NAME=a plain item" + Nl +
            "TYPE=t_normal" + Nl +
            "VALUE=7" + Nl + Nl +
            "[MULTIDEF 064]" + Nl +
            "NAME=probe house" + Nl +
            "TYPE=t_multi" + Nl +
            "VALUE=1000000" + Nl);

        var itemDef = DefinitionLoader.GetItemDef(0x0064);
        var multiDef = DefinitionLoader.GetMultiItemDef(0x0064);

        Assert.NotNull(itemDef);
        Assert.NotNull(multiDef);
        Assert.Equal("a plain item", itemDef!.Name);
        Assert.Equal("probe house", multiDef!.Name);
    }
}
