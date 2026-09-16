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
/// Equipping while the wearer is invisible. The worn-item packet (0x2E) is how
/// the client learns what is on a mobile - its own paperdoll included - so it has
/// to reach the wearer whatever the wearer's visibility is.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class InvisibleEquipVisualTests
{
    private const ushort Pickaxe = 0x0E85;

    private static (GameWorld World, GameClient Client, SphereNet.Game.Objects.Characters.Character Me)
        Setup(bool invisible)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var state = TestHarness.CreateActiveNetState(lf, 4401);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.IsOnline = true;
        me.PrivLevel = PrivLevel.GM;
        me.Str = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);

        if (invisible)
            me.SetStatFlag(StatFlag.Invisible);
        return (world, client, me);
    }

    private static Item GroundPickaxe(GameWorld world)
    {
        var pick = world.CreateItem();
        pick.BaseId = Pickaxe;
        pick.ItemType = ItemType.WeaponMacePick;
        world.PlaceItem(pick, new Point3D(100, 100, 0, 0));
        return pick;
    }

    /// <summary>0x2E: [id][serial:4][0][graphic:2][0][layer][wearer:4][hue:2]</summary>
    private static bool SawWornItem(NetState state, uint itemSerial)
    {
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length < 15 || s[0] != 0x2E) continue;
            uint serial = (uint)((s[1] << 24) | (s[2] << 16) | (s[3] << 8) | s[4]);
            if (serial == itemSerial) return true;
        }
        return false;
    }

    [Fact]
    public void EquippingFromTheGroundTellsTheWearer()
    {
        var (world, client, me) = Setup(invisible: false);
        var pick = GroundPickaxe(world);

        Assert.True(client.Inventory.TryDClickEquip(pick, Layer.OneHanded));
        Assert.True(pick.IsEquipped);
        Assert.True(SawWornItem(client.NetState, pick.Uid.Value),
            "no 0x2E for the equipped item");
    }

    /// <summary>The order the client sees, as opcodes, for that item's serial.</summary>
    private static List<byte> OpcodesFor(NetState state, uint itemSerial)
    {
        var ops = new List<byte>();
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            uint serial;
            switch (s[0])
            {
                case 0x2E when s.Length >= 15:
                case 0x1D when s.Length >= 5:
                    serial = (uint)((s[1] << 24) | (s[2] << 16) | (s[3] << 8) | s[4]);
                    break;
                case 0x1A when s.Length >= 8:
                    serial = (uint)((s[3] << 24) | (s[4] << 16) | (s[5] << 8) | s[6]) & 0x7FFFFFFF;
                    break;
                default:
                    continue;
            }
            if (serial == itemSerial) ops.Add(s[0]);
        }
        return ops;
    }

    [Fact]
    public void NothingDeletesTheItemAfterItIsWorn()
    {
        // The shape to rule out: the client is told the item is worn and then told
        // to delete it, which takes it off the paperdoll and the character both.
        var (world, client, me) = Setup(invisible: true);
        var pick = GroundPickaxe(world);

        Assert.True(client.Inventory.TryDClickEquip(pick, Layer.OneHanded));
        var ops = OpcodesFor(client.NetState, pick.Uid.Value);

        int worn = ops.LastIndexOf((byte)0x2E);
        Assert.True(worn >= 0, "no 0x2E at all");
        Assert.DoesNotContain(ops.Skip(worn + 1), op => op == 0x1D);
    }

    [Fact]
    public void EquippingFromTheGroundWhileInvisibleAlsoTellsTheWearer()
    {
        // A wearer always sees their own equipment: their visibility is about who
        // ELSE is told, never about whether their own paperdoll is updated.
        var (world, client, me) = Setup(invisible: true);
        var pick = GroundPickaxe(world);

        Assert.True(client.Inventory.TryDClickEquip(pick, Layer.OneHanded));
        Assert.True(pick.IsEquipped);
        Assert.True(SawWornItem(client.NetState, pick.Uid.Value),
            "no 0x2E for the equipped item while invisible");
    }
}
