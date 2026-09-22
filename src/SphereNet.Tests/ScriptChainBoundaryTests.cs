using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;
using GameArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ScriptChainBoundaryTests
{
    private static void Load(ScriptRuntimeStack stack, string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"chain-boundary-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, text);
        try { stack.Resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
    }

    private static void Fire(ScriptRuntimeStack stack, ObjBase subject, GameArgs args)
    {
        if (subject is Character ch) stack.Dispatcher.FireCharTriggerByName(ch, "Probe", args);
        else stack.Dispatcher.FireItemTriggerByName((Item)subject, "Probe", args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventChainSharesUnseededLocalRefFloatAndArgoChanges(bool character)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        Load(stack, """
            [EVENTS e_first]
            ON=@Probe
            LOCAL.marker=42
            FLOAT.marker=1.25
            REF1=<UID>
            ARGS=7
            RETURN 0
            [EVENTS e_second]
            ON=@Probe
            TAG.local=<LOCAL.marker>
            TAG.float=<FLOAT.marker>
            TAG.ref=<REF1>
            TAG.argo=<ARGO>
            TAG.n=<ARGN1>
            RETURN 0
            """);
        var world = TestHarness.CreateWorld();
        ObjBase subject = character ? world.CreateCharacter() : world.CreateItem();
        var events = subject is Character ch ? ch.Events : ((Item)subject).Events;
        events.Add(stack.Resources.ResolveDefName("e_first")); events.Add(stack.Resources.ResolveDefName("e_second"));
        var args = new GameArgs { O1 = subject };
        Fire(stack, subject, args);
        void Tag(string key, string expected) { subject.TryGetProperty("TAG." + key, out var value); Assert.Equal(expected, value); }
        Tag("local", "42"); Tag("float", "1.25"); Tag("ref", $"0{subject.Uid.Value:X}"); Tag("argo", "0"); Tag("n", "7");
        Assert.Null(args.O1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EventMutationIsVisibleDuringCurrentDispatch(bool character, bool removeSelf)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        Load(stack, $$"""
            [EVENTS e_first]
            ON=@Probe
            TAG.first=<EVAL <TAG0.first>+1>
            EVENTS={{(removeSelf ? "-e_first" : "+e_added")}}
            RETURN 0
            [EVENTS e_second]
            ON=@Probe
            TAG.second=1
            RETURN 0
            [EVENTS e_added]
            ON=@Probe
            TAG.added=1
            RETURN 0
            """);
        var world = TestHarness.CreateWorld(); ObjBase subject = character ? world.CreateCharacter() : world.CreateItem();
        var events = subject is Character ch ? ch.Events : ((Item)subject).Events;
        var first = stack.Resources.ResolveDefName("e_first");
        events.Add(first); events.Add(stack.Resources.ResolveDefName("e_second"));
        Fire(stack, subject, new GameArgs());
        subject.TryGetProperty("TAG.first", out var count); Assert.Equal("1", count);
        subject.TryGetProperty("TAG.second", out var second); Assert.Equal("1", second);
        if (removeSelf) Assert.DoesNotContain(first, events);
        else { subject.TryGetProperty("TAG.added", out var added); Assert.Equal("1", added); }
    }

    [Theory]
    [InlineData("EVENTS")]
    [InlineData("DSPEECH")]
    public void ResourceListsAppendAndApplyEachRemovalInsteadOfReplacingEverything(string property)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string kind = property == "EVENTS" ? "EVENTS" : "SPEECH";
        Load(stack, $"[{kind} e_one]\n[{kind} e_two]\n[{kind} e_three]\n");
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter();
        var list = property == "EVENTS" ? ch.Events : ch.DSpeech;
        Assert.True(ch.TrySetProperty(property, "e_one"));
        Assert.True(ch.TrySetProperty(property, "e_two,e_three"));
        Assert.Equal(3, list.Count);
        Assert.True(ch.TrySetProperty(property, "-e_two,+e_one")); Assert.Equal(2, list.Count);
        Assert.DoesNotContain(stack.Resources.ResolveDefName("e_two"), list);
        Assert.True(ch.TrySetProperty(property, "-*,+e_three"));
        Assert.Equal(stack.Resources.ResolveDefName("e_three"), Assert.Single(list));
        Assert.True(ch.TrySetProperty(property, "-0")); Assert.Empty(list);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedPendingEventDoesNotRunFromOldSnapshot(bool character)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        Load(stack, """
            [EVENTS e_first]
            ON=@Probe
            IF <TAG0.changed> == 0
            TAG.changed=1
            EVENTS=-e_second
            ENDIF
            RETURN 0
            [EVENTS e_second]
            ON=@Probe
            TAG.removed_ran=1
            RETURN 0
            """);
        var world = TestHarness.CreateWorld(); ObjBase subject = character ? world.CreateCharacter() : world.CreateItem();
        var events = subject is Character ch ? ch.Events : ((Item)subject).Events;
        events.Add(stack.Resources.ResolveDefName("e_first")); events.Add(stack.Resources.ResolveDefName("e_second"));
        Fire(stack, subject, new GameArgs());
        subject.TryGetProperty("TAG0.removed_ran", out var removed); Assert.Equal("0", removed);
        Assert.Single(events);
    }

    [Theory]
    [InlineData(SphereNet.Core.Configuration.SaveFormat.Text)]
    [InlineData(SphereNet.Core.Configuration.SaveFormat.Binary)]
    public void EditedResourceListsSurviveSaveLoad(SphereNet.Core.Configuration.SaveFormat format)
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        Load(stack, "[EVENTS e_one]\n[EVENTS e_two]\n[TYPEDEF t_three]\n[SPEECH sp_one]\n[SPEECH sp_two]\n");
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter(); ch.BaseId = 0x190;
        world.PlaceCharacter(ch, new Point3D(100, 100));
        ch.TrySetProperty("EVENTS", "e_one,e_two"); ch.TrySetProperty("EVENTS", "-e_one,+t_three");
        ch.TrySetProperty("DSPEECH", "sp_one,sp_two"); ch.TrySetProperty("DSPEECH", "-sp_one");
        var expectedEvents = ch.Events.ToArray(); var expectedSpeech = ch.DSpeech.ToArray();
        var item = world.CreateItem(); item.BaseId = 0xEED;
        world.PlaceItem(item, new Point3D(100, 101));
        item.TrySetProperty("EVENTS", "e_two,t_three");
        string path = Path.Combine(Path.GetTempPath(), $"chain-save-{Guid.NewGuid():N}");
        try
        {
            Assert.True(new SphereNet.Persistence.Save.WorldSaver(logs)
            {
                Format = format,
                ResolveResourceName = rid => stack.Resources.GetResource(rid)?.DefName
            }.Save(world, path));
            var loaded = TestHarness.CreateWorld(); new SphereNet.Persistence.Load.WorldLoader(logs).Load(loaded, path);
            var restored = loaded.FindChar(ch.Uid)!;
            Assert.Equal(expectedEvents, restored.Events); Assert.Equal(expectedSpeech, restored.DSpeech);
            Assert.Equal(expectedEvents, loaded.FindItem(item.Uid)!.Events);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData("ARGN")]
    [InlineData("ARGN1")]
    [InlineData("ARGN2")]
    [InlineData("ARGN3")]
    [InlineData("REF1")]
    public void EmptyScopeAssignmentsClearOldValues(string key)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack(); var world = TestHarness.CreateWorld(); var item = world.CreateItem();
        var args = new TriggerArgs { Number1 = 9, Number2 = 9, Number3 = 9 };
        var scope = new ScriptScope(); scope.SetRef(1, $"0{item.Uid.Value:X}");
        stack.Interpreter.Execute([new ScriptKey(key, "")], item, null, args, scope);
        Assert.Equal(0, key switch { "ARGN2" => args.Number2, "ARGN3" => args.Number3, "REF1" => long.Parse(scope.GetRef(1)), _ => args.Number1 });
    }

    [Theory]
    [InlineData("010", 16)]
    [InlineData("2+3", 5)]
    public void TryTimerAcceptsSphereNumberAndExpression(string value, int seconds)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack(); var world = TestHarness.CreateWorld(); var item = world.CreateItem();
        long before = Environment.TickCount64;
        stack.Interpreter.Execute([new ScriptKey("TRY", "TIMER " + value)], item, null, new TriggerArgs(), new ScriptScope());
        Assert.InRange(item.Timeout, before + seconds * 1000, Environment.TickCount64 + seconds * 1000);
    }
}
