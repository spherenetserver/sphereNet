using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The movable flag on a ground item, and who gets it.
///
/// Upstream sets ITEMF_MOVABLE whenever THIS viewer could actually move the item -
/// CanMoveItem, not a staff test (PacketItemWorld::adjustItemData, send.cpp:583). The
/// client turns its absence into IsLocked for anything the tiledata calls heavy
/// (Item.cs:104: no Movable bit AND weight > 90), and a locked ground item cannot be
/// dragged, cannot be highlighted on mouse-over, and sorts differently
/// (GameActions.cs:457, ItemView.cs:123, GameSceneDrawingSorting.cs:1072).
///
/// This engine set the bit for staff only, so an ordinary player could not pick up a
/// heavy-but-movable item at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GroundItemFlagsTests
{
    private readonly ITestOutputHelper _out;
    public GroundItemFlagsTests(ITestOutputHelper output) => _out = output;

    private static (GameClient Client, Character Me, Item It, GameWorld World) Stage(int port)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var lf = LoggerFactory.Create(_ => { });

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.IsOnline = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));
        return (client, me, item, world);
    }

    /// <summary>The 0x1A flags byte, when one was sent. The y field's 0x4000 bit says
    /// whether it follows; z comes first, then the optional hue, then the flags.</summary>
    private static byte? FlagsOf(GameClient client, uint uid)
    {
        foreach (var p in TestHarness.GetQueuedPackets(client.NetState))
        {
            var s = p.Span;
            if (s.Length < 12 || s[0] != 0x1A) continue;
            int o = 3;
            uint serial = (uint)((s[o] << 24) | (s[o + 1] << 16) | (s[o + 2] << 8) | s[o + 3]);
            bool hasAmount = (serial & 0x80000000) != 0;
            serial &= 0x7FFFFFFF;
            if (serial != uid) continue;
            o += 4;
            ushort graphic = (ushort)((s[o] << 8) | s[o + 1]);
            o += 2;
            if ((graphic & 0x8000) != 0) o += 1;
            if (hasAmount) o += 2;
            ushort x = (ushort)((s[o] << 8) | s[o + 1]); o += 2;
            ushort y = (ushort)((s[o] << 8) | s[o + 1]); o += 2;
            if ((x & 0x8000) != 0) o += 1;      // direction byte
            o += 1;                              // z
            if ((y & 0x8000) != 0) o += 2;      // hue
            return (y & 0x4000) != 0 ? s[o] : (byte?)null;
        }
        return null;
    }

    [Fact]
    public void AnOrdinaryPlayerIsToldTheItemIsMovable()
    {
        var (client, _, item, _) = Stage(17310);
        client.SendWorldItem(item);

        byte? flags = FlagsOf(client, item.Uid.Value);
        _out.WriteLine($"flags {(flags.HasValue ? $"0x{flags:X2}" : "<none>")}");
        Assert.True(flags.HasValue, "no flags byte was sent at all");
        Assert.Equal(0x20, flags!.Value & 0x20);
    }

    [Fact]
    public void AnItemTheViewerCannotMoveCarriesNoMovableBit()
    {
        // ATTR_MOVE_NEVER: a fixture. Upstream asks CanMoveItem, and the answer is no.
        var (client, _, item, _) = Stage(17311);
        item.SetAttr(ObjAttributes.Move_Never);
        client.SendWorldItem(item);

        byte? flags = FlagsOf(client, item.Uid.Value);
        _out.WriteLine($"immovable -> {(flags.HasValue ? $"0x{flags:X2}" : "<none>")}");
        Assert.True(flags is null or 0 || (flags.Value & 0x20) == 0);
    }

    [Fact]
    public void StaffStillGetIt()
    {
        var (client, me, item, _) = Stage(17312);
        me.PrivLevel = PrivLevel.GM;
        item.SetAttr(ObjAttributes.Move_Never);      // staff move it anyway
        client.SendWorldItem(item);

        byte? flags = FlagsOf(client, item.Uid.Value);
        Assert.True(flags.HasValue);
        Assert.Equal(0x20, flags!.Value & 0x20);
    }

    [Fact]
    public void ADeadPlayerIsNotToldTheyCanMoveThings()
    {
        // CanMoveItem refuses for a ghost, and the client should not offer the drag.
        var (client, me, item, _) = Stage(17313);
        me.Kill();
        client.SendWorldItem(item);

        byte? flags = FlagsOf(client, item.Uid.Value);
        _out.WriteLine($"ghost -> {(flags.HasValue ? $"0x{flags:X2}" : "<none>")}");
        Assert.True(flags is null or 0 || (flags.Value & 0x20) == 0);
    }
}
