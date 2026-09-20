using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// What a character carries changing redraws their status window.
///
/// Upstream makes this a container invariant rather than something each caller
/// remembers: every content add and remove calls OnWeightChange, which propagates up the
/// containment chain and on a character ends in UpdateStatsFlag (CChar.cpp:1466-1471).
///
/// Here only the paths that thought to ask redrew it. A script dropping gold into a
/// backpack was not one of them, so the coin was in the pack and the figure on the status
/// bar was not - and it corrected itself the next time the player moved something by
/// hand, which is how a shard reported it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CarriedContentStatusTests
{
    private sealed record Bench(SphereNet.Game.World.GameWorld World,
                                SphereNet.Game.Objects.Characters.Character Me,
                                Item Pack);

    private static Bench Build()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
        me.Backpack = pack; me.Equip(pack, Layer.Pack);
        return new Bench(world, me, pack);
    }

    private static Item Gold(Bench b, ushort amount)
    {
        var g = b.World.CreateItem();
        g.BaseId = 0x0EED; g.ItemType = ItemType.Gold; g.Amount = amount;
        return g;
    }

    /// <summary>Consume whatever the setup dirtied, so what follows is measured alone.</summary>
    private static void Settle(Bench b) => b.Me.ConsumeDirty();

    /// <summary>The reported case: a script puts gold in the pack.</summary>
    [Fact]
    public void GoldPutInThePackMarksTheStatusStale()
    {
        var b = Build();
        Settle(b);

        Assert.True(b.Pack.TryAddItem(Gold(b, 500)));

        Assert.True((b.Me.ConsumeDirty() & DirtyFlag.Stats) != 0,
            "the status window was never told the pack had changed");
    }

    /// <summary>A bag inside the pack counts: the walk goes all the way up.</summary>
    [Fact]
    public void GoldPutInABagInsideThePackCountsToo()
    {
        var b = Build();
        var pouch = b.World.CreateItem();
        pouch.BaseId = 0x0E76; pouch.ItemType = ItemType.Container;
        Assert.True(b.Pack.TryAddItem(pouch));
        Settle(b);

        Assert.True(pouch.TryAddItem(Gold(b, 500)));

        Assert.True((b.Me.ConsumeDirty() & DirtyFlag.Stats) != 0);
    }

    /// <summary>And taking something out, which is the other half of the same
    /// invariant.</summary>
    [Fact]
    public void TakingSomethingOutMarksItStaleToo()
    {
        var b = Build();
        var coin = Gold(b, 500);
        Assert.True(b.Pack.TryAddItem(coin));
        Settle(b);

        Assert.True(b.Pack.RemoveItem(coin));

        Assert.True((b.Me.ConsumeDirty() & DirtyFlag.Stats) != 0);
    }

    /// <summary>A chest standing in the world belongs to nobody, so filling it marks
    /// nothing - the walk must stop at the top-level item.</summary>
    [Fact]
    public void AContainerOnTheGroundMarksNobody()
    {
        var b = Build();
        var chest = b.World.CreateItem();
        chest.BaseId = 0x0E7C; chest.ItemType = ItemType.Container;
        b.World.PlaceItem(chest, new Point3D(101, 100, 0, 0));
        Settle(b);

        Assert.True(chest.TryAddItem(Gold(b, 500)));

        Assert.True((b.Me.ConsumeDirty() & DirtyFlag.Stats) == 0);
    }

    [Theory]
    [InlineData(ItemType.Normal, "10")]
    [InlineData(ItemType.Normal, "0")]
    [InlineData(ItemType.WeaponSword, "10")]
    [InlineData(ItemType.WeaponSword, "0")]
    [InlineData(ItemType.Armor, "10")]
    [InlineData(ItemType.Armor, "0")]
    [InlineData(ItemType.Food, "10")]
    [InlineData(ItemType.Food, "0")]
    [InlineData(ItemType.Potion, "10")]
    [InlineData(ItemType.Potion, "0")]
    [InlineData(ItemType.Gold, "10")]
    [InlineData(ItemType.Gold, "0")]
    public void EveryItemTypeNotifiesOnAddAmountChangeAndRemoval(ItemType type, string weight)
    {
        var b = Build();
        var item = b.World.CreateItem();
        item.ItemType = type;
        item.TrySetProperty("BASEWEIGHT", weight);
        Settle(b);
        Assert.True(b.Pack.TryAddItem(item));
        Assert.True(b.Me.ConsumeDirty().HasFlag(DirtyFlag.Stats));
        item.Amount = 20;
        Assert.True(b.Me.ConsumeDirty().HasFlag(DirtyFlag.Stats));
        item.Amount = 20;
        Assert.False(b.Me.ConsumeDirty().HasFlag(DirtyFlag.Stats));
        item.Amount = 3;
        Assert.True(b.Me.ConsumeDirty().HasFlag(DirtyFlag.Stats));
        Assert.True(b.Pack.RemoveItem(item));
        Assert.True(b.Me.ConsumeDirty().HasFlag(DirtyFlag.Stats));
    }

    [Fact]
    public void ScriptGoldMergedIntoExistingStackNotifies()
    {
        var b = Build();
        using var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 256, 256);
        map.SetSyntheticItemTile(0x0EED, new SphereNet.MapData.Tiles.ItemTileData
            { Flags = SphereNet.MapData.Tiles.TileFlag.Generic, Weight = 1 });
        b.World.MapData = map;
        var existing = Gold(b, 200);
        b.Pack.TryAddItem(existing);
        Settle(b);
        var incoming = Gold(b, 300);
        incoming.TrySetProperty("CONT", $"0{b.Pack.Uid.Value:X}");
        Assert.Equal(500, existing.Amount);
        Assert.True(incoming.IsDeleted);
        Assert.True(b.Me.ConsumeDirty().HasFlag(DirtyFlag.Stats));
    }

    [Theory]
    [InlineData(ItemType.EqBankBox, false)]
    [InlineData(ItemType.EqVendorBox, false)]
    [InlineData(ItemType.Container, true)]
    public void UnweighedContainersStillInvalidateInventoryStatus(ItemType type, bool magic)
    {
        var b = Build();
        var bag = b.World.CreateItem();
        bag.ItemType = type;
        if (magic) bag.SetAttr(ObjAttributes.Magic);
        b.Pack.TryAddItem(bag);
        Settle(b);
        var item = Gold(b, 20);
        bag.TryAddItem(item);
        Assert.True(b.Me.ConsumeDirty().HasFlag(DirtyFlag.Stats));
        item.Amount = 40;
        Assert.True(b.Me.ConsumeDirty().HasFlag(DirtyFlag.Stats));
    }

    [Fact]
    public void NestedNonGoldChangesReachTheStatusPacketThroughDirtyDispatch()
    {
        var b = Build();
        using var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, b.World,
            new SphereNet.Game.Accounts.AccountManager(lf), 8958);
        TestHarness.AttachCharacter(client, b.Me);
        var bag = b.World.CreateItem();
        bag.ItemType = ItemType.Container;
        b.Pack.TryAddItem(bag);
        client.SendCharacterStatus(b.Me);
        b.World.ConsumeDirtyObjects();
        TestHarness.ClearQueuedPackets(client.NetState);

        var item = b.World.CreateItem();
        item.ItemType = ItemType.Food;
        item.TrySetProperty("BASEWEIGHT", "10");
        item.Amount = 25;
        bag.TryAddItem(item);

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var program = typeof(SphereNet.Server.Program);
        var worldField = program.GetField("_world", flags)!;
        var previousWorld = worldField.GetValue(null);
        var clients = (System.Collections.IDictionary)program.GetField("_clientsByCharUid", flags)!.GetValue(null)!;
        var previousClient = clients[b.Me.Uid];
        worldField.SetValue(null, b.World);
        clients[b.Me.Uid] = client;
        try
        {
            var dispatch = program.GetMethod("MarkClientsNearDirtyObject", flags)!;
            foreach (var changed in b.World.DrainDirtyObjectsSnapshot())
                dispatch.Invoke(null, [changed]);
            var packet = Assert.Single(TestHarness.GetQueuedPackets(client.NetState),
                p => p.Span[0] == 0x11);
            // 0x11 weight follows gold (4) and armor (2), at byte 64.
            Assert.Equal(b.Me.GetTotalWeight(),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Span[64..]));
        }
        finally
        {
            if (previousClient == null) clients.Remove(b.Me.Uid);
            else clients[b.Me.Uid] = previousClient;
            worldField.SetValue(null, previousWorld);
        }
    }
}
