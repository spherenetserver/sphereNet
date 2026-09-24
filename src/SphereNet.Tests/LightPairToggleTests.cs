using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Lighting or dousing a light re-bases it onto its paired definition
/// (upstream CItem::Use_Light: TDATA3, or the OVERRIDE_LIGHTID tag). Only the type
/// used to flip, so a lamppost kept its lit graphic and the client never saw the
/// light go out or come on.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LightPairToggleTests
{
    private const string Defs =
        "[ITEMDEF 0b20]\nDEFNAME=i_lamppost1_lit\nTYPE=t_light_lit\nTDATA3=i_lamppost1\n\n" +
        "[ITEMDEF 0b21]\nDEFNAME=i_lamppost1\nTYPE=t_light_out\nTDATA3=i_lamppost1_lit\n\n" +
        "[ITEMDEF 0fac]\nDEFNAME=i_fire_pit\nTYPE=t_fire\n";

    private static void Load()
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"light_pair_{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, Defs);
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
        }
        finally { File.Delete(path); }
    }

    private static Item Lamp(ushort id, ItemType type)
    {
        var world = TestHarness.CreateWorld();
        var lamp = world.CreateItem();
        lamp.BaseId = id;
        lamp.ItemType = type;
        world.PlaceItem(lamp, new Point3D(100, 100, 0, 0));
        return lamp;
    }

    [Fact]
    public void DousingSwapsToTheUnlitGraphicAndBack()
    {
        Load();
        var lamp = Lamp(0x0B20, ItemType.LightLit);

        Assert.True(lamp.UseLight());
        Assert.Equal((ushort)0x0B21, lamp.BaseId);
        Assert.Equal(ItemType.LightOut, lamp.ItemType);
        Assert.Equal(0, lamp.Timeout);

        Assert.True(lamp.UseLight());
        Assert.Equal((ushort)0x0B20, lamp.BaseId);
        Assert.Equal(ItemType.LightLit, lamp.ItemType);
        Assert.True(lamp.Timeout > Environment.TickCount64);
        Assert.True(lamp.TryGetTag("LIGHT_CHARGES", out string? charges) && charges == "20");
    }

    [Fact]
    public void ABurnedOutLightCannotBeRelit()
    {
        Load();
        var lamp = Lamp(0x0B21, ItemType.LightOut);
        lamp.SetTag("LIGHT_BURNED", "1");

        Assert.False(lamp.UseLight());
        Assert.Equal((ushort)0x0B21, lamp.BaseId);
    }

    [Fact]
    public void ALightWithNoPairDoesNothing()
    {
        Load();
        var torch = Lamp(0x0A12, ItemType.LightLit);

        Assert.False(torch.UseLight());
        Assert.Equal(ItemType.LightLit, torch.ItemType);
    }

    [Fact]
    public void AFirePitIsNotALightSource()
    {
        Load();
        var pit = Lamp(0x0FAC, ItemType.Fire);

        Assert.False(pit.UseLight());
        Assert.Equal((ushort)0x0FAC, pit.BaseId);
    }
}
