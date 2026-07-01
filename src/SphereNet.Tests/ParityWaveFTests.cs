using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-F (wiki/hedef.txt) — combat formulas + vendor economy:
//   * whole-body coverage-weighted AR (Source-X CChar::CalcArmorDefense) —
//     every worn piece softens every blow by its body-coverage share
//   * VENDORMARKUP drives the sell payout (vendor tag → region tag → default)
public class ParityWaveFTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void CalcArmorDefense_IsCoverageWeightedWholeBody()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        // Chest 35% coverage: AR 50 alone → 50*35/100 = 17.
        var tunic = world.CreateItem();
        tunic.SetTag("ARMOR", "50");
        ch.Equip(tunic, Layer.Chest);
        int chestOnly = CombatEngine.CalcArmorDefense(ch);
        Assert.Equal(17, chestOnly);

        // Adding a helm (15% coverage, AR 20 → +3) raises the TOTAL — the old
        // single-region model would have ignored it on chest hits entirely.
        var helm = world.CreateItem();
        helm.SetTag("ARMOR", "20");
        ch.Equip(helm, Layer.Helm);
        Assert.Equal(20, CombatEngine.CalcArmorDefense(ch));
    }

    [Fact]
    public void VendorSellPrice_UsesMarkup()
    {
        var world = CreateWorld();
        VendorEngine.World = world;
        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

        var item = world.CreateItem();
        item.SetTag("PRICE", "100");

        // Default markup 15%: payout = 100*(100-15)/(100+15) = 73.
        Assert.Equal(15, VendorEngine.GetVendorMarkup(vendor));

        // Vendor tag override wins.
        vendor.SetTag("VENDORMARKUP", "50");
        Assert.Equal(50, VendorEngine.GetVendorMarkup(vendor));
    }
}
