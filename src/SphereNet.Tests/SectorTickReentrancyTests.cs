using System.Collections.Generic;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 09 / H7 — Sector.OnTick and TickItems used to walk the live character/item
/// list by index while firing each object's OnTick. A callback that removed more
/// than one object from the same sector (a death cascade, an item @Timer that
/// deletes several items) shrank the list under the index and the next access
/// threw ArgumentOutOfRangeException on the world tick. Both loops now snapshot
/// first (like the spell-effect reentrancy fix), so a callback may remove several
/// entries or add new ones safely.
///
/// These drive the item loop (TickItems) via the real Item.OnTimerExpired hook; the
/// character loop (OnTick) uses the identical snapshot pattern on _characters.
/// </summary>
public sealed class SectorTickReentrancyTests
{
    [Fact]
    public void ItemTick_CallbackDeletesMultipleSiblings_DoesNotThrow_AndTicksEachStartingItemOnce()
    {
        var world = TestHarness.CreateWorld();
        var pos = new Point3D(100, 100, 0, 0);

        var items = new List<Item>();
        for (int i = 0; i < 5; i++)
        {
            var it = world.CreateItem();
            world.PlaceItem(it, pos);
            it.SetTimeout(1); // due immediately → OnTick fires OnTimerExpired
            items.Add(it);
        }
        var sector = world.GetSector(pos)!;
        Assert.Equal(5, sector.ItemCount);

        var ticked = new List<uint>();
        bool cascaded = false;
        Item.OnTimerExpired = it =>
        {
            ticked.Add(it.Uid.Value);
            if (!cascaded)
            {
                cascaded = true;
                // The first ticked item deletes three OTHER items mid-pass — the
                // interleaving that overran the old index loop.
                world.RemoveItem(items[2]);
                world.RemoveItem(items[3]);
                world.RemoveItem(items[4]);
            }
            return null; // Default: keep this item
        };

        try
        {
            // Driven through the world tick: item timers come from the world's due
            // queue now, so the interleaving this test is about - a callback deleting
            // siblings that are in the same pass - happens there rather than in the
            // sector's item list.
            var ex = Record.Exception(() => world.OnTick());
            Assert.Null(ex);

            // Two items survive; the three the callback removed are gone.
            Assert.Equal(2, sector.ItemCount);
            Assert.True(items[2].IsDeleted && items[3].IsDeleted && items[4].IsDeleted);
            Assert.False(items[0].IsDeleted);
            Assert.False(items[1].IsDeleted);

            // The removed siblings were skipped, not ticked; the survivors ticked once.
            Assert.Equal(new[] { items[0].Uid.Value, items[1].Uid.Value }, ticked);
        }
        finally
        {
            Item.OnTimerExpired = null;
        }
    }

    [Fact]
    public void ItemTick_CallbackAddsNewItem_NewItemDeferredToNextTick()
    {
        var world = TestHarness.CreateWorld();
        var pos = new Point3D(120, 120, 0, 0);

        var first = world.CreateItem();
        world.PlaceItem(first, pos);
        first.SetTimeout(1);
        var sector = world.GetSector(pos)!;

        var ticked = new List<uint>();
        uint newItemUid = 0;
        bool spawned = false;
        Item.OnTimerExpired = it =>
        {
            ticked.Add(it.Uid.Value);
            if (!spawned)
            {
                spawned = true;
                var added = world.CreateItem();
                world.PlaceItem(added, pos);
                added.SetTimeout(1); // would tick if it were in this pass
                newItemUid = added.Uid.Value;
            }
            return null;
        };

        try
        {
            world.OnTick();

            // The item added mid-tick is in the sector now...
            Assert.Equal(2, sector.ItemCount);
            // ...but it was not part of the selection this pass, so it did not tick —
            // only the original item did. The hazard moved with the mechanism: item
            // timers come from the world's due queue rather than the sector's list,
            // and a callback that arms a new one during the drain must not be picked
            // up by the same drain (which, for a timer armed at "now", would not
            // terminate).
            Assert.Equal(new[] { first.Uid.Value }, ticked);
            Assert.DoesNotContain(newItemUid, ticked);
        }
        finally
        {
            Item.OnTimerExpired = null;
        }
    }
}
