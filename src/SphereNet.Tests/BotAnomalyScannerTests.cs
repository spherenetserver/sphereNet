using System.Collections.Concurrent;
using System.Linq;
using SphereNet.Game.Diagnostics;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The headless bot's anomaly scanner is the "automated play" net: it watches the
/// bot's client-side world model and flags what a human would notice. This covers
/// the vanish check that maps to the invisible-item double-click desync — an item
/// the bot stands next to disappearing from view without the bot moving.
/// </summary>
public sealed class BotAnomalyScannerTests
{
    private static BotKnownItem GroundItem(uint serial, short x, short y) =>
        new() { Serial = serial, X = x, Y = y, ContainerSerial = 0 };

    [Fact]
    public void StationaryBot_NearbyItemDisappears_FlagsVanish()
    {
        var world = new BotWorldModel { X = 100, Y = 100 };
        var scanner = new BotAnomalyScanner(1, world);
        var anomalies = new ConcurrentQueue<BotAnomaly>();

        world.KnownItems[0xABCD] = GroundItem(0xABCD, 101, 100); // one tile away

        scanner.Tick(8_000, anomalies);  // baseline scan (>7s gate), item present
        Assert.Empty(anomalies);

        // Server "corrects" it out of the client's view while the bot stays put.
        world.KnownItems.Remove(0xABCD);
        scanner.Tick(16_000, anomalies);

        Assert.Contains(anomalies, a => a.Type == BotAnomalyType.ItemVanishedNearby);
    }

    [Fact]
    public void MovingBot_ItemDropsFromView_IsNotFlagged()
    {
        var world = new BotWorldModel { X = 100, Y = 100 };
        var scanner = new BotAnomalyScanner(2, world);
        var anomalies = new ConcurrentQueue<BotAnomaly>();

        world.KnownItems[0xABCD] = GroundItem(0xABCD, 101, 100);
        scanner.Tick(8_000, anomalies); // baseline

        // The bot walked away; dropping the item from view is normal, not an anomaly.
        world.X = 120;
        world.Y = 120;
        world.KnownItems.Remove(0xABCD);
        scanner.Tick(16_000, anomalies);

        Assert.DoesNotContain(anomalies, a => a.Type == BotAnomalyType.ItemVanishedNearby);
    }

    [Fact]
    public void StationaryBot_ItemStays_NoFalsePositive()
    {
        var world = new BotWorldModel { X = 100, Y = 100 };
        var scanner = new BotAnomalyScanner(3, world);
        var anomalies = new ConcurrentQueue<BotAnomaly>();

        world.KnownItems[0xABCD] = GroundItem(0xABCD, 101, 100);
        scanner.Tick(8_000, anomalies);
        scanner.Tick(16_000, anomalies);

        Assert.DoesNotContain(anomalies, a => a.Type == BotAnomalyType.ItemVanishedNearby);
    }
}
