using System.Reflection;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// Field bug (2026-07-19, round 3): ship deeds BOUGHT FROM A VENDOR stayed
/// blank even after the legacy repair. PopulateVendorStock built bare stock
/// items (BaseId + name + price) — no ITEMDEF routing tags, no type stamp and
/// no @Create — so a bought deed never received its MORE=multi reference, and
/// any vendor-sold item lost its scripted creation. Source-X restocks through
/// CreateTemplate → GenerateScript, which fires @Create per stock item.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VendorStockCreateTriggerTests
{
    private static string WriteScript(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_vendstock_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void VendorStock_FiresCreateTrigger_DeedCarriesMultiReference()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [ITEMDEF 014f1]
            DEFNAME=i_deed_ship
            TYPE=t_deed

            [ITEMDEF i_deed_test_ship]
            ID=i_deed_ship
            NAME=Deed to a Test Ship
            VALUE=12500
            ON=@Create
                MORE=m_test_ship

            [MULTIDEF 0]
            DEFNAME=m_test_ship
            TYPE=t_ship

            [TEMPLATE t_test_shipwright_stock]
            ITEM=i_deed_test_ship
            """);
        stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var oldResolveDefName = Item.ResolveDefName;
        var oldCreateHook = Item.CreateTriggerHook;
        try
        {
            // Production wiring: ItemDef-only numeric resolver (multi defnames
            // fall through to the MORE1_DEFNAME tag) + the @Create dispatcher.
            Item.ResolveDefName = defname =>
            {
                var rid = stack.Resources.ResolveDefName(defname);
                if (rid.IsValid && rid.Type == ResType.ItemDef)
                {
                    var def = DefinitionLoader.GetItemDef(rid.Index);
                    return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
                }
                return 0;
            };
            Item.CreateTriggerHook = item =>
                stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item });

            var world = TestHarness.CreateWorld();
            var vendor = world.CreateCharacter();
            vendor.NpcBrain = NpcBrainType.Vendor;
            world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

            var populate = typeof(SphereNet.Game.Objects.Characters.Character)
                .GetMethod("PopulateVendorStock", BindingFlags.Instance | BindingFlags.NonPublic)!;
            populate.Invoke(vendor, ["t_test_shipwright_stock", false]);

            var stockPack = vendor.GetEquippedItem(Layer.VendorStock);
            Assert.NotNull(stockPack);
            var deed = Assert.Single(stockPack!.Contents);

            // The stock deed must be a fully materialised instance: a def
            // routing tag (ITEMDEF by defname or SCRIPTDEF by index), the deed
            // type, and the @Create-assigned multi reference in a form
            // TryResolveDeedMulti understands (tag or More1).
            Assert.True(deed.TryGetTag("ITEMDEF", out _) || deed.TryGetTag("SCRIPTDEF", out _),
                "stock item carries no def routing tag");
            Assert.Equal(ItemType.Deed, deed.ItemType);
            bool hasMultiRef = deed.More1 != 0 ||
                (deed.TryGetTag("MORE1_DEFNAME", out string? m1d) && m1d == "m_test_ship");
            Assert.True(hasMultiRef, "stock deed carries no multi reference — @Create did not run");
        }
        finally
        {
            Item.ResolveDefName = oldResolveDefName;
            Item.CreateTriggerHook = oldCreateHook;
        }
    }
}
