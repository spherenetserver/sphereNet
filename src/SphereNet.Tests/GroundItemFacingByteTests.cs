using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// The byte after the graphic in a world-item packet is a graphic INCREMENT, not a
/// direction: the client adds it to the graphic (0x1A behind the 0x8000 flag,
/// ClassicUO PacketHandlers.cs:802-806; 0xF3's dedicated graphicInc field, :5699).
/// Upstream fills it for one case only - a corpse's facing (adjustItemData,
/// send.cpp:580) - and says why: "with this packet the item can be flippable OR a
/// light source, not both" (send.cpp:510).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GroundItemFacingByteTests
{
    private const ushort Pickaxe = 0x0E85;   // +2 is 0x0E87, a pitchfork
    private const ushort Katana = 0x13FF;    // +1 is 0x1400, a kryss

    private static (GameWorld World, GameClient Client) Setup(bool stygian)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var state = TestHarness.CreateActiveNetState(lf, 4501);
        state.ClientVersionNumber = stygian ? 70_009_000u : 50_000_000u;
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.IsOnline = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (world, client);
    }

    private static Item Drop(GameWorld world, ushort id, byte facing, ItemType type = ItemType.Normal)
    {
        var it = world.CreateItem();
        it.BaseId = id;
        it.ItemType = type;
        it.Direction = facing;
        world.PlaceItem(it, new Point3D(101, 100, 0, 0));
        return it;
    }

    /// <summary>0xF3: [F3][0001][type][serial:4][graphic:2][graphicInc:1]...</summary>
    private static byte GraphicIncSA(NetState state, uint serial)
    {
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length < 12 || s[0] != 0xF3) continue;
            uint ser = (uint)((s[4] << 24) | (s[5] << 16) | (s[6] << 8) | s[7]);
            if (ser != serial) continue;
            return s[10];
        }
        return 0xFF; // not found
    }

    /// <summary>0x1A: [1A][len:2][serial:4][graphic:2]... x carries 0x8000 when the
    /// flip/light byte is present, and that byte sits after y.</summary>
    private static byte GraphicIncLegacy(NetState state, uint serial)
    {
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length < 14 || s[0] != 0x1A) continue;
            uint ser = (uint)((s[3] << 24) | (s[4] << 16) | (s[5] << 8) | s[6]);
            bool hasAmount = (ser & 0x80000000) != 0;
            ser &= 0x7FFFFFFF;
            if (ser != serial) continue;
            int i = 7 + 2;                  // graphic
            if (hasAmount) i += 2;          // amount
            ushort x = (ushort)((s[i] << 8) | s[i + 1]);
            if ((x & 0x8000) == 0) return 0;   // no flip/light byte at all
            i += 4;                         // x, y
            return s[i];
        }
        return 0xFF;
    }

    [Fact]
    public void AnOrdinaryGroundItemSendsNoGraphicIncrement()
    {
        // A dropped item is given a facing of (items already on the tile % 7) + 1, so
        // this is the everyday case: a pickaxe arriving as a pitchfork.
        var (world, client) = Setup(stygian: true);
        var pick = Drop(world, Pickaxe, facing: 2);

        client.SendWorldItem(pick);

        Assert.Equal(0, GraphicIncSA(client.NetState, pick.Uid.Value));
    }

    [Fact]
    public void AnOrdinaryGroundItemSendsNoFlipByteOnTheLegacyPacket()
    {
        var (world, client) = Setup(stygian: false);
        var katana = Drop(world, Katana, facing: 1);

        client.SendWorldItem(katana);

        Assert.Equal(0, GraphicIncLegacy(client.NetState, katana.Uid.Value));
    }

    [Fact]
    public void ACorpseStillSendsItsFacing()
    {
        // The one case upstream fills it for: m_itCorpse.m_facing_dir.
        var (world, client) = Setup(stygian: true);
        var corpse = Drop(world, 0x2006, facing: 3, type: ItemType.Corpse);

        client.SendWorldItem(corpse);

        Assert.Equal(3, GraphicIncSA(client.NetState, corpse.Uid.Value));
    }
}
