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
/// A worn item that changes how it looks.
///
/// The delta view keeps two tables: one for characters, carrying the WEARER's body and
/// hue, and one for GROUND items. A worn item is in neither, so a script recolouring a
/// robe or swapping its graphic changed nothing on any screen until a resync. Upstream
/// has no such gap - the item's own Update() reaches everyone who can see the wearer.
///
/// `SendItemVisualUpdate` is the packet for it (0x2E, worn item). These pin what it
/// sends and to whom - the server-side hook that decides WHEN to call it lives in the
/// dirty-object pass and is covered by the audit rather than by a unit test.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WornItemVisualTests
{
    private readonly ITestOutputHelper _out;
    public WornItemVisualTests(ITestOutputHelper output) => _out = output;

    private const byte WornItem = 0x2E;

    private static (GameClient Viewer, Character Wearer, Item Robe, GameWorld World) Stage(int port)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var lf = LoggerFactory.Create(_ => { });

        var viewer = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.IsOnline = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(viewer, me);

        var wearer = world.CreateCharacter();
        wearer.IsPlayer = true; wearer.IsOnline = true;
        world.PlaceCharacter(wearer, new Point3D(102, 100, 0, 0));

        var robe = world.CreateItem();
        robe.BaseId = 0x1F03;
        Assert.True(wearer.Equip(robe, Layer.Robe));

        return (viewer, wearer, robe, world);
    }

    private static int CountWorn(GameClient c, uint uid) =>
        TestHarness.GetQueuedPackets(c.NetState).Count(p =>
            p.Span.Length >= 5 && p.Span[0] == WornItem &&
            (uint)((p.Span[1] << 24) | (p.Span[2] << 16) | (p.Span[3] << 8) | p.Span[4]) == uid);

    [Fact]
    public void AViewerInRangeIsToldAboutTheWornItem()
    {
        var (viewer, _, robe, _) = Stage(17210);
        int before = CountWorn(viewer, robe.Uid.Value);

        robe.Hue = new Color(0x0481);
        viewer.SendItemVisualUpdate(robe);

        _out.WriteLine($"worn packets {before} -> {CountWorn(viewer, robe.Uid.Value)}");
        Assert.True(CountWorn(viewer, robe.Uid.Value) > before);
    }

    [Fact]
    public void AViewerOutOfRangeIsNot()
    {
        var (viewer, wearer, robe, world) = Stage(17211);
        world.MoveCharacter(wearer, new Point3D(400, 400, 0, 0));
        int before = CountWorn(viewer, robe.Uid.Value);

        viewer.SendItemVisualUpdate(robe);

        Assert.Equal(before, CountWorn(viewer, robe.Uid.Value));
    }

    [Fact]
    public void AnInternalLayerNeverReachesTheWire()
    {
        // Source-X keeps its own layers off the wire entirely (uofiles_enums.h:589):
        // a memory or a spell effect has no paperdoll slot to land in.
        var (viewer, wearer, _, world) = Stage(17212);
        var memory = world.CreateItem();
        memory.BaseId = 0x2007;
        memory.ItemType = ItemType.EqMemoryObj;
        memory.IsEquipped = true;
        memory.EquipLayer = Layer.Special;
        memory.ContainedIn = wearer.Uid;

        int before = CountWorn(viewer, memory.Uid.Value);
        viewer.SendItemVisualUpdate(memory);

        Assert.Equal(before, CountWorn(viewer, memory.Uid.Value));
    }

    [Fact]
    public void ADeletedItemSendsNothing()
    {
        var (viewer, _, robe, world) = Stage(17213);
        int before = CountWorn(viewer, robe.Uid.Value);
        world.RemoveItem(robe);

        viewer.SendItemVisualUpdate(robe);

        Assert.Equal(before, CountWorn(viewer, robe.Uid.Value));
    }
}
