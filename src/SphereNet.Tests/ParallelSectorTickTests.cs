using System.Collections.Generic;
using System.Linq;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 02 / C1 — the multicore world tick no longer runs Sector.OnTick across
/// sectors concurrently (spawns, deaths and @Timer scripts mutate shared,
/// non-thread-safe sector lists and world indexes, which raced and could hang
/// the main loop). OnTickParallel now serializes the sector phase; the only
/// parallelism left in multicore mode is the NPC read-only pathfinding prestage.
///
/// OnTickParallel had no direct test coverage. These lock in that the serialized
/// multicore tick runs its phases without throwing, is deterministic, and keeps
/// the world's object/index invariants over many ticks.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ParallelSectorTickTests
{
    private static Character AddPlayer(GameWorld world, int x, int y)
    {
        var p = world.CreateCharacter();
        p.IsPlayer = true;
        p.IsOnline = true;
        world.PlaceCharacter(p, new Point3D((short)x, (short)y, 0, 0));
        world.AddOnlinePlayer(p); // populates the active-sector source
        return p;
    }

    [Fact]
    public void OnTickParallel_FiresDueTimersAcrossActiveSectors_WithoutThrowing()
    {
        var world = TestHarness.CreateWorld();
        var fired = new List<string>();
        world.TimerFExpired = (_, entry) => fired.Add(entry.FunctionName);

        // Four players in well-separated areas → many active sectors (the old code
        // would have crossed its 50-sector parallel threshold on a busier shard).
        for (int i = 0; i < 4; i++)
        {
            int bx = 300 + i * 400, by = 300 + i * 400;
            AddPlayer(world, bx, by);
            var item = world.CreateItem();
            world.PlaceItem(item, new Point3D((short)bx, (short)(by + 1), 0, 0));
            item.AddTimerF(0, $"f_{i}", ""); // due immediately
        }

        var ex = Record.Exception(() => world.OnTickParallel());
        Assert.Null(ex);
        Assert.Equal(4, fired.Count);
        Assert.Equal(new[] { "f_0", "f_1", "f_2", "f_3" }, fired.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void OnTickParallel_IsDeterministic_AcrossIdenticalWorlds()
    {
        static GameWorld Build()
        {
            var w = TestHarness.CreateWorld();
            for (int i = 0; i < 6; i++)
            {
                int bx = 200 + i * 300, by = 200 + i * 300;
                var p = w.CreateCharacter();
                p.IsPlayer = true; p.IsOnline = true;
                w.PlaceCharacter(p, new Point3D((short)bx, (short)by, 0, 0));
                w.AddOnlinePlayer(p);
                for (int j = 0; j < 5; j++)
                {
                    var it = w.CreateItem();
                    it.BaseId = (ushort)(0x0EED + j);
                    w.PlaceItem(it, new Point3D((short)(bx + j), (short)(by + 2), 0, 0));
                }
                var mob = w.CreateCharacter();
                mob.MaxHits = 100; mob.Hits = 40; // wounded
                w.PlaceCharacter(mob, new Point3D((short)(bx + 1), (short)(by + 3), 0, 0));
            }
            return w;
        }

        var a = Build();
        var b = Build();
        // Identical construction assigns identical UIDs, so the pre-tick hashes match.
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());

        for (int t = 0; t < 20; t++)
        {
            a.OnTickParallel();
            b.OnTickParallel();
        }

        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void OnTickParallel_PreservesObjectAndIndexInvariants_OverManyTicks()
    {
        var world = TestHarness.CreateWorld();
        for (int i = 0; i < 5; i++)
        {
            int bx = 250 + i * 350, by = 250 + i * 350;
            AddPlayer(world, bx, by);
            for (int j = 0; j < 8; j++)
            {
                var it = world.CreateItem();
                world.PlaceItem(it, new Point3D((short)(bx + j), (short)(by + 1), 0, 0));
            }
            var mob = world.CreateCharacter();
            mob.MaxHits = 100; mob.Hits = 30;
            world.PlaceCharacter(mob, new Point3D((short)(bx + 2), (short)(by + 4), 0, 0));
        }

        var itemsBefore = world.GetAllObjects().OfType<Item>().Where(o => !o.IsDeleted).Select(o => o.Uid).ToList();
        var charsBefore = world.GetAllObjects().OfType<Character>().Where(o => !o.IsDeleted).Select(o => o.Uid).ToList();

        for (int t = 0; t < 50; t++)
            Assert.Null(Record.Exception(() => world.OnTickParallel()));

        // No plain item/character was spuriously created or deleted, and every
        // live object still resolves through its UID index (no corruption).
        var itemsAfter = world.GetAllObjects().OfType<Item>().Where(o => !o.IsDeleted).Select(o => o.Uid).ToList();
        var charsAfter = world.GetAllObjects().OfType<Character>().Where(o => !o.IsDeleted).Select(o => o.Uid).ToList();
        Assert.Equal(itemsBefore.OrderBy(u => u.Value), itemsAfter.OrderBy(u => u.Value));
        Assert.Equal(charsBefore.OrderBy(u => u.Value), charsAfter.OrderBy(u => u.Value));
        foreach (var uid in itemsAfter) Assert.NotNull(world.FindItem(uid));
        foreach (var uid in charsAfter) Assert.NotNull(world.FindChar(uid));
    }
}
