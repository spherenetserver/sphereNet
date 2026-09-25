using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// An assignment whose value is arithmetic is worked out, not stored as text.
///
/// Upstream loads a numeric key with s.GetArgVal(), which is Exp_GetVal on the whole
/// argument (CScript.cpp:154) - operators included. This engine substituted &lt;...&gt;
/// into the line and stopped, so "MORE2=&lt;MOREX&gt;/3" reached the object as the text
/// "30/3" and parsed as zero. Silently: a property set that stores 0 still reports
/// success, so nothing in the log said the value had been thrown away.
///
/// Both packs write 26 of these against object properties inside trigger blocks -
/// DISPID=&lt;DISPIDDEC&gt;+1 came out as graphic 0, TIMER=(a * b) as no timer at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ArithmeticAssignmentTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_arith_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Load a pack, wire the trigger stack, and hand back the world.</summary>
    private (GameWorld World, TriggerDispatcher Dispatcher) Pack(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        File.WriteAllLines(file, lines);

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.CreateTriggerHook = it => dispatcher.FireItemTrigger(it,
            SphereNet.Core.Enums.ItemTrigger.Create,
            new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });
        return (world, dispatcher);
    }

    private static string Read(ObjBase o, string key)
    {
        Assert.True(o.TryGetProperty(key, out string v), $"{key} did not answer");
        return v;
    }

    /// <summary>The shapes the packs write, through a real @Create block. MORE1 and
    /// MORE2 read back in hex, which is the engine's format for them, not the
    /// assignment going astray.</summary>
    [Fact]
    public void AnItemWorksOutItsOwnValues()
    {
        var (world, _) = Pack(
            "[ITEMDEF 013bb]", "DEFNAME=i_probe_arith", "TYPE=t_armor",
            "ON=@Create",
            "MOREX=30",
            "MORE2=<MOREX>/3",     // the pack's own division
            "MORE1=3+4",
            "MOREY=(2|8)",         // bitwise or, how the damage-type flags are written
            "COLOR=010+1",         // leading zero is hex: 0x10 + 1
            "AMOUNT=2*3");

        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x13BB);

        Assert.Equal(10, Convert.ToInt32(Read(it, "MORE2"), 16));
        Assert.Equal(7, Convert.ToInt32(Read(it, "MORE1"), 16));
        Assert.Equal("10", Read(it, "MOREY"));
        Assert.Equal("011", Read(it, "COLOR"));   // 17, read back as Sphere hex
        Assert.Equal("6", Read(it, "AMOUNT"));
    }

    /// <summary>The same on a character, which reaches the object through the same
    /// assignment layer.</summary>
    [Fact]
    public void ACharacterWorksOutItsOwnValues()
    {
        var (world, dispatcher) = Pack(
            "[CHARDEF 012]", "DEFNAME=c_probe_arith", "NAME=probe",
            "ON=@Create",
            "STR=3+4",
            "FAME=30/3",
            "TACTICS=29.0");          // NOT arithmetic - the skill scale must survive

        SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = npc =>
            dispatcher.FireCharTrigger(npc, SphereNet.Core.Enums.CharTrigger.Create,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
        try
        {
            var gem = world.CreateItem();
            world.PlaceItem(gem, new Point3D(100, 100, 0, 0));
            var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };
            var ch = spawn.SpawnSpecific(0x12);
            Assert.NotNull(ch);

            Assert.Equal("7", Read(ch!, "STR"));
            Assert.Equal("10", Read(ch, "FAME"));
            Assert.Equal("290", Read(ch, "TACTICS"));
        }
        finally
        {
            SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = null;
        }
    }

    /// <summary>What counts as arithmetic. Everything a text key could carry must fall
    /// outside it - this is the whole safety of deciding on the value's shape rather
    /// than on the key.</summary>
    [Fact]
    public void OnlyNumbersAndOperatorsCount()
    {
        Assert.True(ScriptArithmetic.IsPlainArithmetic("3+4"));
        Assert.True(ScriptArithmetic.IsPlainArithmetic("30 / 3"));
        Assert.True(ScriptArithmetic.IsPlainArithmetic("(2|8)"));
        Assert.True(ScriptArithmetic.IsPlainArithmetic("010+1"));
        Assert.True(ScriptArithmetic.IsPlainArithmetic("60*60*24*5"));
        Assert.True(ScriptArithmetic.IsPlainArithmetic("-5+3"));

        Assert.False(ScriptArithmetic.IsPlainArithmetic("100"));          // no operator
        Assert.False(ScriptArithmetic.IsPlainArithmetic("29.0"));         // skill decimal
        Assert.False(ScriptArithmetic.IsPlainArithmetic("{100 200}"));    // BraceRange owns it
        Assert.False(ScriptArithmetic.IsPlainArithmetic("-5,-3"));        // a coordinate list
        Assert.False(ScriptArithmetic.IsPlainArithmetic("i_sword+1"));    // a defname
        Assert.False(ScriptArithmetic.IsPlainArithmetic("e_foo"));
        Assert.False(ScriptArithmetic.IsPlainArithmetic("John Smith"));
        Assert.False(ScriptArithmetic.IsPlainArithmetic("3+"));           // unfinished
        Assert.False(ScriptArithmetic.IsPlainArithmetic("(3+4"));         // unbalanced
        Assert.False(ScriptArithmetic.IsPlainArithmetic(""));
    }

    /// <summary>A coordinate is a list upstream parses itself, and a name is text. Both
    /// keep their value whatever it looks like, so a P that resolved to a single number
    /// is not mistaken for a placement.</summary>
    [Fact]
    public void TheTextKeysAreLeftAlone()
    {
        var (world, _) = Pack(
            "[ITEMDEF 013bc]", "DEFNAME=i_probe_text", "TYPE=t_normal",
            "ON=@Create",
            "NAME=3+4",
            "P=120,130,0");

        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x13BC);

        Assert.Equal("3+4", Read(it, "NAME"));
        Assert.Equal(new Point3D(120, 130, 0, 0), it.Position);
    }
}
