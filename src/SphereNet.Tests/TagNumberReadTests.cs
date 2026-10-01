using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A number TAG/VAR is saved in Sphere hex (Source-X CVarDefContNum::GetValStr: 42 is
/// written "02A") and a load keeps that text, so every engine read of a tag as a
/// number must use the Sphere rule - a leading '0' is hex, anything else decimal -
/// not a plain decimal parse, which fails on "02A" and falls back to a default, and
/// reads a Source-X "0123" (0x123) as 123.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TagNumberReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spn_tagnum_{Guid.NewGuid():N}");
    private readonly ILoggerFactory _lf = LoggerFactory.Create(_ => { });

    public TagNumberReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private GameWorld SaveAndLoad(GameWorld world, SaveFormat format, string sub)
    {
        string dir = Path.Combine(_dir, sub);
        var saver = new WorldSaver(_lf) { Format = format, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(world, dir));
        var dst = MakeWorld();
        new WorldLoader(_lf).Load(dst, dir);
        return dst;
    }

    // ---- the shared parser ----------------------------------------------------

    [Theory]
    [InlineData("42", 42)]
    [InlineData("0", 0)]
    [InlineData("00", 0)]
    [InlineData("02A", 42)]
    [InlineData("0a", 10)]
    [InlineData("0123", 0x123)]
    [InlineData("-5", -5)]
    [InlineData("-0A", -10)]
    [InlineData("0FFFFFFFF", -1)]
    [InlineData("0FFFFFFFB", -5)]
    [InlineData("080000000", int.MinValue)]
    [InlineData("0x1F", 31)]
    [InlineData(" 07 ", 7)]
    public void TryParseIntReadsSphereNumbers(string text, int expected)
    {
        Assert.True(ScriptNumber.TryParseInt(text, out int v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1.5")]
    [InlineData("0100000000")]     // wider than 32 bits
    [InlineData("-")]
    public void TryParseIntRefusesNonNumbersAndOutOfRange(string? text)
    {
        Assert.False(ScriptNumber.TryParseInt(text, out int v));
        Assert.Equal(0, v);
    }

    [Fact]
    public void TypedReadsCheckTheirRange()
    {
        Assert.True(ScriptNumber.TryParseUInt("0FFFFFFFF", out uint u));
        Assert.Equal(uint.MaxValue, u);
        Assert.True(ScriptNumber.TryParseUInt("04000123", out u));
        Assert.Equal(0x04000123u, u);
        Assert.False(ScriptNumber.TryParseUInt("-1", out _));
        Assert.False(ScriptNumber.TryParseUInt("0100000000", out _));

        Assert.True(ScriptNumber.TryParseLong("0FFFFFFFF", out long l));
        Assert.Equal(4294967295L, l);
        Assert.True(ScriptNumber.TryParseLong("0100000000", out l));
        Assert.Equal(0x100000000L, l);
        Assert.True(ScriptNumber.TryParseLong("638000000000000000", out l));
        Assert.Equal(638000000000000000L, l);

        Assert.True(ScriptNumber.TryParseShort("0FFFFFFFB", out short s));
        Assert.Equal((short)-5, s);
        Assert.True(ScriptNumber.TryParseShort("01000", out s));
        Assert.Equal((short)0x1000, s);
        Assert.False(ScriptNumber.TryParseShort("08000", out _));

        Assert.True(ScriptNumber.TryParseUShort("0FFFF", out ushort us));
        Assert.Equal(ushort.MaxValue, us);
        Assert.False(ScriptNumber.TryParseUShort("010000", out _));
        Assert.False(ScriptNumber.TryParseUShort("-1", out _));

        Assert.True(ScriptNumber.TryParseByte("0FF", out byte b));
        Assert.Equal((byte)255, b);
        Assert.False(ScriptNumber.TryParseByte("0100", out _));
        Assert.False(ScriptNumber.TryParseByte("-1", out _));

        Assert.True(ScriptNumber.TryParseSByte("0FFFFFFFB", out sbyte sb));
        Assert.Equal((sbyte)-5, sb);
        Assert.True(ScriptNumber.TryParseSByte("-128", out sb));
        Assert.Equal(sbyte.MinValue, sb);
        Assert.False(ScriptNumber.TryParseSByte("080", out _));
    }

    [Fact]
    public void VarMapGetIntReadsSphereHex()
    {
        var map = new SphereNet.Scripting.Variables.VarMap();
        map.Set("A", "02A");
        map.Set("B", "010");
        map.Set("C", "42");
        map.Set("D", "0x10");
        map.Set("E", "text");
        Assert.Equal(42, map.GetInt("A"));
        Assert.Equal(16, map.GetInt("B"));
        Assert.Equal(42, map.GetInt("C"));
        Assert.Equal(16, map.GetInt("D"));
        Assert.Equal(-7, map.GetInt("E", -7));
    }

    // ---- engine reads after a save and a reload ------------------------------

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.Binary)]
    public void NumberTagsReadBackThroughEnginePathsAfterAReload(SaveFormat format)
    {
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        ch.BodyId = 0x190;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        // Number vars: an engine SetInt and an unquoted script assignment both save
        // as Sphere hex.
        ch.Tags.SetInt("BONDING_START", 123456);
        ch.SetTagStr("JAIL_CELL", quoted: false, "10");
        ch.SetTagStr("STATLOCK.0", quoted: false, "2");

        var house = world.CreateItem();
        house.BaseId = 0x0EED;
        world.PlaceItem(house, new Point3D(1010, 1010, 0, 0));
        house.Tags.SetInt(HouseDesign.RevisionTag, 26);
        house.Tags.SetInt("OVERRIDE.REQSTR", 42);

        var dst = SaveAndLoad(world, format, format.ToString());
        var loaded = dst.FindChar(ch.Uid)!;
        var loadedHouse = dst.FindItem(house.Uid)!;

        if (format == SaveFormat.Text)
        {
            Assert.Equal("02A", loadedHouse.Tags.Get("OVERRIDE.REQSTR"));   // the bug's precondition
            Assert.Equal("0A", loaded.Tags.Get("JAIL_CELL"));
        }
        Assert.Equal(42, loadedHouse.ReqStr);
        Assert.Equal(123456, loaded.BondingStartTick);
        Assert.Equal(10, loaded.JailCell);
        Assert.Equal(26u, HouseDesign.LoadFromTags(loadedHouse).Revision);
        loaded.MigrateStatLockFromTags();
        Assert.Equal(2, loaded.GetStatLock(0));
    }

    [Fact]
    public void ASourceXLeadingZeroValueIsHexNotDecimal()
    {
        string src = Path.Combine(_dir, "sx");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "sphereworld.scp"),
            "[WORLDITEM 0eed]\r\nSERIAL=040000301\r\nP=1000,1000,0\r\n" +
            "TAG.HITS=0123\r\nTAG.HITSMAX=0FA\r\n\r\n[EOF]\r\n");

        var world = MakeWorld();
        new WorldLoader(_lf).Load(world, src);
        var item = world.GetAllObjects().OfType<Item>().Single();
        Assert.Equal(0x123, item.HitsCur);
        Assert.Equal(250, item.HitsMax);
    }

    [Fact]
    public void AnAccountNumberTagReadsBackAfterAReload()
    {
        var accounts = new AccountManager(_lf);
        var acc = accounts.CreateAccount("cellmate", "pw")!;
        acc.Tags.SetInt("JailCell", 12);

        string dir = Path.Combine(_dir, "acc");
        Directory.CreateDirectory(dir);
        AccountPersistence.Save(accounts, dir, SaveFormat.Text);
        var loaded = new AccountManager(_lf);
        Assert.Equal(1, AccountPersistence.Load(loaded, dir));
        var back = loaded.FindAccount("cellmate")!;
        Assert.Equal("0C", back.Tags.Get("JailCell"));

        var world = MakeWorld();
        var ch = world.CreateCharacter();
        Character.ResolveAccountForChar = _ => back;
        Assert.Equal(12, ch.JailCell);
    }
}
