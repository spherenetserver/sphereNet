using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Field bugs (2026-07-19, houses): the placed-house sign must survive a
/// double-click (it opens the house gump, nothing more), and .dupe must make
/// FULL clones (a duped house deed lost More1/tags and came out blank) with
/// the Source-X CIV_DUPE copy count honoured.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class HouseSignAndDupeTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character CreatePlayer(GameWorld world)
    {
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        return player;
    }

    private static MultiRegistry CreateHouseRegistryWithSign()
    {
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = 0x0064, Name = "small house" };
        // Wall: visible (client-drawn, never materialized).
        def.Components.Add(new MultiComponent
            { TileId = 0x001A, DeltaX = -2, DeltaY = -2, DeltaZ = 0, Visible = true });
        // Door + sign: invisible placeholders (materialized as working items).
        def.Components.Add(new MultiComponent
            { TileId = 0x06A5, DeltaX = 0, DeltaY = 3, DeltaZ = 0, Visible = false });
        def.Components.Add(new MultiComponent
            { TileId = 0x0BD2, DeltaX = 2, DeltaY = 4, DeltaZ = 0, Visible = false });
        def.RecalcBounds();
        registry.Register(def);
        return registry;
    }

    [Fact]
    public void HouseSignDoubleClick_OpensGump_SignSurvivesUnmoved()
    {
        var world = CreateWorld();
        var housing = new HousingEngine(world, CreateHouseRegistryWithSign());
        var owner = CreatePlayer(world);
        var house = housing.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0))!;

        var sign = house.Components
            .Select(uid => world.FindItem(uid))
            .First(i => i != null && i.BaseId == 0x0BD2)!;
        sign.ItemType = ItemType.SignGump; // pack itemdef 0bd2 → t_sign_gump
        Assert.Equal(house.MultiItem.Uid, sign.Link);
        var signPos = sign.Position;

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2605);
        client.SetEngines(housingEngine: housing);
        TestHarness.AttachCharacter(client, owner);

        client.HandleDoubleClick(sign.Uid.Value);

        Assert.False(sign.IsDeleted);
        Assert.Equal(signPos, sign.Position);
        Assert.Equal((ushort)0x0BD2, sign.BaseId);
        Assert.NotNull(housing.GetHouse(house.MultiItem.Uid));
    }

    /// <summary>The demolish path end-to-end: dclick the sign (from any spot —
    /// the dclick can-see gate is distance-only, no LOS raycast, so standing
    /// INSIDE the house behind its own walls must not hide the sign), press
    /// the Demolish button, and the house converts back to a deed.</summary>
    [Fact]
    public void HouseSignGump_DemolishButton_ReturnsDeedAndTearsDownHouse()
    {
        var world = CreateWorld();
        var housing = new HousingEngine(world, CreateHouseRegistryWithSign());
        var owner = CreatePlayer(world);
        var house = housing.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0))!;
        // Stand INSIDE the footprint, adjacent to the sign (2,4 offset).
        world.MoveCharacter(owner, new Point3D(202, 203, 0, 0));

        var sign = house.Components
            .Select(uid => world.FindItem(uid))
            .First(i => i != null && i.BaseId == 0x0BD2)!;
        sign.ItemType = ItemType.SignGump;
        var componentUids = house.Components.ToArray();

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2607);
        client.SetEngines(housingEngine: housing);
        TestHarness.AttachCharacter(client, owner);

        client.HandleDoubleClick(sign.Uid.Value);
        Assert.False(sign.IsDeleted); // the dclick itself never removes the sign

        // Press "Demolish House" (button 2) on the sign gump.
        client.HandleGumpResponse(owner.Uid.Value, sign.Uid.Value, 2,
            [], Array.Empty<(ushort, string)>());

        Assert.Null(housing.GetHouse(house.MultiItem.Uid));
        Assert.True(house.MultiItem.IsDeleted);
        foreach (var uid in componentUids)
            Assert.Null(world.FindItem(uid));
        var deed = owner.Backpack!.Contents.FirstOrDefault(i => i.ItemType == ItemType.Deed);
        Assert.NotNull(deed);
        Assert.Equal(0x0064u, deed!.More1);
    }

    [Fact]
    public void DupeTarget_MakesFullClones_AndHonoursCount()
    {
        var world = CreateWorld();
        var gm = CreatePlayer(world);
        var deed = world.CreateItem();
        deed.BaseId = 0x14F1;
        deed.ItemType = ItemType.Deed;
        deed.More1 = 0x76;
        deed.Name = "a ship deed";
        deed.SetTag("MORE1_DEFNAME", "m_small_ship");
        world.PlaceItem(deed, new Point3D(150, 150, 0, 0));

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2606);
        TestHarness.AttachCharacter(client, gm);

        client.BeginDupeTarget(5);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId,
            deed.Uid.Value, deed.X, deed.Y, deed.Z, 0);

        var copies = world.GetAllObjects().OfType<Item>()
            .Where(i => i != deed && !i.IsDeleted && i.BaseId == 0x14F1)
            .ToList();
        Assert.Equal(5, copies.Count);
        foreach (var copy in copies)
        {
            Assert.Equal(deed.More1, copy.More1);
            Assert.Equal(ItemType.Deed, copy.ItemType);
            Assert.True(copy.TryGetTag("MORE1_DEFNAME", out string? m1d) && m1d == "m_small_ship",
                "duped deed lost its MORE1_DEFNAME tag");
        }
    }
}
