using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Two record keys Source-X writes that SphereNet left out or wrote from the wrong
/// place. A guild or town stone writes ALIGN whether or not it carries a guild
/// (CItemStone::r_Write, CItemStone.cpp:128). A house or ship writes its LIVE
/// region - REGION.FLAGS in Sphere hex, REGION.EVENTS, REGION.TAG.&lt;name&gt; -
/// (CItemMulti::r_Write -> CRegion::r_WriteBody, CItemMulti.cpp:2564 /
/// CRegion.cpp:623), and reads the lines back into that region (SHL_REGION,
/// CItemMulti.cpp:3011).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MultiRegionStoneSourceXSaveFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spn_mrs_{Guid.NewGuid():N}");

    public MultiRegionStoneSourceXSaveFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Item.ResolveMultiRegion = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private GameWorld SaveAndReload(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, _dir);
        Item.ResolveShip = null;
        Item.ResolveHouse = null;
        Item.ResolveGuild = null;
        Item.ResolveMultiRegion = null;
        var reloaded = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, _dir);
        return reloaded;
    }

    private string Saved() => string.Join("\n", Directory.GetFiles(_dir).Select(File.ReadAllText));

    /// <summary>The lines of the one record whose SERIAL names this uid.</summary>
    private string[] RecordOf(Serial uid)
    {
        string serial = $"SERIAL=0{uid.Value:X8}";
        var lines = Saved().Replace("\r", "").Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim().Equals(serial, StringComparison.OrdinalIgnoreCase));
        Assert.True(start >= 0, $"no record with {serial}");
        int end = Array.FindIndex(lines, start + 1, l => l.StartsWith('['));
        return lines[start..(end < 0 ? lines.Length : end)].Select(l => l.Trim()).ToArray();
    }

    private static MultiRegistry RegistryWithFootprint(ushort id)
    {
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = id, Name = "test multi" };
        def.Components.Add(new MultiComponent { TileId = 0x0001, DeltaX = -2, DeltaY = -2, DeltaZ = 0, Visible = true });
        def.Components.Add(new MultiComponent { TileId = 0x0001, DeltaX = 2, DeltaY = 2, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        registry.Register(def);
        return registry;
    }

    /// <summary>A guarded area around the structures, so the region they realize
    /// inherits flags that are the land's, not the structure's.</summary>
    private static void AddGuardedLand(GameWorld world)
    {
        var land = new Region { Name = "Town", MapIndex = 0, Flags = RegionFlag.Guarded };
        land.AddRect(0, 0, 300, 300);
        world.AddRegion(land);
    }

    // ---------------------------------------------------------------- stones

    [Fact]
    public void AStoneWithNoGuildRecordStillWritesAlign()
    {
        var world = NewWorld();
        var guildStone = world.CreateItem();
        guildStone.ItemType = ItemType.StoneGuild;
        guildStone.Name = "Unfounded guild";
        world.PlaceItem(guildStone, new Point3D(100, 100, 0, 0));
        var townStone = world.CreateItem();
        townStone.ItemType = ItemType.StoneTown;
        townStone.Name = "Unfounded town";
        world.PlaceItem(townStone, new Point3D(102, 100, 0, 0));
        var plain = world.CreateItem();
        world.PlaceItem(plain, new Point3D(104, 100, 0, 0));

        var reloaded = SaveAndReload(world);

        Assert.Contains("ALIGN=0", RecordOf(guildStone.Uid));
        Assert.Contains("ALIGN=0", RecordOf(townStone.Uid));
        Assert.DoesNotContain(RecordOf(plain.Uid), l => l.StartsWith("ALIGN", StringComparison.Ordinal));
        Assert.Equal(2, Regex.Matches(Saved(), @"^ALIGN=", RegexOptions.Multiline).Count);
        Assert.DoesNotContain("TAG.GUILD.", Saved());

        // The line reads back as the stone's alignment.
        Assert.True(reloaded.FindItem(guildStone.Uid)!.TryGetTag("GUILD.ALIGN", out string? align));
        Assert.Equal("0", align);
    }

    // ---------------------------------------------------------------- houses

    [Fact]
    public void AHouseWritesItsLiveRegionAndReadsItBack()
    {
        var world = NewWorld();
        AddGuardedLand(world);
        var multi = world.CreateItem();
        multi.BaseId = 0x7E;
        multi.ItemType = ItemType.Multi;
        multi.Name = "Keep";
        world.PlaceItem(multi, new Point3D(80, 80, 0, 0));

        var housing = new HousingEngine(world, RegistryWithFootprint(0x7E));
        housing.DeserializeFromWorld();
        var region = housing.FindMultiRegion(multi.Uid);
        Assert.NotNull(region);
        Assert.True(region!.IsFlag(RegionFlag.Guarded));          // the land's, inherited

        // The region changes while the shard runs; the item record is never told.
        Assert.True(region.TrySetProperty("SAFE", "1"));
        Assert.True(region.TrySetProperty("EVENTS", "+r_keep_events"));
        Assert.True(region.TrySetProperty("TAG.owner", "0123"));
        // A tag the item carried from before that the region no longer has.
        multi.SetTag("REGION.TAG.STALE", "1");

        housing.SerializeAllToTags();
        var reloaded = SaveAndReload(world);

        var record = RecordOf(multi.Uid);
        // House | Safe - the land's Guarded and the engine's inherit marker are not the
        // structure's own flags. Sphere hex: a leading 0, upper-case digits.
        uint expected = (uint)(RegionFlag.House | RegionFlag.Safe);
        Assert.Contains($"REGION.FLAGS=0{expected:X}", record);
        Assert.Contains("REGION.EVENTS=r_keep_events", record);
        Assert.Contains("REGION.TAG.OWNER=0123", record);
        Assert.DoesNotContain(record, l => l.Contains("STALE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(record, l => l.StartsWith("TAG.REGION", StringComparison.OrdinalIgnoreCase));
        Assert.Single(record, l => l.StartsWith("REGION.FLAGS=", StringComparison.Ordinal));

        AddGuardedLand(reloaded);
        var housing2 = new HousingEngine(reloaded, RegistryWithFootprint(0x7E));
        housing2.DeserializeFromWorld();
        var back = housing2.FindMultiRegion(multi.Uid)!;
        Assert.True(back.IsFlag(RegionFlag.Safe));
        Assert.True(back.IsFlag(RegionFlag.House));
        Assert.True(back.IsFlag(RegionFlag.Guarded));
        Assert.Equal(RegionFlag.None, back.OwnFlags & RegionFlag.Guarded);   // still the land's
        Assert.True(back.TryGetTag("OWNER", out string? owner));
        Assert.Equal("0123", owner);
        Assert.Single(back.Events);
        Assert.Equal("r_keep_events", back.EventName(back.Events[0]));
    }

    [Fact]
    public void AScriptWriteOnTheMultiReachesItsLiveRegion()
    {
        var world = NewWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x7E;
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(80, 80, 0, 0));
        var housing = new HousingEngine(world, RegistryWithFootprint(0x7E));
        housing.DeserializeFromWorld();
        Item.ResolveMultiRegion = housing.FindMultiRegion;
        var region = housing.FindMultiRegion(multi.Uid)!;

        Assert.True(multi.TrySetProperty("REGION.TAG.color", "5"));
        Assert.True(multi.TrySetProperty("REGION.EVENTS", "+r_one,+r_two"));
        Assert.True(multi.TrySetProperty("REGION.EVENTS", "-r_one"));
        // RC_FLAGS replaces the flags, but never the ship bit.
        uint flags = (uint)(RegionFlag.House | RegionFlag.NoDecay);
        Assert.True(multi.TrySetProperty("REGION.FLAGS", $"0{flags:X}"));

        Assert.True(region.TryGetTag("COLOR", out string? color));
        Assert.Equal("5", color);
        Assert.Single(region.Events);
        Assert.Equal("r_two", region.EventName(region.Events[0]));
        Assert.True(region.IsFlag(RegionFlag.NoDecay));
        Assert.True(region.IsFlag(RegionFlag.House));

        housing.SerializeAllToTags();
        SaveAndReload(world);
        var record = RecordOf(multi.Uid);
        Assert.Contains("REGION.TAG.COLOR=5", record);
        Assert.Contains("REGION.EVENTS=r_two", record);
        Assert.Contains($"REGION.FLAGS=0{flags:X}", record);
    }

    [Fact]
    public void AnOldRegionRecordStillLoadsIntoTheRealizedRegion()
    {
        var world = NewWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x7E;
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(80, 80, 0, 0));
        // What an older record carries: flags without the House bit, '+'-prefixed
        // events and a lower-case tag name.
        Assert.True(multi.TrySetProperty("REGION.FLAGS", "02000"));
        Assert.True(multi.TrySetProperty("REGION.EVENTS", "+r_old"));
        Assert.True(multi.TrySetProperty("REGION.TAG.owner", "09191"));

        var housing = new HousingEngine(world, RegistryWithFootprint(0x7E));
        housing.DeserializeFromWorld();
        var region = housing.FindMultiRegion(multi.Uid)!;
        Assert.True(region.IsFlag(RegionFlag.Safe));
        Assert.True(region.IsFlag(RegionFlag.House));             // not stripped
        Assert.True(region.TryGetTag("OWNER", out string? owner));
        Assert.Equal("09191", owner);
        Assert.Equal("r_old", region.EventName(region.Events.Single()));
    }

    // ---------------------------------------------------------------- ships

    [Fact]
    public void AShipWritesItsLiveRegionAndReadsItBack()
    {
        var world = NewWorld();
        AddGuardedLand(world);
        var hull = world.CreateItem();
        hull.BaseId = 0x7E;
        hull.ItemType = ItemType.Ship;
        world.PlaceItem(hull, new Point3D(120, 120, 0, 0));

        var ships = new ShipEngine(world, RegistryWithFootprint(0x7E), null);
        ships.DeserializeFromWorld();
        var region = ships.FindShipRegion(hull.Uid);
        Assert.NotNull(region);
        Assert.True(region!.IsFlag(RegionFlag.Guarded));          // the harbour's
        Assert.True(region.TrySetProperty("UNDERGROUND", "1"));
        Assert.True(region.TrySetProperty("TAG.crew", "3"));
        Assert.True(region.TrySetProperty("EVENTS", "+r_ship_events"));

        ships.SerializeAllToTags();
        var reloaded = SaveAndReload(world);

        var record = RecordOf(hull.Uid);
        uint expected = (uint)(RegionFlag.Ship | RegionFlag.Underground);
        Assert.Contains($"REGION.FLAGS=0{expected:X}", record);
        Assert.Contains("REGION.EVENTS=r_ship_events", record);
        Assert.Contains("REGION.TAG.CREW=3", record);
        // The region comes before the multi's own keys, as CItemMulti::r_Write writes it.
        Assert.True(Array.FindIndex(record, l => l.StartsWith("REGION.FLAGS=", StringComparison.Ordinal))
                    < Array.FindIndex(record, l => l.StartsWith("LOCKDOWNSPERCENT=", StringComparison.Ordinal)));

        AddGuardedLand(reloaded);
        var ships2 = new ShipEngine(reloaded, RegistryWithFootprint(0x7E), null);
        ships2.DeserializeFromWorld();
        var back = ships2.FindShipRegion(hull.Uid)!;
        Assert.True(back.IsFlag(RegionFlag.Underground));
        Assert.True(back.IsFlag(RegionFlag.Ship));
        Assert.True(back.TryGetTag("CREW", out string? crew));
        Assert.Equal("3", crew);
        Assert.Equal("r_ship_events", back.EventName(back.Events.Single()));
    }
}
