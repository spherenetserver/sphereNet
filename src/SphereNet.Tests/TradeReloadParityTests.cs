using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a restart does to goods that were in flight (port plan İŞ-18 / PLAN-401,
/// the "yeniden yükleme" leg).
///
/// Two things a save has to get right about an object's type and an open trade:
///
/// 1. A TYPE the item carries ITSELF has to survive the cycle. Source-X writes the
///    line whenever the instance type differs from its definition's
///    (CItem::r_Write, CItem.cpp:2461). SphereNet wrote it for three structure types
///    only, so a script's <c>TYPE=t_door</c> - or any engine-set type - was reverted
///    to whatever the ITEMDEF said on the next restart.
///
/// 2. A trade window may only exist on a player with a LIVE CLIENT, so every one a
///    save contains is stale. Source-X FixWeirdness (CItem.cpp:1005, result 0x2220)
///    bounces its contents into the owner's pack and deletes it. Keeping it instead
///    sealed the offered goods inside a Special-layer container that no inventory
///    view reaches: a save taken mid-trade simply ate them.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TradeReloadParityTests : IDisposable
{
    private const string Defs = """
        [ITEMDEF 0e75]
        DEFNAME=i_pack_tr
        NAME=Backpack
        TYPE=t_container

        [ITEMDEF 01e5e]
        DEFNAME=i_trade_window_tr
        NAME=Trade Container
        TYPE=t_container

        [ITEMDEF 0f51]
        DEFNAME=i_dagger_tr
        NAME=Dagger
        TYPE=t_weapon_sword

        [ITEMDEF 01000]
        DEFNAME=i_plain_tr
        NAME=Plain thing
        TYPE=t_normal

        [CHARDEF 0190]
        DEFNAME=c_man_tr
        NAME=Man
        """;

    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;
    private readonly System.Collections.Generic.List<string> _dirs = [];

    public TradeReloadParityTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_trrel_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Defs);
        _resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose()
    {
        File.Delete(_scriptPath);
        foreach (string dir in _dirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private GameWorld NewWorld()
    {
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_trreld_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private void Save(GameWorld world, string dir) =>
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, dir);

    private GameWorld LoadFresh(string dir)
    {
        var world = NewWorld();
        var loader = new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }));
        loader.ResolveItemDef = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };
        loader.ResolveItemDefFullIndex = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
        };
        loader.ApplyCharDefFromName = (ch, defname) =>
            CharDefHelper.TryApplyDefName(ch, defname, _resources);
        loader.Load(world, dir);
        return world;
    }

    private static string AllSavedText(string dir) =>
        string.Concat(Directory.EnumerateFiles(dir, "*.scp").OrderBy(f => f).Select(File.ReadAllText));

    private static SphereNet.Game.Objects.Characters.Character MakePlayer(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BaseId = 0x0190;
        ch.Name = "Trader";
        ch.Str = 100; ch.MaxHits = ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    /// <summary>A trade window exactly as the client handler builds one
    /// (CreateTradeContainer): equipped on its owner at LAYER_SPECIAL, typed
    /// IT_EQ_TRADE_WINDOW, holding what that side has offered.</summary>
    private static Item MakeTradeWindow(GameWorld world, Serial ownerUid)
    {
        var cont = world.CreateItem();
        cont.BaseId = 0x1E5E;
        cont.ItemType = ItemType.EqTradeWindow;
        cont.Name = "Trade Container";
        cont.IsEquipped = true;
        cont.EquipLayer = Layer.Special;
        cont.ContainedIn = ownerUid;
        return cont;
    }

    // ---- 1. the item's own type -------------------------------------------

    [Fact]
    public void AnItemsOwnTypeSurvivesTheSaveCycle()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        item.BaseId = 0x1000;                 // ITEMDEF says t_normal
        item.ItemType = ItemType.Door;        // the instance says otherwise
        world.PlaceItem(item, new Point3D(50, 50, 0, 0));

        string dir = NewDir();
        Save(world, dir);

        var reloaded = LoadFresh(dir);
        var back = reloaded.FindItem(item.Uid);
        Assert.NotNull(back);
        // Without the TYPE line MaterializeDefinitionType hands back the def's
        // t_normal and the door is an ordinary object again.
        Assert.Equal(ItemType.Door, back!.ItemType);
    }

    [Fact]
    public void ATypeThatOnlyRepeatsTheDefinitionIsNotWritten()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0F51;                     // ITEMDEF already says t_weapon_sword
        item.ItemType = ItemType.WeaponSword;
        world.PlaceItem(item, new Point3D(51, 50, 0, 0));

        string dir = NewDir();
        Save(world, dir);

        // The def carries it, so persisting it would cost a line per item for
        // nothing - Source-X writes the key only when the two disagree.
        Assert.DoesNotContain("TYPE=", AllSavedText(dir), StringComparison.Ordinal);

        var back = LoadFresh(dir).FindItem(item.Uid);
        Assert.Equal(ItemType.WeaponSword, back!.ItemType);
    }

    [Fact]
    public void AStructureStillPersistsItsType()
    {
        // The three structure types were the whole of the old special case: their
        // BaseId is a raw multi index with no ITEMDEF to recover the type from, so
        // the general rule has to keep covering them.
        var world = NewWorld();
        var ship = world.CreateItem();
        ship.BaseId = 0x4000;
        ship.ItemType = ItemType.Ship;
        world.PlaceItem(ship, new Point3D(60, 60, 0, 0));

        string dir = NewDir();
        Save(world, dir);

        var back = LoadFresh(dir).FindItem(ship.Uid);
        Assert.Equal(ItemType.Ship, back!.ItemType);
    }

    // ---- 2. a save taken while a trade is open ----------------------------

    [Fact]
    public void ASaveTakenMidTradeGivesTheOfferedGoodsBack()
    {
        var world = NewWorld();
        var trader = MakePlayer(world);
        var window = MakeTradeWindow(world, trader.Uid);

        var offered = world.CreateItem();
        offered.BaseId = 0x0F51;
        offered.Name = "Offered dagger";
        Assert.True(window.TryAddItem(offered));

        string dir = NewDir();
        Save(world, dir);

        var reloaded = LoadFresh(dir);
        var backChar = reloaded.FindChar(trader.Uid);
        var backItem = reloaded.FindItem(offered.Uid);

        Assert.NotNull(backChar);
        Assert.NotNull(backItem);
        Assert.Contains(backItem!, backChar!.Backpack!.Contents);

        // And the window itself is gone - it belongs to a session that died with
        // the process (Source-X FixWeirdness 0x2220).
        Assert.Null(reloaded.FindItem(window.Uid));
        Assert.DoesNotContain(reloaded.GetAllObjects().OfType<Item>(),
            i => !i.IsDeleted && i.ItemType == ItemType.EqTradeWindow);
    }

    [Fact]
    public void AnOwnerlessTradeWindowStillGivesUpItsContents()
    {
        // The owner's record can be missing (a scoped export, a hand-edited save).
        // Deleting the window must not take the goods with it.
        var world = NewWorld();
        var window = MakeTradeWindow(world, Serial.Invalid);
        window.ContainedIn = Serial.Invalid;
        world.PlaceItem(window, new Point3D(70, 70, 0, 0));

        var offered = world.CreateItem();
        offered.BaseId = 0x0F51;
        Assert.True(window.TryAddItem(offered));

        string dir = NewDir();
        Save(world, dir);

        var reloaded = LoadFresh(dir);
        var backItem = reloaded.FindItem(offered.Uid);

        Assert.NotNull(backItem);
        Assert.False(backItem!.ContainedIn.IsValid);
        Assert.Null(reloaded.FindItem(window.Uid));
    }
}
