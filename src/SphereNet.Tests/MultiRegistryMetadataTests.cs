using System;
using System.IO;
using SphereNet.Game.Housing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// B3 (house/ship review): the multi placement registry held only the binary geometry
/// from multi.mul; the script [MULTIDEF] metadata (name, type, storage, vendors) was
/// parsed as a resource but never merged in, so placed houses used a blank name and a
/// flat storage default. MergeScriptMetadata overlays that metadata by shared multi id.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MultiRegistryMetadataTests
{
    private static string WriteScript(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_multidef_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void MergeScriptMetadata_PopulatesNameTypeStorageVendors()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [MULTIDEF 064]
            DEFNAME=m_test_stone_house
            NAME=Test Stone House
            TYPE=t_multi
            BaseStorage=489
            BaseVendors=10
            """);
        stack.Resources.LoadResourceFile(path);

        var reg = new MultiRegistry();
        reg.Register(new MultiDef { Id = 0x64 }); // geometry present for id 0x64

        int merged = reg.MergeScriptMetadata(stack.Resources);
        Assert.True(merged >= 1);

        var def = reg.Get(0x64);
        Assert.NotNull(def);
        Assert.Equal("Test Stone House", def!.Name);
        Assert.Equal("t_multi", def.MultiTypeName);
        Assert.Equal(489, def.BaseStorage);
        Assert.Equal(10, def.BaseVendors);
    }

    [Fact]
    public void MergeScriptMetadata_ParsesShipSpeed()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [MULTIDEF 065]
            DEFNAME=m_test_ship
            NAME=Test Ship
            TYPE=t_ship
            SHIPSPEED=2,1
            """);
        stack.Resources.LoadResourceFile(path);

        var reg = new MultiRegistry();
        reg.Register(new MultiDef { Id = 0x65 });
        reg.MergeScriptMetadata(stack.Resources);

        var def = reg.Get(0x65);
        Assert.NotNull(def);
        Assert.Equal(2, def!.ShipSpeedPeriodTenths); // period (tenths of a second)
        Assert.Equal(1, def.ShipSpeedTiles);          // tiles per step
    }

    [Fact]
    public void MergeScriptMetadata_SkipsMetadataWithoutGeometry()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [MULTIDEF 099]
            NAME=No Geometry Here
            TYPE=t_multi
            """);
        stack.Resources.LoadResourceFile(path);

        var reg = new MultiRegistry(); // no geometry registered for 0x99
        int merged = reg.MergeScriptMetadata(stack.Resources);

        Assert.Equal(0, merged);            // id 0x99 has no geometry → not placeable → skipped
        Assert.Null(reg.Get(0x99));
    }
}
