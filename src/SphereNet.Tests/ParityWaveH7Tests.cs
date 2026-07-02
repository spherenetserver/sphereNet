using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H7 (wiki/hedef.txt long tail):
//   * ship pilot: SETPILOT verb assigns/releases the wheel; the 0xBF 0x33
//     wheel-boat move subcommand is registered (Source-X SetPilot /
//     PacketWheelBoatMove)
//   * client verb OPENTRADEWINDOW starts a secure trade from script
public class ParityWaveH7Tests
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
    public void SetPilotVerb_AssignsAndReleasesTheWheel()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
        var engine = new ShipEngine(world, new SphereNet.Game.Housing.MultiRegistry(), null);
        var ship = new Ship(multi);

        var pilot = world.CreateCharacter();
        world.PlaceCharacter(pilot, new Point3D(100, 100, 0, 0));

        Assert.True(engine.ExecuteCommand(ship, "SETPILOT", $"0{pilot.Uid.Value:X}"));
        Assert.Equal(pilot.Uid, ship.Pilot);

        Assert.True(engine.ExecuteCommand(ship, "SETPILOT", ""));
        Assert.False(ship.Pilot.IsValid);
    }

    [Fact]
    public void WheelBoatMoveSubcommand_IsRegistered()
    {
        // The 0xBF registry gate drops unknown subcommands — 0x33 must be
        // known or the client's steering packets never reach the handler.
        Assert.True(ExtendedCommandRegistry.IsKnown(0x0033));
    }

    [Fact]
    public void OpenTradeWindowVerb_StartsSecureTrade()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1901);

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));
        var pack1 = world.CreateItem();
        pack1.ItemType = ItemType.Container;
        initiator.Backpack = pack1;
        initiator.Equip(pack1, Layer.Pack);
        TestHarness.AttachCharacter(client, initiator);

        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        partner.IsOnline = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));
        var pack2 = world.CreateItem();
        pack2.ItemType = ItemType.Container;
        partner.Backpack = pack2;
        partner.Equip(pack2, Layer.Pack);

        var tradeManager = new TradeManager();
        client.SetEngines(tradeManager: tradeManager);

        Assert.True(client.TryExecuteScriptCommand(initiator,
            "OPENTRADEWINDOW", $"0{partner.Uid.Value:X}", null));
        Assert.NotNull(tradeManager.FindTradeFor(initiator));
    }
}
