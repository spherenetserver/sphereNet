using Microsoft.Extensions.Logging;
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
/// The Source-X script profiler (EF_Script_Profiler, CScriptProfiler): calls, total,
/// min, max and average per function and per @trigger, plus the self time (without
/// nested calls) and the per-file totals SphereNet adds.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptProfilerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_prof_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void OffItRecordsNothing()
    {
        var profiler = new ScriptProfiler();

        Assert.False(profiler.Begin("f_x", isTrigger: false, null));
        Assert.Empty(profiler.Functions);
        Assert.Contains("off", profiler.Report()[0]);
    }

    [Fact]
    public void ANestedCallIsTakenOutOfItsCallersSelfTime()
    {
        var profiler = new ScriptProfiler { Enabled = true };

        Assert.True(profiler.Begin("F_Outer arg1", isTrigger: false, "a.scp"));
        Thread.Sleep(20);
        Assert.True(profiler.Begin("@Death", isTrigger: true, "b.scp"));
        Thread.Sleep(40);
        profiler.End();
        profiler.End();

        var outer = profiler.Functions.Single();
        var death = profiler.Triggers.Single();
        Assert.Equal("f_outer", outer.Name);
        Assert.Equal("@death", death.Name);
        Assert.True(outer.TotalMs >= 55, $"outer total {outer.TotalMs}");
        Assert.True(outer.SelfMs < outer.TotalMs - 30, $"outer self {outer.SelfMs} of {outer.TotalMs}");
        Assert.True(death.SelfMs >= 35, $"death self {death.SelfMs}");
        Assert.Equal(2, profiler.Files.Count);
    }

    [Fact]
    public void TheReportUsesTheSourceXLinesAndClearForgetsThem()
    {
        var profiler = new ScriptProfiler { Enabled = true };
        profiler.Begin("f_fast", false, "a.scp"); profiler.End();
        profiler.Begin("f_slow", false, "a.scp"); Thread.Sleep(15); profiler.End();

        var report = profiler.Report();

        Assert.StartsWith("Scripts: called 2 times", report[0]);
        Assert.Contains(report, l => l.StartsWith("FUNCTION 'f_slow' called 1 times, took ", StringComparison.Ordinal));
        Assert.DoesNotContain(report, l => l.StartsWith("FUNCTION 'f_fast'", StringComparison.Ordinal));
        Assert.Equal("Report complete!", report[^1]);

        profiler.Clear();
        Assert.Empty(profiler.Functions);
        Assert.Equal("Script profiler is not yet informational", profiler.Report().Single());
    }

    [Fact]
    public void RealScriptFunctionsAndTriggersAreTimedPerFile()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "prof.scp");
        File.WriteAllLines(file,
        [
            "[FUNCTION f_prof_inner]",
            "LOCAL.X=1",
            "[FUNCTION f_prof_outer]",
            "f_prof_inner",
            "f_prof_inner",
            "[ITEMDEF 013bb]", "DEFNAME=i_prof_probe", "TYPE=t_normal",
            "ON=@Create",
            "f_prof_inner",
        ]);

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        // As the server wires it (Program.EngineWiring): a script's call to a function.
        interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
            runner.TryRunFunction(name, target, source, args, scope, out var r) ? r : SphereNet.Core.Enums.TriggerResult.Default;
        interpreter.CallFunction = (name, target, source, args) =>
            runner.TryRunFunction(name, target, source, args, out var r) ? r : SphereNet.Core.Enums.TriggerResult.Default;
        runner.Profiler.Enabled = true;
        runner.Profiler.BaseDirectory = _dir;
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.CreateTriggerHook = it => dispatcher.FireItemTrigger(it,
            SphereNet.Core.Enums.ItemTrigger.Create, new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });

        var item = world.CreateItem();
        runner.RunFunction("f_prof_outer", item, null, null);
        var probe = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(probe, 0x13BB);

        var functions = runner.Profiler.Functions.ToDictionary(f => f.Name);
        Assert.Equal(1, functions["f_prof_outer"].Called);
        Assert.Equal(3, functions["f_prof_inner"].Called);
        Assert.Contains(runner.Profiler.Triggers, t => t.Name == "@create");
        Assert.Contains(runner.Profiler.Files, f => f.Name.EndsWith("prof.scp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(runner.Profiler.Report(), l => l.Contains("prof.scp: self", StringComparison.Ordinal));
    }
}
