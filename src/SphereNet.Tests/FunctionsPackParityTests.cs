using System.Reflection;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Expressions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Engine features the live functions/ script pack relies on that were
/// missing (Source-X-present only, per the maintainer's rule): the C
/// strftime translation for RTIME/RTICKS.FORMAT (every date/time function
/// produced garbage — %m read as minutes, %H:%M as hour:month, %Y/%w threw),
/// the CHR intrinsic (inverse of ASC), and the CONTP item verb.
/// </summary>
public class FunctionsPackParityTests
{
    private static string Resolve(string property)
    {
        var method = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(null, [property]) ?? "";
    }

    [Fact]
    public void RtimeFormat_TranslatesStrftimeTokens_NotDotNet()
    {
        // The known-good reference: format a FIXED timestamp through
        // RTICKS.FORMAT (which takes an explicit unix time) so the assertion
        // is deterministic. 2023-07-04 13:05:09 UTC.
        var dt = new DateTimeOffset(2023, 7, 4, 13, 5, 9, TimeSpan.Zero);
        long ts = dt.ToUnixTimeSeconds();
        // Local time may shift the clock; compare against the same conversion
        // the engine does (FromUnixTimeSeconds(...).LocalDateTime).
        var local = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;

        // %Y-%m-%d — year, MONTH (not minutes), day.
        Assert.Equal($"{local:yyyy}-{local:MM}-{local:dd}",
            Resolve($"RTICKS.FORMAT {ts},%Y-%m-%d"));

        // %H:%M — hour:MINUTE (the old code read %M as month).
        Assert.Equal($"{local:HH}:{local:mm}",
            Resolve($"RTICKS.FORMAT {ts},%H:%M"));

        // %w — weekday number 0..6 (invalid in .NET, used to throw → default).
        Assert.Equal(((int)local.DayOfWeek).ToString(),
            Resolve($"RTICKS.FORMAT {ts},%w"));

        // %B / %A — full month / weekday names resolve, not literal letters.
        // Invariant culture (Source-X strftime runs the C locale — stable
        // English names regardless of the server's OS locale).
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal($"{local.ToString("MMMM", inv)} {local.ToString("dddd", inv)}",
            Resolve($"RTICKS.FORMAT {ts},%B %A"));

        // Literal text around tokens survives; %% is an escaped percent.
        Assert.Equal($"[{local:yyyy}] 100%",
            Resolve($"RTICKS.FORMAT {ts},[%Y] 100%%"));
    }

    [Fact]
    public void RtimeFormat_CaseSensitiveTokens_AreNotUppercased()
    {
        // The dispatch used to pass an already-uppercased property, folding
        // %d (day) into %D and %m (month) into %M (minute) — the exact bug.
        var local = DateTime.Now;
        string y = Resolve("RTIME.FORMAT %Y");
        Assert.Equal(local.Year.ToString(), y);
        // %m is month (2 digits), NOT the current minute.
        Assert.Equal(local.ToString("MM"), Resolve("RTIME.FORMAT %m"));
    }

    [Fact]
    public void ChrIntrinsic_IsInverseOfAsc()
    {
        var parser = new ExpressionParser();
        Assert.Equal("A", parser.ResolveAngleBrackets("<CHR 65>"));
        Assert.Equal(" ", parser.ResolveAngleBrackets("<CHR 32>"));
        Assert.Equal("0", parser.ResolveAngleBrackets("<CHR 48>"));
        // Round-trip with ASC (which emits hex): 0x41 = 65 = 'A'.
        Assert.Equal("41", parser.ResolveAngleBrackets("<ASC A>"));
    }

    [Fact]
    public void ContpVerb_RepositionsAContainedItem()
    {
        var world = TestHarness.CreateWorld();
        Item.ResolveWorld = () => world;

        var bag = world.CreateItem();
        bag.ItemType = ItemType.Container;
        world.PlaceItem(bag, new Point3D(100, 100, 0, 0));

        var coin = world.CreateItem();
        bag.TryAddItem(coin);

        Assert.True(coin.TryExecuteCommand("CONTP", "44,121", null!));
        Assert.Equal(44, coin.X);
        Assert.Equal(121, coin.Y);
        Assert.Equal(bag.Uid, coin.ContainedIn); // still contained

        // A ground item ignores CONTP (Source-X: not-in-container is a no-op).
        var loose = world.CreateItem();
        world.PlaceItem(loose, new Point3D(200, 200, 0, 0));
        Assert.True(loose.TryExecuteCommand("CONTP", "10,10", null!));
        Assert.Equal(200, loose.X);
    }
}
