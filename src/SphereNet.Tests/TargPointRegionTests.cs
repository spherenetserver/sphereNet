using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The spell scripts ask what area a picked point lies in -
/// <c>&lt;SRC.TARGP.REGION.FLAGS&gt;</c> before letting a field or a gate land there.
/// TARGP is a point, and a point answers REGION.x like any object's position does
/// (CPointBase::r_WriteVal, CPointMap).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TargPointRegionTests
{
    [Fact]
    public void TheLastPickedPointAnswersItsAreaThroughTarGp()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"targp-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [FUNCTION probe]
            TAG.RN=<SRC.TARGP.REGION.NAME>
            TAG.RF=<SRC.TARGP.REGION.FLAGS>
            TAG.RX=<SRC.TARGP.X>
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            var area = new Region { Name = "Picked Area", Flags = RegionFlag.Safe, MapIndex = 0 };
            area.AddRect(290, 290, 310, 310);
            world.AddRegion(area);

            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            ch.SetTag("TARGP", "300,300,0,0");

            stack.Runner.TryRunFunction("probe", ch, null, new TriggerArgs(ch), out _);

            Assert.True(ch.TryGetTag("RN", out var name));
            Assert.Equal("Picked Area", name);
            Assert.True(ch.TryGetTag("RF", out var flags));
            Assert.True(ScriptNumber.TryParseToken(flags!, out long flagValue));
            Assert.NotEqual(0, flagValue & (long)RegionFlag.Safe);
            Assert.True(ch.TryGetTag("RX", out var x));
            Assert.Equal("300", x);
        }
        finally { File.Delete(path); }
    }
}
