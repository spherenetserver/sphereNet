using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The vendor boxes (LAYER_VENDOR_STOCK/EXTRA/BUYS) are ordinary worn containers
/// and save with everything in them, at any depth, as Source-X writes every worn
/// item (CChar::r_Write -> r_WriteContent). The saver once skipped the stock box as
/// "virtual" and had to chase nested bags so none was left with a CONT pointing at
/// a box that was never written; with the box saved, the whole chain comes back.
/// </summary>
public sealed class VendorStockFilterTests
{
    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    [Fact]
    public void VendorStockNestedThreeDeep_IsPersisted_LikePlayerNesting()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_vstock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var world = MakeWorld();

            // Vendor: stock container -> bag -> item (three levels).
            var vendor = world.CreateCharacter();
            world.PlaceCharacter(vendor, new Point3D(1000, 1000, 0, 0));
            var stockContainer = world.CreateItem(); stockContainer.BaseId = 0x0E75;
            vendor.Equip(stockContainer, Layer.VendorStock);
            var stockBag = world.CreateItem(); stockBag.BaseId = 0x0E76;
            Assert.True(stockContainer.TryAddItem(stockBag));
            var stockItem = world.CreateItem(); stockItem.BaseId = 0x0EED;
            Assert.True(stockBag.TryAddItem(stockItem));

            // Player: backpack -> bag -> item (three levels).
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(1010, 1000, 0, 0));
            var backpack = world.CreateItem(); backpack.BaseId = 0x0E75;
            player.Equip(backpack, Layer.Pack);
            var playerBag = world.CreateItem(); playerBag.BaseId = 0x0E76;
            Assert.True(backpack.TryAddItem(playerBag));
            var playerItem = world.CreateItem(); playerItem.BaseId = 0x0EED;
            Assert.True(playerBag.TryAddItem(playerItem));

            var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
            {
                Format = SaveFormat.Text,
                ShardCount = 0,
            };
            Assert.True(saver.Save(world, dir));

            var dst = MakeWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(dst, dir);

            // The whole vendor-stock chain (including the item three levels deep)
            // comes back where it was.
            Assert.NotNull(dst.FindItem(stockContainer.Uid));
            Assert.Equal(stockContainer.Uid, dst.FindItem(stockBag.Uid)!.ContainedIn);
            Assert.Equal(stockBag.Uid, dst.FindItem(stockItem.Uid)!.ContainedIn);
            Assert.Same(dst.FindItem(stockContainer.Uid),
                dst.FindChar(vendor.Uid)!.GetEquippedItem(Layer.VendorStock));

            // Normal player nesting is unaffected — all three levels persist.
            Assert.NotNull(dst.FindItem(backpack.Uid));
            Assert.NotNull(dst.FindItem(playerBag.Uid));
            Assert.NotNull(dst.FindItem(playerItem.Uid));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
