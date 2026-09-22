using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// ONAME (every object, CObjBase OC_ONAME) and OWNEDBY (items, CItem IC_OWNEDBY):
/// strings a script parks in the object's base defs. Upstream reads them back as
/// written, drops them on an empty value, copies them with DUPE and saves them as
/// KEY="value" (CVarDefMap::r_WritePrefix). Neither had anywhere to live here, so a
/// disguise deed's SRC.ONAME=&lt;SRC.NAME&gt; and a market's NEW.OWNEDBY=&lt;UID&gt;
/// were written into nothing and read back as 0.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BaseDefStringKeyTests
{
    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    [Fact]
    public void ONameReadsBackOnCharsAndItems()
    {
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        Assert.True(ch.TryGetProperty("ONAME", out string before));
        Assert.Equal("", before);

        Assert.True(ch.TrySetProperty("ONAME", "Lord British"));
        Assert.True(ch.TryGetProperty("ONAME", out string after));
        Assert.Equal("Lord British", after);

        var item = world.CreateItem();
        Assert.True(item.TrySetProperty("ONAME", "\"a quoted name\""));
        Assert.True(item.TryGetProperty("ONAME", out string itemName));
        Assert.Equal("a quoted name", itemName);
    }

    [Fact]
    public void AnEmptyValueDropsTheKey()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        item.TrySetProperty("OWNEDBY", "04000123");
        item.TrySetProperty("OWNEDBY", "");
        Assert.True(item.TryGetProperty("OWNEDBY", out string v));
        Assert.Equal("", v);
    }

    [Fact]
    public void OwnedByReadsBackAsWritten()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        Assert.True(item.TrySetProperty("OWNEDBY", "04000123"));
        Assert.True(item.TryGetProperty("OWNEDBY", out string v));
        Assert.Equal("04000123", v);
    }

    [Fact]
    public void DupeCarriesThem()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        item.OwnedBy = "04000123";
        item.OName = "heirloom";
        var copy = item.CreateDupe(world);
        Assert.Equal("04000123", copy.OwnedBy);
        Assert.Equal("heirloom", copy.OName);
    }

    [Fact]
    public void TheyRoundTripThroughASave()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"spn_basedef_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.BodyId = 0x190;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            ch.OName = "Real Name";
            var item = src.CreateItem();
            item.BaseId = 0x0EED;
            src.PlaceItem(item, new Point3D(1001, 1000, 0, 0));
            item.OwnedBy = "04000123";
            item.OName = "old coin";

            Assert.True(new WorldSaver(lf).Save(src, dir));

            var dst = MakeWorld();
            new WorldLoader(lf).Load(dst, dir);

            Assert.Equal("Real Name", dst.FindChar(ch.Uid)!.OName);
            var loaded = dst.FindItem(item.Uid)!;
            Assert.Equal("04000123", loaded.OwnedBy);
            Assert.Equal("old coin", loaded.OName);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
