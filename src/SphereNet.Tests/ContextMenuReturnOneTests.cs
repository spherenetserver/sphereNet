using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;

namespace SphereNet.Tests;

/// <summary>
/// RETURN 1 from a character's @ContextMenuRequest means the script built the menu.
///
/// Upstream adds the hardcoded character entries - paperdoll, vendor buy and sell,
/// the banker's box and the rest - only when the trigger did NOT return TRUE
/// (CClientEvent.cpp:2596-2608). Adding them regardless gave a script that had
/// claimed the menu the engine's entries on top of its own.
///
/// Nothing in the shipped packs returns TRUE here: all 26 @ContextMenuRequest blocks
/// hang off items, where upstream has no hardcoded entries either and this engine
/// already matched. So this changes no pack behaviour - it makes the return value
/// mean what a script writing one would expect, the same misreading that had
/// @ClientTooltip throwing a script's own tooltip away.
/// </summary>
public sealed class ContextMenuReturnOneTests
{
    /// <summary>Open the context menu on <paramref name="onSelf"/> with a char
    /// trigger that returns what the caller asks, and hand back the entry tags the
    /// client was sent.</summary>
    private static ushort[] Open(TriggerResult charTriggerResult, bool scriptAddsEntry)
    {
        using var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ContextMenuRequest", (target, _) =>
        {
            if (scriptAddsEntry)
                Sent(target)?.Add((200, 3006999, 0));
            return charTriggerResult;
        });

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 5300);
        client.SetEngines(triggerDispatcher: dispatcher);

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        _live = client;

        var send = typeof(SphereNet.Game.Clients.ClientWorldFeaturesHandler)
            .GetMethod("SendContextMenu", System.Reflection.BindingFlags.Instance |
                                          System.Reflection.BindingFlags.NonPublic)!;
        send.Invoke(client.WorldFeatures, [ch.Uid.Value]);

        // Read the entry tags back off the packet the client was sent.
        foreach (var buf in TestHarness.GetQueuedPackets(client.NetState))
        {
            var span = buf.Span;
            // 0xBF: id, length(2), sub-command(2)=0x0014, style(2), serial(4), count(1)
            if (span.Length < 13 || span[0] != 0xBF) continue;
            if (span[3] != 0x00 || span[4] != 0x14) continue;
            int count = span[11];
            for (int i = 0; i < count; i++)
            {
                int at = 12 + i * 8;
                if (at + 1 < span.Length)
                    _captured.Add((ushort)((span[at] << 8) | span[at + 1]));
            }
        }
        return _captured.ToArray();
    }

    // The handler hands the script its list through the client context; the test
    // reaches the same list to add an entry from "script" code.
    private static SphereNet.Game.Clients.GameClient? _live;
    private static readonly List<ushort> _captured = [];

    private static List<(ushort EntryTag, uint ClilocId, ushort Flags)>? Sent(object _) =>
        _live == null ? null
            : ((SphereNet.Game.Clients.IClientContext)_live).ScriptContextEntries;

    /// <summary>RETURN 0 keeps the built-in entries, which is every menu today.</summary>
    [Fact]
    public void ReturnZeroKeepsTheBuiltInEntries()
    {
        _captured.Clear();
        var tags = Open(TriggerResult.Default, scriptAddsEntry: false);
        Assert.Contains((ushort)1, tags);   // Open Paperdoll
    }

    /// <summary>RETURN 1 hands the menu to the script: its entry, and none of the
    /// engine's.</summary>
    [Fact]
    public void ReturnOneLeavesOnlyTheScriptEntries()
    {
        _captured.Clear();
        var tags = Open(TriggerResult.True, scriptAddsEntry: true);

        Assert.Contains((ushort)200, tags);      // the script's own line
        Assert.DoesNotContain((ushort)1, tags);  // not the engine's paperdoll
    }

    /// <summary>And RETURN 0 with a script entry merges both, which is what every
    /// shipped block relies on.</summary>
    [Fact]
    public void ReturnZeroMergesBoth()
    {
        _captured.Clear();
        var tags = Open(TriggerResult.Default, scriptAddsEntry: true);

        Assert.Contains((ushort)1, tags);
        Assert.Contains((ushort)200, tags);
    }

    /// <summary>And on the way back: RETURN 1 from @ContextMenuSelect means the script
    /// handled the entry, so the engine's own action for that tag is skipped
    /// (CClientEvent.cpp:2778). The return value used to be discarded and both ran -
    /// a script answering "Open Paperdoll" itself got the paperdoll anyway.</summary>
    [Fact]
    public void ReturnOneFromSelectSkipsTheEngineAction()
    {
        Assert.False(SelectPaperdollOpensIt(TriggerResult.True));
        Assert.True(SelectPaperdollOpensIt(TriggerResult.Default));
    }

    /// <summary>Pick entry 1 (Open Paperdoll) on ourselves with a select trigger that
    /// returns what the caller asks; answer whether the paperdoll went out.</summary>
    private static bool SelectPaperdollOpensIt(TriggerResult selectResult)
    {
        using var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ContextMenuSelect", (_, _) => selectResult);

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 5301);
        client.SetEngines(triggerDispatcher: dispatcher);

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        TestHarness.ClearQueuedPackets(client.NetState);

        client.WorldFeatures.HandleContextMenuResponse(ch.Uid.Value, 1);

        // 0x88 is the paperdoll packet.
        return TestHarness.GetQueuedPackets(client.NetState).Any(p => p.Span[0] == 0x88);
    }
}
