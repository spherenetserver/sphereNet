using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Magic;
using SphereNet.Game.Scripting;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class HealingStoneCooldownTests
{
    [Fact]
    public void MarkedStonesWaitForMoreZToClearBeforeCallingTheScript()
    {
        using var logs = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);

        var stone = world.CreateItem();
        stone.BaseId = 0x4078;
        stone.ItemType = ItemType.Spell;
        Assert.True(pack.TryAddItem(stone));
        stone.SetTag(SpellEngine.HealingStoneLegacyChargesTag, "1");
        stone.MoreP = new Point3D(100, 0, 1, 0);

        int clicks = 0;
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "DClick", (_, _) =>
        {
            clicks++;
            return TriggerResult.True;
        });
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19568);
        client.SetEngines(triggerDispatcher: dispatcher);
        TestHarness.AttachCharacter(client, player);

        client.HandleDoubleClick(stone.Uid.Value);
        Assert.Equal(0, clicks);

        Assert.True(stone.TrySetProperty("MOREZ", "0"));
        client.HandleDoubleClick(stone.Uid.Value);
        Assert.Equal(1, clicks);

        stone.RemoveTag(SpellEngine.HealingStoneLegacyChargesTag);
        Assert.True(stone.TrySetProperty("MOREZ", "1"));
        client.HandleDoubleClick(stone.Uid.Value);
        Assert.Equal(2, clicks);
    }
}
