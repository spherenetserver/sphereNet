using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Save;

namespace SphereNet.Tests;

/// <summary>
/// The snapshot capture reuses one writer per THREAD rather than allocating one
/// per object - the per-object writers and their property lists were the bulk of
/// the allocation burst that stops the world for the length of a save. Reuse only
/// pays if each record still carries its OWN properties: hand the live list over
/// instead of copying it and every record ends up describing whichever object that
/// thread happened to write last, which is a corrupt save that still loads.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SnapshotWriterReuseTests
{
    private static GameWorld World()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void EverySavedItemKeepsItsOwnName()
    {
        var world = World();
        const int count = 400;
        for (int i = 0; i < count; i++)
        {
            var it = world.CreateItem();
            it.BaseId = (ushort)(0x4000 + (i % 0x400));
            it.Name = $"probe-{i}";
            world.PlaceItem(it, new Point3D((short)(10 + i % 60), (short)(10 + i / 60), 0, 0));
        }

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_snapreuse_{Guid.NewGuid():N}");
        try
        {
            var saver = new WorldSaver(LoggerFactory.Create(_ => { }));
            Assert.True(saver.Save(world, dir));

            string all = string.Join(Environment.NewLine, Directory.EnumerateFiles(dir)
                .Where(f => Path.GetFileName(f).StartsWith("sphereworld", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

            // Each name exactly once. A shared property list would repeat one name
            // for every object the thread that owned it captured.
            for (int i = 0; i < count; i++)
            {
                string needle = $"NAME=probe-{i}";
                int first = all.IndexOf(needle, StringComparison.Ordinal);
                Assert.True(first >= 0, $"{needle} missing from the save");
                // "probe-1" must not match inside "probe-10": require a line end.
                int after = first + needle.Length;
                Assert.True(after >= all.Length || char.IsControl(all[after]),
                    $"{needle} truncated or merged with another value");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
