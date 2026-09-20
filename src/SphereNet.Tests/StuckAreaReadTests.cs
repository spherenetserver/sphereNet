using System.Reflection;
using SphereNet.Core.Types;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class StuckAreaReadTests(ITestOutputHelper output)
{
    [Fact]
    public void NestedArraySelectionResolvesAreaNameAndDestination()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Interpreter.ResolveFunctionExpressionWithScope = (name, rawArgs, target, source, args, scope) =>
            stack.Runner.TryEvaluateFunction(name, rawArgs, target, source, args, scope, out var value) ? value : null;
        string path = Path.Combine(Path.GetTempPath(), $"stuck-area-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [FUNCTION ARRAY]
            LOCAL.TEMP = <ARGV[<EVAL <ARGV> - 1>]> -1
            RETURN <ARGV[<DLOCAL.TEMP>]>
            [FUNCTION ARRAYCOUNT]
            RETURN <EVAL <ARGV>>
            [AREADEF a_townBritain]
            NAME=Britain
            P=1496,1629,10,0
            RECT=1400,1500,1700,1800,0
            [AREADEF a_townMoonglow]
            NAME=Moonglow
            P=4467,1283,5,0
            RECT=4400,1200,4500,1400,0
            [FUNCTION probe]
            LOCAL.Stuck_Area_List a_townBritain,a_townMoonglow
            FOR s 1 <ARRAYCOUNT <LOCAL.Stuck_Area_List>>
                TAG.NAME_<DLOCAL.s> <SERV.AREA.<ARRAY <LOCAL.Stuck_Area_List>,<DLOCAL.s>>.NAME>
                TAG.P_<DLOCAL.s> <SERV.AREA.<ARRAY <LOCAL.Stuck_Area_List>,<DLOCAL.s>>.P>
            ENDFOR
            """);
        var world = TestHarness.CreateWorld();
        var field = typeof(SphereNet.Server.Program).GetField("_world", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        var resourcesField = typeof(SphereNet.Server.Program).GetField("_resources", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldResources = resourcesField.GetValue(null);
        var logField = typeof(SphereNet.Server.Program).GetField("_log", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldLog = logField.GetValue(null);
        var resolver = typeof(SphereNet.Server.Program).GetMethod("ResolveServerProperty", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            stack.Resources.LoadResourceFile(path);
            field.SetValue(null, world);
            resourcesField.SetValue(null, stack.Resources);
            logField.SetValue(null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            typeof(SphereNet.Server.Program).GetMethod("LoadRegionDefs", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            stack.Interpreter.ServerPropertyResolver = p => { var v = (string?)resolver.Invoke(null, [p]); output.WriteLine($"{p} => {v}"); return v; };
            var ch = world.CreateCharacter();
            stack.Runner.TryRunFunction("probe", ch, null, new TriggerArgs(ch), out _);
            Assert.True(ch.TryGetTag("NAME_1", out var name));
            Assert.Equal("Britain", name);
            Assert.True(ch.TryGetTag("NAME_2", out name));
            Assert.Equal("Moonglow", name);
            Assert.True(ch.TryGetTag("P_1", out var point));
            Assert.Equal("1496,1629,10,0", point);
        }
        finally { field.SetValue(null, previous); resourcesField.SetValue(null, oldResources); logField.SetValue(null, oldLog); File.Delete(path); }
    }
}
