using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// ACT is a reference HEAD on a character, not just a uid to read.
///
/// Upstream dereferences it when it is written with a dot and says so in as many
/// words - "only used as a ref!" (CHR_ACT, CChar.cpp:2211-2215). Everything a
/// script does to a freshly created object goes through it: SERV.NEWITEM sets ACT
/// and the lines after it are src.act.p, src.act.type, src.act.amount. With no
/// reference head those lines resolve to nothing at all, so the item appears
/// unplaced and unconfigured and nothing reports a problem - the shipped newbie
/// script alone writes sixteen hundred of them.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ActReferenceHeadTests
{
    [Theory]
    [InlineData("SRC.ACT.NAME")]
    [InlineData("SRC.ACT.LINK.NAME")]
    [InlineData("SRC.TOPOBJ.ACT.NAME")]
    public void InterpreterReadsActThroughSourceAndChainedReferences(string expression)
    {
        var (world, ch) = Setup();
        var actual = world.CreateItem();
        actual.Name = "selected item";
        actual.Link = actual.Uid;
        ch.TrySetProperty("ACT", $"0{actual.Uid.Value:X}");
        var unrelated = world.CreateItem();
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Interpreter.Execute([
            new SphereNet.Scripting.Parsing.ScriptKey("TAG.RESULT", $"<{expression}>")],
            unrelated, null, new SphereNet.Scripting.Execution.TriggerArgs(ch),
            new SphereNet.Scripting.Execution.ScriptScope());
        unrelated.TryGetTag("RESULT", out var result);
        Assert.Equal("selected item", result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActCommandChangesOnlyTheSelectedObject(bool hasTarget)
    {
        var (world, ch) = Setup();
        var actual = world.CreateItem();
        actual.Name = "original";
        if (hasTarget) ch.TrySetProperty("ACT", $"0{actual.Uid.Value:X}");
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Interpreter.Execute([
            new SphereNet.Scripting.Parsing.ScriptKey("SRC.ACT.NAME", "changed")],
            ch, null, new SphereNet.Scripting.Execution.TriggerArgs(ch),
            new SphereNet.Scripting.Execution.ScriptScope());
        Assert.Equal(hasTarget ? "changed" : "original", actual.Name);
        Assert.NotEqual("changed", ch.Name);
    }

    [Fact]
    public void DeletedActTargetCannotBeReadOrModifiedThroughAChain()
    {
        var (world, ch) = Setup();
        var actual = world.CreateItem();
        actual.Name = "original";
        ch.TrySetProperty("ACT", $"0{actual.Uid.Value:X}");
        world.DeleteObject(actual);
        Assert.True(ch.TryGetProperty("ACT.NAME", out var name));
        Assert.Equal("0", name);
        ScriptTestBootstrap.CreateRuntimeStack().Interpreter.Execute([
            new SphereNet.Scripting.Parsing.ScriptKey("SRC.ACT.NAME", "changed")],
            ch, null, new SphereNet.Scripting.Execution.TriggerArgs(ch),
            new SphereNet.Scripting.Execution.ScriptScope());
        Assert.Equal("original", actual.Name);
        Assert.NotEqual("changed", ch.Name);
    }

    [Fact]
    public void InterpreterActReadsTheCharacterTargetInsteadOfTriggerObject2()
    {
        var (world, ch) = Setup();
        var actual = world.CreateItem();
        actual.Name = "actual target";
        var unrelated = world.CreateItem();
        unrelated.Name = "unrelated trigger object";
        ch.TrySetProperty("ACT", $"0{actual.Uid.Value:X}");
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Interpreter.ResolveObjectRef = (obj, head) => (obj as ObjBase)?.ResolveScriptRefHead(head);
        stack.Interpreter.Execute([
            new SphereNet.Scripting.Parsing.ScriptKey("TAG.ACTUID", "<ACT>"),
            new SphereNet.Scripting.Parsing.ScriptKey("TAG.ACTNAME", "<ACT.NAME>")],
            ch, null, new SphereNet.Scripting.Execution.TriggerArgs { Object2 = unrelated },
            new SphereNet.Scripting.Execution.ScriptScope());
        ch.TryGetProperty("TAG.ACTUID", out var uid);
        ch.TryGetProperty("TAG.ACTNAME", out var name);
        Assert.Equal($"0{actual.Uid.Value:X}", uid);
        Assert.Equal("actual target", name);
    }

    private static (GameWorld World, SphereNet.Game.Objects.Characters.Character Ch) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    [Fact]
    public void ActDereferencesToTheObjectItPointsAt()
    {
        var (world, ch) = Setup();
        var made = world.CreateItem();
        made.BaseId = 0x0EED;
        world.PlaceItem(made, new Point3D(101, 100, 0, 0));
        Assert.True(ch.TrySetProperty("ACT", $"0{made.Uid.Value:X}"));

        var target = ch.ResolveRefHead("ACT");

        Assert.NotNull(target);
        Assert.Same(made, target);
    }

    [Fact]
    public void AnUnsetActDereferencesToNothing()
    {
        // Not to the character itself, and not to some stale object: a script that
        // writes act.p before anything set ACT must change nothing.
        var (_, ch) = Setup();
        Assert.Null(ch.ResolveRefHead("ACT"));
    }

    [Fact]
    public void TheBareWordStillReadsAsAUid()
    {
        // Upstream only dereferences the dotted form; <SRC.ACT> stays a uid.
        var (world, ch) = Setup();
        var made = world.CreateItem();
        world.PlaceItem(made, new Point3D(101, 100, 0, 0));
        ch.TrySetProperty("ACT", $"0{made.Uid.Value:X}");

        Assert.True(ch.TryGetProperty("ACT", out string uid));
        Assert.Equal($"0{made.Uid.Value:X}", uid);
    }

    [Fact]
    public void TheSharedHeadsStillResolve()
    {
        // Adding one head must not shadow the base ones.
        var (_, ch) = Setup();
        Assert.Same(ch, ch.ResolveRefHead("TOPOBJ"));
        Assert.Null(ch.ResolveRefHead("NOT_A_HEAD"));
    }
}
