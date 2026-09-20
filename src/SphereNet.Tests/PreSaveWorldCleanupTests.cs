using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
[ResetEngineStatics]
public sealed class PreSaveWorldCleanupTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("FORCEGARBAGECOLLECT=0", false)]
    [InlineData("FORCEGARBAGECOLLECT=1", true)]
    public void IniControlsPreSaveCleanup(string setting, bool expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[SPHERE]\n" + setting);
            var ini = new IniParser();
            ini.Load(path);
            var config = new SphereConfig();
            config.LoadFromIni(ini);
            Assert.Equal(expected, config.ForceGarbageCollect);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavePreparationRepairsOnlyWhenEnabled_AndPreservesHealthyInventory(bool enabled)
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED;
        gold.Amount = 41;
        pack.AddItem(gold);
        var broken = world.CreateItem();
        broken.BaseId = 0x0F26;
        world.PlaceItem(broken, owner.Position);
        broken.ContainedIn = new Serial(0x40FFFFFF);

        var method = typeof(SphereNet.Server.Program).GetMethod("BeginWorldSave",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var timer = (Stopwatch)method.Invoke(null, [world, enabled, NullLogger.Instance])!;

        Assert.True(timer.IsRunning);
        Assert.Equal(!enabled, broken.ContainedIn.IsValid);
        Assert.False(gold.IsDeleted);
        Assert.Equal(pack.Uid, gold.ContainedIn);
        Assert.Equal(41, (int)gold.Amount);
        Assert.Contains(gold, pack.Contents);
        Assert.True(pack.IsEquipped);
    }
}
