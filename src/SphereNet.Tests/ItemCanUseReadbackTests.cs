using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
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
/// An item answers for the CAN and CANUSE its definition declares.
///
/// Upstream keeps both masks on the base and an instance read chains straight to it
/// (IBC_CAN / IBC_CANUSE), so &lt;CANUSE&gt; resolves inside a trigger running on the
/// item. Neither had a case here.
///
/// CANUSE is the one that bites: the reference pack's equip gate is written entirely
/// around it - &lt;CanUse&gt;&amp;&lt;def.can_u_gargoyle&gt; and the male/female pair -
/// and it hangs off 907 items by TEVENTS. With the read missing the gate tested a
/// blank on every one of them, which looks exactly like an item that declared no
/// restriction at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemCanUseReadbackTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_cu_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private (TriggerDispatcher Dispatcher, Item Probe) Load(string script)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        File.WriteAllText(file, script);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(),
            lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

        return (dispatcher, new Item { BaseId = 0x13BB });
    }

    /// <summary>The masks read back in the leading-zero hex a script compares
    /// against.</summary>
    [Fact]
    public void AnItemAnswersForItsDeclaredMasks()
    {
        var (_, item) = Load(
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_gown" + Nl +
            "TYPE=t_normal" + Nl +
            "CAN=04000" + Nl +          // can_i_dcignorelos
            "CANUSE=02" + Nl);          // can_u_female

        Assert.True(item.TryGetProperty("CANUSE", out string canUse));
        Assert.Equal("02", canUse);
        Assert.True(item.TryGetProperty("CAN", out string can));
        Assert.Equal("04000", can);
    }

    /// <summary>An item whose definition declares neither still answers, with
    /// nothing - a script testing a bit of it must read 0, not an empty string.</summary>
    [Fact]
    public void AnItemWithNoMasksAnswersZero()
    {
        var (_, item) = Load(
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_plain" + Nl +
            "TYPE=t_normal" + Nl);

        Assert.True(item.TryGetProperty("CANUSE", out string canUse));
        Assert.Equal("00", canUse);
    }

    /// <summary>End to end, in the shape the reference pack uses: the gate hangs off
    /// the item by TEVENTS and decides from CANUSE. Both halves had to be wired for
    /// this to do anything - the TEVENTS reference had to reach a [TYPEDEF], and the
    /// item had to answer for CANUSE.</summary>
    [Fact]
    public void TheEquipGateReadsTheMaskThroughATeventsTypedef()
    {
        var (dispatcher, item) = Load(
            "[TYPEDEF ei_probe_equipitem]" + Nl +
            "ON=@EquipTest" + Nl +
            "IF (<CANUSE>&02)" + Nl +
            "  TAG.GATE=female_only" + Nl +
            "  RETURN 1" + Nl +
            "ENDIF" + Nl +
            "TAG.GATE=open" + Nl +
            "RETURN 0" + Nl + Nl +
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_gown" + Nl +
            "TYPE=t_normal" + Nl +
            "CANUSE=02" + Nl +
            "TEVENTS=ei_probe_equipitem" + Nl);

        var result = dispatcher.FireItemTrigger(item, ItemTrigger.EquipTest,
            new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        Assert.True(item.TryGetProperty("TAG.GATE", out string gate));
        Assert.Equal("female_only", gate);
    }
}
