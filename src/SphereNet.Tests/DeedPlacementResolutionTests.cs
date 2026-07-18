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
    [InlineData("i_deed_test_house")]
    [InlineData("i_deed_test_ship")] // multi id 0 — the small-ship-north edge
    public void NamedDeed_AtCreateMore_RaisesPlacementCursorOnDClick(string deedDefname)
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
            ItemDefHelper.ApplyInstanceMetadata(deed, defIdx);
            deed.FireCreateTrigger();
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
}
