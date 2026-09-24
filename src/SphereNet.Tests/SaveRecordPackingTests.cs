using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.World;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>The save capture keeps each object as one packed byte array rather than a
/// list of strings (it held the world still while the GC traced millions of them).
/// What the writer rebuilds from it must be exactly what was captured.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SaveRecordPackingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sn_pack_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void EveryPropertyComesBackAsWritten()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        item.Name = "Çiğdem'in şişesi ✓";
        item.SetTag("FORMULA", "a=b=c");
        item.SetTag("EMPTY", "");
        for (int i = 0; i < 300; i++)
            item.SetTag($"K{i}", new string('x', i));
        world.PlaceItem(item, new Point3D(100, 200, 5, 0));

        var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.WritePrepared(saver.Prepare(world), _dir));

        string text = File.ReadAllText(Path.Combine(_dir, "sphereworld.scp"));
        Assert.Contains("NAME=Çiğdem'in şişesi ✓", text);
        Assert.Contains("TAG.FORMULA=a=b=c", text);
        Assert.Contains("P=100,200,5", text);
        Assert.Contains($"TAG.K299={new string('x', 299)}", text);
        Assert.Contains("[WORLDITEM", text);
    }
}
