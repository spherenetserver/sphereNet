using SphereNet.Core.Enums;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Parsing;
using Xunit;
using Xunit.Abstractions;
using GameArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// The script hot path stopped allocating for things that never change, and these
/// pin the behaviour each of those shortcuts assumes. They are here because the
/// shortcuts are invisible: every one of them produces the same answer as the code
/// it replaced, right up until it does not, and none of them would fail loudly.
///
/// The cost they are about is real. A live pack's minigame arena runs a 10-line
/// @Timer on 870 ground items at TIMERD 1 — 8,700 executions a second — and before
/// this it allocated 6.6 KB per execution: an upper-cased copy of every command
/// key, a diagnostic label nothing read with scriptdebug off, four closures per
/// resolved &lt;X&gt; and per IF, a copy of every loop body, and a LINQ chain over
/// the sector walk. That is 57 MB/s of garbage from one script.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptHotPathAllocationTests(ITestOutputHelper output)
{
    private static void Load(ScriptRuntimeStack stack, string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"hotpath-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, text);
        try { stack.Resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
    }

    // ---------------------------------------------------------------- ScriptKey

    [Fact]
    public void TheCachedUpperKeyFollowsTheKeyWhenTheLineIsReparsed()
    {
        var key = new ScriptKey();
        key.Parse("timerd 1".AsSpan());
        Assert.Equal("TIMERD", key.KeyUpper);

        // A ScriptKey instance is reused by the parser, so the cache has to be
        // dropped with the key it was computed from. Holding a stale upper-case
        // command would run the PREVIOUS line's verb — silently, and only for
        // whichever key happened to be recycled.
        key.Parse("remove".AsSpan());
        Assert.Equal("REMOVE", key.KeyUpper);
        Assert.Equal(key.Key.ToUpperInvariant(), key.KeyUpper);

        // The ++ / -- rewrite takes its own path through the parser and must also
        // land on the property rather than the backing field.
        key.Parse("tag.count ++".AsSpan());
        Assert.Equal("TAG.COUNT", key.KeyUpper);
    }

    // ------------------------------------------------------- resolver context

    [Fact]
    public void NestedResolutionKeepsEachFramesOwnTarget()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        Load(stack, """
            [FUNCTION f_inner_name]
            RETURN <NAME>

            [ITEMDEF 0401a]
            DEFNAME=i_hotpath_outer
            NAME=outer

            ON=@Probe
            TAG.seen=<f_inner_name>
            TAG.mine=<NAME>
            """);
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x401a;

        stack.Dispatcher.FireItemTriggerByName(item, "Probe", new GameArgs { ItemSrc = item });

        // The variable resolver is no longer a closure built per call; it reads
        // interpreter fields that the push/pop pair saves and restores. If a nested
        // frame failed to restore its caller's context, the line AFTER the nested
        // call would resolve against the callee's object instead of its own.
        Assert.True(item.TryGetProperty("TAG.seen", out string seen));
        Assert.True(item.TryGetProperty("TAG.mine", out string mine));
        output.WriteLine($"inner saw '{seen}', the caller then saw '{mine}'");
        Assert.Equal("outer", mine);
    }

    // ------------------------------------------------------ conditional split

    [Theory]
    // No '|' and no '&': the fast path, which skips the || / && splitter.
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("(2 == 2)", true)]
    [InlineData("!(2 == 3)", true)]
    // A single '&' or '|' is the BITWISE operator, not a logical split — these go
    // through the splitter exactly as they did before, and must not be split.
    [InlineData("(6 & 4)", true)]
    [InlineData("(6 & 1)", false)]
    [InlineData("(4 | 0)", true)]
    // Genuine logical operators still split and short-circuit.
    [InlineData("(1 == 2) || (3 == 3)", true)]
    [InlineData("(1 == 1) && (3 == 4)", false)]
    [InlineData("(1 == 1) && (3 == 3)", true)]
    public void TheConditionalFastPathAgreesWithTheSplitter(string expr, bool expected)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        Assert.Equal(expected, stack.Interpreter.Expressions.EvaluateConditional(expr));
    }

    // ---------------------------------------------------------- loop body view

    [Fact]
    public void ALoopBodyRunsTheSameLinesWhenItIsAViewRatherThanACopy()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        Load(stack, """
            [ITEMDEF 0401b]
            DEFNAME=i_hotpath_loop
            NAME=loop

            ON=@Probe
            LOCAL.total=0
            FOR 1 4
              LOCAL.total=<EVAL <LOCAL.total>+<DLOCAL._FOR>>
            ENDFOR
            TAG.total=<LOCAL.total>
            """);
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x401b;

        stack.Dispatcher.FireItemTriggerByName(item, "Probe", new GameArgs { ItemSrc = item });

        // The body is no longer copied into a fresh array per iteration; it is a
        // window onto the cached trigger body. Off-by-one in the window's start or
        // count would run the ENDFOR, or drop the first line of the body.
        Assert.True(item.TryGetProperty("TAG.total", out string total));
        output.WriteLine($"FOR 1 4 summed to {total}");
        Assert.Equal("10", total);
    }

    // ------------------------------------------------------ radius query shape

    [Fact]
    public void TheRadiusQueryFastPathMatchesTheExpressionPath()
    {
        var world = TestHarness.CreateWorld();
        var centre = new SphereNet.Core.Types.Point3D(300, 300, 0, 0);

        var here = world.CreateItem();
        here.BaseId = 0x0EED;
        world.PlaceItem(here, centre);
        var oneAway = world.CreateItem();
        oneAway.BaseId = 0x0EED;
        world.PlaceItem(oneAway, new SphereNet.Core.Types.Point3D(301, 300, 0, 0));
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, centre);

        // "0" takes the plain-integer fast path; "<EVAL 0>" cannot, and falls
        // through to the expression parser. Both must select the same objects in
        // the same order — items first, then characters, which is the order the
        // OrderBy in the old LINQ chain produced and which scripts that read the
        // first result depend on.
        var fast = SphereNet.Game.Scripting.ScriptObjectQueries.Query(
            world, here, "FORITEMS", "0", null);
        var slow = SphereNet.Game.Scripting.ScriptObjectQueries.Query(
            world, here, "FORITEMS", "<EVAL 0>", null);
        Assert.Equal(fast.Select(o => ((ObjBase)o).Uid), slow.Select(o => ((ObjBase)o).Uid));
        Assert.Equal([here.Uid], fast.Select(o => ((ObjBase)o).Uid));

        var objs = SphereNet.Game.Scripting.ScriptObjectQueries.Query(
            world, here, "FOROBJS", "1", null);
        var uids = objs.Select(o => ((ObjBase)o).Uid).ToArray();
        output.WriteLine($"FOROBJS 1 returned {uids.Length}: " +
                         string.Join(", ", objs.Select(o => o is Item ? "item" : "char")));
        Assert.Equal(3, uids.Length);
        Assert.All(objs.Take(2), o => Assert.IsType<Item>(o));
        Assert.IsType<SphereNet.Game.Objects.Characters.Character>(objs[2]);

        // A negative radius selects nothing on either path.
        Assert.Empty(SphereNet.Game.Scripting.ScriptObjectQueries.Query(
            world, here, "FORITEMS", "<EVAL 0-1>", null));
    }
}
