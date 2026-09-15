using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Looting a corpse, through the real handlers.
///
/// Field report: an item taken out of a corpse changes appearance, changes again when
/// it is dropped on the ground, and will not go into the backpack at all - though the
/// ground accepts it. Three symptoms that could be one cause or three, so each is
/// asked separately and against the packets the client actually receives.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CorpseLootPathTests
{
    private readonly ITestOutputHelper _out;
    public CorpseLootPathTests(ITestOutputHelper output) => _out = output;

    private sealed class Stage
    {
        public GameWorld World = null!;
        public GameClient Client = null!;
        public Character Me = null!;
        public Item Pack = null!;
        public Item Corpse = null!;
    }

    private static Stage Build(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var state = TestHarness.CreateActiveNetState(lf, port);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.IsOnline = true;
        me.Str = 100;
        me.MaxHits = 100; me.Hits = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        Assert.True(me.Equip(pack, Layer.Pack));

        var corpse = world.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = ItemType.Corpse;
        world.PlaceItem(corpse, new Point3D(101, 100, 0, 0));

        return new Stage { World = world, Client = client, Me = me, Pack = pack, Corpse = corpse };
    }

    private static Item Loot(Stage s, ushort graphic, ushort hue = 0)
    {
        var item = s.World.CreateItem();
        item.BaseId = graphic;
        item.Hue = new Color(hue);
        Assert.True(s.Corpse.TryAddItem(item));
        return item;
    }

    /// <summary>Every graphic this client has been told for the uid, in order, across
    /// the three packets that can carry one: 0x1A ground, 0x25 container, 0xF3.</summary>
    private static string Graphics(GameClient client, uint uid)
    {
        var seen = new System.Collections.Generic.List<string>();
        foreach (var p in TestHarness.GetQueuedPackets(client.NetState))
        {
            var s = p.Span;
            if (s.Length < 8) continue;
            switch (s[0])
            {
                case 0x1A:
                {
                    uint ser = (uint)((s[3] << 24) | (s[4] << 16) | (s[5] << 8) | s[6]) & 0x7FFFFFFF;
                    if (ser != uid) break;
                    ushort g = (ushort)(((s[7] << 8) | s[8]) & 0x7FFF);
                    seen.Add($"0x1A:{g:X4}");
                    break;
                }
                case 0x25:
                {
                    uint ser = (uint)((s[1] << 24) | (s[2] << 16) | (s[3] << 8) | s[4]);
                    if (ser != uid) break;
                    ushort g = (ushort)((s[5] << 8) | s[6]);
                    seen.Add($"0x25:{g:X4}");
                    break;
                }
            }
        }
        return string.Join(" -> ", seen);
    }

    [Fact]
    public void AnItemTakenFromACorpseAndPutInThePackKeepsItsGraphic()
    {
        var stage = Build(16610);
        var loot = Loot(stage, 0x13B9);          // a viking sword
        ushort before = loot.DispIdFull;

        stage.Client.HandleDoubleClick(stage.Corpse.Uid.Value);   // the player opens it
        stage.Client.HandleItemPickup(loot.Uid.Value, 1);
        stage.Client.HandleItemDrop(loot.Uid.Value, 20, 20, 0, stage.Pack.Uid.Value);

        _out.WriteLine($"graphic {before:X4}; client saw [{Graphics(stage.Client, loot.Uid.Value)}]");
        _out.WriteLine($"ended up in 0x{loot.ContainedIn.Value:X} (pack is 0x{stage.Pack.Uid.Value:X})");
        Assert.Equal(before, loot.DispIdFull);
        Assert.Equal(stage.Pack.Uid, loot.ContainedIn);
    }

    [Fact]
    public void TheSameItemOnTheGroundKeepsItToo()
    {
        var stage = Build(16611);
        var loot = Loot(stage, 0x13B9);
        ushort before = loot.DispIdFull;

        stage.Client.HandleDoubleClick(stage.Corpse.Uid.Value);   // the player opens it
        stage.Client.HandleItemPickup(loot.Uid.Value, 1);
        stage.Client.HandleItemDrop(loot.Uid.Value, 100, 101, 0, 0);

        _out.WriteLine($"graphic {before:X4}; client saw [{Graphics(stage.Client, loot.Uid.Value)}]");
        Assert.Equal(before, loot.DispIdFull);
        Assert.False(loot.ContainedIn.IsValid);
    }

    [Fact]
    public void AStackOfSomethingKeepsItsGraphicThroughBothMoves()
    {
        // Amount changes the 0x1A encoding (the serial carries a flag and the count
        // follows), which is where a graphic can slip in the reading.
        var stage = Build(16612);
        var loot = Loot(stage, 0x0F3F);          // arrows
        loot.Amount = 25;
        ushort before = loot.DispIdFull;

        stage.Client.HandleDoubleClick(stage.Corpse.Uid.Value);
        stage.Client.HandleItemPickup(loot.Uid.Value, 25);
        stage.Client.HandleItemDrop(loot.Uid.Value, 20, 20, 0, stage.Pack.Uid.Value);

        _out.WriteLine($"graphic {before:X4}; client saw [{Graphics(stage.Client, loot.Uid.Value)}]");
        Assert.Equal(before, loot.DispIdFull);
    }

    [Fact]
    public void AnEquippedPieceOnTheCorpseIsNotStillWorn()
    {
        // A corpse holds what the dead character was wearing, tagged with the layer it
        // came off. If it were still flagged equipped, every rule that asks "is this
        // item worn" would answer yes for something lying in a bag.
        var stage = Build(16613);
        var loot = Loot(stage, 0x13BB);
        loot.SetTag("EQUIPLAYER", ((byte)Layer.Chest).ToString());

        Assert.False(loot.IsEquipped);

        stage.Client.HandleDoubleClick(stage.Corpse.Uid.Value);   // the player opens it
        stage.Client.HandleItemPickup(loot.Uid.Value, 1);
        stage.Client.HandleItemDrop(loot.Uid.Value, 20, 20, 0, stage.Pack.Uid.Value);
        Assert.Equal(stage.Pack.Uid, loot.ContainedIn);
    }
}
