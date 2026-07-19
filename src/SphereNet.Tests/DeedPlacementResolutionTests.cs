using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// Field report (2026-07-19): double-clicking a house/ship deed says
/// "Cannot place house here". Reproduces the LIVE pack structure end to end:
/// a named deed ("[ITEMDEF i_deed_x] ID=i_deed" + "ON=@Create MORE=m_x") whose
/// multi reference is assigned by the @Create trigger, with the production
/// resolver wiring (Item.ResolveDefName = ItemDef-only; Item.ResolveMultiDefId
/// = MultiDef-only), then the deed double-click.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DeedPlacementResolutionTests
{
    private static string WriteScript(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_deedres_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, contents);
        return path;
    }

    [Theory]
    [InlineData("i_deed_test_house", false)]
    [InlineData("i_deed_test_ship", false)] // multi id 0 — the small-ship-north edge
    [InlineData("i_deed_test_house", true)] // legacy save item: @Create never ran
    [InlineData("i_deed_test_ship", true)]
    public void NamedDeed_AtCreateMore_RaisesPlacementCursorOnDClick(string deedDefname, bool legacyLoadPath)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [ITEMDEF 014ef]
            DEFNAME=i_deed
            TYPE=t_deed

            [ITEMDEF 014f1]
            DEFNAME=i_deed_ship
            TYPE=t_deed

            [ITEMDEF i_deed_test_house]
            ID=i_deed
            NAME=Deed to a Test House
            ON=@Create
                MORE=m_test_house

            [ITEMDEF i_deed_test_ship]
            ID=i_deed_ship
            NAME=Deed to a Test Ship
            ON=@Create
                MORE=m_test_ship

            [MULTIDEF 064]
            DEFNAME=m_test_house
            TYPE=t_multi

            [MULTIDEF 0]
            DEFNAME=m_test_ship
            TYPE=t_ship
            """);
        stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var oldResolveDefName = Item.ResolveDefName;
        var oldResolveMulti = Item.ResolveMultiDefId;
        var oldCreateHook = Item.CreateTriggerHook;
        var oldShipEngine = Item.ResolveShipEngine;
        try
        {
            // EXACT production wiring (Program.cs / EngineWiring):
            Item.ResolveDefName = defname =>
            {
                var rid = stack.Resources.ResolveDefName(defname);
                if (rid.IsValid && rid.Type == ResType.ItemDef)
                {
                    var def = DefinitionLoader.GetItemDef(rid.Index);
                    return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
                }
                return 0;
            };
            Item.ResolveMultiDefId = defname =>
            {
                var rid = stack.Resources.ResolveDefName(defname);
                return rid.IsValid && rid.Type == ResType.MultiDef ? rid.Index : -1;
            };
            Item.CreateTriggerHook = item =>
                stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item });

            var world = TestHarness.CreateWorld();
            using var loggerFactory = TestHarness.CreateLoggerFactory();
            var registry = new MultiRegistry();
            var housing = new HousingEngine(world, registry);
            var ships = new SphereNet.Game.Ships.ShipEngine(world, registry, null);
            Item.ResolveShipEngine = () => ships;

            var state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(20_000, 30_000));
            state.ClientVersionNumber = 70_020_000;
            var client = new GameClient(state, world, new AccountManager(loggerFactory),
                loggerFactory.CreateLogger<GameClient>());
            client.SetEngines(housingEngine: housing, triggerDispatcher: stack.Dispatcher,
                commands: new SphereNet.Game.Speech.CommandHandler { Resources = stack.Resources });
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, player);

            // Create the deed the way .add / NEWITEM does.
            int defIdx = stack.Resources.ResolveDefName(deedDefname).Index;
            var deed = world.CreateItem();
            if (legacyLoadPath)
            {
                // Simulate a deed materialised by the WORLD LOADER on an old
                // save: fields stamped, routing tags present, but @Create never
                // fired and no MORE reference survived. The dclick handler's
                // legacy repair must re-run @Create and still resolve.
                deed.BaseId = deedDefname.Contains("ship") ? (ushort)0x14F1 : (ushort)0x14EF;
                deed.ItemType = ItemType.Deed;
                deed.Name = "legacy deed";
                deed.SetTag("ITEMDEF", deedDefname);
                deed.SetTag("SCRIPTDEF", defIdx.ToString());
            }
            else
            {
                ItemDefHelper.ApplyInstanceMetadata(deed, defIdx);
                deed.FireCreateTrigger();
            }
            world.PlaceItem(deed, player.Position);

            // The @Create MORE=<multidef defname> must be resolvable at dclick.
            client.HandleDoubleClick(deed.Uid.Value);

            var packets = TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]).ToList();
            // A placement cursor must be raised (0x99 preview or plain 0x6C) —
            // NOT the "Cannot place house here" failure path.
            Assert.True(packets.Contains(0x99) || packets.Contains(0x6C),
                $"expected a target cursor, got opcodes: {string.Join(",", packets.Select(b => b.ToString("X2")))}");
        }
        finally
        {
            Item.ResolveDefName = oldResolveDefName;
            Item.ResolveMultiDefId = oldResolveMulti;
            Item.CreateTriggerHook = oldCreateHook;
            Item.ResolveShipEngine = oldShipEngine;
        }
    }

    /// <summary>Source-X ITEMID_MULTI: the live interpreter evaluates a multi
    /// defname to 0x4000 + raw index, so MORE=m_small_ship_n stores
    /// More1=0x4000 (raw index 0 — the value that used to read as blank).
    /// The deed resolver must strip the base and accept id 0.</summary>
    [Theory]
    [InlineData(0x4000u, 0x14F1)] // small ship north: raw multi id 0
    [InlineData(0x4064u, 0x14EF)] // stone&plaster house: raw multi id 0x64
    public void MultiBasedMore1_ResolvesAndRaisesCursor(uint more1, int deedBaseId)
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var registry = new MultiRegistry();
        var housing = new HousingEngine(world, registry);
        var ships = new SphereNet.Game.Ships.ShipEngine(world, registry, null);
        var oldShipEngine = Item.ResolveShipEngine;
        try
        {
            Item.ResolveShipEngine = () => ships;
            var state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(20_000, 30_000));
            state.ClientVersionNumber = 70_020_000;
            var client = new GameClient(state, world, new AccountManager(loggerFactory),
                loggerFactory.CreateLogger<GameClient>());
            client.SetEngines(housingEngine: housing);
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, player);

            var deed = world.CreateItem();
            deed.BaseId = (ushort)deedBaseId;
            deed.ItemType = ItemType.Deed;
            deed.Name = "test deed";
            deed.More1 = more1;
            world.PlaceItem(deed, player.Position);

            client.HandleDoubleClick(deed.Uid.Value);

            var packets = TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]).ToList();
            Assert.True(packets.Contains(0x99) || packets.Contains(0x6C),
                $"expected a target cursor, got opcodes: {string.Join(",", packets.Select(b => b.ToString("X2")))}");
        }
        finally
        {
            Item.ResolveShipEngine = oldShipEngine;
        }
    }
}
