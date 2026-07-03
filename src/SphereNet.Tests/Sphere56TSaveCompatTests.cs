using Microsoft.Extensions.Logging;
using SphereNet.Persistence.Load;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Real-data compatibility check for the 0.56T-Release save family
/// (C:\56T\save). Skips cleanly when the directory is absent (CI); on the
/// dev/VDS box it asserts the loader ingests the world and reports which
/// keys fell through to SAVE.* tags so format gaps are visible, not silent.
/// </summary>
public class Sphere56TSaveCompatTests
{
    private const string SaveDir = @"C:\56T\save";
    private readonly ITestOutputHelper _out;

    public Sphere56TSaveCompatTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Loads56TSaves_AndReportsUnhandledKeys()
    {
        if (!File.Exists(Path.Combine(SaveDir, "sphereworld.scp")))
        {
            _out.WriteLine($"SKIP: 56T save data not found at {SaveDir}");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var world = new SphereNet.Game.World.GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        var loader = new WorldLoader(lf);

        var (items, chars) = loader.Load(world, SaveDir);
        _out.WriteLine($"56T load: {items} items, {chars} chars");

        // The shard dump carries ~76k items / ~4.2k chars — a large shortfall
        // means whole sections stopped parsing.
        Assert.True(items > 70_000, $"expected >70k items, got {items}");
        Assert.True(chars > 4_000, $"expected >4k chars, got {chars}");

        // Spot-check a known player from spherechars.scp.
        var stigma = world.FindChar(new SphereNet.Core.Types.Serial(0x0F9E6));
        Assert.NotNull(stigma);
        Assert.Equal("Stigma", stigma!.Name);

        // Inventory of keys the loader could not map (parked as SAVE.* tags).
        var unhandled = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Tally(SphereNet.Game.Objects.ObjBase obj)
        {
            foreach (var tag in obj.Tags.GetAll())
                if (tag.Key.StartsWith("SAVE.", StringComparison.OrdinalIgnoreCase))
                {
                    string key = tag.Key[5..];
                    unhandled[key] = unhandled.GetValueOrDefault(key) + 1;
                }
        }
        foreach (var obj in world.GetAllObjects()) Tally(obj);

        foreach (var kv in unhandled.OrderByDescending(k => k.Value).Take(25))
            _out.WriteLine($"  unhandled {kv.Key} x{kv.Value}");
        _out.WriteLine($"total unhandled key kinds: {unhandled.Count}");
    }
}
