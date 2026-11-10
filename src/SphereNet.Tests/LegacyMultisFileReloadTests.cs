using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A classic save keeps its structures in spheremultis, a file this engine reads but
/// never rewrites: once the world has been saved here, every structure lives in
/// sphereworld. The old file stays on disk, so a house demolished afterwards used to
/// come back on every boot. Its ATTR_STATIC records are the exception - the world save
/// leaves statics out, so the old file is still where they come from.
/// </summary>
public sealed class LegacyMultisFileReloadTests : IDisposable
{
    private const uint Loose = 0x40000010, House = 0x40000020, StaticStructure = 0x40000021;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sphnet_multis_{Guid.NewGuid():N}");

    public LegacyMultisFileReloadTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "sphereworld.scp"),
            "[WORLDITEM 0eed]\nSERIAL=040000010\nP=100,100,0\n\n[EOF]\n");
        File.WriteAllText(Path.Combine(_dir, "spheremultis.scp"),
            "[WORLDITEM 064]\nSERIAL=040000020\nP=120,120,0\n\n" +
            "[WORLDITEM 065]\nSERIAL=040000021\nATTR=08000\nP=130,130,0\n\n[EOF]\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private GameWorld Load()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        new WorldLoader(LoggerFactory.Create(_ => { })).Load(world, _dir);
        return world;
    }

    private void Save(GameWorld world) =>
        Assert.True(new WorldSaver(LoggerFactory.Create(_ => { })).Save(world, _dir));

    [Fact]
    public void AClassicSave_LoadsItsMultisFileInFull()
    {
        var world = Load();

        Assert.NotNull(world.FindItem(new Serial(Loose)));
        Assert.NotNull(world.FindItem(new Serial(House)));
        Assert.NotNull(world.FindItem(new Serial(StaticStructure)));
        Assert.True(world.FindItem(new Serial(StaticStructure))!.IsAttr(ObjAttributes.Static));
    }

    [Fact]
    public void AStructureStillStanding_SurvivesTheFirstSaveAndReload()
    {
        Save(Load());

        var world = Load();

        Assert.NotNull(world.FindItem(new Serial(House)));
        Assert.NotNull(world.FindItem(new Serial(StaticStructure)));
    }

    [Fact]
    public void AStructureRemovedAfterTheImport_DoesNotComeBack()
    {
        var first = Load();
        Save(first);

        var second = Load();
        second.DeleteObject(second.FindItem(new Serial(House))!);
        Save(second);

        var third = Load();

        Assert.Null(third.FindItem(new Serial(House)));
        Assert.NotNull(third.FindItem(new Serial(Loose)));
        // The world save leaves ATTR_STATIC items out; the old file still supplies it.
        Assert.NotNull(third.FindItem(new Serial(StaticStructure)));
    }
}
