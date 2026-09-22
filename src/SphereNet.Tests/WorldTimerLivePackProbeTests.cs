using System.Diagnostics;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Core.Enums;
using SphereNet.Persistence.Load;
using SphereNet.Scripting.Resources;
using Xunit.Abstractions;
using GameArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class WorldTimerLivePackProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void ProfileConfiguredSaveTimersWithoutExternalServices()
    {
        string? root = Environment.GetEnvironmentVariable("SPHERENET_TIMER_PROBE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        foreach (string path in ScriptResourceManifest.Resolve(Path.Combine(root, "scripts")))
            stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        stack.Dispatcher.BuildUsedTriggerCache();
        stack.Dispatcher.GlobalItemEvents.Add(DefinitionLoader.ResolveEventName("ei_generic_items", stack.Resources));
        ObjBase.RunScriptFunction = (obj, name, args, console) =>
        {
            var fnArgs = new SphereNet.Scripting.Execution.TriggerArgs { Source = console?.GetSourceChar() };
            fnArgs.InitFromRaw(args);
            return stack.Runner.TryRunFunction(name, obj, console, fnArgs, out _);
        };
        var world = TestHarness.CreateWorld();
        var loader = new WorldLoader(stack.LoggerFactory)
        {
            ResolveItemDefFullIndex = name => stack.Resources.ResolveDefName(name).Index,
            ResolveItemBase = index =>
            {
                var def = DefinitionLoader.GetItemDef(index);
                return def == null ? null : def.DispIndex > 0 ? def.DispIndex : (ushort)index;
            },
            ResolveItemDef = name =>
            {
                var rid = stack.Resources.ResolveDefName(name);
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def?.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            },
            ResolveCharDef = name => CharDefHelper.ResolveBodyId(
                CharDefHelper.ResolveDefIndex(name, stack.Resources), stack.Resources),
            ApplyCharDefFromName = (ch, name) => CharDefHelper.TryApplyDefName(ch, name, stack.Resources),
        };
        var counts = loader.Load(world, Path.Combine(root, "save"));
        output.WriteLine($"Loaded: {counts}");
        var measured = new Dictionary<uint, (long Bytes, int Calls, string Def)>();
        Item.OnTimerExpired = item =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Timer, new GameArgs { ItemSrc = item });
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            measured.TryGetValue(item.Uid.Value, out var previous);
            measured[item.Uid.Value] = (previous.Bytes + bytes, previous.Calls + 1,
                DefinitionLoader.GetItemDef(ItemDefHelper.ResolveInstanceDefIndex(item, stack.Resources))?.DefName ?? $"0{item.BaseId:X}");
            return result;
        };
        for (int tick = 0; tick < 8; tick++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            world.OnTickParallel();
            output.WriteLine($"tick={tick} ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2} allocKB={(GC.GetAllocatedBytesForCurrentThread() - before) / 1024}");
            Thread.Sleep(100);
        }
        foreach (var pair in measured.OrderByDescending(p => p.Value.Bytes).Take(20))
            output.WriteLine($"uid=0{pair.Key:X} def={pair.Value.Def} calls={pair.Value.Calls} allocKB={pair.Value.Bytes / 1024}");
        Assert.NotEmpty(world.GetAllObjects());
    }
}
