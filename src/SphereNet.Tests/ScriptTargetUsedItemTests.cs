using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field report: a dye tub whose @DClick runs TARGET opened a cursor, but picking
/// the item did nothing. Source-X OV_TARGET records the object running the verb
/// as the used item (m_Targ_UID) and opens CLIMODE_TARG_USE_ITEM, so the pick
/// fires that item's @TargOn_Item. The bare cursor answered nobody.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptTargetUsedItemTests
{
    [Fact]
    public void TargetRunOnAnItemFiresItsTargOnItemWithTheScriptSeeingThePick()
    {
        var logs = LoggerFactory.Create(_ => { });
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"targ-use-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [TYPEDEF e_test_tub]
            ON=@TARGON_ITEM
            TAG.PICKED=<SRC.TARG.UID>
            SRC.TARG.COLOR 0481
            RETURN 1
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            ObjBase.ResolveWorld = () => world;
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19611);
            ObjBase.ResolveClientConsole = _ => client;

            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
            var tub = world.CreateItem();
            world.PlaceItem(tub, new Point3D(100, 101, 0, 0));
            tub.Events.Add(stack.Resources.ResolveDefName("e_test_tub"));
            var robe = world.CreateItem();
            world.PlaceItem(robe, new Point3D(101, 100, 0, 0));

            client.SetEngines(triggerDispatcher: stack.Dispatcher);
            TestHarness.AttachCharacter(client, player);

            // What the tub's @DClick does.
            Assert.True(client.TryExecuteScriptCommand(tub, "TARGET", "", new SphereNet.Scripting.Execution.TriggerArgs(player)));
            Assert.Equal(tub.Uid, client.Targets.ItemUid);

            client.HandleTargetResponse(0, client.ActiveTargetCursorId, robe.Uid.Value, 101, 100, 0, 0);

            Assert.True(tub.TryGetTag("PICKED", out var picked));
            Assert.True(ScriptNumber.TryParseToken(picked!, out long pickedUid));
            Assert.Equal(robe.Uid.Value, (uint)pickedUid);
            Assert.Equal(0x0481, robe.Hue);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Unless the trigger RETURNs 1 the pick gets the item type's own use, as
    /// upstream's OnTarg_Use_Item runs its type switch after @TargOn_Item
    /// (CClientTarg.cpp:1756). A script that opens the cursor itself and leaves an
    /// ordinary case to the engine - a pack's lockpick on a plain locked chest - got
    /// nothing, the script cursor having no native use.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutReturnOneThePickGetsTheItemTypesOwnUse(bool returnOne)
    {
        var logs = LoggerFactory.Create(_ => { });
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"targ-native-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, $"""
            [TYPEDEF e_test_key]
            ON=@TARGON_ITEM
            TAG.SEEN=1
            {(returnOne ? "RETURN 1" : "")}
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            ObjBase.ResolveWorld = () => world;
            SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19612);
            ObjBase.ResolveClientConsole = _ => client;

            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
            var key = world.CreateItem();
            key.ItemType = SphereNet.Core.Enums.ItemType.Key;
            key.More1 = 0x40001234;
            world.PlaceItem(key, new Point3D(100, 101, 0, 0));
            key.Events.Add(stack.Resources.ResolveDefName("e_test_key"));
            var chest = world.CreateItem();
            chest.ItemType = SphereNet.Core.Enums.ItemType.Container;
            chest.More1 = 0x40001234;
            world.PlaceItem(chest, new Point3D(101, 100, 0, 0));

            client.SetEngines(triggerDispatcher: stack.Dispatcher);
            TestHarness.AttachCharacter(client, player);

            Assert.True(client.TryExecuteScriptCommand(key, "TARGET", "", new SphereNet.Scripting.Execution.TriggerArgs(player)));
            client.HandleTargetResponse(0, client.ActiveTargetCursorId, chest.Uid.Value, 101, 100, 0, 0);

            Assert.True(key.TryGetTag("SEEN", out _));
            Assert.Equal(returnOne ? SphereNet.Core.Enums.ItemType.Container : SphereNet.Core.Enums.ItemType.ContainerLocked,
                chest.ItemType);
        }
        finally { File.Delete(path); }
    }
}
