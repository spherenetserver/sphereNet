using System.Reflection;
using SphereNet.Core.Types;
using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
/// <summary>
/// Field report: a dye tub guarded by the pack's ISINSAFE ([FUNCTION] comparing
/// &lt;REGION.DEFNAME&gt; with a_safe_zone) refused a player standing inside the
/// area. A character's REGION came only from the tag a region crossing writes, so
/// one placed straight into an area (login, world load) had none; an item's REGION
/// was the display name, not the area reference, and an item in a pack looked its
/// area up at its container-local coordinates.
/// </summary>
public sealed class RegionReadAfterPlacementTests
{
    [Fact]
    public void ACharacterAndItsItemsKnowTheAreaTheyWerePlacedIn()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Interpreter.ResolveFunctionExpressionWithScope = (name, rawArgs, target, source, args, scope) =>
            stack.Runner.TryEvaluateFunction(name, rawArgs, target, source, args, scope, out var value) ? value : null;
        stack.Interpreter.FunctionLookup = stack.Runner.HasFunction;
        string path = Path.Combine(Path.GetTempPath(), $"safe-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [AREADEF a_safe_zone]
            NAME=Safe Zone
            FLAGS=REGION_FLAG_INSTA_LOGOUT|REGION_ANTIMAGIC_GATE
            GROUP=Islands
            P=1978 2080,0,0
            RECT=1822,2048,1994,2164,0
            [FUNCTION ISINSAFE]
            IF (<REGION>)
            	IF (<REGION.DEFNAME> == a_safe_zone)
            		RETURN 1
            	ENDIF
            ENDIF
            RETURN 0
            [FUNCTION probe]
            TAG.R0=<REGION>
            TAG.R1=<REGION.DEFNAME>
            TAG.R2=<ISINSAFE>
            TAG.R3=<SRC.ISINSAFE>
            TAG.R4=<EVAL (<REGION.DEFNAME> == a_safe_zone)>
            TAG.R5=<REGION.NAME>
            """);
        var world = TestHarness.CreateWorld();
        var p = typeof(SphereNet.Server.Program);
        var field = p.GetField("_world", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        var resourcesField = p.GetField("_resources", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldResources = resourcesField.GetValue(null);
        var logField = p.GetField("_log", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldLog = logField.GetValue(null);
        var resolver = p.GetMethod("ResolveServerProperty", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            stack.Resources.LoadResourceFile(path);
            field.SetValue(null, world);
            resourcesField.SetValue(null, stack.Resources);
            logField.SetValue(null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            p.GetMethod("LoadRegionDefs", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            stack.Interpreter.ServerPropertyResolver = k => (string?)resolver.Invoke(null, [k]);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(1899, 2081, 28, 0));
            stack.Runner.TryRunFunction("probe", ch, null, new TriggerArgs(ch), out _);
            AssertInSafeZone(ch);
            Assert.False(ch.TryGetTag("CURRENT_REGION_UID", out _));   // placed, never crossed

            var tub = world.CreateItem();
            world.PlaceItem(tub, new Point3D(1900, 2081, 28, 0));
            stack.Runner.TryRunFunction("probe", tub, null, new TriggerArgs(ch), out _);
            AssertInSafeZone(tub);
            var pack = world.CreateItem();
            ch.Equip(pack, SphereNet.Core.Enums.Layer.Pack);
            var inPack = world.CreateItem();
            pack.AddItem(inPack);
            stack.Runner.TryRunFunction("probe", inPack, null, new TriggerArgs(ch), out _);
            AssertInSafeZone(inPack);
            AssertInSafeZone(ch);
        }
        finally { field.SetValue(null, previous); resourcesField.SetValue(null, oldResources); logField.SetValue(null, oldLog); File.Delete(path); }
    }

    private static void AssertInSafeZone(SphereNet.Game.Objects.ObjBase obj)
    {
        string Tag(string k) => obj.TryGetTag(k, out var v) ? v ?? "" : "";
        Assert.NotEqual("", Tag("R0"));                 // REGION is a reference
        Assert.Equal("a_safe_zone", Tag("R1"));
        Assert.Equal("1", Tag("R2"));                   // the pack's ISINSAFE
        Assert.Equal("1", Tag("R4"));
        Assert.Equal("Safe Zone", Tag("R5"));
    }
}
