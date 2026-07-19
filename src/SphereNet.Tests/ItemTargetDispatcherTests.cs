using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit findings (wiki/test.txt #1/#2) — Source-X OnTarg_Use_Item parity:
/// a native "use item → pick target" flow pins the used item and its parent;
/// the response refuses the use when the source was moved/handed away since
/// the cursor opened, and the classic blade targets (corpse carving) are
/// reachable through the weapon target flow.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class ItemTargetDispatcherTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (SphereNet.Game.Clients.GameClient Client, Character Player) CreatePlayerClient(
        GameWorld world, DeathEngine? death = null)
    {
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2611);
        if (death != null)
            client.SetEngines(deathEngine: death);
        TestHarness.AttachCharacter(client, player);
        return (client, player);
    }

    [Fact]
    public void UsedItemMovedAfterCursorOpened_RefusesTheUse()
    {
        var world = CreateWorld();
        var (client, player) = CreatePlayerClient(world);

        var door = world.CreateItem();
        door.BaseId = 0x06A5;
        door.ItemType = ItemType.Door;
        world.PlaceItem(door, new Point3D(100, 101, 0, 0));

        var key = world.CreateItem();
        key.BaseId = 0x100F;
        key.ItemType = ItemType.Key;
        key.SetTag("LINK", door.Uid.Value.ToString());
        key.Link = door.Uid;
        player.Backpack!.AddItem(key);

        client.HandleDoubleClick(key.Uid.Value);
        uint cursorId = client.ActiveTargetCursorId;
        Assert.NotEqual(0u, cursorId);

        // The key changes hands while the cursor is up (Source-X m_Targ_Prv_UID
        // check: the pinned parent no longer matches).
        player.Backpack.RemoveItem(key);
        world.PlaceItem(key, new Point3D(110, 110, 0, 0));

        client.HandleTargetResponse(0, cursorId, door.Uid.Value, door.X, door.Y, door.Z, 0);

        // HandleKeyUse never ran: the linked door was NOT toggled to locked.
        Assert.Equal(ItemType.Door, door.ItemType);
    }

    [Fact]
    public void BladeOnCorpse_CarvesThroughDeathEngine()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var (client, player) = CreatePlayerClient(world, death);

        var sword = world.CreateItem();
        sword.BaseId = 0x13B9;
        sword.ItemType = ItemType.WeaponSword;
        player.Backpack!.AddItem(sword);

        var corpse = world.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = ItemType.Corpse;
        world.PlaceItem(corpse, new Point3D(100, 101, 0, 0));

        client.HandleDoubleClick(sword.Uid.Value);
        uint cursorId = client.ActiveTargetCursorId;
        Assert.NotEqual(0u, cursorId);
        client.HandleTargetResponse(0, cursorId, corpse.Uid.Value, corpse.X, corpse.Y, corpse.Z, 0);

        Assert.True(corpse.TryGetTag("CORPSE_CARVED", out _),
            "blade target did not route to DeathEngine.CarveCorpse");
    }
}
