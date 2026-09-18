using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;

namespace SphereNet.Tests;

/// <summary>
/// @ClientTooltip_AfterDefault fires on a CHARACTER too, not only on an item.
///
/// Upstream calls it on the object - pObj-&gt;OnTrigger(...) - and keeps a
/// character-specific slot beside the item one (TRIGGER_CHARCLIENTTOOLTIP_AFTERDEFAULT,
/// CClientMsg_AOSTooltip.cpp:121). Only the item half existed here, gated on
/// `obj is Item`, on the grounds that no shipped pack hooked the character one.
///
/// That is a reason to expect the trigger unused, not a reason for it to be
/// unanswerable: a pack that writes the block got nothing, with no way to tell why.
/// </summary>
public sealed class CharTooltipAfterDefaultTests
{
    private sealed record Bench(SphereNet.Game.World.GameWorld World,
                                SphereNet.Game.Clients.GameClient Client,
                                SphereNet.Game.Objects.Characters.Character Me,
                                TriggerDispatcher Triggers);

    private static Bench Build(int port, TriggerDispatcher dispatcher)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        // AOS tooltips have to be switched on for the build to run at all: the mode,
        // the server feature bit and a client new enough to receive 0xD6.
        world.ToolTipMode = 1;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        client.NetState.ClientVersionNumber = 70_020_000;
        client.SetEngines(triggerDispatcher: dispatcher);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return new Bench(world, client, me, dispatcher);
    }

    /// <summary>The half that was missing.</summary>
    [Fact]
    public void ACharacterTooltipReachesTheAfterDefaultTrigger()
    {
        var dispatcher = new TriggerDispatcher();
        int fired = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ClientTooltipAfterDefault",
            (_, _) => { fired++; return TriggerResult.Default; });

        var b = Build(8951, dispatcher);
        var other = b.World.CreateCharacter();
        other.IsPlayer = true;          // EVENTSPLAYER only reaches players
        other.Name = "someone";
        b.World.PlaceCharacter(other, new Point3D(101, 100, 0, 0));

        b.Client.SendAosTooltip(other, requested: true);

        Assert.Equal(1, fired);
    }

    /// <summary>The item half still fires, and the two do not fire for each other.</summary>
    [Fact]
    public void AnItemTooltipStillReachesTheItemTrigger()
    {
        var dispatcher = new TriggerDispatcher();
        int charFired = 0, itemFired = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ClientTooltipAfterDefault",
            (_, _) => { charFired++; return TriggerResult.Default; });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ClientTooltipAfterDefault",
            (_, _) => { itemFired++; return TriggerResult.Default; });

        var b = Build(8952, dispatcher);
        var it = b.World.CreateItem();
        b.World.PlaceItem(it, new Point3D(101, 100, 0, 0));

        b.Client.SendAosTooltip(it, requested: true);

        Assert.Equal(1, itemFired);
        Assert.Equal(0, charFired);
    }

    /// <summary>A shard whose pack hooks neither pays nothing: the trigger is gated on
    /// being used, as every hot-path trigger here is.</summary>
    [Fact]
    public void NothingFiresWhenNoPackHooksIt()
    {
        var dispatcher = new TriggerDispatcher();
        var b = Build(8953, dispatcher);
        var other = b.World.CreateCharacter();
        other.IsPlayer = true;
        b.World.PlaceCharacter(other, new Point3D(101, 100, 0, 0));

        var ex = Record.Exception(() => b.Client.SendAosTooltip(other, requested: true));
        Assert.Null(ex);
    }
}
