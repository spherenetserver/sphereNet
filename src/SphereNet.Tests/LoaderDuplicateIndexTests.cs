using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 18 / M8 — a duplicate SERIAL or UUID in a save deletes the just-parsed
/// object, but the loader kept processing its properties: the UUID branch then
/// re-indexed the already-deleted object (FindByUuid returned a dead object), and
/// deleting a duplicate-UUID object evicted the OTHER live object's index entry.
/// The loader now drains the rest of a retired record with no side effects, and
/// DeleteObject only drops the UUID entry that actually points at the object.
/// </summary>
public sealed class LoaderDuplicateIndexTests
{
    private static readonly Guid U1 = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid U2 = new("22222222-2222-2222-2222-222222222222");

    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static void WriteItemRecord(ISaveWriter w, string serialHex, Guid uuid, short x)
    {
        w.BeginRecord("WORLDITEM");
        w.WriteProperty("SERIAL", serialHex); // SERIAL before UUID — the failing order
        w.WriteProperty("UUID", uuid.ToString());
        w.WriteProperty("ID", "0EED");
        w.WriteProperty("P", $"{x},1000,0,0");
        w.EndRecord();
    }

    private static (GameWorld world, int items) LoadTwoItems(
        string s1, Guid u1, string s2, Guid u2)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_dup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        using (var w = SaveIO.OpenWriter(Path.Combine(dir, "sphereworld.scp"), SaveFormat.Text))
        {
            WriteItemRecord(w, s1, u1, 1000);
            WriteItemRecord(w, s2, u2, 1001);
        }
        var world = MakeWorld();
        var (items, _) = new WorldLoader(LoggerFactory.Create(_ => { })).Load(world, dir);
        try { Directory.Delete(dir, recursive: true); } catch { }
        return (world, items);
    }

    [Fact]
    public void DuplicateSerial_ThenUniqueUuid_DoesNotIndexTheDeletedObject()
    {
        // Record 2 reuses record 1's serial (skipped) but has a unique UUID U2.
        var (world, _) = LoadTwoItems("040000001", U1, "040000001", U2);

        // U2 belonged only to the skipped/deleted record — it must not resolve.
        Assert.Null(world.FindByUuid(U2));
        // U1 still resolves to the surviving object.
        Assert.NotNull(world.FindByUuid(U1));
        Assert.False(((Item)world.FindByUuid(U1)!).IsDeleted);
        // Exactly one item survived.
        Assert.Single(world.GetAllObjects().OfType<Item>(), i => !i.IsDeleted);
    }

    [Fact]
    public void DuplicateUuid_DifferentSerial_KeepsTheFirstObjectsIndexIntact()
    {
        // Record 2 has a unique serial but reuses record 1's UUID U1 (skipped).
        var (world, _) = LoadTwoItems("040000001", U1, "040000002", U1);

        // U1 must still resolve to the FIRST object (serial 0x40000001), not be
        // evicted by deleting the duplicate-UUID second object.
        var byUuid = world.FindByUuid(U1);
        Assert.NotNull(byUuid);
        Assert.Equal(0x40000001u, byUuid!.Uid.Value);
        // The first object is present; the second (0x40000002) was deleted.
        Assert.NotNull(world.FindItem(new Serial(0x40000001)));
        Assert.Null(world.FindItem(new Serial(0x40000002)));
        Assert.Single(world.GetAllObjects().OfType<Item>(), i => !i.IsDeleted);
    }
}
