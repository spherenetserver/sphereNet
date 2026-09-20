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
/// Source-X CClientUse IT_SHIP_TILLER: normal player reach, classic speech,
/// HS pilot assignment, guarded default shore-side dry docking and script REDEED.
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

        // Moored two tiles away: within touch range, outside the ship region.
        var ship = ships.PlaceShip(me, 0x4000, new Point3D(102, 100, 0, 0),
                                   Direction.North, magic: true);
        Assert.NotNull(ship);

        var tiller = ship!.Components
            .Select(world.FindItem)
            .First(c => c!.ItemType == ItemType.ShipTiller)!;
        me.PrivLevel = PrivLevel.Player;
        client.NetState.ClientVersionNumber = 60_000_000;
        return new Bench(world, ships, ship, client, me, tiller);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void HoldOpeningRequiresBeingOnLinkedShipExceptForGm(bool aboard, bool gm, bool opens)
    {
        var b = Build(8971);
        var hold = b.Ship.GetHold(b.World)!;
        if (aboard) b.World.MoveCharacter(b.Me, b.Ship.MultiItem.Position);
        if (gm) b.Me.PrivLevel = PrivLevel.GM;
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.ItemUse.HandleDoubleClick(hold.Uid.Value);
        var packets = TestHarness.GetQueuedPackets(b.Client.NetState);
        Assert.Equal(opens, packets.Any(p => p.Span[0] == 0x24));
        Assert.Equal(opens, packets.Any(p => p.Span[0] == 0x3C));
        if (!opens)
            Assert.Contains(packets, p => p.Length > 48 && p.Span[0] == 0xAE &&
                System.Text.Encoding.BigEndianUnicode.GetString(p.Span[48..]).TrimEnd('\0') ==
                SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.ItemuseHatchFail));
    }

    [Fact]
    public void BeingOnAnotherShipDoesNotAllowOpeningTheHold()
    {
        var b = Build(8972);
        b.Ships.MaxShipsPerAccount = 4;
        var hold = b.Ship.GetHold(b.World)!;
        b.Me.PrivLevel = PrivLevel.Owner;
        var other = b.Ships.PlaceShip(b.Me, 0x4000, new Point3D(100, 100, 0, 0), Direction.North, magic: true);
        Assert.NotNull(other);
        b.Me.PrivLevel = PrivLevel.Player;
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.ItemUse.HandleDoubleClick(hold.Uid.Value);
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(b.Client.NetState), p => p.Span[0] is 0x24 or 0x3C);
    }

    /// <summary>The reported case, end to end. If the handler throws, this test says so
    /// with the exception rather than leaving it to a live session.</summary>
    [Fact]
    public void ClassicTillerDClickDryDocksEmptyOwnedShipFromShore()
    {
        var b = Build(8951);
        var multiUid = b.Ship.MultiItem.Uid;

        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);

        Assert.Null(b.Ships.GetShip(multiUid));
        Assert.Single(b.Me.Backpack!.Contents, i => i.ItemType == ItemType.Deed);
        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
        Assert.Single(b.Me.Backpack!.Contents, i => i.ItemType == ItemType.Deed);
    }

    /// <summary>Classic tiller use does not change the hold contents.</summary>
    [Fact]
    public void ClassicTillerDClickLeavesCargoAlone()
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
        Assert.NotNull(b.Ships.GetShip(b.Ship.MultiItem.Uid));
        Assert.DoesNotContain(b.Me.Backpack!.Contents, i => i.ItemType == ItemType.Deed);
    }

    [Theory]
    [InlineData("other-owner", "tiller_notyourship")]
    [InlineData("moving", "ship_drydock_moving")]
    [InlineData("passenger", "ship_drydock_passengers")]
    [InlineData("deck-item", "ship_drydock_deck")]
    [InlineData("full-pack", "ship_drydock_pack")]
    [InlineData("hold", "ship_drydock_hold")]
    [InlineData("invalid", "ship_drydock_invalid")]
    public void ShoreDryDockRejectsUnsafeRequestsWithoutDeletingShip(string reason, string messageKey)
    {
        var b = Build(8968);
        switch (reason)
        {
            case "other-owner": b.Ship.Owner = b.World.CreateCharacter().Uid; break;
            case "moving": b.Ship.MovementType = ShipMovementType.Normal; break;
            case "invalid": b.Ship.RegionUid = 0; break;
            case "hold":
                Assert.True(b.Ship.GetHold(b.World)!.TryAddItem(b.World.CreateItem()));
                break;
            case "passenger":
                b.World.PlaceCharacter(b.World.CreateCharacter(), b.Ship.MultiItem.Position);
                break;
            case "deck-item":
                b.World.PlaceItem(b.World.CreateItem(), b.Ship.MultiItem.Position);
                break;
            case "full-pack":
                while (b.Me.Backpack!.Contents.Count < Item.MaxContainerItems)
                    Assert.True(b.Me.Backpack.TryAddItem(b.World.CreateItem()));
                break;
        }
        var resources = ScriptTestBootstrap.CreateRuntimeStack().Resources;
        string messagesPath = Path.Combine(_dir, "messages.scp");
        File.WriteAllText(messagesPath, $"[DEFMESSAGE]\n{messageKey}=Translated {reason}\n");
        resources.LoadResourceFile(messagesPath);
        var oldMessage = SphereNet.Game.Messages.ServerMessages.Get(messageKey);
        try
        {
            SphereNet.Game.Messages.ServerMessages.LoadOverrides(resources.GetAllDefMessages());
            TestHarness.ClearQueuedPackets(b.Client.NetState);
            b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
            Assert.Contains(TestHarness.GetQueuedPackets(b.Client.NetState), p =>
                p.Length > 48 && p.Span[0] == 0xAE &&
                System.Text.Encoding.BigEndianUnicode.GetString(p.Span[48..]).TrimEnd('\0') == $"Translated {reason}");
        }
        finally { SphereNet.Game.Messages.ServerMessages.SetOverride(messageKey, oldMessage); }
        Assert.NotNull(b.Ships.GetShip(b.Ship.MultiItem.Uid));
        Assert.All(b.Ship.Components, uid => Assert.NotNull(b.World.FindItem(uid)));
        Assert.DoesNotContain(b.Me.Backpack!.Contents, i => i.ItemType == ItemType.Deed);
    }

    /// <summary>Source-X identifies the ship for use commands by region, including
    /// positions in that rectangle which do not have a multi component.</summary>
    [Fact]
    public void ShipCommandsUseTheSourceXRegionEvenBetweenHullTiles()
    {
        var b = Build(8953);
        var hull = b.Ship.MultiItem;

        // (1,1) is inside the bounding rectangle of this hull and is not one of its
        // tiles. At the deck's own height, so nothing but the tile separates the two.
        var quay = new Point3D((short)(hull.X + 1), (short)(hull.Y + 1), hull.Z, 0);
        b.World.MoveCharacter(b.Me, quay);

        Assert.NotNull(b.Ships.FindShipAt(quay));          // the rectangle covers it
        Assert.Equal(b.Ship, b.Ships.FindShipCarrying(b.Me));

        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
        Assert.NotNull(b.Ships.GetShip(hull.Uid));
    }

    /// <summary>Classic tiller use on board also leaves the ship intact.</summary>
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

    [Fact]
    public void HighSeasTillerAssignsAndReleasesTheOwnerAsPilot()
    {
        var b = Build(8955);
        b.Client.NetState.ClientVersionNumber = 70_009_000;
        b.World.MoveCharacter(b.Me, b.Ship.MultiItem.Position);
        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
        Assert.Equal(b.Me.Uid, b.Ship.Pilot);
        Assert.NotNull(b.Me.GetEquippedItem(Layer.Horse));
        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
        Assert.False(b.Ship.Pilot.IsValid);
        Assert.Null(b.Me.GetEquippedItem(Layer.Horse));
        Assert.NotNull(b.Ships.GetShip(b.Ship.MultiItem.Uid));
    }

    [Fact]
    public void HighSeasTillerFromTheShoreDryDocksWithoutAssigningPilot()
    {
        var b = Build(8956);
        b.Client.NetState.ClientVersionNumber = 70_009_000;
        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
        Assert.False(b.Ship.Pilot.IsValid);
        Assert.Null(b.Ships.GetShip(b.Ship.MultiItem.Uid));
        Assert.Single(b.Me.Backpack!.Contents, i => i.ItemType == ItemType.Deed);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, 24)]
    public void NormalPlayerStillNeedsSourceXTouchRange(int dx, int dz)
    {
        var b = Build(8957);
        b.Client.NetState.ClientVersionNumber = 70_009_000;
        b.World.MoveCharacter(b.Me, new Point3D((short)(b.Tiller.X + dx),
            b.Tiller.Y, (sbyte)(b.Tiller.Z + dz), b.Tiller.MapIndex));
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.ItemUse.HandleDoubleClick(b.Tiller.Uid.Value);
        Assert.False(b.Ship.Pilot.IsValid);
        Assert.Contains(TestHarness.GetQueuedPackets(b.Client.NetState), p =>
            p.Length > 50 && p.Span[0] == 0xAE &&
            System.Text.Encoding.BigEndianUnicode.GetString(p.Span[48..]).TrimEnd('\0')
                == "You can't reach that.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RedeedVerbPreservesItsFlagsAndSource(bool show, bool bank)
    {
        var b = Build(8959);
        Item.RedeedShip = (uid, display, toBank, source) =>
        {
            Assert.Equal(show, display);
            Assert.Equal(bank, toBank);
            Assert.Same(b.Me, source);
            return b.Ships.RedeedFromScript(uid, display, toBank, source);
        };
        b.Ships.OnShipRedeed = (multi, args) =>
        {
            Assert.Same(b.Ship.MultiItem, multi);
            Assert.Same(b.Me, args.CharSrc);
            Assert.IsType<Item>(args.O1);
            Assert.Equal(0x14F1, args.N1);
            Assert.Equal(1, args.N2);
            Assert.Equal(bank ? 1 : 0, args.N3);
            return TriggerResult.Default;
        };
        try
        {
            Assert.True(b.Ship.MultiItem.TryExecuteCommand("REDEED",
                $"{(show ? 1 : 0)},{(bank ? 1 : 0)}", b.Client));
            var destination = bank ? b.Me.GetEquippedItem(Layer.BankBox) : b.Me.Backpack;
            Assert.Contains(destination!.Contents, i => i.ItemType == ItemType.Deed);
            Assert.Null(b.Ships.GetShip(b.Ship.MultiItem.Uid));
        }
        finally { Item.RedeedShip = null; }
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    public void RedeedReadsBackTransferFlags(long transfer, long bank, bool expectedBank)
    {
        var b = Build(8960);
        b.Ships.OnShipRedeed = (_, args) =>
        {
            args.N2 = transfer;
            args.N3 = bank;
            return TriggerResult.Default;
        };
        var deed = b.Ships.RedeedFromScript(b.Ship.MultiItem.Uid, moveToBank: true);
        var destination = expectedBank ? b.Me.GetEquippedItem(Layer.BankBox) : b.Me.Backpack;
        Assert.Equal(destination!.Uid, deed!.ContainedIn);
    }

    [Fact]
    public void RedeedReturnOneSuppressesDeedButStillRemovesShip()
    {
        var b = Build(8961);
        Item? created = null;
        b.Ships.OnShipRedeed = (_, args) =>
        {
            created = (Item)args.O1!;
            return TriggerResult.True;
        };
        Assert.Null(b.Ships.RedeedFromScript(b.Ship.MultiItem.Uid));
        Assert.True(created!.IsDeleted);
        Assert.Null(b.Ships.GetShip(b.Ship.MultiItem.Uid));
        Assert.DoesNotContain(b.Me.Backpack!.Contents, i => i.ItemType == ItemType.Deed);
    }

    [Fact]
    public void NoRedeedTriggerLeavesLooseItemsWhereTheyAre()
    {
        var b = Build(8962);
        var cargo = b.World.CreateItem();
        b.World.PlaceItem(cargo, b.Ship.MultiItem.Position.WithZ(3));
        var oldPosition = cargo.Position;
        Assert.NotNull(b.Ships.RedeedFromScript(b.Ship.MultiItem.Uid));
        Assert.False(cargo.IsDeleted);
        Assert.False(cargo.ContainedIn.IsValid);
        Assert.Equal(oldPosition, cargo.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedeedCargoFollowsScriptBankFlag(bool bank)
    {
        var b = Build(8963);
        var cargo = b.World.CreateItem();
        b.World.PlaceItem(cargo, b.Ship.MultiItem.Position.WithZ(3));
        b.Ships.OnShipRedeed = (_, args) =>
        {
            args.N2 = 1;
            args.N3 = bank ? 1 : 0;
            return TriggerResult.Default;
        };
        Assert.NotNull(b.Ships.RedeedFromScript(b.Ship.MultiItem.Uid));
        var crate = b.World.FindItem(cargo.ContainedIn);
        Assert.NotNull(crate);
        if (bank)
            Assert.Equal(b.Me.GetEquippedItem(Layer.BankBox)!.Uid, crate.ContainedIn);
        else
        {
            Assert.False(crate.ContainedIn.IsValid);
            Assert.Equal(b.Ship.MultiItem.Position.WithZ(-20), crate.Position);
        }
    }
}
