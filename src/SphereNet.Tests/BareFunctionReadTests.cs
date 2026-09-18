using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// A bare &lt;f_something&gt; CALLS the [FUNCTION], it is not a name to look up.
///
/// Upstream does this inside r_WriteVal: a key the object's own table does not name is
/// looked up with r_GetFunctionIndex and called (CObjBase.cpp:971, CScriptObj.cpp:1481),
/// before anything treats the word as a constant. Here only the SERV branch did that,
/// so a bare call fell through to the defname fallback - and a FUNCTION *is* a named
/// resource, so that fallback answered with its resource index.
///
/// The read therefore did not fail quietly: it came back as a large non-zero number,
/// which is TRUE. Every guard written IF (&lt;f_x&gt;) passed and every IF !(&lt;f_x&gt;)
/// failed, regardless of what the function would have said.
///
/// The shipped pack gates clothing on exactly that shape - type_equipitem.scp asks
/// IF ((&lt;CanUse&gt;&amp;can_u_human) &amp;&amp; !&lt;SRC.f_isHuman&gt;) and answers
/// "You cannot equip that." - so no human could put on a single piece of clothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BareFunctionReadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_bf_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed record Bench(ScriptRuntimeStack Stack,
                                SphereNet.Game.Objects.Characters.Character Ch);

    /// <summary>Load the script, wire the host the way the server does, and hand back
    /// a character with a human body.</summary>
    private Bench Build(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "f.scp");
        File.WriteAllLines(file, lines);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.ScpBaseDir = _dir;
        stack.Resources.LoadResourceFile(file);
        new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();

        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        stack.Interpreter.ServerPropertyResolver = p => (string?)resolve.Invoke(null, [p]);
        typeof(SphereNet.Server.Program)
            .GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, stack.Resources);

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        typeof(SphereNet.Server.Program)
            .GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, world);

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x190;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return new Bench(stack, ch);
    }

    /// <summary>The same read, but with SRC set - which is how the packs reach a
    /// function: &lt;SRC.f_isHuman&gt;, not &lt;f_isHuman&gt;. Reading without a source
    /// measures the harness, not the engine: every SRC.x answers "0" then, including
    /// SRC.BODY.</summary>
    private static string ReadWithSource(Bench b, string expr, ObjBase src)
    {
        var item = b.Stack.Interpreter;
        item.Execute([new ScriptKey("TAG.OUT", expr)], b.Ch, null,
            new TriggerArgs { Source = src },
            new ScriptScope());
        b.Ch.TryGetProperty("TAG.OUT", out string v);
        return v;
    }

    private static string Read(Bench b, string expr)
    {
        b.Stack.Interpreter.Execute([new ScriptKey("TAG.OUT", expr)], b.Ch, null,
            new TriggerArgs(), new ScriptScope());
        b.Ch.TryGetProperty("TAG.OUT", out string v);
        return v;
    }

    private static readonly string[] IsHuman =
    [
        "[CHARDEF 0190]", "DEFNAME=c_man", "NAME=Man", "",
        "[CHARDEF 0191]", "DEFNAME=c_woman", "NAME=Woman", "",
        "[FUNCTION f_isHuman]",
        "IF (<BODY>==c_man) || (<BODY>==c_woman)",
        "\tRETURN 1",
        "ENDIF",
        "RETURN 0",
        "",
        "[FUNCTION f_always_zero]",
        "RETURN 0",
    ];


    /// <summary>The shape the pack's clothing gate is built on.</summary>
    [Fact]
    public void ABareFunctionIsCalled()
    {
        var b = Build(IsHuman);
        Assert.Equal("1", Read(b, "<f_isHuman>"));
    }

    /// <summary>The half that made it dangerous: a function that answers NO has to
    /// come back as 0, not as the resource index of its own name.</summary>
    [Fact]
    public void AFunctionThatAnswersNoReadsZero()
    {
        var b = Build(IsHuman);
        Assert.Equal("0", Read(b, "<f_always_zero>"));
        Assert.Equal("0", Read(b, "<EVAL <f_always_zero>>"));
    }

    /// <summary>And the negation the pack actually writes.</summary>
    [Fact]
    public void TheNegatedGuardAnswersCorrectly()
    {
        var b = Build(IsHuman);
        Assert.Equal("0", Read(b, "<EVAL !(<f_isHuman>)>"));
        Assert.Equal("1", Read(b, "<EVAL !(<f_always_zero>)>"));
    }

    /// <summary>A name that is not a function keeps the old answer - the defname
    /// fallback still resolves a real constant.</summary>
    [Fact]
    public void ADefnameConstantStillResolves()
    {
        var b = Build(IsHuman);
        Assert.Equal("400", Read(b, "<c_man>"));   // 0x190
        Assert.Equal("1", Read(b, "<EVAL <BODY>==c_man>"));
    }

    /// <summary>A name that is neither reads as nothing.</summary>
    [Fact]
    public void AnUnknownNameStillReadsZero()
    {
        var b = Build(IsHuman);
        Assert.Equal("0", Read(b, "<f_no_such_function>"));
    }

    /// <summary>The spelling the pack actually writes.
    ///
    /// SRC.&lt;name&gt; was read as a property and nothing else, so a [FUNCTION] reached
    /// through SRC was never called. Upstream resolves the reference and then runs
    /// r_WriteVal on it, which falls through to r_GetFunctionIndex and calls it
    /// (CObjBase.cpp:971). The clothing gate asks !&lt;SRC.f_isHuman&gt;, so every human
    /// was refused every garment - the bare-name fix alone did not reach this.</summary>
    [Fact]
    public void AFunctionReachedThroughSrcIsCalled()
    {
        var b = Build(IsHuman);

        // Calibration first: if SRC itself does not resolve here, the rest measures
        // nothing.
        Assert.Equal("0190", ReadWithSource(b, "<SRC.BODY>", b.Ch));

        Assert.Equal("1", ReadWithSource(b, "<SRC.f_isHuman>", b.Ch));
        Assert.Equal("0", ReadWithSource(b, "<SRC.f_always_zero>", b.Ch));
    }

    /// <summary>And the negation the gate is built on.</summary>
    [Fact]
    public void TheClothingGateNoLongerRefusesAHuman()
    {
        var b = Build(IsHuman);
        Assert.Equal("0", ReadWithSource(b, "<EVAL !(<SRC.f_isHuman>)>", b.Ch));
    }

    /// <summary>A property still wins over a function of the same name, and an unknown
    /// name still reads 0 rather than something invented.</summary>
    [Fact]
    public void APropertyStillWinsAndUnknownStillReadsZero()
    {
        var b = Build(IsHuman);
        Assert.Equal("0190", ReadWithSource(b, "<SRC.BODY>", b.Ch));
        Assert.Equal("0", ReadWithSource(b, "<SRC.f_no_such_function>", b.Ch));
    }
}
