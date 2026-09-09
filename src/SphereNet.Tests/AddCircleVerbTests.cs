using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// ADDCIRCLE fills a spellbook a circle at a time (port plan İŞ-15).
///
/// Upstream (CItem.cpp:3213) writes the eight spells of the circle it is given, and
/// every circle below it when the second argument is not zero; anything that is not a
/// spellbook is refused. The magery script in the pack here calls it nine times and got
/// nothing, because the verb did not exist.
/// </summary>
public sealed class AddCircleVerbTests
{
    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Player;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => null;
    }

    private static Item Book()
    {
        var world = TestHarness.CreateWorld();
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        return book;
    }

    private static bool Has(Item book, int spell)
    {
        Assert.True(book.TryGetProperty("MORE1", out string? m1));
        Assert.True(book.TryGetProperty("MORE2", out string? m2));
        uint more1 = uint.Parse(m1!, System.Globalization.NumberStyles.HexNumber);
        uint more2 = uint.Parse(m2!, System.Globalization.NumberStyles.HexNumber);
        return spell < 32 ? (more1 & (1u << spell)) != 0 : (more2 & (1u << (spell - 32))) != 0;
    }

    [Fact]
    public void OneCircleIsEightSpells()
    {
        var book = Book();

        Assert.True(book.TryExecuteCommand("ADDCIRCLE", "2", new Console()));

        for (int spell = 9; spell <= 16; spell++)         // circle 2 is spells 9..16
            Assert.True(Has(book, spell), $"spell {spell} should be in the book");
        Assert.False(Has(book, 8));                       // and nothing from circle 1
        Assert.False(Has(book, 17));
    }

    [Fact]
    public void TheSecondArgumentAddsEveryCircleBelow()
    {
        var book = Book();

        Assert.True(book.TryExecuteCommand("ADDCIRCLE", "3,1", new Console()));

        for (int spell = 1; spell <= 24; spell++)         // circles 1, 2 and 3
            Assert.True(Has(book, spell), $"spell {spell} should be in the book");
        Assert.False(Has(book, 25));
    }

    [Fact]
    public void AZeroSecondArgumentIsJustTheOneCircle()
    {
        var book = Book();

        Assert.True(book.TryExecuteCommand("ADDCIRCLE", "3,0", new Console()));

        Assert.False(Has(book, 1));
        for (int spell = 17; spell <= 24; spell++)
            Assert.True(Has(book, spell));
    }

    [Fact]
    public void AnythingThatIsNotASpellbookRefuses()
    {
        var world = TestHarness.CreateWorld();
        var rock = world.CreateItem();
        rock.ItemType = ItemType.Normal;

        Assert.False(rock.TryExecuteCommand("ADDCIRCLE", "1", new Console()));
    }

    [Fact]
    public void NoArgumentIsRefused()
    {
        Assert.False(Book().TryExecuteCommand("ADDCIRCLE", "", new Console()));
    }
}
