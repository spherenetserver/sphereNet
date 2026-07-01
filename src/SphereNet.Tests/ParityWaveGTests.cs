using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-G (wiki/hedef.txt) — housing/ship long tail:
//   * lockdown/secure stamp ATTR_LOCKEDDOWN / ATTR_SECURE on the item and link
//     it to the multi (Source-X CItemMulti::LockItem/Secure)
//   * house placement generates a pack key + bank spare (Multi_Setup GenerateKey)
//   * redeed sweeps LOOSE floor items into the moving crate (TRANSFER_ALL)
//   * ship plank opens (side→plank, 5s autoclose timer) and closes back
public class ParityWaveGTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (HousingEngine housing, Character owner) MakeHousingSetup(GameWorld world)
    {
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = 0x1404, Name = "small house" };
        def.Components.Add(new MultiComponent { TileId = 0x0064, DeltaX = -2, DeltaY = -2, DeltaZ = 0, Visible = true });
        def.Components.Add(new MultiComponent { TileId = 0x0065, DeltaX = 2, DeltaY = 2, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        registry.Register(def);
        var housing = new HousingEngine(world, registry);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(1490, 1490, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);
        var bank = world.CreateItem();
        bank.ItemType = ItemType.Container;
        owner.Equip(bank, Layer.BankBox);

        return (housing, owner);
    }

    [Fact]
    public void PlaceHouse_GeneratesPackKeyAndBankSpare()
    {
        var world = CreateWorld();
        var (housing, owner) = MakeHousingSetup(world);

        var house = housing.PlaceHouse(owner, 0x1404, new Point3D(1500, 1500, 0, 0));
        Assert.NotNull(house);

        var packKey = owner.Backpack!.Contents.FirstOrDefault(i => i.ItemType == ItemType.Key);
        Assert.NotNull(packKey);
        Assert.True(packKey!.TryGetTag("LINK", out string? lk));
        Assert.Equal(house!.MultiItem.Uid.Value.ToString(), lk);

        var bank = owner.GetEquippedItem(Layer.BankBox)!;
        Assert.Contains(bank.Contents, i => i.ItemType == ItemType.Key);
    }

    [Fact]
    public void Lockdown_StampsAttrAndLink_ReleaseClears()
    {
        var world = CreateWorld();
        var (housing, owner) = MakeHousingSetup(world);
        var house = housing.PlaceHouse(owner, 0x1404, new Point3D(1500, 1500, 0, 0))!;

        var chair = world.CreateItem();
        world.PlaceItem(chair, new Point3D(1500, 1500, 0, 0));

        Assert.True(house.Lockdown(chair.Uid, owner.Uid));
        Assert.True(chair.IsAttr(ObjAttributes.LockedDown)); // was never set pre-W-G
        Assert.Equal(house.MultiItem.Uid, chair.Link);

        Assert.True(house.ReleaseLockdown(chair.Uid, owner.Uid));
        Assert.False(chair.IsAttr(ObjAttributes.LockedDown));

        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        world.PlaceItem(chest, new Point3D(1500, 1501, 0, 0));
        Assert.True(house.SecureContainer(chest.Uid, owner.Uid));
        Assert.True(chest.IsAttr(ObjAttributes.Secure));
    }

    [Fact]
    public void Redeed_SweepsLooseFloorItemsIntoMovingCrate()
    {
        var world = CreateWorld();
        var (housing, owner) = MakeHousingSetup(world);
        var house = housing.PlaceHouse(owner, 0x1404, new Point3D(1500, 1500, 0, 0))!;

        // A loose (non-locked) item sitting on the house floor.
        var vase = world.CreateItem();
        vase.Name = "a vase";
        world.PlaceItem(vase, new Point3D(1500, 1499, 0, 0));

        var deed = house.Redeed(world);
        Assert.NotNull(deed);

        // The vase rode into the moving crate (delivered to the bank box)
        // instead of being orphaned on the open ground.
        var bank = owner.GetEquippedItem(Layer.BankBox)!;
        var crate = bank.Contents.FirstOrDefault(i => i.Name == "a moving crate");
        Assert.NotNull(crate);
        Assert.Contains(crate!.Contents, i => i.Uid == vase.Uid);
    }

    [Fact]
    public void ShipPlank_OpensFromSide_AndClosesBack()
    {
        var world = CreateWorld();
        var side = world.CreateItem();
        side.BaseId = 0x3EB1;
        side.More1 = 0x3E84; // open-plank graphic counterpart
        side.ItemType = ItemType.ShipSideLocked;

        Assert.True(side.OpenPlank());
        Assert.Equal(ItemType.ShipPlank, side.ItemType);
        Assert.Equal(0x3E84, side.BaseId);        // graphic swapped
        Assert.Equal(0x3EB1u, side.More1);        // old graphic remembered
        Assert.True(side.Timeout > 0);            // 5s autoclose armed

        Assert.True(side.ClosePlank());
        Assert.Equal(ItemType.ShipSideLocked, side.ItemType); // original side type restored
        Assert.Equal(0x3EB1, side.BaseId);
        Assert.Equal(0L, side.Timeout);
    }
}
