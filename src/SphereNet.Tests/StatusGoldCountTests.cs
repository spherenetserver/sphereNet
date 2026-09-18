using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// The gold on the status bar is the gold the character can spend.
///
/// Upstream picks that figure with the same switch that decides where payment comes
/// from - virtual gold, the pack only, or everything carried (send.cpp:235) - so the
/// number shown and the number spendable are one thing. Here the status counted the
/// pack's TOP LEVEL only, and only the single graphic 0x0EED, while payment counted
/// the pack recursively and by item type. Gold in a pouch was money the player had and
/// could not see.
///
/// There were two copies of that top-level count in the status path, which is how they
/// came to disagree with the payment path and with each other.
/// </summary>
public sealed class StatusGoldCountTests
{
    private static (SphereNet.Game.World.GameWorld World,
                    SphereNet.Game.Clients.GameClient Client,
                    SphereNet.Game.Objects.Characters.Character Me,
                    Item Pack) Build(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        SphereNet.Game.Trade.VendorEngine.World = world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.PrivLevel = PrivLevel.Player;
        me.Str = 100; me.Dex = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
        me.Backpack = pack; me.Equip(pack, Layer.Pack);
        return (world, client, me, pack);
    }

    private static Item Gold(SphereNet.Game.World.GameWorld w, ushort amount, ushort id = 0x0EED)
    {
        var g = w.CreateItem();
        g.BaseId = id;
        g.ItemType = ItemType.Gold;
        g.Amount = amount;
        return g;
    }

    /// <summary>Read the gold field back off the status packet the client was sent.</summary>
    private static int StatusGold(SphereNet.Game.Clients.GameClient client)
    {
        TestHarness.GetQueuedPackets(client.NetState).Clear();
        client.SendCharacterStatus(client.Character!);
        foreach (var buf in TestHarness.GetQueuedPackets(client.NetState))
        {
            var span = buf.Span;
            if (span.Length < 60 || span[0] != 0x11) continue;
            // 0x11: id, len(2), serial(4), name(30), hits(2), maxhits(2), namechange(1),
            // flag(1), sex(1), str(2), dex(2), int(2), stam(2), maxstam(2), mana(2),
            // maxmana(2), gold(4)
            int at = 1 + 2 + 4 + 30 + 2 + 2 + 1 + 1 + 1 + 2 + 2 + 2 + 2 + 2 + 2 + 2;
            return (span[at] << 24) | (span[at + 1] << 16) | (span[at + 2] << 8) | span[at + 3];
        }
        return -1;
    }

    [Fact]
    public void GoldLyingInThePackIsCounted()
    {
        var (w, client, _, pack) = Build(8931);
        Assert.True(pack.TryAddItem(Gold(w, 500)));
        Assert.Equal(500, StatusGold(client));
    }

    /// <summary>The case that was invisible: a pouch of gold inside the pack.</summary>
    [Fact]
    public void GoldInsideAPouchIsCountedToo()
    {
        var (w, client, _, pack) = Build(8932);
        Assert.True(pack.TryAddItem(Gold(w, 500)));

        var pouch = w.CreateItem();
        pouch.BaseId = 0x0E76; pouch.ItemType = ItemType.Container;
        Assert.True(pack.TryAddItem(pouch));
        Assert.True(pouch.TryAddItem(Gold(w, 250)));

        Assert.Equal(750, StatusGold(client));
    }

    /// <summary>A gold def drawn as something else still counts - the type says what
    /// it is, not the graphic.</summary>
    [Fact]
    public void GoldWithAnotherGraphicStillCounts()
    {
        var (w, client, _, pack) = Build(8933);
        Assert.True(pack.TryAddItem(Gold(w, 300, id: 0x0EEA)));
        Assert.Equal(300, StatusGold(client));
    }
}
