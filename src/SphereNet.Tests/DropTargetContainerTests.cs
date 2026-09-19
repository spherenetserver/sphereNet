using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// A drop aimed at a container lands IN it.
///
/// The drop handler carried its own list of container types and it was the shorter of
/// the two in this engine: it did not name the ship hold, the trash can, the keyring or
/// the game board, every one of which Item.IsContainerItemType does and every one of
/// which upstream builds as a CItemContainer. A drop aimed at one of them took the
/// plain-item path instead - "put it where that item lives" - and for a thing standing on
/// the ground that means the tile under it.
///
/// So a ship's hold refused everything and left the goods lying on the deck, which is how
/// a shard reported it, and a trash can could not be thrown into.
/// </summary>
public sealed class DropTargetContainerTests
{
    private sealed record Bench(SphereNet.Game.World.GameWorld World,
                                SphereNet.Game.Clients.GameClient Client,
                                SphereNet.Game.Objects.Characters.Character Me,
                                Item Pack);

    private static Bench Build(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.PrivLevel = PrivLevel.Player;
        me.Str = me.Dex = me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
        me.Backpack = pack; me.Equip(pack, Layer.Pack);
        return new Bench(world, client, me, pack);
    }

    /// <summary>Drop a fresh item from the pack onto <paramref name="target"/> and answer
    /// where it ended up.</summary>
    private static Item DropOnto(Bench b, Item target)
    {
        var goods = b.World.CreateItem();
        goods.BaseId = 0x1BFB; goods.ItemType = ItemType.WeaponMaceSharp;
        Assert.True(b.Pack.TryAddItem(goods));

        b.Client.Inventory.HandleItemPickup(goods.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(goods.Uid.Value, -1, -1, 0, target.Uid.Value);
        return goods;
    }

    private static Item StandingContainer(Bench b, ItemType type, ushort graphic)
    {
        var it = b.World.CreateItem();
        it.BaseId = graphic; it.ItemType = type;
        b.World.PlaceItem(it, new Point3D(101, 100, 0, 0));
        return it;
    }

    /// <summary>The reported case.</summary>
    [Fact]
    public void AShipHoldTakesWhatIsDroppedOnIt()
    {
        var b = Build(8941);
        var hold = StandingContainer(b, ItemType.ShipHold, 0x0E7C);

        var goods = DropOnto(b, hold);

        Assert.Equal(hold.Uid, goods.ContainedIn);
        Assert.Contains(goods, hold.Contents);
    }

    /// <summary>And a trash can, which had the same hole.</summary>
    [Fact]
    public void ATrashCanTakesWhatIsDroppedOnIt()
    {
        var b = Build(8942);
        var bin = StandingContainer(b, ItemType.TrashCan, 0x0E77);

        var goods = DropOnto(b, bin);

        Assert.Equal(bin.Uid, goods.ContainedIn);
    }

    /// <summary>A plain item is still not a container: the drop goes to the tile it
    /// stands on, which is the behaviour the short list was written for.</summary>
    [Fact]
    public void APlainItemIsStillNotAContainer()
    {
        var b = Build(8943);
        var anvil = StandingContainer(b, ItemType.Anvil, 0x0FAF);

        var goods = DropOnto(b, anvil);

        Assert.False(goods.ContainedIn.IsValid);
        Assert.Equal(new Point3D(101, 100, 0, 0), goods.Position);
    }

    /// <summary>The guardrail: the drop handler and the item model must not drift apart
    /// again. Every type the engine calls a container is one a drop can land in.</summary>
    [Fact]
    public void EveryContainerTypeIsADropTarget()
    {
        var predicate = typeof(SphereNet.Game.Clients.ClientInventoryHandler)
            .GetMethod("IsDropTargetContainer", System.Reflection.BindingFlags.Static |
                                                System.Reflection.BindingFlags.NonPublic)!;
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var missing = new List<ItemType>();
        foreach (ItemType type in Enum.GetValues<ItemType>())
        {
            if (!Item.IsContainerItemType(type)) continue;
            var probe = world.CreateItem();
            probe.ItemType = type;
            if (!(bool)predicate.Invoke(null, [probe])!)
                missing.Add(type);
        }

        Assert.True(missing.Count == 0,
            "a drop cannot land in: " + string.Join(", ", missing));
    }
}
