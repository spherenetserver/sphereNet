using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// AUTOHOUSEKEYS decides whether placing a house hands out a key.
///
/// Off, upstream generates none and the doors answer to the house privilege alone
/// (CItemMulti.cpp:417, 1162). The engine generated two either way - one into the
/// pack and a spare into the bank - so a shard that turned the setting off still got
/// keys, and the reference pack's own door script, which branches on the setting to
/// decide whether to run its own access check, was reading a value nothing answered.
/// </summary>
public sealed class AutoHouseKeysTests
{
    private static (HousingEngine Engine, GameWorld World, SphereNet.Game.Objects.Characters.Character Owner)
        Build(bool autoHouseKeys)
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new MultiRegistry();
        var def = new MultiDef { Id = 0x0064, Name = "probe house" };
        def.Components.Add(new MultiComponent
        {
            TileId = 0x0001, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true,
        });
        def.RecalcBounds();
        registry.Register(def);

        var engine = new HousingEngine(world, registry)
        {
            MaxHousesPerPlayer = 10,
            MaxHousesPerAccount = 10,
            AutoHouseKeys = autoHouseKeys,
        };

        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        // A key goes to the pack, and to the ground when there is none - give the
        // owner one so the test observes the ordinary path.
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        owner.Equip(pack, Layer.Pack);
        return (engine, world, owner);
    }

    /// <summary>Keys linked to the placed multi, wherever they landed - the owner in
    /// this harness has no backpack, so a generated key falls to the ground.</summary>
    /// <summary>Keys for this house in the owner's pack.</summary>
    private static int KeyCount(SphereNet.Game.Objects.Characters.Character owner, Item multi) =>
        owner.Backpack?.Contents.Count(i => i.ItemType == ItemType.Key && i.Link == multi.Uid) ?? 0;

    [Fact]
    public void OnTheOwnerGetsAKey()
    {
        var (engine, _, owner) = Build(autoHouseKeys: true);
        var house = engine.PlaceHouse(owner, 0x0064, new Point3D(120, 120, 0, 0));

        Assert.NotNull(house);
        Assert.True(KeyCount(owner, house!.MultiItem) > 0, "placing a house hands out a key");
    }

    [Fact]
    public void OffTheHouseIsPlacedWithNoKeyAtAll()
    {
        var (engine, _, owner) = Build(autoHouseKeys: false);
        var house = engine.PlaceHouse(owner, 0x0064, new Point3D(120, 120, 0, 0));

        Assert.NotNull(house);
        Assert.Equal(0, KeyCount(owner, house!.MultiItem));
    }
}
