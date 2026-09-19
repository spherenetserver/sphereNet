using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Double-clicking the tillerman from the dock turns the ship back into a deed, and
/// cannot take the connection down trying.
///
/// A handler that throws costs the player their session: the network loop logs it and
/// marks the connection closing (NetworkManager), which from the client's side is a
/// socket that stops answering - reported from a shard as the client freezing and needing
/// a restart, with the ship still afloat afterwards.
///
/// This path only became reachable once the reach test started honouring the tillerman's
/// own CAN_I_DCIGNORELOS, so it had never actually run against a shard's ship.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TillerRedeedDClickTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_tiller_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Item.ResolveShipEngine = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A ship component's category comes from its ITEMDEF TYPE, as upstream's
    /// does, so the definitions have to be loaded for the tiller to be a tiller.</summary>
    private void LoadShipPartDefs()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "ship.scp");
        File.WriteAllLines(file,
        [
            "[ITEMDEF 03e4a]", "DEFNAME=i_probe_tillerman", "TYPE=t_ship_tiller",
            "CAN=04000", "",
            "[ITEMDEF 03eae]", "DEFNAME=i_probe_hold", "TYPE=t_ship_hold", "",
            "[ITEMDEF 03eb2]", "DEFNAME=i_probe_plank", "TYPE=t_ship_plank", "",
        ]);
        using var lf = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(
            lf.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new SphereNet.Game.Definitions.DefinitionLoader(
            resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
    }

    private sealed record Bench(GameWorld World, ShipEngine Ships, Ship Ship,
                                SphereNet.Game.Clients.GameClient Client,
                                SphereNet.Game.Objects.Characters.Character Me,
                                Item Tiller);

    private Bench Build(int port)
    {
        LoadShipPartDefs();
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        // A hull with the components a ship really has: an invisible tiller, a hold and
        // a plank, plus a visible deck tile the client draws itself.
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = 0x4000, Name = "a small ship" };
        void Part(ushort tile, short dx, short dy, bool visible) =>
            def.Components.Add(new MultiComponent
            { TileId = tile, DeltaX = dx, DeltaY = dy, DeltaZ = 0, Visible = visible });
        Part(0x3E4A, 0, 2, false);    // tillerman
        Part(0x3EAE, 0, 0, false);    // hold
        Part(0x3EB2, 1, 0, false);    // plank
        Part(0x0001, 0, 1, true);     // deck the client draws
        def.RecalcBounds();
        registry.Register(def);

        var ships = new ShipEngine(world, registry, null) { MaxShipsPerPlayer = 4 };
        Item.ResolveShipEngine = () => ships;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.PrivLevel = PrivLevel.Owner;
        me.Str = me.Dex = me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
        me.Backpack = pack; me.Equip(pack, Layer.Pack);

        // Moored two tiles away, so the character stands OFF it - which is what the
        // redeed requires.
        var ship = ships.PlaceShip(me, 0x4000, new Point3D(102, 100, 0, 0),
                                   Direction.North, magic: true);
        Assert.NotNull(ship);

        var tiller = ship!.Components
            .Select(world.FindItem)
            .First(c => c!.ItemType == ItemType.ShipTiller)!;
        return new Bench(world, ships, ship, client, me, tiller);
    }

    /// <summary>The reported case, end to end. If the handler throws, this test says so
    /// with the exception rather than leaving it to a live session.</summary>
    [Fact]
    public void TheTillerDClickRedeedsTheShipWithoutThrowing()
    {
        var b = Build(8951);
        var multiUid = b.Ship.MultiItem.Uid;

        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);

        Assert.Null(b.Ships.GetShip(multiUid));
        Assert.True(b.World.FindItem(multiUid) == null ||
                    b.World.FindItem(multiUid)!.IsDeleted,
            "the hull is still in the world");
        Assert.Contains(b.Me.Backpack!.Contents, i => i.ItemType == ItemType.Deed);
    }

    /// <summary>And with cargo in the hold, which is the path that builds moving
    /// crates.</summary>
    [Fact]
    public void CargoInTheHoldSurvivesTheRedeed()
    {
        var b = Build(8952);
        var hold = b.Ship.Components.Select(b.World.FindItem)
                         .First(c => c!.ItemType is ItemType.ShipHold or ItemType.ShipHoldLock)!;
        var bank = b.World.CreateItem();
        bank.BaseId = 0x09AB; bank.ItemType = ItemType.EqBankBox;
        b.Me.Equip(bank, Layer.BankBox);

        var cargo = b.World.CreateItem();
        cargo.BaseId = 0x1BFB; cargo.ItemType = ItemType.WeaponMaceSharp;
        Assert.True(hold.TryAddItem(cargo));

        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);

        Assert.False(cargo.IsDeleted, "the cargo went down with the ship");
    }

    /// <summary>The reported refusal: standing on the quay beside the bow. The tile is
    /// inside the hull's bounding RECTANGLE but is not a tile the hull occupies - a hull
    /// narrows at the bow, so its rectangle takes in the water and the quay next to it.
    /// Asking the ship region, which is that rectangle, said the owner was aboard, and the
    /// redeed needs them off the ship, so the tillerman only ever talked.
    ///
    /// Height cannot make this distinction: a dock sits at about the height of the deck
    /// it serves.</summary>
    [Fact]
    public void StandingBesideTheHullIsNotBeingAboard()
    {
        var b = Build(8953);
        var hull = b.Ship.MultiItem;

        // (1,1) is inside the bounding rectangle of this hull and is not one of its
        // tiles. At the deck's own height, so nothing but the tile separates the two.
        var quay = new Point3D((short)(hull.X + 1), (short)(hull.Y + 1), hull.Z, 0);
        b.World.MoveCharacter(b.Me, quay);

        Assert.NotNull(b.Ships.FindShipAt(quay));          // the rectangle covers it
        Assert.Null(b.Ships.FindShipCarrying(b.Me));       // the hull does not

        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
        Assert.Null(b.Ships.GetShip(hull.Uid));
    }

    /// <summary>And someone standing on a tile the hull occupies is aboard, so the
    /// tillerman talks instead of dry-docking the ship out from under them.</summary>
    [Fact]
    public void StandingOnTheDeckIsBeingAboard()
    {
        var b = Build(8954);
        var hull = b.Ship.MultiItem;
        b.World.MoveCharacter(b.Me, new Point3D(hull.X, (short)(hull.Y + 1), hull.Z, 0));

        Assert.Equal(b.Ship, b.Ships.FindShipCarrying(b.Me));

        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
        Assert.NotNull(b.Ships.GetShip(hull.Uid));   // still afloat, with them on it
    }
}
