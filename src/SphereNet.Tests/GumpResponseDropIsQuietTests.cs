using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Items;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A gump response for an id this client was never sent is dropped without raising
/// an alarm about it.
///
/// Dropping it is right and is upstream's own sanity check:
///
///     auto itGumpFound = client->m_mapOpenedGumps.find(context);
///     if (itGumpFound == m_mapOpenedGumps.end() || itGumpFound->second &lt;= 0)
///         return true;
///
/// (PacketGumpDialogRet::onReceive, receive.cpp:2240-2243) — and it logs nothing.
/// SphereNet logged a WARNING naming the player and the word "forged", which an
/// ordinary client produces just by playing: an old-style MENU (0x7C) is not a gump
/// and its id is in no gump map, yet closing one sends a 0xB1 for that id beside the
/// 0x7D that carries the actual choice. So every GM who ran .edit and closed the
/// window was accused of forging a packet.
/// </summary>
public sealed class GumpResponseDropIsQuietTests(ITestOutputHelper output)
{
    private sealed class Capture : ILoggerProvider
    {
        public readonly List<(LogLevel Level, string Text)> Entries = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Entries);
        public void Dispose() { }

        private sealed class Sink(List<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries) entries.Add((level, formatter(state, ex)));
            }
        }
    }

    [Fact]
    public void ClosingTheEditMenuRaisesNoWarning()
    {
        var capture = new Capture();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var client = TestHarness.CreateClient(lf, world, accounts, 19601);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Mortal";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        // A container with something in it, so .edit offers the 0x7C item list
        // rather than going straight to the prop dialog.
        var bag = world.CreateItem();
        bag.BaseId = 0x0E75;
        bag.ItemType = ItemType.Container;
        world.PlaceItem(bag, player.Position);
        var inside = world.CreateItem();
        inside.BaseId = 0x0EED;
        bag.AddItem(inside);

        client.ShowInspectDialog(bag.Uid.Value);
        capture.Entries.Clear();

        // What the client sends when that window closes: a gump response carrying
        // the MENU's id, which no gump map holds.
        const uint EditMenuId = 0xFFED;
        client.HandleGumpResponse(player.Uid.Value, EditMenuId, 0, [], []);

        var warnings = capture.Entries
            .Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text).ToArray();
        foreach (var w in warnings) output.WriteLine($"WARN: {w}");
        Assert.Empty(warnings);

        // And the 0x7D that follows still does the real work — index 0 is the
        // cancel, which must be accepted quietly too.
        client.HandleMenuChoice(player.Uid.Value, (ushort)EditMenuId, 0, 0);
        Assert.DoesNotContain(capture.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void AGumpIdThisClientWasNeverSentIsStillDropped()
    {
        var capture = new Capture();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 19603);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Mortal";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        capture.Entries.Clear();

        // The check itself stays: an id the client was never handed does nothing.
        client.HandleGumpResponse(player.Uid.Value, 0x1234ABCD, 7, [1, 2], [(0, "x")]);

        Assert.DoesNotContain(capture.Entries, e => e.Level >= LogLevel.Warning);
        // It is still recorded, just at a level nobody is paged about.
        Assert.Contains(capture.Entries, e =>
            e.Level == LogLevel.Debug && e.Text.Contains("Dropped gump response"));
        output.WriteLine(capture.Entries.First(e => e.Level == LogLevel.Debug).Text);
    }
}
