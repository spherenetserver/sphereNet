using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class TimerFCommandIdentityTests
{
    [Theory]
    [InlineData("f_job=7")]
    [InlineData("f_job,7")]
    [InlineData("f_job   7")]
    [InlineData("f_job\t7")]
    public void StopAndQueryMatchOriginalCommand(string command)
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.ScheduleTimerF("60, " + command, 1000);
        Assert.True(item.GetTimerFRemaining(command, Environment.TickCount64) > 0);
        Assert.Equal(0, item.GetTimerFRemaining("f_job 7", Environment.TickCount64));
        Assert.Equal(1, item.ClearTimerF(command));
        Assert.Empty(item.TimerFEntries);
    }

    [Theory]
    [InlineData(SaveFormat.Text, false)]
    [InlineData(SaveFormat.Binary, false)]
    [InlineData(SaveFormat.Text, true)]
    [InlineData(SaveFormat.Binary, true)]
    public void NativeSaveKeepsCommandIdentityWithoutDuplicatingJobs(SaveFormat format, bool character)
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        ObjBase obj;
        if (character)
        {
            var ch = world.CreateCharacter(); world.PlaceCharacter(ch, new Point3D(100, 100)); obj = ch;
        }
        else
        {
            var item = world.CreateItem(); item.BaseId = 0xEED;
            world.PlaceItem(item, new Point3D(100, 100)); obj = item;
        }
        string command = "f_job=one|two";
        obj.ScheduleTimerF("60, " + command, 1000);
        obj.AddTimerF(60000, "f_other", "legacy");
        var uid = obj.Uid;
        string dir = Path.Combine(Path.GetTempPath(), $"timer-identity-{Guid.NewGuid():N}");
        try
        {
            Assert.True(new WorldSaver(logs) { Format = format }.Save(world, dir));
            var loaded = TestHarness.CreateWorld(); new WorldLoader(logs).Load(loaded, dir);
            var restored = loaded.FindObject(uid)!;
            Assert.Equal(2, restored.TimerFEntries.Count);
            Assert.True(restored.GetTimerFRemaining(command, Environment.TickCount64) > 0);
            Assert.Equal(1, restored.ClearTimerF(command));
            Assert.Single(restored.TimerFEntries);
            Assert.Equal(1, restored.ClearTimerF("f_other legacy"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void MalformedMetadataCannotRenameOrDuplicateRestoredWork()
    {
        var world = TestHarness.CreateWorld(); var item = world.CreateItem();
        Assert.False(item.TrySetProperty("TIMERFCOMMAND", "f_other=7"));
        Assert.True(item.TryLoadTimerFEntry("60000|f_job|7"));
        Assert.False(item.TrySetProperty("TIMERFCOMMAND", "f_other=7"));
        Assert.Single(item.TimerFEntries);
        Assert.True(item.GetTimerFRemaining("f_job 7", Environment.TickCount64) > 0);
        Assert.False(item.TryLoadTimerFEntry("invalid"));
        Assert.False(item.TrySetProperty("TIMERFCOMMAND", "f_job=7"));
        Assert.Equal(1, item.ClearTimerF("f_job 7"));
    }
}
