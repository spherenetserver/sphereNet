using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;
using Xunit;
using TriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ObjectLoopParityTests
{
    [Fact]
    public void NearbyLoopAllocationDoesNotGrowWithDistantWorldPopulation()
    {
        var world = TestHarness.CreateWorld();
        var subject = world.CreateItem(); world.PlaceItem(subject, new Point3D(100, 100));
        long Measure()
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++)
                Assert.Same(subject, Assert.Single(ScriptObjectQueries.Query(world, subject, "FORITEMS", "0", null)));
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Measure(); // Warm the query and assertions before measuring growth.
        long small = Measure();
        for (int i = 0; i < 20000; i++)
        {
            var far = world.CreateItem(); world.PlaceItem(far, new Point3D(2000, 2000));
        }
        long large = Measure();
        Assert.True(large <= small + 16384, $"Nearby queries grew from {small} to {large} bytes with distant items.");
    }

    [Fact]
    public void SectorQueryCrossesBoundariesAndSnapshotsBeforeScriptMutation()
    {
        var world = TestHarness.CreateWorld(); world.InitMap(1, 6144, 4096);
        var subject = world.CreateItem(); world.PlaceItem(subject, new Point3D(63, 63));
        var adjacent = world.CreateItem(); world.PlaceItem(adjacent, new Point3D(64, 64));
        var otherMap = world.CreateItem(); world.PlaceItem(otherMap, new Point3D(63, 63, 0, 1));
        var ch = world.CreateCharacter(); world.PlaceCharacter(ch, new Point3D(64, 63));
        var worn = world.CreateItem(); ch.Equip(worn, Layer.Shirt);
        var found = ScriptObjectQueries.Query(world, subject, "FOROBJS", "1", null);
        Assert.Equal(3, found.Count);
        Assert.Contains(subject, found); Assert.Contains(adjacent, found);
        Assert.Same(ch, found[^1]);
        Assert.DoesNotContain(otherMap, found); Assert.DoesNotContain(worn, found);
        world.PlaceItem(adjacent, new Point3D(2000, 2000));
        Assert.Contains(adjacent, found);
        Assert.DoesNotContain(adjacent, ScriptObjectQueries.Query(world, subject, "FORITEMS", "1", null));
        Assert.Empty(ScriptObjectQueries.Query(world, subject, "FORITEMS", "-1", null));
        Assert.Equal(2, ScriptObjectQueries.Query(world, subject, "FORITEMS", "2147483647", null).Count);
    }

    private sealed class Console : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "server";
        public void SysMessage(string text) { }
    }

    [Theory]
    [InlineData("FORCHARS")]
    [InlineData("FORPLAYERS")]
    [InlineData("FORCLIENTS")]
    public void RadiusUsesTopObjectAndDefaultViewRange(string query)
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter(); ch.IsPlayer = true; ch.IsOnline = true;
        world.PlaceCharacter(ch, new Point3D(100, 100));
        var other = world.CreateCharacter(); other.IsPlayer = true; other.IsOnline = true;
        world.PlaceCharacter(other, new Point3D(110, 100));
        var item = world.CreateItem(); ch.Equip(item, Layer.Shirt);
        ITextConsole console = new Console();
        Assert.Contains(other, console.QueryScriptObjects(query, item, "", null));
        Assert.DoesNotContain(other, console.QueryScriptObjects(query, item, "0", null));
        Assert.Contains(other, console.QueryScriptObjects(query, item, "05+5", null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterpreterLoopsWithoutConnectedClient(bool delayed)
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter(); world.PlaceCharacter(ch, new Point3D(100, 100));
        var nearby = world.CreateItem(); world.PlaceItem(nearby, new Point3D(102, 100));
        var resources = Load(logs, """
            [FUNCTION f_scan]
            IF 1
            FORITEMS
            TAG.visited=1
            ENDFOR
            ENDIF
            """);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), logs.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, logs.CreateLogger<TriggerRunner>());
        if (delayed) new DelayedCallDispatcher(() => runner, null, new Console()).Run(ch, "f_scan", "");
        else Assert.True(runner.TryRunFunction("f_scan", ch, null, new TriggerArgs(), out _));
        Assert.True(nearby.TryGetProperty("TAG.visited", out var value)); Assert.Equal("1", value);
    }

    [Fact]
    public void ServerHookCanEnumerateGlobalTimersWithoutWorldObjectTarget()
    {
        var world = TestHarness.CreateWorld(); var item = world.CreateItem();
        item.AddTimerF(10000, "f_job", "");
        var type = typeof(SphereNet.Server.Program).GetNestedType("ServerHookContext", System.Reflection.BindingFlags.NonPublic)!;
        var target = (IScriptObj)Activator.CreateInstance(type, nonPublic: true)!;
        ITextConsole console = new Console();
        Assert.Same(item, Assert.Single(console.QueryScriptObjects("FORTIMERF", target, "f_job", null)));
        Assert.Empty(console.QueryScriptObjects("FORITEMS", target, "", null));
    }

    [Fact]
    public void ContainerLoopsMatchFullDefinitionAndSearchEquipmentAndNestedBags()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var resources = Load(logs, "[ITEMDEF i_loop_custom]\nID=0100\n[TYPEDEF t_loop_type]\n");
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter();
        var pack = world.CreateItem(); pack.ItemType = ItemType.Container; ch.Equip(pack, Layer.Pack);
        var nested = world.CreateItem(); nested.ItemType = ItemType.Container; Assert.True(pack.TryAddItem(nested));
        int id = resources.ResolveDefName("i_loop_custom").Index;
        var worn = world.CreateItem(); worn.BaseId = 0x100; worn.SetTag("SCRIPTDEF", id.ToString()); ch.Equip(worn, Layer.Shirt);
        var inside = world.CreateItem(); inside.BaseId = 0x100; inside.SetTag("SCRIPTDEF", id.ToString()); Assert.True(nested.TryAddItem(inside));
        var different = world.CreateItem(); different.BaseId = 0x100; Assert.True(pack.TryAddItem(different));
        var found = ScriptObjectQueries.Query(world, ch, "FORCONTID", "i_loop_custom", resources);
        Assert.Equal(2, found.Count); Assert.Contains(worn, found); Assert.Contains(inside, found); Assert.DoesNotContain(different, found);
        Assert.Same(worn, Assert.Single(ScriptObjectQueries.Query(world, ch, "FORCONTID", "i_loop_custom,0", resources)));
        Assert.Contains(inside, ScriptObjectQueries.Query(world, ch, "FORCONT", $"0{ch.Uid.Value:X},255", resources));
        Assert.Empty(ScriptObjectQueries.Query(world, ch, "FORCONT", $"0{ch.Uid.Value:X}", resources));
        nested.ItemType = ItemType.ContainerLocked;
        Assert.DoesNotContain(inside, ScriptObjectQueries.Query(world, ch, "FORCONTID", "i_loop_custom", resources));
    }

    [Fact]
    public void TypeLoopResolvesScriptAliasAndInstancesUseDecimalNumbers()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var resources = Load(logs, "[DEFNAME loop_types]\nloop_gold=017\n");
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter();
        var item = world.CreateItem(); item.BaseId = 256; item.ItemType = (ItemType)0x17; ch.Equip(item, Layer.Shirt);
        Assert.Same(item, Assert.Single(ScriptObjectQueries.Query(world, ch, "FORCONTTYPE", "loop_gold", resources)));
        Assert.Same(item, Assert.Single(ScriptObjectQueries.Query(world, ch, "FORINSTANCES", "256", resources)));
        Assert.Empty(ScriptObjectQueries.Query(world, ch, "FORINSTANCES", "257", resources));
    }

    [Fact]
    public void TimerLoopRetainsCommandIdentityAndGlobalInsertionOrder()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld(); var first = world.CreateItem(); var second = world.CreateItem();
        second.AddTimerF(10000, "f_job", "7", originalCommand: "f_job=7");
        first.AddTimerF(10000, "f_job", "7", originalCommand: "f_job,7");
        first.AddTimerF(10000, "f_job", "7", originalCommand: "f_job=7");
        second.AddTimerF(10000, "f_job", "7", originalCommand: "f_job=7");
        Assert.Equal(new IScriptObj[] { second, first, second }, first.QueryScriptObjects("FORTIMERF", "F_JOB=7", null));
        Assert.Empty(first.QueryScriptObjects("FORTIMERF", "f_job*", null));
        var interpreter = new ScriptInterpreter(new ExpressionParser(), logs.CreateLogger<ScriptInterpreter>());
        interpreter.Execute([
            new ScriptKey("IF", "1"), new ScriptKey("FORTIMERF", "f_job=7"),
            new ScriptKey("TAG.visits", "<EVAL <TAG0.visits>+1>"),
            new ScriptKey("ENDFOR", ""), new ScriptKey("ENDIF", "")], first, null, new TriggerArgs(), new ScriptScope());
        first.TryGetProperty("TAG.visits", out var a); second.TryGetProperty("TAG.visits", out var b);
        Assert.Equal("1", a); Assert.Equal("2", b);
    }

    [Fact]
    public void ObjectLoopPreservesTriggerArgoAndHonorsBreak()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld(); var owner = world.CreateItem(); var argument = world.CreateItem();
        owner.AddTimerF(10000, "f_job", ""); owner.AddTimerF(10000, "f_job", "");
        var interpreter = new ScriptInterpreter(new ExpressionParser(), logs.CreateLogger<ScriptInterpreter>());
        var args = new TriggerArgs { Object1 = argument };
        interpreter.Execute([new ScriptKey("FORTIMERF", "f_job"),
            new ScriptKey("ARGO.TAG.visits", "<EVAL <ARGO.TAG0.visits>+1>"),
            new ScriptKey("BREAK", ""), new ScriptKey("ENDFOR", "")], owner, null, args, new ScriptScope());
        Assert.Same(argument, args.Object1);
        argument.TryGetProperty("TAG.visits", out var visits); Assert.Equal("1", visits);
        owner.TryGetProperty("TAG0.visits", out var untouched); Assert.Equal("0", untouched);
    }

    [Fact]
    public void GarbageCounterAndLogDoNotClaimVetoedDeletion()
    {
        var world = TestHarness.CreateWorld(); var item = world.CreateItem(); item.BaseId = 0;
        world.ItemDeleteAllowed = _ => false;
        var messages = new List<string>(); var stats = world.GarbageCollection(messages.Add);
        Assert.Equal(0, stats.Deleted); Assert.False(item.IsDeleted);
        Assert.DoesNotContain(messages, m => m.Contains("deleted graphicless"));
        world.ItemDeleteAllowed = _ => true;
        Assert.Equal(1, world.GarbageCollection().Deleted);
    }

    [Fact]
    public void ClientArgoExposesClientSessionWithoutBecomingCharacter()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter();
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 905);
        TestHarness.AttachCharacter(client, ch); client.NetState.ClientVersion = "7.0.20.0";
        IScriptObj reference = client;
        client.NetState.ClientTypeFlag = 1;
        Assert.True(reference.TryGetProperty("CLIENTIS3D", out var is3d)); Assert.Equal("1", is3d);
        Assert.True(reference.TryGetProperty("CLIENTVERSION", out var version)); Assert.Equal("7.0.20.0", version);
        Assert.False(reference.TryGetProperty("STR", out _));
        var interpreter = new ScriptInterpreter(new ExpressionParser(), logs.CreateLogger<ScriptInterpreter>());
        interpreter.Execute([new ScriptKey("TAG.has_client", "<ARGO>"), new ScriptKey("TAG.client_version", "<ARGO.CLIENTVERSION>"),
            new ScriptKey("ARGO.CTAG.delete_seen", "1")], ch, new Console(), new TriggerArgs(ch) { Object1 = client }, new ScriptScope());
        ch.TryGetProperty("TAG.client_version", out var observed); Assert.Equal(version, observed);
        ch.TryGetProperty("TAG.has_client", out var hasClient); Assert.Equal("1", hasClient);
        Assert.Equal("1", ch.CTags.Get("delete_seen"));
    }

    private static ResourceHolder Load(ILoggerFactory logs, string script)
    {
        var resources = new ResourceHolder(logs.CreateLogger<ResourceHolder>());
        string path = Path.Combine(Path.GetTempPath(), $"object-loop-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        try { resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        return resources;
    }
}
