using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H9 (wiki/hedef.txt long tail):
//   * IT_MAP display: dclick sends the 0x90 map gump + replays stored pins
//     via 0x56 (Source-X CClient::addDrawMap); blank maps only report blank
//   * 0x56 pin-edit actions realigned to Source-X MAPCMD (1 add, 2 insert,
//     3 move, 4 delete, 5 clear, 6 toggle -> mode-7 reply)
//   * <ISMYPET> resolves against SRC in the interpreter (Source-X CHC_ISMYPET)
public class ParityWaveH9Tests
{
    private static (GameWorld world, SphereNet.Game.Clients.GameClient client, Character player)
        CreateClientEnv(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);
        return (world, client, player);
    }

    [Fact]
    public void MapDclick_SendsDisplayGumpAndReplaysPins()
    {
        var (world, client, player) = CreateClientEnv(2001);

        var map = world.CreateItem();
        map.BaseId = 0x14EB;
        map.ItemType = ItemType.Map;
        // m_itMap layout: MORE1 = top(lo)/left(hi), MORE2 = bottom(lo)/right(hi)
        map.More1 = (1000u << 16) | 800u;   // left=1000, top=800
        map.More2 = (1400u << 16) | 1200u;  // right=1400, bottom=1200
        map.SetTag("PIN_1", "1200,1000");
        player.Backpack!.AddItem(map);

        client.HandleDoubleClick(map.Uid.Value);

        var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
        // 0x90 display with the world rect
        Assert.Contains(packets, p => p.Span.Length >= 19 && p.Span[0] == 0x90 &&
            (p.Span[7] << 8 | p.Span[8]) == 1000 &&   // left
            (p.Span[9] << 8 | p.Span[10]) == 800);    // top
        // pin replay: 0x56 mode 1 at 1200,1000
        Assert.Contains(packets, p => p.Span.Length >= 11 && p.Span[0] == 0x56 &&
            p.Span[5] == 1 &&
            (p.Span[7] << 8 | p.Span[8]) == 1200 &&
            (p.Span[9] << 8 | p.Span[10]) == 1000);
    }

    [Fact]
    public void BlankMap_OnlyReportsBlank()
    {
        var (world, client, player) = CreateClientEnv(2002);

        var map = world.CreateItem();
        map.BaseId = 0x14EC;
        map.ItemType = ItemType.MapBlank;
        player.Backpack!.AddItem(map);

        client.HandleDoubleClick(map.Uid.Value);

        var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
        Assert.DoesNotContain(packets, p => p.Span.Length > 0 && p.Span[0] == 0x90);
    }

    [Fact]
    public void PinEdit_FollowsSourceXCommandSemantics()
    {
        var (world, client, _) = CreateClientEnv(2003);

        var map = world.CreateItem();
        map.ItemType = ItemType.Map;
        world.PlaceItem(map, new Point3D(100, 100, 0, 0));
        uint uid = map.Uid.Value;

        client.HandleMapPinEdit(uid, 1, 0, 100, 200); // add -> PIN_1
        client.HandleMapPinEdit(uid, 1, 0, 300, 400); // add -> PIN_2
        Assert.Equal("100,200", map.Tags.Get("PIN_1"));
        Assert.Equal("300,400", map.Tags.Get("PIN_2"));

        client.HandleMapPinEdit(uid, 3, 1, 333, 444); // move pin index 1 (PIN_2)
        Assert.Equal("333,444", map.Tags.Get("PIN_2"));

        client.HandleMapPinEdit(uid, 4, 0, 0, 0); // delete pin index 0 -> tail shifts
        Assert.Equal("333,444", map.Tags.Get("PIN_1"));
        Assert.True(string.IsNullOrEmpty(map.Tags.Get("PIN_2")));

        client.HandleMapPinEdit(uid, 5, 0, 0, 0); // clear
        Assert.True(string.IsNullOrEmpty(map.Tags.Get("PIN_1")));

        // toggle plot mode -> server replies 0x56 mode 7 with the new state
        client.HandleMapPinEdit(uid, 6, 0, 0, 0);
        var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
        Assert.Contains(packets, p => p.Span.Length >= 11 && p.Span[0] == 0x56 &&
            p.Span[5] == 7 && p.Span[6] == 1);
        Assert.Equal("1", map.Tags.Get("PLOTMODE"));
    }

    [Fact]
    public void IsMyPet_ResolvesAgainstSrc()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();

        var owner = world.CreateCharacter();
        owner.Name = "Owner";
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var stranger = world.CreateCharacter();
        stranger.Name = "Stranger";
        world.PlaceCharacter(stranger, new Point3D(101, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Rex";
        world.PlaceCharacter(pet, new Point3D(102, 100, 0, 0));
        Assert.True(pet.TrySetProperty("NPCMASTER", $"0{owner.Uid.Value:X}"));

        var interpreter = new ScriptInterpreter(
            new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());

        var lines = new[] { new ScriptKey("TAG.MINE", "<ISMYPET>") };

        interpreter.Execute(lines, pet, null, new TriggerArgs { Source = owner }, new ScriptScope());
        Assert.True(pet.TryGetProperty("TAG.MINE", out var mine));
        Assert.Equal("1", mine);

        interpreter.Execute(lines, pet, null, new TriggerArgs { Source = stranger }, new ScriptScope());
        Assert.True(pet.TryGetProperty("TAG.MINE", out var notMine));
        Assert.Equal("0", notMine);
    }
}
