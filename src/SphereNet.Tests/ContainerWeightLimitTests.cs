using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What bounds a container is the container (port plan İŞ-16).
///
/// The reference has no global cap for chests and bags: the limit is the container's own
/// MODMAXWEIGHT, which lives on the shared object base (CObjBase.cpp:1104) and starts at
/// zero, and the check is skipped entirely while it is not positive
/// (CItemContainer.cpp:906). Only the player's backpack has a real limit, from its
/// owner's carry weight plus BACKPACKOVERLOAD.
///
/// SphereNet shipped a flat 400 stones for every container - a number the reference does
/// not contain - and had MODMAXWEIGHT on characters only, so a script could say how much
/// a chest holds and nothing listened.
/// </summary>
public sealed class ContainerWeightLimitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sphnet_cw_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void NothingCapsAnOrdinaryContainerByDefault()
    {
        // The setting a host copies into the world, unset.
        var config = new SphereNet.Core.Configuration.SphereConfig();
        Assert.Equal(0, config.ContainerMaxWeight);
        Assert.Equal(0, new GameWorld(LoggerFactory.Create(_ => { })).MaxContainerWeight);
    }

    [Fact]
    public void AContainerCanSayHowMuchItHolds()
    {
        var world = TestHarness.CreateWorld();
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;

        Assert.True(chest.TrySetProperty("MODMAXWEIGHT", "250"));
        Assert.Equal(250, chest.ModMaxWeight);

        Assert.True(chest.TryGetProperty("MODMAXWEIGHT", out string? v));
        Assert.Equal("250", v);
    }

    [Fact]
    public void ItStartsAtZeroWhichMeansNoLimit()
    {
        var world = TestHarness.CreateWorld();
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;

        Assert.Equal(0, chest.ModMaxWeight);
        Assert.True(chest.TryGetProperty("MODMAXWEIGHT", out string? v));
        Assert.Equal("0", v);
    }

    [Fact]
    public void TheLimitSurvivesARestart()
    {
        var world = TestHarness.CreateWorld();
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.BaseId = 0x0E75;
        chest.ModMaxWeight = 300;
        world.PlaceItem(chest, new Point3D(120, 120, 0, 0));

        Directory.CreateDirectory(_dir);
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, _dir);

        var reloaded = new GameWorld(LoggerFactory.Create(_ => { }));
        reloaded.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => reloaded;
        Item.ResolveWorld = () => reloaded;
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, _dir);

        var back = reloaded.GetAllObjects().OfType<Item>().FirstOrDefault(i => i.BaseId == 0x0E75);
        Assert.NotNull(back);
        Assert.Equal(300, back!.ModMaxWeight);
    }
}
